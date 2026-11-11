namespace DigitalTwin.Api;

public interface IReranker
{
    string Name { get; }
    Task<IReadOnlyList<KnowledgeHit>> RerankAsync(
        string question, IReadOnlyList<KnowledgeHit> candidates, CancellationToken ct);
}

public sealed class NoOpReranker : IReranker
{
    public string Name => "none";
    public Task<IReadOnlyList<KnowledgeHit>> RerankAsync(
        string question, IReadOnlyList<KnowledgeHit> candidates, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(candidates);
    }
}
