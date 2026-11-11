using DigitalTwin.Api;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace DigitalTwin.Api.Tests;

internal sealed class GuardrailTestHarness : IDisposable
{
    public TwinSettings Settings { get; }
    public SessionRegistry Sessions { get; } = new();
    public ScriptedChatClient Model { get; }
    private readonly GuardrailChatClient _client;
    public AIAgent Agent { get; }
    public GuardrailAgentRunner Runner { get; }
    public string SessionId { get; } = Guid.NewGuid().ToString();

    public GuardrailTestHarness(Func<int, ChatMessage[], CancellationToken, Task<ChatResponse>> script,
        AITool[]? tools = null, TwinSettings? settings = null)
    {
        Settings = settings ?? new TwinSettings();
        Model = new ScriptedChatClient(script);
        _client = new GuardrailChatClient(Model, Settings);
        Agent = new ChatClientAgent(_client, instructions: "Use the requested tools and cite knowledge filenames.", tools: tools);
        Runner = new(Settings, Sessions, new InputGuardrailService(), new ToolAuthorizationService(), new OutputValidationService(Settings));
    }

    public Task<string> Run(string prompt = "Answer the question.", bool allowWeb = false, bool privateContext = false,
        bool requireKnowledge = false, string? userMessage = null, CancellationToken ct = default, bool allowHandoff = false) =>
        Runner.RunAsync(Agent, SessionId, prompt, userMessage ?? prompt, privateContext, allowWeb, requireKnowledge, ct, allowHandoff);

    public static AIFunction Tool(Delegate callback, string name) =>
        AIFunctionFactory.Create(callback, new AIFunctionFactoryOptions { Name = name });

    public static ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant, text));
    public static ChatResponse Call(int turn, string name, Dictionary<string, object?>? arguments = null) =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-" + turn, name, arguments)]));

    public void Dispose() => _client.Dispose();

    internal sealed class ScriptedChatClient(Func<int, ChatMessage[], CancellationToken, Task<ChatResponse>> script) : IChatClient
    {
        private int _calls;
        public int Calls => _calls;
        public int? LastMaxOutputTokens { get; private set; }
        public int? LastToolCount { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastMaxOutputTokens = options?.MaxOutputTokens;
            LastToolCount = options?.Tools?.Count;
            return script(Interlocked.Increment(ref _calls), messages.ToArray(), cancellationToken);
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("The test agent uses non-streaming calls.");
        public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;
        public void Dispose() { }
    }
}
