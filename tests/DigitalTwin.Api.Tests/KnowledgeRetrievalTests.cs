using System.Diagnostics;
using DigitalTwin.Api;
using Microsoft.Extensions.AI;
using Xunit;

namespace DigitalTwin.Api.Tests;

public sealed class KnowledgeRetrievalTests
{
    [Fact]
    public async Task RetrievesTenAndSelectsFiveByRelevanceKeepingVectorScores()
    {
        using var backend = new KnowledgeBackend();
        for (int i = 1; i <= 12; i++) backend.Seed($"passage-{i}.txt", $"Passage {i}", 1 - i * .02);
        backend.RerankResponse = "{\"rankings\":[" + string.Join(',', Enumerable.Range(1, 10).Select(i => $"{{\"id\":{i},\"score\":{i * 10}}}")) + "]}";

        var result = await backend.Retrieval().SearchAsync("Which passage answers the question?", default);

        Assert.Equal(10, backend.LastSearch!["limit"]!.GetValue<int>());
        Assert.Equal(10, result.Candidates.Count);
        Assert.Equal(5, result.Hits.Count);
        Assert.Equal("passage-10.txt", result.Hits[0].Source);
        Assert.Equal(10, result.Hits[0].VectorRank);
        Assert.Equal(1, result.Hits[0].Rank);
        Assert.Equal(100, result.Hits[0].RerankScore);
        Assert.Equal(.8, result.Hits[0].Score, precision: 8);
        Assert.False(result.FallbackUsed);
        Assert.False(backend.LastRerank!["think"]!.GetValue<bool>());
        Assert.Equal("object", backend.LastRerank["format"]!["type"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"rankings\":[]}")]
    [InlineData("{\"rankings\":[{\"id\":1,\"score\":90},{\"id\":1,\"score\":80}]}")]
    [InlineData("{\"rankings\":[{\"id\":1,\"score\":90},{\"id\":77,\"score\":80}]}")]
    [InlineData("{\"rankings\":[{\"id\":1,\"score\":101},{\"id\":2,\"score\":80}]}")]
    public async Task MalformedScoringFallsBackWithoutLosingEvidence(string response)
    {
        using var backend = new KnowledgeBackend { RerankResponse = response };
        backend.Seed("first.txt", "First passage", .9);
        backend.Seed("second.txt", "Second passage", .8);
        var result = await backend.Retrieval().SearchAsync("question", default);
        Assert.True(result.FallbackUsed);
        Assert.NotNull(result.Warning);
        Assert.Equal(new[] { "first.txt", "second.txt" }, result.Hits.Select(h => h.Source));
        Assert.All(result.Hits, h => Assert.Null(h.RerankScore));
    }

    [Fact]
    public async Task ProviderFailureFallsBack()
    {
        using var backend = new KnowledgeBackend { FailRerank = true };
        backend.Seed("source.txt", "Evidence");
        var result = await backend.Retrieval().SearchAsync("question", default);
        Assert.True(result.FallbackUsed);
        Assert.Single(result.Hits);
    }

    [Fact]
    public async Task RerankingTimeoutFallsBack()
    {
        using var backend = new KnowledgeBackend { WaitForRerankCancellation = true };
        backend.Settings.Reranking.TimeoutSeconds = 1;
        backend.Seed("source.txt", "Evidence");
        var result = await backend.Retrieval().SearchAsync("question", default);
        Assert.True(result.FallbackUsed);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var backend = new KnowledgeBackend { WaitForRerankCancellation = true };
        backend.Seed("source.txt", "Evidence");
        using var cancellation = new CancellationTokenSource();
        var search = backend.Retrieval().SearchAsync("question", cancellation.Token);
        await backend.RerankStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
    }

    [Fact]
    public async Task EmptyOrBelowThresholdResultsNeverCallReranker()
    {
        using var backend = new KnowledgeBackend();
        backend.Seed("irrelevant.txt", "Unrelated", .1);
        var result = await backend.Retrieval().SearchAsync("question", default);
        Assert.Empty(result.Hits);
        Assert.Equal(0, backend.RerankCalls);
    }

    [Fact]
    public async Task DisabledRerankingKeepsVectorOrderAndHasNoFallbackWarning()
    {
        using var backend = new KnowledgeBackend();
        backend.Seed("source.txt", "Evidence");
        var result = await backend.Retrieval(new NoOpReranker()).SearchAsync("question", default, 1);
        Assert.Single(result.Hits);
        Assert.False(result.FallbackUsed);
        Assert.Null(result.Warning);
        Assert.Equal(0, backend.RerankCalls);
    }

    [Fact]
    public async Task MafToolKeepsItsNameAndDoesNotExposeCancellationTokenToModel()
    {
        using var backend = new KnowledgeBackend();
        backend.Seed("policy.txt", "Approval is required.");
        var function = AIFunctionFactory.Create(backend.Retrieval(new NoOpReranker()).SearchKnowledge);
        Assert.Equal("SearchKnowledge", function.Name);
        var properties = function.JsonSchema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("query", out _));
        Assert.True(properties.TryGetProperty("maxResults", out _));
        Assert.False(properties.TryGetProperty("ct", out _));
        var result = await function.InvokeAsync(new AIFunctionArguments { ["query"] = "approval", ["maxResults"] = 1 });
        Assert.Contains("policy.txt", result?.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task InvalidQueryIsRejectedBeforeEmbedding(string query)
    {
        using var backend = new KnowledgeBackend();
        await Assert.ThrowsAsync<ArgumentException>(() => backend.Retrieval().SearchAsync(query, default));
        Assert.Equal(0, backend.Embeddings);
    }

    [Fact]
    public async Task TracesSeparateRetrievalAndRerankingWithoutCapturingEvidence()
    {
        var activities = new List<Activity>();
        using var root = new Activity("retrieval-test").Start();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DeveloperTwinTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => { if (activity.TraceId == root.TraceId) { lock (activities) activities.Add(activity); } }
        };
        ActivitySource.AddActivityListener(listener);
        using var backend = new KnowledgeBackend();
        backend.Seed("private-file.txt", "Private contents");
        var result = await backend.Retrieval(new NoOpReranker()).SearchAsync("private question", default);
        var retrieve = Assert.Single(activities, a => a.OperationName == "knowledge.retrieve");
        var rerank = Assert.Single(activities, a => a.OperationName == "knowledge.rerank");
        Assert.Equal(retrieve.ParentSpanId, rerank.ParentSpanId);
        Assert.DoesNotContain(activities.SelectMany(a => a.Tags), tag => tag.Value?.Contains("private", StringComparison.OrdinalIgnoreCase) == true);
    }
}
