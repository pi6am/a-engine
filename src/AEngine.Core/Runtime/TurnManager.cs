using AEngine.Core.Actions;
using AEngine.Core.World;

namespace AEngine.Core.Runtime;

/// <summary>
/// Turn manager. In turn-based mode each performed action advances the
/// turn counter and flushes due scheduled actions; in real-time mode the
/// driver (e.g. the CLI's per-second timer) calls <see cref="Tick"/> to
/// advance time instead, and actions leave the acting agent busy for
/// their affordance's data-driven duration (seconds/turns, default 1).
/// Successful actions emit their affordance's sensory signals to
/// observers. RunNpcTurns drives autonomous agents through their policies
/// via an async-ready start/skip/validate-execute pipeline.
/// </summary>
public sealed class TurnManager
{
    private readonly GameEngine _engine;

    /// <summary>In-flight policy selections, per agent id.</summary>
    private readonly Dictionary<string, Task<AvailableAction?>> _inFlightSelections =
        new(StringComparer.Ordinal);

    /// <summary>Per-agent busy-until turn, from action durations.</summary>
    private readonly Dictionary<string, int> _busyUntil = new(StringComparer.Ordinal);

    /// <summary>Per-agent consecutive-repeat streaks for backoff affordances (idle verbs).</summary>
    private readonly Dictionary<string, (string Verb, int Count)> _repeatStreaks =
        new(StringComparer.Ordinal);

    /// <summary>Agents whose current busy spell is idle backoff — interruptible by new signals.</summary>
    private readonly HashSet<string> _busyInterruptible = new(StringComparer.Ordinal);

    public TurnManager(GameEngine engine) => _engine = engine;

    public int Turn { get; private set; }

    /// <summary>
    /// Advance one turn and flush due scheduled actions. The real-time
    /// driver calls this on a wall-clock timer; in turn-based mode the
    /// turn advances per action instead (see <see cref="PerformAction"/>).
    /// </summary>
    public void Tick()
    {
        lock (_engine.SyncRoot)
        {
            AdvanceTurn();
        }
    }

    /// <summary>
    /// Execute an action for an agent. Noop results (the intended end
    /// state already held) consume no turn and emit no signals; failures
    /// still take time (the attempt happened). Turn-consuming actions mark
    /// the agent busy for the affordance's duration (or the handler's
    /// dynamic override, e.g. say scales with text length). In turn-based
    /// mode the turn then advances; in real-time mode time advances via
    /// <see cref="Tick"/>.
    /// </summary>
    public ActionResult PerformAction(WorldObject agent, AvailableAction action, string? text = null)
    {
        lock (_engine.SyncRoot)
        {
            var departureRoomId = _engine.World.RoomOf(agent.Id).Id;
            var result = EvaluateCheck(agent, action)
                         ?? Execute(agent, action.HandlerId, action.TargetId, text, action.Verb);
            // remember your own action and its outcome (a look result is too
            // verbose to store verbatim)
            _engine.Memory.Record(agent,
                action.Verb == "look" ? "You look around." : result.Message);
            if (result.Outcome == ActionOutcome.Noop)
                return result;
            if (result.Success)
                EmitSignals(agent, action, text, departureRoomId);
            _busyUntil[agent.Id] = Turn + BusyDuration(agent, action, result);
            if (_engine.TimeMode == TimeMode.TurnBased)
                AdvanceTurn();
            return result;
        }
    }

    /// <summary>Execute a handler by id without advancing the turn.</summary>
    public ActionResult Execute(
        WorldObject agent, string handlerId, string? targetId = null, string? text = null,
        string? verb = null)
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
                Verb = verb,
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
    /// clears so a fresh selection starts next turn. Busy agents (a
    /// long-running action in progress) don't START new selections — but
    /// a selection already in flight always runs to completion and
    /// executes: idle backoff is interruptible, and a woken agent's
    /// pending signal queue is drained into the planning context, so
    /// gating execution on busy would stall the chosen action until the
    /// backoff expired.
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

