using AEngine.Core.Modules;
using AEngine.Core.World;

namespace AEngine.Core.Runtime;

/// <summary>
/// Per-agent memory of recent events: signals the agent observed
/// (recorded by <see cref="Signals.SignalBus"/> at delivery time) and the
/// results of the agent's own actions (recorded by
/// <see cref="TurnManager"/>). Bounded per agent; the capacity is
/// data-driven via the agent module's memoryLength field (default 25).
/// Rendered into NPC LLM contexts so agents keep continuity across plans
/// and conversations. Two anti-bloat rules: consecutive duplicates are
/// dropped ("You wait." × 17 carries no information), and entries recorded
/// with a snapshot key (an examine result, a look) supersede the previous
/// snapshot of the same subject instead of piling up — only the freshest
/// state survives.
/// </summary>
public sealed class AgentMemory
{
    public const int DefaultCapacity = 25;

    private readonly ModuleRegistry _modules;
    private readonly Dictionary<string, List<(long Seq, string? Key, string Text)>> _entries =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _nextSeq = new(StringComparer.Ordinal);

    public AgentMemory(ModuleRegistry modules) => _modules = modules;

    /// <summary>
    /// Append an event, truncating the oldest beyond the agent's configured
    /// capacity. A <paramref name="snapshotKey"/> marks a state snapshot:
    /// the previous entry with the same key is removed first. Each entry
    /// gets a per-agent sequence number so tooling can track new entries
    /// even while the capacity trim drops old ones.
    /// </summary>
    public void Record(WorldObject agent, string entry, string? snapshotKey = null)
    {
        if (!_entries.TryGetValue(agent.Id, out var list))
            _entries[agent.Id] = list = [];
        if (snapshotKey is not null)
            list.RemoveAll(e => e.Key == snapshotKey);
        else if (list.Count > 0 && list[^1].Text == entry)
            return; // consecutive duplicate — nothing new to remember
        var seq = _nextSeq.TryGetValue(agent.Id, out var n) ? n + 1 : 1;
        _nextSeq[agent.Id] = seq;
        list.Add((seq, snapshotKey, entry));
        var capacity = CapacityOf(agent);
        while (list.Count > capacity)
            list.RemoveAt(0);
    }

    /// <summary>The agent's remembered events, oldest first.</summary>
    public IReadOnlyList<string> Recall(string agentId) =>
        _entries.TryGetValue(agentId, out var list)
            ? list.Select(e => e.Text).ToArray()
            : [];

    /// <summary>The highest sequence number recorded for the agent (0 = none).</summary>
    public long LatestSeq(string agentId) =>
        _nextSeq.TryGetValue(agentId, out var n) ? n : 0;

    /// <summary>
    /// Entries recorded after <paramref name="afterSeq"/>, oldest first,
    /// with the newest sequence — a cursor that survives the capacity trim
    /// (an index-based cursor goes stale once the list is full and its
    /// length stops growing).
    /// </summary>
    public (IReadOnlyList<string> Entries, long LastSeq) NewSince(string agentId, long afterSeq)
    {
        if (!_entries.TryGetValue(agentId, out var list))
            return ([], afterSeq);
        var fresh = list.Where(e => e.Seq > afterSeq).ToArray();
        return (fresh.Select(e => e.Text).ToArray(),
                fresh.Length > 0 ? fresh[^1].Seq : afterSeq);
    }

    /// <summary>Forget everything (e.g. the agent was destroyed).</summary>
    public void Clear(string agentId) => _entries.Remove(agentId);

    private int CapacityOf(WorldObject agent) =>
        agent.HasModule("agent")
            ? Math.Max(1, _modules.ResolveInt(agent, "agent", "memoryLength", DefaultCapacity))
            : DefaultCapacity;
}
