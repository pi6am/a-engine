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
/// and conversations.
/// </summary>
public sealed class AgentMemory
{
    public const int DefaultCapacity = 25;

    private readonly ModuleRegistry _modules;
    private readonly Dictionary<string, Queue<string>> _entries = new(StringComparer.Ordinal);

    public AgentMemory(ModuleRegistry modules) => _modules = modules;

    /// <summary>Append an event, truncating the oldest beyond the agent's configured capacity.</summary>
    public void Record(WorldObject agent, string entry)
    {
        if (!_entries.TryGetValue(agent.Id, out var queue))
            _entries[agent.Id] = queue = new Queue<string>();
        queue.Enqueue(entry);
        var capacity = CapacityOf(agent);
        while (queue.Count > capacity)
            queue.Dequeue();
    }

    /// <summary>The agent's remembered events, oldest first.</summary>
    public IReadOnlyList<string> Recall(string agentId) =>
        _entries.TryGetValue(agentId, out var queue) ? queue.ToArray() : [];

    /// <summary>Forget everything (e.g. the agent was destroyed).</summary>
    public void Clear(string agentId) => _entries.Remove(agentId);

    private int CapacityOf(WorldObject agent) =>
        agent.HasModule("agent")
            ? Math.Max(1, _modules.ResolveInt(agent, "agent", "memoryLength", DefaultCapacity))
            : DefaultCapacity;
}
