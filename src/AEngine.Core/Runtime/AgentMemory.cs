using AEngine.Core.Modules;
using AEngine.Core.World;

namespace AEngine.Core.Runtime;

/// <summary>One remembered event, with its salience and current age-adjusted score.</summary>
public sealed record MemoryEntry(long Seq, string? Key, string Text, int Salience, long Score);

/// <summary>
/// Per-agent memory of recent events: signals the agent observed
/// (recorded by <see cref="Signals.SignalBus"/> at delivery time) and the
/// results of the agent's own actions (recorded by
/// <see cref="TurnManager"/>). Bounded per agent; the capacity is
/// data-driven via the agent module's memoryLength field (default 25).
/// Rendered into NPC LLM contexts so agents keep continuity across plans
/// and conversations.
///
/// Retention is salience-ranked by aging: an entry's score is its
/// salience minus its age (in recorded events), and overflow evicts the
/// lowest score (ties: oldest). Salience buys age-resistance, not
/// immunity — an addressed-to-you message outlives ambient chatter by
/// the agent's memorySalienceBoost (default 8 events) but a stale
/// high-salience entry still loses to fresh context, so the log never
/// locks up. High salience comes from being addressed (a directed say, a
/// gift offered), private sensations, the agent's own actions, and
/// per-signal data overrides (a bomb blast high, a jukebox low). The
/// newest entry is never the one evicted by its own arrival — it is
/// always delivered, even when everything else outranks it; the next
/// arrival may take it back out.
///
/// Two anti-bloat rules: consecutive duplicates are dropped ("You
/// wait." × 17 carries no information), and entries recorded with a
/// snapshot key (an examine result, a look) supersede the previous
/// snapshot of the same subject instead of piling up — only the freshest
/// state survives.
/// </summary>
public sealed class AgentMemory
{
    public const int DefaultCapacity = 25;

    /// <summary>
    /// How many events of age-resistance high salience buys when the
    /// agent module doesn't declare memorySalienceBoost.
    /// </summary>
    public const int DefaultSalienceBoost = 8;

    private readonly ModuleRegistry _modules;
    private readonly Dictionary<string, List<(long Seq, string? Key, string Text, int Salience)>> _entries =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _nextSeq = new(StringComparer.Ordinal);

    public AgentMemory(ModuleRegistry modules) => _modules = modules;

    /// <summary>
    /// Append an event, evicting the lowest-scoring older entry beyond
    /// the agent's configured capacity (score = salience − age; the entry
    /// just added is never the one evicted). A <paramref name="snapshotKey"/>
    /// marks a state snapshot: the previous entry with the same key is
    /// removed first. Each entry gets a per-agent sequence number so
    /// tooling can track new entries even while the trim drops old ones.
    /// </summary>
    public void Record(WorldObject agent, string entry, string? snapshotKey = null, int salience = 0)
    {
        if (!_entries.TryGetValue(agent.Id, out var list))
            _entries[agent.Id] = list = [];
        if (snapshotKey is not null)
            list.RemoveAll(e => e.Key == snapshotKey);
        else if (list.Count > 0 && list[^1].Text == entry)
            return; // consecutive duplicate — nothing new to remember
        var seq = _nextSeq.TryGetValue(agent.Id, out var n) ? n + 1 : 1;
        _nextSeq[agent.Id] = seq;
        list.Add((seq, snapshotKey, entry, salience));
        Trim(agent, list);
    }

    /// <summary>
    /// Evict beyond capacity by lowest score (salience − age, age
    /// relative to the newest seq; ties: oldest). The just-added entry
    /// (the list's last) is excluded — the newest message is always
    /// delivered, even if every older entry outranks it; the next
    /// arrival may evict it instead.
    /// </summary>
    private void Trim(WorldObject agent, List<(long Seq, string? Key, string Text, int Salience)> list)
    {
        var capacity = CapacityOf(agent);
        while (list.Count > capacity)
        {
            var latest = _nextSeq[agent.Id];
            var worst = -1;
            var worstScore = long.MaxValue;
            var worstSeq = long.MaxValue;
            for (var i = 0; i < list.Count - 1; i++)
            {
                var score = list[i].Salience - (latest - list[i].Seq);
                if (score < worstScore || (score == worstScore && list[i].Seq < worstSeq))
                {
                    worst = i;
                    worstScore = score;
                    worstSeq = list[i].Seq;
                }
            }
            if (worst < 0)
                break; // nothing but the newcomer — shouldn't happen (capacity >= 1)
            list.RemoveAt(worst);
        }
    }

    /// <summary>The agent's remembered events, oldest first.</summary>
    public IReadOnlyList<string> Recall(string agentId) =>
        _entries.TryGetValue(agentId, out var list)
            ? list.Select(e => e.Text).ToArray()
            : [];

    /// <summary>
    /// Structured recall for tooling (the debug memory panel): entries
    /// with sequence, salience, and the current age-adjusted score — the
    /// score that decides who gets evicted next.
    /// </summary>
    public IReadOnlyList<MemoryEntry> RecallDetailed(string agentId)
    {
        if (!_entries.TryGetValue(agentId, out var list))
            return [];
        var latest = _nextSeq.GetValueOrDefault(agentId);
        return list
            .Select(e => new MemoryEntry(e.Seq, e.Key, e.Text, e.Salience, e.Salience - (latest - e.Seq)))
            .ToArray();
    }

    /// <summary>
    /// The agent's high-salience boost: how many events of age-resistance
    /// being addressed (or acting, or feeling a private sensation) buys.
    /// Data-driven via the agent module's memorySalienceBoost field.
    /// </summary>
    public int SalienceBoostOf(WorldObject agent) =>
        agent.HasModule("agent")
            ? _modules.ResolveInt(agent, "agent", "memorySalienceBoost", DefaultSalienceBoost)
            : DefaultSalienceBoost;

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
