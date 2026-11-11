using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace DigitalTwin.Api;

public sealed class GuardrailViolationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Trusted, per-run state. Model arguments and retrieved text cannot set permissions.</summary>
public sealed class GuardrailRunState(GuardrailSettings limits, bool allowWebSearch, bool requireKnowledge,
    bool allowHandoff, bool privateContext, CancellationTokenSource cancellation, Action markSessionPrivate)
{
    private int _toolCalls, _modelCalls, _private = privateContext ? 1 : 0, _knowledgeSearches, _discardSession;
    private readonly ConcurrentDictionary<string, int> _repetitions = new();
    private readonly ConcurrentDictionary<string, byte> _sources = new(StringComparer.OrdinalIgnoreCase);
    private GuardrailViolationException? _aborted;
    private readonly List<(string Text, IReadOnlyList<WebSource> Sources)> _webEvidence = [];
    public string WebEvidence => string.Join("\n\n", _webEvidence.Select(e => e.Text));
    public IReadOnlyList<WebSource> WebSources => _webEvidence.SelectMany(e => e.Sources).DistinctBy(s => s.Url).ToArray();
    public bool CanCallModel => Volatile.Read(ref _modelCalls) < limits.MaxModelCalls;
    public int ModelCalls => Volatile.Read(ref _modelCalls);
    public WebSearchFailure? WebFailure { get; set; }
    public void RecordWebEvidence(string text, IReadOnlyList<WebSource> sources)
    {
        if (sources.Count == 0) return;
        if (_webEvidence.Count == 3) _webEvidence.RemoveAt(0);
        _webEvidence.Add((text[..Math.Min(text.Length, 8000)], sources));
    }
    // Compatibility metadata only; public search no longer depends on this flag or private context.
    public bool AllowWebSearch { get; } = allowWebSearch;
    public bool RequireKnowledge { get; } = requireKnowledge;
    public bool AllowHandoff { get; } = allowHandoff;
    public bool HasPrivateContext => Volatile.Read(ref _private) != 0;
    public int KnowledgeSearches => Volatile.Read(ref _knowledgeSearches);
    public bool DiscardSession => Volatile.Read(ref _discardSession) != 0;
    public void DiscardConversationContext() => Interlocked.Exchange(ref _discardSession, 1);
    public IReadOnlyCollection<string> KnowledgeSources => _sources.Keys.ToArray();
    public GuardrailViolationException? Aborted => _aborted;
    public CancellationToken CancellationToken => cancellation.Token;
    public SemaphoreSlim ToolGate { get; } = new(1, 1);

    public void MarkPrivate()
    {
        Interlocked.Exchange(ref _private, 1);
        markSessionPrivate();
    }

    public void RecordKnowledge(IEnumerable<string> sources)
    {
        MarkPrivate();
        Interlocked.Increment(ref _knowledgeSearches);
        foreach (var source in sources) _sources.TryAdd(source, 0);
    }

    public void BeforeTool(string name, string canonicalArguments)
    {
        ThrowIfStopped();
        // Only hashes live in budget state; never retain arguments or private evidence in diagnostics.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name + canonicalArguments)));
        if (Interlocked.Increment(ref _toolCalls) > limits.MaxToolCalls ||
            _repetitions.AddOrUpdate(hash, 1, (_, count) => count + 1) > limits.MaxRepeatedToolCalls)
            Abort("tool_budget", "The agent reached its tool execution limit. Narrow the request and try again.");
    }

    public void BeforeModel()
    {
        ThrowIfStopped();
        if (Interlocked.Increment(ref _modelCalls) > limits.MaxModelCalls)
            Abort("model_budget", "The agent reached its model-call limit. Narrow the request and try again.");
    }

    private void Abort(string code, string message)
    {
        var error = new GuardrailViolationException(code, message);
        Interlocked.CompareExchange(ref _aborted, error, null);
        cancellation.Cancel();
        throw _aborted;
    }

    public void ThrowIfStopped()
    {
        if (_aborted is { } error) throw error;
        cancellation.Token.ThrowIfCancellationRequested();
    }
}

public static class GuardrailContext
{
    private static readonly AsyncLocal<GuardrailRunState?> State = new();
    public static GuardrailRunState? Current => State.Value;
    public static GuardrailRunState Required => Current ?? throw new GuardrailViolationException(
        "missing_run_context", "Agent execution requires a validated request context.");

    public static IDisposable Enter(GuardrailRunState state)
    {
        var previous = State.Value;
        State.Value = state;
        return new Scope(() => State.Value = previous);
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
