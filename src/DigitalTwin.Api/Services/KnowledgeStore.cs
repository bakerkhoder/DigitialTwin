using System.ComponentModel;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DigitalTwin.Api;

public sealed record KnowledgeDocument(string Id, string Source, int Chunks, string Revision, string? IndexedAtUtc);

/// <summary>
/// Qdrant is the source of truth for indexed passages. A document's identity is its
/// normalized filename in THIS single-user demo. Re-uploading that name replaces
/// the old revision; use distinct filenames for different source documents.
/// </summary>
public sealed class KnowledgeStore(HttpClient http, TwinSettings settings)
{
    // Serialize changes so a simultaneous delete/upload cannot leave stray chunks.
    private readonly SemaphoreSlim _mutation = new(1, 1);
    private string CollectionPath => $"{settings.QdrantUrl.TrimEnd('/')}/collections/{Uri.EscapeDataString(settings.Collection)}";
    private static string DocumentId(string source) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant()[..24];

    public async Task<(bool Qdrant, bool Ollama, string Message)> Health(CancellationToken ct)
    {
        bool db = false, embed = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try { using var r = await http.GetAsync($"{settings.QdrantUrl}/collections", timeout.Token); db = r.IsSuccessStatusCode; }
        catch (Exception ex) when ((ex is HttpRequestException or OperationCanceledException) && !ct.IsCancellationRequested) { }
        try { using var r = await http.GetAsync($"{settings.OllamaUrl}/api/tags", timeout.Token); embed = r.IsSuccessStatusCode; }
        catch (Exception ex) when ((ex is HttpRequestException or OperationCanceledException) && !ct.IsCancellationRequested) { }
        return (db, embed, !db ? "Qdrant is offline. Start the Docker Qdrant container." :
            !embed ? "Ollama embeddings are offline. Start your installed nomic-embed-text model." :
            "Qdrant and Ollama are reachable.");
    }
    private async Task<float[]> Embed(string text, CancellationToken ct)
    {
        using var res = await http.PostAsJsonAsync($"{settings.OllamaUrl}/api/embed",
            new { model = settings.EmbeddingModel, input = text }, ct);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("embeddings")[0].EnumerateArray().Select(x => x.GetSingle()).ToArray();
    }
    public async Task<bool> CollectionExists(CancellationToken ct)
    {
        using var response = await http.GetAsync(CollectionPath, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }
    public async Task EnsureCollection(CancellationToken ct)
    {
        if (await CollectionExists(ct)) return;
        using var res = await http.PutAsJsonAsync(CollectionPath,
            new { vectors = new { size = settings.EmbeddingDimensions, distance = "Cosine" } }, ct);
        res.EnsureSuccessStatusCode();
    }
    private sealed record IndexedPoint(string Id, string Source, string Revision, string? IndexedAtUtc);

    // Scroll ALL pages; returning just the first page would leak stale/unlisted documents.
    private async Task<List<IndexedPoint>> Scroll(string? source, CancellationToken ct)
    {
        if (!await CollectionExists(ct)) return [];
        var all = new List<IndexedPoint>();
        JsonElement? offset = null;
        do
        {
            object? filter = source is null ? null : new
            {
                must = new[] { new { key = "source", match = new { value = source } } }
            };
            var request = new Dictionary<string, object?>
            {
                ["limit"] = 200,
                ["with_payload"] = true,
                ["with_vector"] = false,
                ["filter"] = filter,
                ["offset"] = offset
            };
            using var response = await http.PostAsJsonAsync($"{CollectionPath}/points/scroll", request, ct);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var result = json.RootElement.GetProperty("result");
            foreach (var point in result.GetProperty("points").EnumerateArray())
            {
                if (!point.TryGetProperty("payload", out var payload)) continue;
                var name = payload.TryGetProperty("source", out var src) ? src.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;
                all.Add(new IndexedPoint(point.GetProperty("id").ToString(), name,
                    payload.TryGetProperty("revision", out var revision) ? revision.GetString() ?? "legacy" : "legacy",
                    payload.TryGetProperty("indexedAtUtc", out var when) ? when.GetString() : null));
            }
            offset = result.TryGetProperty("next_page_offset", out var next) && next.ValueKind != JsonValueKind.Null
                ? next.Clone() : null;
            if (all.Count > 250_000) throw new InvalidOperationException("Knowledge library has exceeded the demo listing limit.");
        } while (offset.HasValue);
        return all;
    }
    public async Task<IReadOnlyList<KnowledgeDocument>> ListDocuments(CancellationToken ct)
    {
        var points = await Scroll(null, ct);
        return points.GroupBy(p => p.Source, StringComparer.Ordinal).Select(g =>
            new KnowledgeDocument(DocumentId(g.Key), g.Key, g.Count(),
                g.OrderByDescending(p => p.IndexedAtUtc).First().Revision,
                g.Max(p => p.IndexedAtUtc))).OrderBy(x => x.Source, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private async Task DeletePointIds(IEnumerable<string> pointIds, CancellationToken ct)
    {
        foreach (var chunk in pointIds.Distinct().Chunk(100))
        {
            using var response = await http.PostAsJsonAsync($"{CollectionPath}/points/delete?wait=true",
                new { points = chunk }, ct);
            response.EnsureSuccessStatusCode();
        }
    }
    public async Task<bool> DeleteDocument(string id, CancellationToken ct)
    {
        await _mutation.WaitAsync(ct);
        try
        {
            var matched = (await Scroll(null, ct)).Where(p => DocumentId(p.Source) == id).ToArray();
            if (matched.Length == 0) return false;
            await DeletePointIds(matched.Select(p => p.Id), ct); // Deletes vectors AND payload.
            return true;
        }
        finally { _mutation.Release(); }
    }
    public async Task<int> Ingest(string source, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Document is empty.");
        source = Path.GetFileName(source);
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Missing document name.");
        using var activity = DeveloperTwinTelemetry.Activities.StartActivity("knowledge.ingest");
        var health = await Health(ct);
        if (!health.Qdrant || !health.Ollama) throw new InvalidOperationException(health.Message);
        // Compute embeddings BEFORE touching indexed content. If the model fails,
        // the previous indexed revision remains intact.
        const int size = 1100, overlap = 160;
        var chunks = new List<string>();
        for (int i = 0; i < text.Length && chunks.Count < 300; i += size - overlap)
            chunks.Add(text.Substring(i, Math.Min(size, text.Length - i)));
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        var indexedAt = DateTimeOffset.UtcNow.ToString("O");
        var points = new List<(string Id, object Point)>();
        for (int i = 0; i < chunks.Count; i++)
        {
            var vector = await Embed(chunks[i], ct);
            if (vector.Length != settings.EmbeddingDimensions)
                throw new InvalidOperationException($"Embedding dimension mismatch: expected {settings.EmbeddingDimensions}, got {vector.Length}.");
            // Deterministic UUID for same document, revision and chunk = retry-safe.
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(source + "|" + revision + "|" + i));
            var id = new Guid(bytes.AsSpan(0, 16)).ToString();
            points.Add((id, new
            {
                id,
                vector,
                payload = new
                {
                    source,
                    documentId = DocumentId(source),
                    revision,
                    indexedAtUtc = indexedAt,
                    text = chunks[i],
                    chunk = i
                }
            }));
        }
        await _mutation.WaitAsync(ct);
        try
        {
            await EnsureCollection(ct);
            var oldIds = (await Scroll(source, ct)).Select(x => x.Id).ToHashSet();
            var newIds = points.Select(x => x.Id).ToHashSet();
            try
            {
                foreach (var batch in points.Chunk(25))
                {
                    using var response = await http.PutAsJsonAsync($"{CollectionPath}/points?wait=true",
                        new { points = batch.Select(x => x.Point).ToArray() }, ct);
                    response.EnsureSuccessStatusCode();
                }
            }
            catch
            {
                // Best-effort cleanup of newly staged points after a failed upsert.
                try { await DeletePointIds(newIds.Except(oldIds), CancellationToken.None); }
                catch { /* Next re-index or manual delete can repair partial state. */ }
                throw;
            }
            // Remove every previous point, including legacy source+chunk IDs and
            // extra chunks left by shorter replacements. No old version survives.
            await DeletePointIds(oldIds.Except(newIds), ct);
        }
        finally { _mutation.Release(); }
        activity?.SetTag("knowledge.chunks", chunks.Count);
        DeveloperTwinTelemetry.KnowledgeChunks.Add(chunks.Count);
        return chunks.Count;
    }

    [Description(
        "Search ALL indexed user-uploaded knowledge using " +
        "semantic similarity. Includes business documents, " +
        "ERP requirements, technical notes, policies, " +
        "personal profiles, hobbies, names, preferences, " +
        "and any other authorized uploaded information. " +
        "Call this tool when answering questions about " +
        "the user's uploaded files. Return grounded " +
        "information and cite the source filename. " +
        "Do not invent missing information."
    )]
    public async Task<string> SearchKnowledge(
            [Description("Required natural-language question about uploaded documents, maximum 500 characters.")] string query,
            [Description("Maximum passages to return, 1 to 5, default 5.")] int maxResults = 5)
    {
        using var telemetry = DeveloperTwinTelemetry.BeginTool("SearchKnowledge");
        if (string.IsNullOrWhiteSpace(query) || query.Length > 500 || maxResults is < 1 or > 5)
        {
            telemetry.Fail("invalid_request");
            return JsonSerializer.Serialize(new { error = "query must be 1–500 characters and maxResults 1–5." });
        }
        try { return JsonSerializer.Serialize(await Search(query, CancellationToken.None, maxResults)); }
        catch { telemetry.Fail(); throw; }
    }
    public async Task<IReadOnlyList<KnowledgeHit>> Search(string query, CancellationToken ct, int maxResults = 5)
    {
        using var activity = DeveloperTwinTelemetry.Activities.StartActivity("knowledge.retrieve");
        var health = await Health(ct);
        if (!health.Qdrant || !health.Ollama) throw new InvalidOperationException(health.Message);
        if (!await CollectionExists(ct)) return [];
        var vector = await Embed(query, ct);
        using var response = await http.PostAsJsonAsync($"{CollectionPath}/points/search",
            new { vector, limit = Math.Clamp(maxResults, 1, 5), score_threshold = Math.Clamp(settings.KnowledgeMinScore, 0, 1), with_payload = true }, ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.GetProperty("result").EnumerateArray().Select(p => new KnowledgeHit(
            p.GetProperty("payload").GetProperty("source").GetString() ?? "unknown",
            p.GetProperty("payload").GetProperty("text").GetString() ?? "",
            p.GetProperty("score").GetDouble())).ToArray();
    }
}
