using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DigitalTwin.Api;
using Microsoft.Extensions.Logging.Abstractions;

namespace DigitalTwin.Api.Tests;

/// <summary>In-process HTTP contract fixture; never uses a real collection or model.</summary>
internal sealed class KnowledgeBackend : HttpMessageHandler
{
    public TwinSettings Settings { get; } = new()
    {
        QdrantUrl = "http://qdrant.test", OllamaUrl = "http://ollama.test", Collection = "test",
        EmbeddingDimensions = 2, KnowledgeMinScore = 0.3
    };
    public Dictionary<string, JsonObject> Points { get; } = new();
    public int Embeddings { get; private set; }
    public int RerankCalls { get; private set; }
    public bool FailEmbedding { get; set; }
    public bool FailRerank { get; set; }
    public bool WaitForRerankCancellation { get; set; }
    public TaskCompletionSource RerankStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string? RerankResponse { get; set; }
    public JsonObject? LastSearch { get; private set; }
    public JsonObject? LastRerank { get; private set; }
    public bool Exists { get; set; } = true;
    private readonly HttpClient _http;
    public KnowledgeStore Store { get; }

    public KnowledgeBackend()
    {
        _http = new HttpClient(this, disposeHandler: false);
        Store = new KnowledgeStore(_http, Settings);
    }

    public KnowledgeRetrievalService Retrieval(IReranker? reranker = null) => new(Store,
        reranker ?? new LlmReranker(_http, Settings), Settings, NullLogger<KnowledgeRetrievalService>.Instance);

    public string Seed(string source, string text, double score = .8, string? status = "active")
    {
        var id = Guid.NewGuid().ToString();
        var payload = new JsonObject { ["source"] = source, ["text"] = text, ["chunk"] = 0, ["revision"] = "seed" };
        if (status is not null) payload["status"] = status;
        Points[id] = new JsonObject { ["id"] = id, ["payload"] = payload, ["score"] = score };
        return id;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? new JsonObject() : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct))!.AsObject();
        if (path == "/api/embed")
        {
            Embeddings++;
            return FailEmbedding ? Reply(new { error = "unavailable" }, HttpStatusCode.ServiceUnavailable)
                : Reply(new { embeddings = new[] { new[] { 1.0, 0.0 } } });
        }
        if (path == "/api/chat")
        {
            LastRerank = body;
            RerankCalls++;
            RerankStarted.TrySetResult();
            if (WaitForRerankCancellation) await Task.Delay(Timeout.Infinite, ct);
            if (FailRerank) return Reply(new { error = "unavailable" }, HttpStatusCode.ServiceUnavailable);
            return Reply(new { message = new { content = RerankResponse ?? "{}" } });
        }
        if (path == "/collections/test")
        {
            if (request.Method == HttpMethod.Put) Exists = true;
            return Reply(new { result = new { } }, Exists ? HttpStatusCode.OK : HttpStatusCode.NotFound);
        }
        if (path.EndsWith("/points/scroll"))
        {
            var offset = body["offset"]?.GetValue<int>() ?? 0;
            var limit = body["limit"]!.GetValue<int>();
            var all = Points.Values.Where(p => Matches(p["payload"]!.AsObject(), body["filter"])).ToArray();
            return Reply(new { result = new { points = all.Skip(offset).Take(limit), next_page_offset = offset + limit < all.Length ? (int?)(offset + limit) : null } });
        }
        if (path.EndsWith("/points/search"))
        {
            LastSearch = body;
            var hits = Points.Values.Where(p => Matches(p["payload"]!.AsObject(), body["filter"]) &&
                    p["score"]!.GetValue<double>() >= body["score_threshold"]!.GetValue<double>())
                .OrderByDescending(p => p["score"]!.GetValue<double>()).Take(body["limit"]!.GetValue<int>()).ToArray();
            return Reply(new { result = hits });
        }
        if (path.EndsWith("/points/payload"))
        {
            foreach (var point in Points.Values.Where(p => Matches(p["payload"]!.AsObject(), body["filter"])))
                foreach (var field in body["payload"]!.AsObject()) point["payload"]![field.Key] = field.Value?.DeepClone();
            return Reply(new { result = new { status = "completed" } });
        }
        if (path.EndsWith("/points/delete"))
        {
            foreach (var id in body["points"]!.AsArray()) Points.Remove(id!.GetValue<string>());
            return Reply(new { result = new { status = "completed" } });
        }
        if (path.EndsWith("/points") && request.Method == HttpMethod.Put)
        {
            foreach (var node in body["points"]!.AsArray())
            {
                var point = node!.DeepClone().AsObject();
                point["score"] = .8;
                Points[point["id"]!.GetValue<string>()] = point;
            }
            return Reply(new { result = new { status = "completed" } });
        }
        throw new InvalidOperationException($"Unexpected test HTTP request: {request.Method} {path}");
    }

    private static bool Matches(JsonObject payload, JsonNode? filter)
    {
        if (filter is null) return true;
        if (filter["must"] is JsonArray must && !must.All(c => Matches(payload, c))) return false;
        if (filter["should"] is JsonArray should && !should.Any(c => Matches(payload, c))) return false;
        if (filter["key"] is JsonNode key)
            return JsonNode.DeepEquals(payload[key.GetValue<string>()], filter["match"]!["value"]);
        if (filter["is_empty"] is JsonNode empty) return payload[empty["key"]!.GetValue<string>()] is null;
        return true;
    }

    private static HttpResponseMessage Reply(object data, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(data), System.Text.Encoding.UTF8, "application/json") };

    protected override void Dispose(bool disposing)
    {
        if (disposing) _http.Dispose();
        base.Dispose(disposing);
    }
}
