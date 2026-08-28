using AEngine.Core.Actions;
using AEngine.Core.World;

namespace AEngine.Core.Runtime;

/// <summary>
/// Turn-based turn manager: each performed action advances the turn
/// counter and flushes due scheduled actions. Successful actions emit
/// their affordance's sensory signals to observers. RunNpcTurns drives
/// autonomous agents through their policies via an async-ready
/// start/skip/validate-execute pipeline.
/// </summary>
public sealed class TurnManager
{
    private readonly GameEngine _engine;

    /// <summary>In-flight policy selections, per agent id.</summary>
    private readonly Dictionary<string, Task<AvailableAction?>> _inFlightSelections =
        new(StringComparer.Ordinal);

    public TurnManager(GameEngine engine) => _engine = engine;

    public int Turn { get; private set; }

    /// <summary>Execute an action for an agent and advance the turn.</summary>
    public ActionResult PerformAction(WorldObject agent, AvailableAction action, string? text = null)
    {
        lock (_engine.SyncRoot)
        {
            var departureRoomId = agent.Parent;
            var result = Execute(agent, action.HandlerId, action.TargetId, text);
            if (result.Success)
                EmitSignals(agent, action, text, departureRoomId);
            AdvanceTurn();
            return result;
        }
    }

    /// <summary>Execute a handler by id without advancing the turn.</summary>
    public ActionResult Execute(
        WorldObject agent, string handlerId, string? targetId = null, string? text = null)
    {
        lock (_engine.SyncRoot)
        {
            var handler = _engine.HandlerRegistry.Get(handlerId);
            var ctx = new ActionContext
            {
                World = _engine.World,
                Modules = _engine.ModuleRegistry,
                Agent = agent,
                Target = targetId is null ? null : _engine.World.GetObject(targetId),
                Args = text is null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string> { ["text"] = text },
            };
            return handler.Execute(ctx);
        }
    }

    /// <summary>
    /// Give every autonomous agent (agent module with policy != "player")
    /// its turn. Selection is async: the first call starts
    /// ChooseActionAsync and the agent skips; while the task is
    /// incomplete the agent keeps skipping; once complete, the chosen
    /// (verb, targetId) is re-validated against the current world —
    /// executed if still available, discarded if stale — and the slot
    /// clears so a fresh selection starts next turn.
    /// </summary>
    public void RunNpcTurns()
    {
        lock (_engine.SyncRoot)
        {
            foreach (var agentId in NpcAgentIds())
            {
                if (!_engine.World.HasObject(agentId))
                {
                    _inFlightSelections.Remove(agentId); // destroyed mid-selection
                    continue;
                }
                var agent = _engine.World.GetObject(agentId);
                var policyId = _engine.ModuleRegistry.ResolveString(agent, "agent", "policy")!;
                if (!_engine.PolicyRegistry.Has(policyId))
                    continue;
                var policy = _engine.PolicyRegistry.Get(policyId);

                if (!_inFlightSelections.TryGetValue(agentId, out var selection))
                {
                    var actions = _engine.ActionResolver.Resolve(agent);
                    _inFlightSelections[agentId] =
                        policy.ChooseActionAsync(_engine, agent, actions, CancellationToken.None);
                    continue; // deciding counts as this agent's turn
                }
                if (!selection.IsCompleted)
                    continue; // still deciding (a slow policy may take many turns)

                _inFlightSelections.Remove(agentId);
                var chosen = selection.IsCompletedSuccessfully ? selection.Result : null;
                if (chosen is null)
                    continue; // policy passed (or failed) — fresh selection next turn

                // Validate: the world may have changed since the choice was made.
                var available = _engine.ActionResolver.Resolve(agent);
                var action = available.FirstOrDefault(a =>
                    a.Verb == chosen.Verb && a.TargetId == chosen.TargetId);
                if (action is null)
                    continue; // stale choice — discard, fresh selection next turn
                PerformAction(agent, action, chosen.Text);
            }
        }
    }

    private List<string> NpcAgentIds()
    {
        var ids = new List<string>();
        if (!_engine.ModuleRegistry.Has("agent"))
            return ids;
        foreach (var (id, obj) in _engine.World.Objects)
        {
            if (!obj.HasModule("agent"))
                continue;
            var policy = _engine.ModuleRegistry.ResolveString(obj, "agent", "policy") ?? "player";
            if (policy != "player")
                ids.Add(id);
        }
        return ids;
    }

    /// <summary>Emit the affordance's signal specs for a successful action.</summary>
    private void EmitSignals(WorldObject agent, AvailableAction action, string? text, string departureRoomId)
    {
        if (!_engine.ModuleRegistry.Has(action.ModuleId))
            return;
        var affordance = _engine.ModuleRegistry.Get(action.ModuleId).Affordances
            .FirstOrDefault(a => a.Verb == action.Verb);
        if (affordance is null || affordance.Signals.Count == 0)
            return;
        var target = action.TargetId is not null && _engine.World.HasObject(action.TargetId)
            ? _engine.World.GetObject(action.TargetId)
            : null;
        var traversal = BuildTraversal(agent, action, target, departureRoomId);
        _engine.SignalBus.Emit(agent, target, affordance.Signals, text, traversal);
    }

    /// <summary>
    /// Build the traversal context when a successful "go" moved the agent
    /// through a portal into another room; null for non-traversal actions.
    /// The entry side is the portal in the arrival room sharing the exit
    /// side's stateRef (falling back to a side pointing back at the
    /// departure room); null for one-way portals with no return side.
    /// </summary>
    private Signals.TraversalContext? BuildTraversal(
        WorldObject agent, AvailableAction action, WorldObject? target, string departureRoomId)
    {
        if (action.Verb != "go" || target is null || !target.HasModule("portal"))
            return null;
        var arrivalRoomId = agent.Parent;
        if (arrivalRoomId == departureRoomId)
            return null;

        var exitStateRef = _engine.ModuleRegistry.ResolveString(target, "portal", "stateRef");
        var entrySide = _engine.World.ChildrenOf(arrivalRoomId).FirstOrDefault(c =>
            c.HasModule("portal") && c.Id != target.Id &&
            (exitStateRef is not null
                ? _engine.ModuleRegistry.ResolveString(c, "portal", "stateRef") == exitStateRef
                : _engine.ModuleRegistry.ResolveString(c, "portal", "to") == departureRoomId));
        return new Signals.TraversalContext(departureRoomId, arrivalRoomId, target, entrySide);
    }

    private void AdvanceTurn()
    {
        Turn++;
        foreach (var scheduled in _engine.Scheduler.CollectDue(Turn))
        {
            if (!_engine.World.HasObject(scheduled.AgentId))
                continue;
            var agent = _engine.World.GetObject(scheduled.AgentId);
            if (scheduled.TargetId is not null && !_engine.World.HasObject(scheduled.TargetId))
                continue;
            Execute(agent, scheduled.HandlerId, scheduled.TargetId);
        }
    }
}
