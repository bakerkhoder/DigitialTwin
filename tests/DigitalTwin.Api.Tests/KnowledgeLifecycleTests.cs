using DigitalTwin.Api;
using Xunit;

namespace DigitalTwin.Api.Tests;

public sealed class KnowledgeLifecycleTests
{
    [Fact]
    public async Task RepeatedRevisionSkipsEmbeddingAndPreservesTimestamp()
    {
        using var backend = new KnowledgeBackend();
        var first = await backend.Store.Ingest("policy.txt", "Project access requires approval.", default);
        var before = Assert.Single(await backend.Store.ListDocuments(default));
        backend.FailEmbedding = true; // Unchanged uploads work even with the model offline.
        var repeated = await backend.Store.Ingest("policy.txt", "Project access requires approval.", default);
        Assert.False(first.Unchanged);
        Assert.True(repeated.Unchanged);
        Assert.Equal(1, backend.Embeddings);
        Assert.Equal(before, Assert.Single(await backend.Store.ListDocuments(default)));
    }

    [Fact]
    public async Task DeprecateAndRestoreFilterBeforeSelectingCandidates()
    {
        using var backend = new KnowledgeBackend();
        await backend.Store.Ingest("policy.txt", "Project access requires approval.", default);
        var document = Assert.Single(await backend.Store.ListDocuments(default));
        Assert.True(await backend.Store.SetDocumentStatus(document.Id, "deprecated", default));
        var deprecated = await backend.Retrieval(new NoOpReranker()).SearchAsync("access", default);
        Assert.Empty(deprecated.Candidates);
        Assert.Equal("deprecated", Assert.Single(await backend.Store.ListDocuments(default)).Status);
        Assert.Single(backend.Points);

        Assert.True(await backend.Store.SetDocumentStatus(document.Id, "active", default));
        Assert.Single((await backend.Retrieval(new NoOpReranker()).SearchAsync("access", default)).Hits);
        Assert.Equal("active", Assert.Single(await backend.Store.ListDocuments(default)).Status);
    }

    [Fact]
    public async Task DeprecatedHighScoresDoNotCrowdOutActiveCandidatesAndLegacyStillWorks()
    {
        using var backend = new KnowledgeBackend();
        for (int i = 0; i < 15; i++) backend.Seed($"old-{i}.txt", "Deprecated", .99, "deprecated");
        backend.Seed("legacy.txt", "Legacy evidence", .8, status: null);
        backend.Seed("active.txt", "Current evidence", .7);
        var result = await backend.Retrieval(new NoOpReranker()).SearchAsync("question", default);
        Assert.Equal(new[] { "legacy.txt", "active.txt" }, result.Hits.Select(h => h.Source));
        Assert.NotNull(backend.LastSearch!["filter"]!["should"]);
    }

    [Fact]
    public async Task ReuploadDoesNotReactivateDeprecatedDocument()
    {
        using var backend = new KnowledgeBackend();
        await backend.Store.Ingest("policy.txt", "Initial policy", default);
        var id = Assert.Single(await backend.Store.ListDocuments(default)).Id;
        await backend.Store.SetDocumentStatus(id, "deprecated", default);
        var unchanged = await backend.Store.Ingest("policy.txt", "Initial policy", default);
        var replacement = await backend.Store.Ingest("policy.txt", "Revised policy", default);
        Assert.True(unchanged.Unchanged);
        Assert.Equal("deprecated", replacement.Status);
        Assert.Equal("deprecated", Assert.Single(await backend.Store.ListDocuments(default)).Status);
        Assert.Empty(await backend.Store.SearchCandidates("policy", default));
    }

    [Fact]
    public async Task ShorterReplacementRemovesObsoleteChunks()
    {
        using var backend = new KnowledgeBackend();
        await backend.Store.Ingest("policy.txt", new string('a', 4000), default);
        var previousIds = backend.Points.Keys.ToArray();
        var replacement = await backend.Store.Ingest("policy.txt", "Short replacement", default);
        Assert.Equal(1, replacement.Chunks);
        Assert.Single(backend.Points);
        Assert.DoesNotContain(backend.Points.Keys, id => previousIds.Contains(id));
    }

    [Fact]
    public async Task FailedEmbeddingLeavesPreviousRevisionIntact()
    {
        using var backend = new KnowledgeBackend();
        await backend.Store.Ingest("policy.txt", "Working policy", default);
        var before = Assert.Single(await backend.Store.ListDocuments(default));
        backend.FailEmbedding = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => backend.Store.Ingest("policy.txt", "New policy", default));
        Assert.Equal(before, Assert.Single(await backend.Store.ListDocuments(default)));
    }

    [Fact]
    public async Task ChangedEmbeddingModelReindexesSameText()
    {
        using var backend = new KnowledgeBackend();
        await backend.Store.Ingest("policy.txt", "Policy", default);
        backend.Settings.EmbeddingModel = "another-installed-model";
        var replacement = await backend.Store.Ingest("policy.txt", "Policy", default);
        Assert.False(replacement.Unchanged);
        Assert.Equal(2, backend.Embeddings);
        Assert.Single(backend.Points);
    }

    [Fact]
    public async Task HardDeleteRemovesEveryPageOfLegacyPoints()
    {
        using var backend = new KnowledgeBackend();
        for (int i = 0; i < 205; i++) backend.Seed("legacy.txt", $"Chunk {i}", status: null);
        backend.Seed("keep.txt", "Keep this source");
        var document = (await backend.Store.ListDocuments(default)).Single(d => d.Source == "legacy.txt");
        Assert.Equal(205, document.Chunks);
        Assert.True(await backend.Store.DeleteDocument(document.Id, default));
        Assert.Equal("keep.txt", Assert.Single(await backend.Store.ListDocuments(default)).Source);
        Assert.Single(backend.Points);
    }

    [Fact]
    public async Task ConcurrentIdenticalUploadsEmbedOnlyOnce()
    {
        using var backend = new KnowledgeBackend();
        var results = await Task.WhenAll(
            backend.Store.Ingest("policy.txt", "Policy", default),
            backend.Store.Ingest("policy.txt", "Policy", default));
        Assert.Single(results, r => r.Unchanged);
        Assert.Equal(1, backend.Embeddings);
        Assert.Single(backend.Points);
    }

    [Fact]
    public async Task OversizedDocumentIsRejectedRatherThanSilentlyTruncated()
    {
        using var backend = new KnowledgeBackend();
        await Assert.ThrowsAsync<ArgumentException>(() => backend.Store.Ingest("large.txt", new string('a', TextExtractor.MaxCharacters + 1), default));
        Assert.Empty(backend.Points);
        Assert.Equal(0, backend.Embeddings);
    }
}
