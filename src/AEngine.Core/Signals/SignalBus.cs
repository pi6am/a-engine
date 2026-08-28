using AEngine.Core.Modules;
using AEngine.Core.World;

namespace AEngine.Core.Signals;

/// <summary>
/// Delivers sensory signals to agents. Each successful action may emit
/// signal specs (declared on the affordance); every other agent that can
/// perceive the action receives the single highest-priority receivable
/// signal (ties: first listed) in an ephemeral per-agent queue.
///
/// Propagation: an observer in the origin room receives all senses; an
/// observer one portal away receives a sense only if the portal side in
/// the origin room transmits it (portal fields transmitVisual /
/// transmitAudio: always | whenOpen | never — whenOpen reads the shared
/// doorstate via the side's own stateRef). Anything farther gets nothing.
/// </summary>
public sealed class SignalBus
{
    private readonly World.World _world;
    private readonly ModuleRegistry _modules;
    private readonly Dictionary<string, Queue<Signal>> _queues = new(StringComparer.Ordinal);

    public SignalBus(World.World world, ModuleRegistry modules)
    {
        _world = world;
        _modules = modules;
    }

    /// <summary>Format and deliver the given specs for an action performed by <paramref name="actor"/>.</summary>
    public void Emit(WorldObject actor, WorldObject? target, IReadOnlyList<SignalSpec> specs, string? arg = null)
    {
        if (specs.Count == 0)
            return;
        var originRoomId = actor.Parent;
        foreach (var observer in _world.Objects.Values)
        {
            if (observer.Id == actor.Id || !observer.HasModule("agent"))
                continue;
            var best = BestReceivable(observer, originRoomId, actor, target, specs, arg);
            if (best is null)
                continue;
            if (!_queues.TryGetValue(observer.Id, out var queue))
                _queues[observer.Id] = queue = new Queue<Signal>();
            queue.Enqueue(best);
        }
    }

    /// <summary>Return and clear the agent's pending signals.</summary>
    public IReadOnlyList<Signal> Drain(string agentId)
    {
        if (!_queues.TryGetValue(agentId, out var queue) || queue.Count == 0)
            return [];
        var drained = queue.ToArray();
        queue.Clear();
        return drained;
    }

    /// <summary>Return the agent's pending signals without clearing (for tooling).</summary>
    public IReadOnlyList<Signal> Peek(string agentId) =>
        _queues.TryGetValue(agentId, out var queue) ? queue.ToArray() : [];

    private Signal? BestReceivable(
        WorldObject observer, string originRoomId,
        WorldObject actor, WorldObject? target,
        IReadOnlyList<SignalSpec> specs, string? arg)
    {
        WorldObject? portalSide = null;
        if (observer.Parent != originRoomId)
        {
            // adjacent-room observer: find the portal side in the origin
            // room leading toward the observer — that side's transmission
            // fields decide what gets through (one-way by data).
            if (!_world.HasObject(originRoomId))
                return null;
            portalSide = _world.ChildrenOf(originRoomId).FirstOrDefault(c =>
                c.HasModule("portal") &&
                _modules.ResolveString(c, "portal", "to") == observer.Parent);
            if (portalSide is null)
                return null; // not adjacent
        }

        Signal? best = null;
        foreach (var spec in specs)
        {
            if (portalSide is not null && !Transmits(portalSide, spec.Sense))
                continue;
            if (best is null || spec.Priority > best.Priority)
            {
                best = new Signal(
                    spec.Sense, spec.Priority,
                    Format(spec.Text, actor, target, arg), originRoomId);
            }
        }
        return best;
    }

    private bool Transmits(WorldObject portalSide, SignalSense sense)
    {
        var field = sense == SignalSense.Visual ? "transmitVisual" : "transmitAudio";
        var fallback = sense == SignalSense.Visual ? "whenOpen" : "always";
        var mode = _modules.ResolveString(portalSide, "portal", field) ?? fallback;
        return mode switch
        {
            "always" => true,
            "never" => false,
            "whenOpen" => IsOpen(portalSide),
            _ => false,
        };
    }

    private bool IsOpen(WorldObject portalSide)
    {
        var stateRef = _modules.ResolveString(portalSide, "portal", "stateRef");
        return stateRef is not null && _world.HasObject(stateRef) &&
            _modules.ResolveBool(_world.GetObject(stateRef), "doorstate", "open");
    }

    private static string Format(string template, WorldObject actor, WorldObject? target, string? arg) =>
        template
            .Replace("{agent}", actor.Name, StringComparison.Ordinal)
            .Replace("{target}", target?.Name ?? "", StringComparison.Ordinal)
            .Replace("{arg}", arg ?? "", StringComparison.Ordinal);
}
