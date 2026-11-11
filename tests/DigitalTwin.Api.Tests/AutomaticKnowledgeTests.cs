using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalTwin.Api;
using Microsoft.Extensions.AI;
using Xunit;
using static DigitalTwin.Api.Tests.GuardrailTestHarness;
using ChatHost = DigitalTwin.Api.Tests.GuardrailChatEndpointTests.ChatHost;

namespace DigitalTwin.Api.Tests;

public sealed class AutomaticKnowledgeTests : IDisposable
{
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "automatic-knowledge-tests", Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [Fact]
    public void SourcePresentationUsesOnlyRetrievedNamesAndDoesNotDuplicateExistingCitations()
    {
        var cited = KnowledgeAnswerPresentation.EnsureSources("Your name is Baker.", ["profile.txt"]);
        Assert.Contains("profile.txt", cited);
        Assert.Equal(cited, KnowledgeAnswerPresentation.EnsureSources(cited, ["profile.txt"]));
        Assert.Equal("A conversation answer.", KnowledgeAnswerPresentation.EnsureSources("A conversation answer.", []));
    }

    [Fact]
    public void FilenameBackticksAreSafelyWrappedAsText()
    {
        var cited = KnowledgeAnswerPresentation.EnsureSources("A document answer.", ["my`profile.txt"]);
        Assert.Contains("`` my`profile.txt ``", cited);
    }

    [Fact]
    public async Task PrematurePersonalInformationRefusalGetsOneReadOnlyRecoveryThroughMaf()
    {
        var searches = 0;
        var knowledge = Tool((string query) =>
        {
            searches++;
            GuardrailContext.Required.RecordKnowledge(["profile.txt"]);
            return "The user's name is Baker.";
        }, "SearchKnowledge");
        var write = Tool((Func<string>)(() => throw new InvalidOperationException("Recovery must not offer a write tool.")), "ProposeHandoff");
        using var harness = new GuardrailTestHarness((turn, messages, _) =>
        {
            if (turn == 1) return Task.FromResult(Text("I don't have access to your personal information unless you provide it."));
            if (turn == 2)
            {
                Assert.Contains(messages, message => message.Role == ChatRole.System && message.Text.Contains("SearchKnowledge tool is available"));
                return Task.FromResult(Call(turn, "SearchKnowledge", new() { ["query"] = "user personal profile name" }));
            }
            return Task.FromResult(Text("Your name is Baker."));
        }, [knowledge, write]);
        var answer = await harness.Run("What is my name?");
        Assert.Contains("Baker", answer);
        Assert.Contains("profile.txt", answer);
        Assert.Equal(1, searches);
        Assert.Equal(3, harness.Model.Calls);
    }

    [Fact]
    public async Task RepeatedRefusalDoesNotCreateARecoveryLoop()
    {
        var knowledge = Tool(() => "No facts", "SearchKnowledge");
        using var harness = new GuardrailTestHarness((_, _, _) => Task.FromResult(Text("I don't know your name.")), [knowledge]);
        await harness.Run("What is my name?");
        Assert.Equal(2, harness.Model.Calls);
        Assert.Equal(1, harness.Model.LastToolCount);
    }

    [Fact]
    public async Task CompletedEmptyKnowledgeSearchDoesNotTriggerAnotherRecovery()
    {
        var knowledge = Tool(() => { GuardrailContext.Required.RecordKnowledge([]); return "No relevant passages."; }, "SearchKnowledge");
        using var harness = new GuardrailTestHarness((turn, _, _) => Task.FromResult(turn == 1
            ? Call(turn, "SearchKnowledge") : Text("I don't know your name from the uploaded notes.")), [knowledge]);
        await harness.Run("What is my name?");
        Assert.Equal(2, harness.Model.Calls);
    }

    [Fact]
    public async Task HostedAgentRecoveryOffersOnlyKnowledgeAndPreservesTheOriginalQuestion()
    {
        var knowledge = Tool((string query) =>
        {
            GuardrailContext.Required.RecordKnowledge(["profile.txt"]);
            return "User profile: the user's name is Baker.";
        }, "SearchKnowledge");
        ChatHost? host = null;
        await using var running = await ChatHost.Start((turn, messages, _) =>
        {
            if (turn == 1) return Task.FromResult(Text("I don't have access to your personal information unless you provide it."));
            if (turn == 2)
            {
                Assert.Equal(1, host!.Model.LastToolCount);
                Assert.Contains(messages, message => message.Role == ChatRole.User && message.Text.Contains("what is my name?"));
                return Task.FromResult(Call(turn, "SearchKnowledge", new() { ["query"] = "user personal profile name" }));
            }
            return Task.FromResult(Text("Your name is Baker."));
        }, [knowledge, Tool(() => "Unrequested web result", "SearchWeb"), Tool(() => "Unrequested write", "ProposeHandoff")]);
        host = running;
        using var response = await running.Http.PostAsJsonAsync("/api/agent/ask", new { message = "what is my name?", useKnowledge = true });
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Baker", result.GetProperty("reply").GetString());
        Assert.Contains("profile.txt", result.GetProperty("reply").GetString());
        Assert.Equal(3, running.Model.Calls);
    }

