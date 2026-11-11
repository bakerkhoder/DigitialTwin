using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DigitalTwin.Api;

public sealed record KnowledgeDocument(string Id, string Source, int Chunks, string Revision, string? IndexedAtUtc,
    string Status = "active", string ReviewStatus = "legacy", string[]? RiskReasons = null);
public sealed record KnowledgeIngestResult(int Chunks, bool Unchanged, string Revision, string Status,
    string ReviewStatus = "not-required", string[]? RiskReasons = null);
public sealed record KnowledgeReviewChunk(int Chunk, string Text);
public sealed record KnowledgeReviewPreview(KnowledgeDocument Document, IReadOnlyList<KnowledgeReviewChunk> Chunks);
public sealed class KnowledgeReviewConflictException() : Exception("The document changed after it was opened for review. Reload it before approving.");

/// <summary>
/// Qdrant is the source of truth for indexed passages. A document's identity is its
/// normalized filename in THIS single-user demo. Re-uploading that name replaces
/// the old revision; use distinct filenames for different source documents.
/// </summary>
public sealed class KnowledgeStore(HttpClient http, TwinSettings settings, ContentSafetyService? safety = null)
{
    private readonly ContentSafetyService _safety = safety ?? new ContentSafetyService();
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
    private sealed record IndexedPoint(string Id, string Source, string Revision, string? IndexedAtUtc, string Status,
        string Pipeline, string ReviewStatus, string[] RiskReasons, int Chunk, string Text);

