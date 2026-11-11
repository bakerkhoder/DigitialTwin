using DigitalTwin.Api;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OllamaSharp;
using Xunit;
using Xunit.Abstractions;
using LiveKnowledgeBackend = DigitalTwin.Api.Tests.LiveKnowledgeIntegrationTests.LiveKnowledgeBackend;

namespace DigitalTwin.Api.Tests;

public sealed class LivePersonalKnowledgeTests(ITestOutputHelper output)
{
    [LiveQdrantFact]
    [Trait("Category", "LiveQdrant")]
    public async Task LocalAgentChoosesUploadedProfileAndRetainsAConversationCorrection()
    {
        await using var backend = await LiveKnowledgeBackend.CreateAsync(output);
        var settings = backend.Settings;
        const string source = "owner-profile.txt";
        await backend.Store.Ingest(source,
            "Personal profile of the person using DeveloperTwin. My name is Samir CedarTest. " +
            "I am a .NET software developer based in Lebanon. This is my own personal profile.", default);
        var directory = Path.Combine(AppContext.BaseDirectory, "live-personal-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var database = new LocalStateDatabase(Path.Combine(directory, "state.db"));
            var memories = new MemoryStore(database);
            var retrieval = new KnowledgeRetrievalService(backend.Store, new LlmReranker(backend.Http, settings), settings,
                NullLogger<KnowledgeRetrievalService>.Instance);
            using IChatClient client = new GuardrailChatClient(
                new OllamaChatClient(new OllamaApiClient(new Uri(settings.OllamaUrl), settings.ChatModel), settings), settings)
                .AsBuilder().UseAIContextProviders(new SavedMemoryContextProvider(memories)).Build();
            var agent = new ChatClientAgent(client, instructions: AgentInstructions.DeveloperTwin, tools:
            [
                AIFunctionFactory.Create(new MemoryTools(memories).SearchMemories),
                AIFunctionFactory.Create(retrieval.SearchKnowledge)
            ]);
            var sessions = new SessionRegistry(new(database));
            var runner = new GuardrailAgentRunner(settings, sessions, new InputGuardrailService(),
                new ToolAuthorizationService(), new OutputValidationService(settings));
            var id = Guid.NewGuid().ToString();
            Task<string> Ask(string message, string? sessionId = null) =>
                runner.RunAsync(agent, sessionId ?? id, message, message, false, false, false, default);

            var embeddingsBeforeQuestion = backend.EmbeddingCalls;
            var identity = await Ask("What is my name?");
            output.WriteLine("Uploaded profile answer: " + identity);
            Assert.Contains("Samir", identity, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(source, identity, StringComparison.OrdinalIgnoreCase);
            Assert.True(backend.EmbeddingCalls > embeddingsBeforeQuestion, "The model must actually search uploaded knowledge.");
            Assert.Empty(memories.List());

            var background = await Ask("Who am I and what do I do?", Guid.NewGuid().ToString());
            output.WriteLine("Uploaded background answer: " + background);
            Assert.Contains("Samir", background, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("developer", background, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(source, background, StringComparison.OrdinalIgnoreCase);

            var correction = await Ask("My name is Renamed Demo. Use this name in this conversation.");
            output.WriteLine("Conversation correction: " + correction);
            Assert.Contains("Renamed Demo", correction, StringComparison.OrdinalIgnoreCase);
            var followUp = await Ask("What is my name?");
            output.WriteLine("Same-session follow-up: " + followUp);
            Assert.Contains("Renamed Demo", followUp, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Samir CedarTest", followUp, StringComparison.OrdinalIgnoreCase);

            var beforeGreeting = backend.EmbeddingCalls;
            var greeting = await Ask("Hi, how are you?", Guid.NewGuid().ToString());
            Assert.False(string.IsNullOrWhiteSpace(greeting));
            Assert.Equal(beforeGreeting, backend.EmbeddingCalls);
            Assert.DoesNotContain("knowledge base was not searched", greeting, StringComparison.OrdinalIgnoreCase);
            output.WriteLine("Greeting answered without knowledge retrieval; conversation correction retained without creating persistent memory.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
