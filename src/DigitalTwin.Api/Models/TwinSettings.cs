using System.Text.Json.Serialization;
namespace DigitalTwin.Api;

public sealed class TwinSettings
{
    [JsonIgnore] public string? OpenRouterApiKey { get; set; }
    public string Workspace { get; set; } = "./workspace";
    public string StateDatabase { get; set; } = "./data/state/developertwin.db";
    public string OllamaUrl { get; set; } = "http://127.0.0.1:11434";
    public string Provider { get; set; } = "ollama";
    public bool EnableLocalFallback { get; set; } = true;
    public string OpenRouterChatModel { get; set; } = "cohere/north-mini-code:free";
    public string OpenRouterVisionModel { get; set; } = "openrouter/free";
    public string ChatModel { get; set; } = "qwen3:8b";
    public bool EnableChatThinking { get; set; } = false;
    public int ChatContextLength { get; set; } = 8192;
    public string ChatTimeZone { get; set; } = "Asia/Beirut";
    // Empty by default because the user's existing qwen3:8b is a TEXT model.
    public string VisionModel { get; set; } = "";
    public string EmbeddingModel { get; set; } = "nomic-embed-text:latest";
    public int EmbeddingDimensions { get; set; } = 768;
    // Cosine similarity threshold (0..1). Calibrate with the installed embedding model.
    public double KnowledgeMinScore { get; set; } = 0.30;
    public int KnowledgeCandidateCount { get; set; } = 10;
    public RerankingSettings Reranking { get; set; } = new();
    public GuardrailSettings Guardrails { get; set; } = new();
    public string QdrantUrl { get; set; } = "http://127.0.0.1:6333";
    public string Collection { get; set; } = "developer_knowledge";
    public string[] AllowedProjectRoots { get; set; } = [];
}

public sealed class RerankingSettings
{
    // Reuse the installed local chat model. No hosted API or additional download.
    public string Provider { get; set; } = "ollama";
    public string Model { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 90;
}

public sealed class GuardrailSettings
{
    public int MaxToolCalls { get; set; } = 12;
    public int MaxRepeatedToolCalls { get; set; } = 3;
    public int MaxModelCalls { get; set; } = 8;
    public int RunTimeoutSeconds { get; set; } = 180;
    public int MaxOutputTokens { get; set; } = 4096;
    public int MaxOutputCharacters { get; set; } = 40000;
}

public sealed record KnowledgeHit(string Source, string Text, double Score,
    string Id = "", string DocumentId = "", string Revision = "", int Chunk = 0,
    int VectorRank = 0, double? RerankScore = null, int Rank = 0);
public sealed record ProposedAction(Guid Id, string Kind, string Summary, string Payload, DateTimeOffset CreatedAt);
public sealed record HandoffDraft(string Title, string Content);