                if (_inFlightSelections.TryGetValue(agentId, out var selection))
                {
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
                    continue;
                }

                // no selection in flight: busy agents skip (idle backoff is
                // interruptible — new signals wake the agent to start deciding)
                if (IsBusy(agentId) && !CanWake(agentId))
                    continue;
                var availableActions = _engine.ActionResolver.Resolve(agent);
                _inFlightSelections[agentId] =
                    policy.ChooseActionAsync(_engine, agent, availableActions, CancellationToken.None);
                // deciding counts as this agent's turn
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

    /// <summary>The turn until which the agent is busy with its current action (0 = free). For tooling/tests.</summary>
    public int BusyUntilTurn(string agentId) =>
        _busyUntil.TryGetValue(agentId, out var until) ? until : 0;

    /// <summary>
    /// Evaluate the affordance's stat/skill check, if any. Returns null
    /// when there is no check or the check passes (the handler then runs);
    /// a failed check returns a Failure result without running the handler
    /// — it still consumes the turn and records memory via the caller.
    /// </summary>
    private ActionResult? EvaluateCheck(WorldObject agent, AvailableAction action)
    {
        if (LookupAffordance(action)?.Check is not { } check)
            return null;
        var margin = Checks.Evaluate(_engine.World, _engine.ModuleRegistry, _engine.Random, agent, check);
        if (margin >= 0)
            return null;
        var targetName = action.TargetId is not null && _engine.World.HasObject(action.TargetId)
            ? _engine.World.GetObject(action.TargetId).Name
            : null;
        return ActionResult.Fail(check.FailText ?? (targetName is not null
            ? $"You try to {action.Verb} the {targetName}, but fail."
            : $"You try to {action.Verb}, but fail."));
    }

    private bool IsBusy(string agentId) =>
        _busyUntil.TryGetValue(agentId, out var until) && Turn < until;

    /// <summary>
    /// Idle backoff (repeated look/wait) is interruptible: a busy agent
    /// whose busy spell came from a backoff affordance wakes early when
    /// new signals are pending, so idling agents stay reactive.
    /// </summary>
    private bool CanWake(string agentId) =>
        _busyInterruptible.Contains(agentId) && _engine.SignalBus.Peek(agentId).Count > 0;

    /// <summary>
    /// The action's effective duration: the handler's dynamic override
    /// (e.g. say) or the affordance's declared duration. Idle verbs
    /// (affordances with repeatBackoff) back off exponentially on
    /// consecutive repeats — 1x, 2x, 4x, ... up to repeatBackoffCap — and
    /// mark the busy spell interruptible; any other verb resets the
    /// streak.
    /// </summary>
    private int BusyDuration(WorldObject agent, AvailableAction action, ActionResult result)
    {
        var affordance = LookupAffordance(action);
        var baseDuration = result.Duration ?? affordance?.Duration ?? 1;
        if (affordance is not { RepeatBackoff: true })
        {
            _repeatStreaks.Remove(agent.Id);
            _busyInterruptible.Remove(agent.Id);
            return baseDuration;
        }
        _repeatStreaks.TryGetValue(agent.Id, out var streak);
        var count = streak.Verb == action.Verb ? streak.Count + 1 : 1;
        _repeatStreaks[agent.Id] = (action.Verb, count);
        _busyInterruptible.Add(agent.Id);
        var scaled = baseDuration << Math.Min(count - 1, 10); // 1x, 2x, 4x, ...
        return Math.Min(scaled, Math.Max(baseDuration, affordance.RepeatBackoffCap));
    }

    private Modules.AffordanceDefinition? LookupAffordance(AvailableAction action)
    {
        if (!_engine.ModuleRegistry.Has(action.ModuleId))
            return null;
        return _engine.ModuleRegistry.Get(action.ModuleId).Affordances
            .FirstOrDefault(a => a.Verb == action.Verb);
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