    // Scroll ALL pages; returning just the first page would leak stale/unlisted documents.
    private async Task<List<IndexedPoint>> Scroll(string? source, CancellationToken ct, bool includeText = false)
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
                    payload.TryGetProperty("indexedAtUtc", out var when) ? when.GetString() : null,
                    payload.TryGetProperty("status", out var status) ? status.GetString() ?? "active" : "active",
                    payload.TryGetProperty("pipeline", out var pipeline) ? pipeline.GetString() ?? "" : "",
                    payload.TryGetProperty("reviewStatus", out var review) ? review.GetString() ?? "legacy" : "legacy",
                    payload.TryGetProperty("riskReasons", out var risks) && risks.ValueKind == JsonValueKind.Array
                        ? risks.EnumerateArray().Select(r => r.GetString() ?? "").ToArray() : [],
                    payload.TryGetProperty("chunk", out var chunk) ? chunk.GetInt32() : 0,
                    includeText && payload.TryGetProperty("text", out var text) ? text.GetString() ?? "" : ""));
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
        return points.GroupBy(p => p.Source, StringComparer.Ordinal).Select(g => Describe(g.ToArray()))
            .OrderBy(x => x.Source, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static KnowledgeDocument Describe(IReadOnlyList<IndexedPoint> points)
    {
        var latest = points.OrderByDescending(p => p.IndexedAtUtc).First();
        var review = points.Any(p => p.ReviewStatus == "pending") ? "pending" : latest.ReviewStatus;
        return new(DocumentId(latest.Source), latest.Source, points.Count, latest.Revision,
            points.Max(p => p.IndexedAtUtc), points.Any(p => p.Status == "deprecated") ? "deprecated" : "active",
            review, points.SelectMany(p => p.RiskReasons).Distinct().ToArray());
    }

    public async Task<KnowledgeReviewPreview?> GetReview(string id, CancellationToken ct)
    {
        await _mutation.WaitAsync(ct);
        try
        {
            var source = (await Scroll(null, ct)).FirstOrDefault(p => DocumentId(p.Source) == id)?.Source;
            if (source is null) return null;
            var points = await Scroll(source, ct, includeText: true);
            return new(Describe(points), points.OrderBy(p => p.Chunk)
                .Select(p => new KnowledgeReviewChunk(p.Chunk, SensitiveDataPolicy.Redact(p.Text))).ToArray());
        }
        finally { _mutation.Release(); }
    }

    public async Task<bool> ApproveReview(string id, string expectedRevision, CancellationToken ct)
    {
        await _mutation.WaitAsync(ct);
        try
        {
            var points = (await Scroll(null, ct)).Where(p => DocumentId(p.Source) == id).ToArray();
            if (points.Length == 0) return false;
            if (points.Any(p => p.Revision != expectedRevision)) throw new KnowledgeReviewConflictException();
            if (points.Any(p => p.RiskReasons.Contains("possible-credential")))
                throw new GuardrailViolationException("credential_document", "Remove possible credentials from the original document and re-upload it before approval.");
            using var response = await http.PostAsJsonAsync($"{CollectionPath}/points/payload?wait=true", new
            {
                payload = new { reviewStatus = "approved", reviewedAtUtc = DateTimeOffset.UtcNow.ToString("O") },
                filter = new { must = new[]
                {
                    new { key = "source", match = new { value = points[0].Source } },
                    new { key = "revision", match = new { value = expectedRevision } }
                } }
            }, ct);
            response.EnsureSuccessStatusCode();
            return true;
        }
        finally { _mutation.Release(); }
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
    public async Task<bool> SetDocumentStatus(string id, string status, CancellationToken ct)
    {
        if (status is not ("active" or "deprecated")) throw new ArgumentException("Status must be active or deprecated.");
        await _mutation.WaitAsync(ct);
        try
        {
            var matched = (await Scroll(null, ct)).Where(p => DocumentId(p.Source) == id).ToArray();
            if (matched.Length == 0) return false;
            // One server-side filter updates all chunks, including legacy points.
            using var response = await http.PostAsJsonAsync($"{CollectionPath}/points/payload?wait=true", new
            {
                payload = new { status, statusChangedAtUtc = DateTimeOffset.UtcNow.ToString("O") },
                filter = new { must = new[] { new { key = "source", match = new { value = matched[0].Source } } } }
            }, ct);
            response.EnsureSuccessStatusCode();
            return true;
        }
        finally { _mutation.Release(); }
    }

    public async Task<KnowledgeIngestResult> Ingest(string source, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Document is empty.");
        if (text.Length > TextExtractor.MaxCharacters)
            throw new ArgumentException($"Document exceeds {TextExtractor.MaxCharacters:N0} extracted characters. Split it into smaller documents.");
        source = Path.GetFileName(source);
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Missing document name.");
        using var activity = DeveloperTwinTelemetry.Activities.StartActivity("knowledge.ingest");
        const int size = 1100, overlap = 160;
        var chunks = new List<string>();
        for (int i = 0; i < text.Length; i += size - overlap)
        {
            chunks.Add(text.Substring(i, Math.Min(size, text.Length - i)));
            if (i + size >= text.Length) break;
        }
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        // Re-index once when changing embedding models/dimensions or chunking rules.
        var pipeline = $"chunks-v3|{settings.EmbeddingModel}|{settings.EmbeddingDimensions}|screening-v1";
        var assessment = _safety.Assess(source + "\n" + text);
        var indexedAt = DateTimeOffset.UtcNow.ToString("O");
        var newIds = Enumerable.Range(0, chunks.Count).Select(i => new Guid(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{source}|{revision}|{pipeline}|{i}")).AsSpan(0, 16)).ToString()).ToArray();
        // Keep revision checks and writes under one gate so concurrent uploads cannot
        // both embed the same revision or accidentally reactivate deprecated content.
        await _mutation.WaitAsync(ct);
        try
        {
            var previous = await Scroll(source, ct);
            var status = previous.Any(p => p.Status == "deprecated") ? "deprecated" : "active";
            var oldIds = previous.Select(x => x.Id).ToHashSet();
            if (previous.Count == chunks.Count && previous.All(p => p.Revision == revision && p.Pipeline == pipeline)
                && oldIds.SetEquals(newIds))
            {
                activity?.SetTag("knowledge.unchanged", true);
                return new(chunks.Count, true, revision, status, previous[0].ReviewStatus, previous[0].RiskReasons);
            }
            // All embeddings must succeed before any indexed content is changed.
            var points = new List<object>();
            for (int i = 0; i < chunks.Count; i++)
            {
                var vector = await Embed(chunks[i], ct);
                if (vector.Length != settings.EmbeddingDimensions || vector.Any(v => !float.IsFinite(v)))
                    throw new InvalidOperationException($"Embedding must contain {settings.EmbeddingDimensions} finite dimensions.");
                points.Add(new
                {
                    id = newIds[i], vector,
                    payload = new
                    {
                        source, documentId = DocumentId(source), revision, indexedAtUtc = indexedAt,
                        text = chunks[i], chunk = i, status, pipeline, trustLevel = "user-uploaded",
                        reviewStatus = assessment.ReviewStatus, riskReasons = assessment.RiskReasons
                    }
                });
            }
            await EnsureCollection(ct);
            try
            {
                foreach (var batch in points.Chunk(25))
                {
                    using var response = await http.PutAsJsonAsync($"{CollectionPath}/points?wait=true",
                        new { points = batch }, ct);
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
            activity?.SetTag("knowledge.chunks", chunks.Count);
            activity?.SetTag("knowledge.unchanged", false);
            DeveloperTwinTelemetry.KnowledgeChunks.Add(chunks.Count);
            return new(chunks.Count, false, revision, status, assessment.ReviewStatus, assessment.RiskReasons);
        }
        finally { _mutation.Release(); }
    }

    public async Task<IReadOnlyList<KnowledgeHit>> SearchCandidates(string query, CancellationToken ct, int candidateCount = 10)
    {
        using var activity = DeveloperTwinTelemetry.Activities.StartActivity("knowledge.retrieve");
        if (string.IsNullOrWhiteSpace(query) || query.Length > 500 || candidateCount is < 1 or > 50)
            throw new ArgumentException("Invalid knowledge search query or candidate count.");
        if (!await CollectionExists(ct)) return [];
        var vector = await Embed(query, ct);
        if (vector.Length != settings.EmbeddingDimensions || vector.Any(v => !float.IsFinite(v)))
            throw new InvalidOperationException($"Embedding must contain {settings.EmbeddingDimensions} finite dimensions.");
        // Apply lifecycle filtering inside Qdrant BEFORE selecting top candidates.
        // Missing status means active for compatibility with the existing library.
        var filter = new { must = new object[]
        {
            new { should = new object[]
            {
                new { key = "status", match = new { value = "active" } },
                new { is_empty = new { key = "status" } }
            } },
            new { should = new object[]
            {
                new { key = "reviewStatus", match = new { value = "approved" } },
                new { key = "reviewStatus", match = new { value = "not-required" } },
                new { is_empty = new { key = "reviewStatus" } }
            } }
        } };
        using var response = await http.PostAsJsonAsync($"{CollectionPath}/points/search",
            new { vector, limit = candidateCount, score_threshold = settings.KnowledgeMinScore, with_payload = true, filter }, ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var hits = new List<KnowledgeHit>();
        foreach (var p in json.RootElement.GetProperty("result").EnumerateArray())
        {
            var payload = p.GetProperty("payload");
            if (payload.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String && status.GetString() != "active") continue;
            var source = payload.GetProperty("source").GetString() ?? "unknown";
            var text = payload.GetProperty("text").GetString() ?? "";
            var reviewStatus = payload.TryGetProperty("reviewStatus", out var review) ? review.GetString() : null;
            if (reviewStatus is not (null or "approved" or "not-required")) continue;
            // Legacy points remain compatible, but detected attacks are screened before reaching a model.
            if (reviewStatus is null && _safety.Assess(source + "\n" + text).RequiresReview) continue;
            hits.Add(new(source, SensitiveDataPolicy.Redact(text), p.GetProperty("score").GetDouble(),
                p.GetProperty("id").ToString(), DocumentId(source),
                payload.TryGetProperty("revision", out var revision) ? revision.GetString() ?? "legacy" : "legacy",
                payload.TryGetProperty("chunk", out var chunk) ? chunk.GetInt32() : 0, hits.Count + 1));
        }
        activity?.SetTag("knowledge.candidates", hits.Count);
        return hits;
    }
}
