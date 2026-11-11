namespace DigitalTwin.Api;

public static class RetrievalRegistrationExtensions
{
    public static IServiceCollection AddKnowledgeRetrieval(this IServiceCollection services, TwinSettings settings)
    {
        if (settings.KnowledgeCandidateCount is < 5 or > 10)
            throw new InvalidOperationException("Twin:KnowledgeCandidateCount must be between 5 and 10.");
        if (!double.IsFinite(settings.KnowledgeMinScore) || settings.KnowledgeMinScore is < 0 or > 1)
            throw new InvalidOperationException("Twin:KnowledgeMinScore must be between 0 and 1.");
        if (settings.Reranking.TimeoutSeconds is < 1 or > 180)
            throw new InvalidOperationException("Twin:Reranking:TimeoutSeconds must be between 1 and 180.");
        switch (settings.Reranking.Provider.Trim().ToLowerInvariant())
        {
            case "ollama":
                services.AddHttpClient<LlmReranker>(client => client.Timeout = Timeout.InfiniteTimeSpan);
                services.AddSingleton<IReranker>(sp => sp.GetRequiredService<LlmReranker>());
                break;
            case "none": services.AddSingleton<IReranker, NoOpReranker>(); break;
            default: throw new InvalidOperationException("Twin:Reranking:Provider must be ollama or none.");
        }
        services.AddSingleton<KnowledgeRetrievalService>();
        return services;
    }
}
