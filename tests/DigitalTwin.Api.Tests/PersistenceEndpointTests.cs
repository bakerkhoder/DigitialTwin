using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalTwin.Api;
using Microsoft.Extensions.AI;
using Xunit;
using static DigitalTwin.Api.Tests.GuardrailTestHarness;
using ChatHost = DigitalTwin.Api.Tests.GuardrailChatEndpointTests.ChatHost;

namespace DigitalTwin.Api.Tests;

public sealed class PersistenceEndpointTests : IDisposable
{
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "persistence-http-tests", Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "state.db");
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [Fact]
    public async Task HostedAgentRestoresContextAndVisibleHistoryAcrossHostRestarts()
    {
        string id;
        await using (var first = await ChatHost.Start((_, _, _) => Task.FromResult(Text("Your project is Cedar.")), databasePath: DatabasePath))
        {
            using var response = await first.Http.PostAsJsonAsync("/api/agent/ask", new { message = "My project is Cedar." });
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            id = result.GetProperty("sessionId").GetString()!;
        }
        await using var restarted = await ChatHost.Start((_, messages, _) =>
        {
            Assert.Contains(messages, message => message.Role == ChatRole.Assistant && message.Text.Contains("Cedar"));
            return Task.FromResult(Text("Cedar, as discussed earlier."));
        }, databasePath: DatabasePath);
        var listing = await restarted.Http.GetFromJsonAsync<ConversationSummary[]>("/api/agent/sessions");
        Assert.Equal(id, Assert.Single(listing!).Id);
        var detail = await restarted.Http.GetFromJsonAsync<ConversationDetail>($"/api/agent/sessions/{id}");
        Assert.Equal(2, detail!.Messages.Length);
        var raw = await restarted.Http.GetStringAsync($"/api/agent/sessions/{id}");
        Assert.DoesNotContain("agent_state", raw);
        using var continued = await restarted.Http.PostAsJsonAsync("/api/agent/ask", new { sessionId = id, message = "What is my project?" });
        continued.EnsureSuccessStatusCode();
        Assert.Contains("Cedar", await continued.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MemoryApiSupportsRememberEditForgetAndValidatesUserInput()
    {
        await using var host = await ChatHost.Start((_, _, _) => Task.FromResult(Text("Done.")), databasePath: DatabasePath);
        using var rejected = await host.Http.PostAsJsonAsync("/api/memories", new { content = "sk-examplecredential12345678901234567890" });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var invalidKind = await host.Http.PostAsJsonAsync("/api/memories", new { content = "Celsius", kind = "system" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidKind.StatusCode);
        using var added = await host.Http.PostAsJsonAsync("/api/memories", new { content = "Use Celsius.", kind = "preference" });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var saved = (await added.Content.ReadFromJsonAsync<SavedMemory>())!;
        using var edited = await host.Http.PutAsJsonAsync($"/api/memories/{saved.Id}", new { content = "Use Fahrenheit.", kind = "preference" });
        edited.EnsureSuccessStatusCode();
        Assert.Equal("Use Fahrenheit.", Assert.Single((await host.Http.GetFromJsonAsync<SavedMemory[]>("/api/memories"))!).Content);
        using var removed = await host.Http.DeleteAsync($"/api/memories/{saved.Id}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Empty((await host.Http.GetFromJsonAsync<SavedMemory[]>("/api/memories"))!);
        using var missing = await host.Http.PutAsJsonAsync($"/api/memories/{saved.Id}", new { content = "Must not be recreated." });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task ClearAllDeletesConversationsWhileKeepingExplicitMemories()
    {
        await using var host = await ChatHost.Start((_, _, _) => Task.FromResult(Text("Response.")), databasePath: DatabasePath);
        using var memory = await host.Http.PostAsJsonAsync("/api/memories", new { content = "Prefer concise answers." });
        memory.EnsureSuccessStatusCode();
        for (var i = 0; i < 2; i++)
        {
            using var turn = await host.Http.PostAsJsonAsync("/api/agent/ask", new { message = $"Question {i}" });
            turn.EnsureSuccessStatusCode();
        }
        Assert.Equal(2, (await host.Http.GetFromJsonAsync<ConversationSummary[]>("/api/agent/sessions"))!.Length);
        using var deleted = await host.Http.DeleteAsync("/api/agent/sessions");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty((await host.Http.GetFromJsonAsync<ConversationSummary[]>("/api/agent/sessions"))!);
        Assert.Single((await host.Http.GetFromJsonAsync<SavedMemory[]>("/api/memories"))!);
    }

    [Fact]
    public async Task DeleteWaitsForAnActiveRunAndPreventsItsContextFromReturning()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await ChatHost.Start(async (_, _, _) =>
        {
            started.SetResult(); await release.Task;
            return Text("Completed response.");
        }, databasePath: DatabasePath);
        var id = Guid.NewGuid().ToString();
        var running = host.Http.PostAsJsonAsync("/api/agent/ask", new { sessionId = id, message = "An active request." });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var deleting = host.Http.DeleteAsync($"/api/agent/sessions/{id}");
        release.SetResult();
        using var completed = await running;
        using var deleted = await deleting;
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var missing = await host.Http.GetAsync($"/api/agent/sessions/{id}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var stale = await host.Http.PostAsJsonAsync("/api/agent/ask", new { sessionId = id, message = "Late stale request." });
        Assert.Equal(HttpStatusCode.Gone, stale.StatusCode);
    }
}
