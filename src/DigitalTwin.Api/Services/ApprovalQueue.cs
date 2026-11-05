namespace DigitalTwin.Api;
public sealed class ApprovalQueue
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, ProposedAction> _pending = new();
    public ProposedAction Propose(string kind, string summary, string payload)
    {
        var action = new ProposedAction(Guid.NewGuid(), kind, summary, payload, DateTimeOffset.UtcNow);
        _pending[action.Id] = action;
        return action;
    }
    public IReadOnlyCollection<ProposedAction> List() => _pending.Values.OrderBy(x => x.CreatedAt).ToArray();
    public bool TryGet(Guid id, out ProposedAction? action) => _pending.TryGetValue(id, out action);
    public bool TryRemove(Guid id, out ProposedAction? action) => _pending.TryRemove(id, out action);
}
