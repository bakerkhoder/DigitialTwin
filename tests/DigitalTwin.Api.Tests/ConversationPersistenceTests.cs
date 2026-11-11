using DigitalTwin.Api;
using Microsoft.Extensions.AI;
using Xunit;
using static DigitalTwin.Api.Tests.GuardrailTestHarness;

namespace DigitalTwin.Api.Tests;

public sealed class ConversationPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "state-tests", Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "state.db");
    private ConversationStore OpenStore() => new(new LocalStateDatabase(DatabasePath));
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [Fact]
    public async Task NativeMafSessionAndToolHistorySurviveNewStoreAndAgentInstances()
    {
        var id = Guid.NewGuid().ToString();
        var source = Tool(() => "Evidence: project codename Cedar.", "ReadFile");
        using (var first = new GuardrailTestHarness((turn, _, _) => Task.FromResult(turn == 1
            ? Call(turn, "ReadFile") : Text("The project is Cedar.")), [source], sessions: new(OpenStore()), sessionId: id))
            await first.Run("Remember the project from this file.", privateContext: true);

        var reopened = OpenStore();
        using var second = new GuardrailTestHarness((_, messages, _) =>
        {
            Assert.Contains(messages, message => message.Role == ChatRole.Assistant && message.Text.Contains("Cedar"));
            Assert.Contains(messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>(),
                result => result.Result?.ToString()?.Contains("Cedar") == true);
            return Task.FromResult(Text("Cedar, from the earlier file."));
        }, [source], sessions: new(reopened), sessionId: id);
        Assert.True(second.Sessions.HasPrivateContext(id));
        await second.Run("What was its name?");
        var transcript = Assert.IsType<ConversationDetail>(reopened.Get(id));
        Assert.Equal(4, transcript.Messages.Length);
        Assert.Equal(["user", "assistant", "user", "assistant"], transcript.Messages.Select(message => message.Role));
        Assert.Equal("What was its name?", transcript.Messages[2].Text);
    }

    [Fact]
    public async Task DeleteRemovesTranscriptAndPreventsStaleImportsOrRequestsFromRestoringIt()
    {
        var store = OpenStore();
        using var first = new GuardrailTestHarness((_, _, _) => Task.FromResult(Text("Acknowledged.")), sessions: new(store));
        await first.Run("My project is Cedar.");
        var history = store.Get(first.SessionId)!;
        await first.Sessions.Remove(first.SessionId, default);
        var reopened = OpenStore();
        Assert.Null(reopened.Get(first.SessionId));
        Assert.Empty(reopened.List());
        reopened.Import([new(first.SessionId, history.Title, history.Created, history.Messages)]);
        Assert.Null(reopened.Get(first.SessionId));
        using var stale = new GuardrailTestHarness((_, _, _) => Task.FromResult(Text("Must not run.")), sessions: new(reopened), sessionId: first.SessionId);
        await Assert.ThrowsAsync<ConversationDeletedException>(() => stale.Run("Continue."));
        Assert.Equal(0, stale.Model.Calls);
    }

    [Fact]
    public async Task KnowledgeInvalidationCannotBeUndoneByAnInFlightSaveOrRestart()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = OpenStore();
        using var first = new GuardrailTestHarness(async (_, _, _) =>
        {
            started.SetResult();
            await release.Task;
            return Text("Old retrieved evidence.");
        }, sessions: new(store));
        var running = first.Run("Read the old document.");
        await started.Task;
        first.Sessions.InvalidateAllAfterKnowledgeChange();
        release.SetResult();
        await running;
        using var resumed = new GuardrailTestHarness((_, messages, _) =>
        {
            Assert.DoesNotContain(messages, message => message.Text.Contains("Old retrieved evidence"));
            return Task.FromResult(Text("Fresh context."));
        }, sessions: new(OpenStore()), sessionId: first.SessionId);
        await resumed.Run("Continue.");
        Assert.Equal(4, OpenStore().Get(first.SessionId)!.Messages.Length);
    }

    [Fact]
    public async Task FailedTurnDoesNotPersistPartialModelHistory()
    {
        var store = OpenStore();
        using var first = new GuardrailTestHarness((turn, _, _) => turn == 1
            ? Task.FromResult(Text("A successful response.")) : throw new HttpRequestException("Simulated provider failure."), sessions: new(store));
        await first.Run("A successful question.");
        await Assert.ThrowsAsync<HttpRequestException>(() => first.Run("Failed question."));
        Assert.Equal(2, store.Get(first.SessionId)!.Messages.Length);
        using var resumed = new GuardrailTestHarness((_, messages, _) =>
        {
            Assert.DoesNotContain(messages, message => message.Text.Contains("Failed question"));
            Assert.Contains(messages, message => message.Text.Contains("A successful response"));
            return Task.FromResult(Text("Fresh response."));
        }, sessions: new(OpenStore()), sessionId: first.SessionId);
        await resumed.Run("A new question.");
    }

    [Fact]
    public void BrowserImportIsIdempotentAndPreservesUnicodeAttachmentsAndErrorMarkers()
    {
        var id = Guid.NewGuid().ToString();
        var store = OpenStore();
        ConversationImport item = new(id, "Previous conversation", 123,
            [new("user", "مرحبا", ["document.pdf"]), new("assistant", "Previous error", Error: true)]);
        store.Import([item]);
        store.Import([item]);
        var restored = OpenStore().Get(id)!;
        Assert.Equal(2, restored.Messages.Length);
        Assert.Equal("مرحبا", restored.Messages[0].Text);
        Assert.Equal("document.pdf", Assert.Single(restored.Messages[0].Attachments!));
        Assert.True(restored.Messages[1].Error);
        Assert.Equal(123, restored.Created);
    }
}
