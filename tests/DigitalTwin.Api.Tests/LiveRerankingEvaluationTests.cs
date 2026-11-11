using System.Text.Json;
using System.Diagnostics;
using DigitalTwin.Api;
using Xunit;
using Xunit.Abstractions;

namespace DigitalTwin.Api.Tests;

public sealed class LiveRerankingEvaluationTests(ITestOutputHelper output)
{
    [LiveOllamaFact]
    [Trait("Category", "Live")]
    public async Task ExpectedPassagesRankInTopThreeOnInstalledLocalModel()
    {
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "reranking-evaluation.json")));
        var passages = fixture.RootElement.GetProperty("passages").EnumerateArray().Select((p, i) => new KnowledgeHit(
            p.GetProperty("source").GetString()!, p.GetProperty("text").GetString()!, .9 - i * .02,
            Id: (i + 1).ToString(), VectorRank: i + 1)).ToArray();
        var settings = new TwinSettings();
        settings.Reranking.Model = Environment.GetEnvironmentVariable("TWIN_EVAL_MODEL") ?? settings.ChatModel;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
        var reranker = new LlmReranker(http, settings);
        output.WriteLine($"Local model: {settings.Reranking.Model}; {passages.Length} fixed candidates; top-3 acceptance.");
        foreach (var question in fixture.RootElement.GetProperty("questions").EnumerateArray())
        {
            var query = question.GetProperty("query").GetString()!;
            var expected = question.GetProperty("expectedSource").GetString()!;
            var timer = Stopwatch.StartNew();
            var ranked = await reranker.RerankAsync(query, passages, default);
            var rank = ranked.Select((p, i) => (p, rank: i + 1)).Single(x => x.p.Source == expected);
            output.WriteLine($"{expected}: vector #{rank.p.VectorRank} -> reranked #{rank.rank}; relevance {rank.p.RerankScore}/100; {timer.Elapsed.TotalSeconds:F1}s");
            Assert.True(rank.rank <= 3, $"Expected {expected} in the top 3 for: {query}; actual rank {rank.rank}.");
        }
    }
}

public sealed class LiveOllamaFactAttribute : FactAttribute
{
    public LiveOllamaFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TWIN_RUN_LIVE_RERANK_EVAL") != "1")
            Skip = "Opt in with TWIN_RUN_LIVE_RERANK_EVAL=1; requires the existing local Ollama model.";
    }
}