    [Fact]
    public async Task NormalAnswerWithKnowledgeAvailableDoesNotAddARecoveryCall()
    {
        var knowledge = Tool(() => "Unrequested document", "SearchKnowledge");
        using var harness = new GuardrailTestHarness((_, _, _) => Task.FromResult(Text("Hello! How can I help?")), [knowledge]);
        Assert.Contains("Hello!", await harness.Run("Hi, how are you?"));
        Assert.Equal(1, harness.Model.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NameGivenInConversationIsRetainedRegardlessOfLegacyKnowledgeFlag(bool legacyFlag)
    {
        await using var host = await ChatHost.Start((turn, messages, _) =>
        {
            Assert.False(GuardrailContext.Required.RequireKnowledge);
            Assert.DoesNotContain(messages, message => message.Text.Contains("SearchKnowledge is required"));
            if (turn == 2)
                Assert.Contains(messages, message => message.Role == ChatRole.User && message.Text.Contains("my name is Baker"));
            return Task.FromResult(Text(turn == 1 ? "Got it, Baker. I'll use your name in this conversation." : "Your name is Baker."));
        });
        using var introduction = await host.Http.PostAsJsonAsync("/api/agent/ask", new { message = "I am telling you my name is Baker.", useKnowledge = legacyFlag });
        introduction.EnsureSuccessStatusCode();
        var first = await introduction.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Baker", first.GetProperty("reply").GetString());
        using var followUp = await host.Http.PostAsJsonAsync("/api/agent/ask", new
        {
            sessionId = first.GetProperty("sessionId").GetString(), message = "What is my name?", useKnowledge = legacyFlag
        });
        followUp.EnsureSuccessStatusCode();
        var result = await followUp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Baker", result.GetProperty("reply").GetString());
        Assert.DoesNotContain("knowledge base was not searched", result.GetProperty("reply").GetString());
    }

    [Fact]
    public async Task GreetingDoesNotRequireADocumentSearchEvenFromAnOlderClient()
    {
        await using var host = await ChatHost.Start((_, _, _) => Task.FromResult(Text("Hello! How can I help?")));
        using var greeting = await host.Http.PostAsJsonAsync("/api/agent/ask", new { message = "Hi, how are you?", useKnowledge = true });
        greeting.EnsureSuccessStatusCode();
        Assert.Contains("Hello!", await greeting.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AttachmentWithLegacyFlagDoesNotForceAnUnrelatedKnowledgeSearch()
    {
        await using var host = await ChatHost.Start((_, messages, _) =>
        {
            Assert.False(GuardrailContext.Required.RequireKnowledge);
            Assert.DoesNotContain(messages, message => message.Text.Contains("SearchKnowledge is required"));
            return Task.FromResult(Text("Got it, Baker. The attached note says the meeting starts at 9."));
        });
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("My name is Baker. Summarize this note."), "message");
        form.Add(new StringContent("true"), "useKnowledge");
        form.Add(new StringContent("The meeting starts at 9."), "files", "meeting.txt");
        using var response = await host.Http.PostAsync("/api/chat/attachments", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Baker", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EmptySavedMemoryCanFallBackToAnUploadedProfileWithoutAnOptInFlag()
    {
        using var backend = new KnowledgeBackend();
        backend.Seed("owner-profile.txt", "User personal profile. My name is Baker. I work as a .NET developer.");
        var database = new LocalStateDatabase(Path.Combine(_directory, "state.db"));
        var memoryTool = AIFunctionFactory.Create(new MemoryTools(new MemoryStore(database)).SearchMemories);
        var knowledgeTool = AIFunctionFactory.Create(backend.Retrieval(new NoOpReranker()).SearchKnowledge);
        await using var host = await ChatHost.Start((turn, messages, _) =>
        {
            if (turn == 1) return Task.FromResult(Call(turn, "SearchMemories", new() { ["query"] = "name" }));
            if (turn == 2)
            {
                Assert.Contains(messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>(),
                    result => result.Result?.ToString()?.Contains("check uploaded profiles") == true);
                return Task.FromResult(Call(turn, "SearchKnowledge", new() { ["query"] = "user personal profile name background" }));
            }
            Assert.Equal(1, GuardrailContext.Required.KnowledgeSearches);
            Assert.Contains(messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>(),
                result => result.Result?.ToString()?.Contains("My name is Baker") == true);
            return Task.FromResult(Text("Your name is Baker. [owner-profile.txt]"));
        }, [memoryTool, knowledgeTool]);
        using var response = await host.Http.PostAsJsonAsync("/api/agent/ask", new { message = "What is my name?" });
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Baker", result.GetProperty("reply").GetString());
        Assert.Contains("owner-profile.txt", result.GetProperty("reply").GetString());
        Assert.Equal(1, backend.Embeddings);
    }
}
