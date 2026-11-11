using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Agents.AI;

namespace DigitalTwin.Api;

/// <summary>Per-conversation synchronization and a cache of durable native MAF sessions.</summary>
public sealed class SessionRegistry(ConversationStore? store = null)
{
    private sealed record CachedSession(AgentSession Session, long Generation);
    private readonly ConcurrentDictionary<string, CachedSession> _sessions = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, byte> _privateSessions = new();
    private readonly ConcurrentDictionary<string, byte> _deleted = new();
    private long _generation;
    private long Generation => store?.Generation ?? Interlocked.Read(ref _generation);
    public bool HasPrivateContext(string id) => _privateSessions.ContainsKey(id) || store?.LoadContext(id, false)?.Private == true;
    public void MarkPrivate(string id) => _privateSessions.TryAdd(id, 0);
    public void DiscardUncommittedTurn(string id) => _sessions.TryRemove(id, out _);
    public void DiscardContext(string id)
    {
        store?.DiscardContext(id);
        _sessions.TryRemove(id, out _);
    }
    /// <summary>
    /// Invalidate cached multi-turn context when a knowledge revision changes. This
    /// prevents the next prompt from reusing already-deleted retrieved passages.
    /// Existing browser-visible messages remain; new agent requests start fresh.
    /// </summary>
    public void InvalidateAllAfterKnowledgeChange()
    {
        store?.InvalidateAllContexts();
        Interlocked.Increment(ref _generation);
        _sessions.Clear();
    }

    public ConversationSummary[] List() => store?.List() ?? [];
    public ConversationDetail? Get(string id) => store?.Get(id);
    public void Import(ConversationImport[] conversations) =>
        (store ?? throw new InvalidOperationException("Conversation persistence is not configured.")).Import(conversations);

    public async Task RemoveAll(CancellationToken ct)
    {
        var ids = List().Select(item => item.Id).Concat(_sessions.Keys).Distinct().ToArray();
        foreach (var id in ids) await Remove(id, ct);
    }

    public async Task Remove(string id, CancellationToken ct)
    {
        var gate = GetLock(id);
        await gate.WaitAsync(ct);
        try
        {
            store?.Delete(id);
            _deleted.TryAdd(id, 0);
            _sessions.TryRemove(id, out _);
            _privateSessions.TryRemove(id, out _);
        }
        finally { gate.Release(); }
    }
    public SemaphoreSlim GetLock(string id) => _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
    public async Task<AgentSession> GetOrCreate(string id, AIAgent agent, CancellationToken ct)
    {
        if (_deleted.ContainsKey(id)) throw new ConversationDeletedException();
        var generation = Generation;
        if (_sessions.TryGetValue(id, out var found) && found.Generation == generation) return found.Session;
        var saved = store?.LoadContext(id);
        AgentSession session;
        if (saved?.Json is { } json && saved.Generation == generation)
        {
            using var document = JsonDocument.Parse(json);
            session = await agent.DeserializeSessionAsync(document.RootElement, cancellationToken: ct);
        }
        else session = await agent.CreateSessionAsync(cancellationToken: ct);
        _sessions[id] = new(session, generation);
        if (saved?.Private == true) MarkPrivate(id);
        return session;
    }

    // The caller holds the per-session gate throughout inference and this commit.
    public async Task SaveTurn(string id, AIAgent agent, AgentSession session, string userMessage,
        string reply, string[]? attachments, bool discardContext, CancellationToken ct)
    {
        if (store is null) return;
        string? json = null;
        if (!discardContext)
            json = (await agent.SerializeSessionAsync(session, cancellationToken: ct)).GetRawText();
        ct.ThrowIfCancellationRequested();
        var generation = _sessions.TryGetValue(id, out var cached) ? cached.Generation : -1;
        store.SaveTurn(id, json, generation, HasPrivateContext(id),
            new("user", userMessage, attachments), new("assistant", reply));
    }
}
