using System.Collections.Concurrent;
using Microsoft.Agents.AI;

namespace DigitalTwin.Api;

/// <summary>Multi-turn sessions live in memory for this training sample.</summary>
public sealed class SessionRegistry
{
    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, byte> _privateSessions = new();
    public bool HasPrivateContext(string id) => _privateSessions.ContainsKey(id);
    public void MarkPrivate(string id) => _privateSessions.TryAdd(id, 0);
    public void DiscardContext(string id) => _sessions.TryRemove(id, out _);
    /// <summary>
    /// Invalidate cached multi-turn context when a knowledge revision changes. This
    /// prevents the next prompt from reusing already-deleted retrieved passages.
    /// Existing browser-visible messages remain; new agent requests start fresh.
    /// </summary>
    public void InvalidateAllAfterKnowledgeChange() => _sessions.Clear();

    public async Task Remove(string id, CancellationToken ct)
    {
        var gate = GetLock(id);
        await gate.WaitAsync(ct);
        try { _sessions.TryRemove(id, out _); _privateSessions.TryRemove(id, out _); }
        finally { gate.Release(); }
    }
    public SemaphoreSlim GetLock(string id) => _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
    public async Task<AgentSession> GetOrCreate(string id, AIAgent agent, CancellationToken ct)
    {
        if (_sessions.TryGetValue(id, out var found)) return found;
        var created = await agent.CreateSessionAsync(cancellationToken: ct);
        return _sessions.GetOrAdd(id, created);
    }
}
