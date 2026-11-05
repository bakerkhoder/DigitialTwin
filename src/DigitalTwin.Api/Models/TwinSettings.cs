using System.Text.Json.Serialization;
namespace DigitalTwin.Api;

public sealed class TwinSettings
{
    [JsonIgnore] public string? OpenRouterApiKey { get; set; }
    public string Workspace { get; set; } = "./workspace";
    public string OllamaUrl { get; set; } = "http://127.0.0.1:11434";
    public string Provider { get; set; } = "auto";
    public bool EnableLocalFallback { get; set; } = true;
    public string OpenRouterChatModel { get; set; } = "cohere/north-mini-code:free";
    public string OpenRouterVisionModel { get; set; } = "openrouter/free";
    public string ChatModel { get; set; } = "qwen3:8b";
    // Empty by default because the user's existing qwen3:8b is a TEXT model.
    public string VisionModel { get; set; } = "";
    public string EmbeddingModel { get; set; } = "nomic-embed-text:latest";
    public int EmbeddingDimensions { get; set; } = 768;
    // Cosine similarity threshold (0..1). Calibrate with the installed embedding model.
    public double KnowledgeMinScore { get; set; } = 0.30;
    public string QdrantUrl { get; set; } = "http://127.0.0.1:6333";
    public string Collection { get; set; } = "developer_knowledge";
    public string[] AllowedProjectRoots { get; set; } = [];
}

public sealed record KnowledgeHit(string Source, string Text, double Score);
public sealed record ProposedAction(Guid Id, string Kind, string Summary, string Payload, DateTimeOffset CreatedAt);
public sealed record HandoffDraft(string Title, string Content);
