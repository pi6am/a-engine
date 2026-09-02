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
/// via an async-ready start/skip/validate-execute pipeline, throttled by
/// level of detail: agents outside every player's room and its adjacent
/// rooms start new work only every `npcLodFactor` turns (rules module;
/// default 10) — in-flight policy decisions always run to completion.
/// </summary>
public sealed class TurnManager
{
    private readonly GameEngine _engine;

    /// <summary>In-flight policy selections, per agent id.</summary>
    private readonly Dictionary<string, Task<AvailableAction?>> _inFlightSelections =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Per-agent busy-until turn for the action track, from action
    /// durations. Gates policy selection (see <see cref="RunNpcTurns"/>).
    /// </summary>
    private readonly Dictionary<string, int> _busyUntil = new(StringComparer.Ordinal);

    /// <summary>
    /// Per-agent busy-until turn for the speech track (affordances with
    /// `speech: true`, e.g. say). Talking paces itself but doesn't block
    /// the action track — an agent can move or fight mid-monologue.
    /// </summary>
    private readonly Dictionary<string, int> _speechBusyUntil = new(StringComparer.Ordinal);

    /// <summary>Per-agent consecutive-repeat streaks for backoff affordances (idle verbs).</summary>
    private readonly Dictionary<string, (string Verb, int Count)> _repeatStreaks =
        new(StringComparer.Ordinal);

    /// <summary>Per-object elapsed seconds toward the next ambient emission.</summary>
    private readonly Dictionary<string, int> _ambientElapsed = new(StringComparer.Ordinal);

    /// <summary>Per-object randomized delay (seconds) at which the next ambient emission fires.</summary>
    private readonly Dictionary<string, int> _ambientNextDue = new(StringComparer.Ordinal);

    /// <summary>Agents whose current busy spell is idle backoff — interruptible by new signals.</summary>
    private readonly HashSet<string> _busyInterruptible = new(StringComparer.Ordinal);

    /// <summary>
    /// Per-agent expiry turn for the recorded body-track activity (the
    /// agent-module `activity` field is the observable copy — look,
    /// examine, and LLM contexts read the world, not this manager).
    /// </summary>
    private readonly Dictionary<string, int> _activityUntil = new(StringComparer.Ordinal);

    /// <summary>
    /// Per-agent queues of their own action-outcome messages (bounded), for
    /// tooling: signals never reach the actor, so an auto-played character's
    /// actions would otherwise be invisible to a spectator. Parked-action
    /// resolutions ride <see cref="ReactionManager.DrainResolved"/> instead.
    /// Drained via <see cref="DrainOutcomes"/>.
    /// </summary>
    private readonly Dictionary<string, Queue<string>> _outcomes = new(StringComparer.Ordinal);

    private const int MaxQueuedOutcomes = 50;

    public TurnManager(GameEngine engine) => _engine = engine;

    public int Turn { get; private set; }

    /// <summary>
    /// Re-evaluate upkeep-derived state (metabolism condition bands)
    /// without advancing time — called once after a scenario loads so
    /// agents start with the conditions their initial field values imply
    /// (the drunk elf is drunk on turn 0).
    /// </summary>
    public void EvaluateUpkeep()
    {
        lock (_engine.SyncRoot)
        {
            Metabolism.Advance(_engine, 0);
        }
    }

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
            var affordance = LookupAffordance(action);
            // execution gates run first: prerequisites before quick-time
            // reactions and dice — an action that can't happen shouldn't
            // telegraph. A blocked gate fails with its failText and
            // consumes the turn (the affordance's failSignals fire).
            var gateFailure = EvaluateGates(agent, action, text, affordance);
            // quick-time reactions: an action targeting another agent with a
            // reaction spec telegraphs and parks until the defender responds.
            // The defender is the target agent — or, for an item-targeted
            // action (bartering for a held ware), the agent holding it.
            if (gateFailure is null &&
                affordance?.Reaction is { Window: > 0 } reaction &&
                ResolveReactionDefender(agent, action) is { } defender)
            {
                var options = reaction.Options.Where(o =>
                    o.RequiresWornModule is null ||
                    Actions.Clothing.WornItems(_engine.World, _engine.ModuleRegistry, defender)
                        .Any(w => w.HasModule(o.RequiresWornModule))).ToList();
                // no real choice (fewer than two options) → resolve normally
                if (options.Count >= 2)
                    return ParkReaction(agent, action, text, reaction, defender, options);
            }
            var departureRoomId = _engine.World.RoomOf(agent.Id).Id;
            // capture the target's holder before the handler moves it, so
            // signal templates can name it via {container} ("picks up the
            // carving knife from the cupboard")
            var holderBefore = action.TargetId is not null && _engine.World.HasObject(action.TargetId)
                ? _engine.World.GetObject(action.TargetId).Parent
                : null;
            // and its name before the handler runs — a handler may rename
            // its target (a consumed drink becoming "empty mug"), and
            // observers should hear what it WAS
            var targetNameBefore = action.TargetId is not null && _engine.World.HasObject(action.TargetId)
                ? _engine.World.GetObject(action.TargetId).Name
                : null;
            var result = gateFailure
                         ?? EvaluateCheck(agent, action)
                         ?? Execute(agent, action.HandlerId, action.TargetId, text, action.Verb,
                             auxTargetId: action.AuxTargetId, affordanceData: affordance?.Data);
            if (result.EndsGame)
                _engine.GameOver ??= result.Message;
            // remember your own action and its outcome (a look result is too
            // verbose to store verbatim; look/examine are state snapshots —
            // only the freshest of each survives in memory)
            RecordOutcome(agent, action, result.Message);
            QueueOutcome(agent.Id, result.Message);
            if (result.Outcome == ActionOutcome.Noop)
                return result;
            // a player-driven action grants the NPCs their round — this
            // is what makes turn-based turn-based (the CLI also bumps
            // explicitly via NewNpcRound before RunNpcTurns; idempotent)
            if ((_engine.ModuleRegistry.ResolveString(agent, "agent", "policy") ?? "player") == "player")
                _roundEpoch++;
            if (result.Success)
                EmitSignals(agent, action, text, departureRoomId, holderBefore, targetNameBefore);
            else
                EmitFailSignals(agent, action);
            var duration = BusyDuration(agent, action, result);
            // turn-based pacing: every action is one turn (classic
            // text-adventure semantics) — durations pace the world clock
            // (metabolism, chatter) and real-time mode, not turns. Busy
            // spells last through the current round only.
            var busySpan = _engine.TimeMode == TimeMode.TurnBased ? 2 : duration;
            // speech rides its own track: it paces talking (and replanning,
            // see LlmPolicy) without blocking movement or attacks
            if (affordance?.Speech == true)
            {
                _speechBusyUntil[agent.Id] = Turn + busySpan;
                ClearPendingSpeech(agent.Id); // the queued words are being said
            }
            else
            {
                _busyUntil[agent.Id] = Turn + busySpan;
                RecordActivity(agent, action, affordance, busySpan);
            }
            if (_engine.TimeMode == TimeMode.TurnBased)
            {
                // ambient time passes with the actor's own activity — and
                // so do their metabolism and intimacy: each action
                // advances only the ACTOR's per-agent clocks (the cast
                // size must not speed up everyone's world), while the
                // room-objects' chatter rides the player's clock (the POV
                // experiences the TV)
                AdvanceAmbient(duration, agent.Id);
                Metabolism.Advance(_engine, duration, agent.Id);
                if ((_engine.ModuleRegistry.ResolveString(agent, "agent", "policy") ?? "player") == "player")
                    Chatter.Advance(_engine, duration);
                AdvanceTurn();
            }
            return result;
        }
    }

    /// <summary>
    /// The agent who may react to an action: the target when it's an agent
    /// (shove, hug, give), else the agent holding the target item (bartering
    /// for a held ware — the holder consents or declines). Null when no
    /// (non-incapacitated, non-actor) agent qualifies.
    /// </summary>
    private WorldObject? ResolveReactionDefender(WorldObject agent, AvailableAction action)
    {
        if (action.TargetId is null || !_engine.World.HasObject(action.TargetId))
            return null;
        var target = _engine.World.GetObject(action.TargetId);
        WorldObject? defender = target.HasModule("agent") ? target : null;
        if (defender is null && target.Parent.Length > 0 && _engine.World.HasObject(target.Parent) &&
            _engine.World.GetObject(target.Parent) is { } holder && holder.HasModule("agent"))
            defender = holder;
        return defender is null || defender.Id == agent.Id ||
            Actions.Health.IsIncapacitated(_engine.World, _engine.ModuleRegistry, defender)
            ? null
            : defender;
    }

    /// <summary>
    /// Telegraph a reaction-eligible action: the attempt is observable and
    /// the actor is committed (busy, turn spent), but the check and handler
    /// wait for the defender's reaction — chosen via the UI (player), the
    /// defender's policy (NPC), or the deadline default. Resolution lands
    /// in <see cref="ResolveParked"/>.
    /// </summary>
    private ActionResult ParkReaction(
        WorldObject agent, AvailableAction action, string? text,
        Modules.ReactionSpec spec, WorldObject defender, List<Modules.ReactionOptionSpec> options)
    {
        var telegraph = spec.Telegraph ?? $"{{agent}} tries to {action.Verb} {{target}}.";
        // {target} is the action's target (for barter: the ware, not the
        // defending holder); "you" substitutions apply only when the
        // defender IS the target
        var target = action.TargetId is not null && _engine.World.HasObject(action.TargetId)
            ? _engine.World.GetObject(action.TargetId)
            : null;
        _engine.SignalBus.Emit(agent, target,
            [new Signals.SignalSpec { Sense = Signals.SignalSense.Visual, Priority = 10, Text = telegraph }],
            text, extra: Extras(null, action.AuxTargetId));
        // the announcement is the DEFENDER's popup: the attacker renders by
        // the name the defender can print (incognito until learned)
        var announcement = telegraph
            .Replace("{agent}", Actions.Knowledge.NameFor(_engine.ModuleRegistry, defender, agent),
                StringComparison.Ordinal);
        // part-targeted actions ("leans in to kiss {holder}'s neck"):
        // the holder IS the defender
        if (target is not null && target.Parent.Length > 0 && _engine.World.HasObject(target.Parent) &&
            _engine.World.GetObject(target.Parent).Id == defender.Id)
            announcement = announcement.Replace("{holder}", "you", StringComparison.Ordinal);
        announcement = target is not null && target.Id == defender.Id
            ? announcement
                .Replace("the {target}", "you", StringComparison.Ordinal)
                .Replace("{target}", "you", StringComparison.Ordinal)
            : announcement
                .Replace("the {target}", target is null ? "" : Perception.WithDefiniteArticle(target.Name),
                    StringComparison.Ordinal)
                .Replace("{target}", target is null ? "" : Perception.WithDefiniteArticle(target.Name),
                    StringComparison.Ordinal);
        announcement = announcement
            .Replace("{item}", AuxName(action.AuxTargetId), StringComparison.Ordinal);
        var pending =         _engine.Reactions.Add(
            agent.Id, defender.Id, action, text, announcement, options, Turn + spec.Window);
        // the committed actor's round counts (players park too)
        if ((_engine.ModuleRegistry.ResolveString(agent, "agent", "policy") ?? "player") == "player")
            _roundEpoch++;
        // an NPC defender's policy picks the reaction; synchronous policies
        // (random/auto without LLM) resolve immediately in PollPolicies
        var policyId = _engine.ModuleRegistry.ResolveString(defender, "agent", "policy") ?? "player";
        if (policyId != "player" && _engine.PolicyRegistry.Has(policyId))
            pending.PolicySelection = _engine.PolicyRegistry.Get(policyId)
                .ChooseReactionAsync(_engine, defender, pending, CancellationToken.None);
        _engine.Reactions.PollPolicies();

        // the actor's own copy names the defender as the actor knows them
        var actorText = (spec.ActorText ?? "You {verb} {target}.")
            .Replace("{verb}", action.Verb, StringComparison.Ordinal)
            .Replace("{agent}", agent.Name, StringComparison.Ordinal)
            .Replace("the {target}", Perception.WithDefiniteArticle(
                Actions.Knowledge.NameFor(_engine.ModuleRegistry, agent, target ?? defender)), StringComparison.Ordinal)
            .Replace("{target}", Perception.WithDefiniteArticle(
                Actions.Knowledge.NameFor(_engine.ModuleRegistry, agent, target ?? defender)), StringComparison.Ordinal)
            .Replace("{item}", AuxName(action.AuxTargetId), StringComparison.Ordinal);
        // part-targeted telegraphs ("You lean in to kiss {holder}."):
        // the holder renders by the name the actor can print
        if (target is not null && target.Parent.Length > 0 && _engine.World.HasObject(target.Parent) &&
            _engine.World.GetObject(target.Parent).HasModule("agent"))
            actorText = actorText.Replace("{holder}",
                Actions.Knowledge.NameFor(_engine.ModuleRegistry, agent, _engine.World.GetObject(target.Parent)),
                StringComparison.Ordinal);
        actorText = actorText.Replace("{holder}", "", StringComparison.Ordinal);
        _engine.Memory.Record(agent, actorText);
        QueueOutcome(agent.Id, actorText);
        var parkDuration = BusyDuration(agent, action, ActionResult.Ok(actorText));
        _busyUntil[agent.Id] = Turn + (_engine.TimeMode == TimeMode.TurnBased ? 2 : parkDuration);
        RecordActivity(agent, action, LookupAffordance(action),
            _engine.TimeMode == TimeMode.TurnBased ? 2 : parkDuration);
        if (_engine.TimeMode == TimeMode.TurnBased)
        {
            AdvanceAmbient(parkDuration, agent.Id);
            Metabolism.Advance(_engine, parkDuration, agent.Id);
            if ((_engine.ModuleRegistry.ResolveString(agent, "agent", "policy") ?? "player") == "player")
                Chatter.Advance(_engine, parkDuration);
            AdvanceTurn();
        }
        return ActionResult.Ok(actorText);
    }

    /// <summary>
    /// Resolve a parked action with the defender's chosen reaction: the
    /// reaction replaces the defender's side of any opposed check (gate or
    /// handler-rolled), then the normal tail runs — handler, signals,
    /// memory for both sides. The actor's eligibility is re-validated
    /// first — a parked action whose actor was incapacitated, knocked
    /// prone, or grabbed during the window fizzles ("The moment passes."),
    /// mirroring how stale NPC policy choices are discarded. No turn
    /// advance (resolution happens inside the parker's turn flow or a
    /// Tick); the actor was busied at park time.
    /// </summary>
    internal void ResolveParked(PendingReaction pending, Modules.ReactionOptionSpec option)
    {
        lock (_engine.SyncRoot)
        {
            if (!_engine.World.HasObject(pending.ActorId) ||
                !_engine.World.HasObject(pending.DefenderId) ||
                (pending.Action.TargetId is not null && !_engine.World.HasObject(pending.Action.TargetId)) ||
                (pending.Action.AuxTargetId is not null && !_engine.World.HasObject(pending.Action.AuxTargetId)))
                return; // a participant is gone — the moment passes
            var agent = _engine.World.GetObject(pending.ActorId);
            // stale: the defender slipped out of reach during the window
            if (_engine.World.RoomOf(agent.Id).Id != _engine.World.RoomOf(pending.DefenderId).Id)
            {
                _engine.Memory.Record(agent, "The moment passes.");
                _engine.Reactions.RecordResolved(pending.ActorId, "The moment passes.");
                return;
            }
            // re-validate the actor's eligibility: the world moved on while
            // the reaction was pending — the actor may have been knocked
            // out, shoved prone, grabbed, or disarmed. ResolvePotential
            // enforces posture/carried/incapacitation rules; a parked
            // action that is no longer available fizzles.
            if (!_engine.ActionResolver.ResolvePotential(agent).Any(a =>
                    a.Verb == pending.Action.Verb && a.TargetId == pending.Action.TargetId &&
                    a.AuxTargetId == pending.Action.AuxTargetId))
            {
                _engine.Memory.Record(agent, "The moment passes.");
                _engine.Reactions.RecordResolved(pending.ActorId, "The moment passes.");
                return;
            }
            var departureRoomId = _engine.World.RoomOf(agent.Id).Id;
            var defender = _engine.World.GetObject(pending.DefenderId);
            // the target's pre-handler name, for the same reason as
            // PerformAction: handlers may rename what they touch
            var targetNameBefore =
                pending.Action.TargetId is not null && _engine.World.HasObject(pending.Action.TargetId)
                    ? _engine.World.GetObject(pending.Action.TargetId).Name
                    : null;
            // the choice itself is otherwise invisible to the actor (their
            // own signals never reach them): report it ahead of the outcome
            if (option.Report is not null)
            {
                var report = FormatReactionReport(
                    agent, option.Report, defender, AuxName(pending.Action.AuxTargetId));
                _engine.Memory.Record(agent, report);
                _engine.Reactions.RecordResolved(pending.ActorId, report);
            }
            // the defender's choice is felt and shown BEFORE the outcome
            // resolves ("You try to dodge the blow." ahead of the hit) —
            // SendTo both records it to their memory and queues it for
            // display; the actor sees the option's report instead
            if (option.Text is not null)
                _engine.SignalBus.SendTo(defender, option.Text);
            var result = EvaluateGates(agent, pending.Action, pending.Text, LookupAffordance(pending.Action))
                         ?? EvaluateCheck(agent, pending.Action, option)
                         ?? Execute(agent, pending.Action.HandlerId, pending.Action.TargetId,
                             pending.Text, pending.Action.Verb, option, pending.Action.AuxTargetId,
                             LookupAffordance(pending.Action)?.Data);
            if (result.EndsGame)
                _engine.GameOver ??= result.Message;
            RecordOutcome(agent, pending.Action, result.Message);
            // the actor isn't an observer of their own signals — record
            // the outcome separately so the UI can show it to them
            _engine.Reactions.RecordResolved(pending.ActorId, result.Message);
            if (result.Outcome == ActionOutcome.Noop)
                return;
            if (result.Success)
                EmitSignals(agent, pending.Action, pending.Text, departureRoomId,
                    targetName: targetNameBefore);
            else
                EmitFailSignals(agent, pending.Action);
        }
    }

    /// <summary>
    /// Render an option's actor-facing report: {agent} is the reacting
    /// defender ("the arena duelist"), {target} is the actor — "you", since
    /// the report is shown to (and remembered by) the actor; {item} is a
    /// two-object verb's aux target. The result is sentence-capitalized
    /// ("The arena duelist attempts to dodge.").
    /// </summary>
    private string FormatReactionReport(WorldObject actor, string template, WorldObject defender, string? itemName)
    {
        // the report goes to the ACTOR: the reacting defender renders by
        // the name the actor can print
        var text = template
            .Replace("{agent}", Perception.WithDefiniteArticle(
                Actions.Knowledge.NameFor(_engine.ModuleRegistry, actor, defender)), StringComparison.Ordinal)
            .Replace("{target}", "you", StringComparison.Ordinal)
            .Replace("{item}", itemName ?? "", StringComparison.Ordinal);
        return string.Concat(text[..1].ToUpperInvariant(), text.AsSpan(1));
    }

    /// <summary>The name of a two-object verb's aux target, or "" when unset.</summary>
    private string AuxName(string? auxTargetId) =>
        auxTargetId is not null && _engine.World.HasObject(auxTargetId)
            ? _engine.World.GetObject(auxTargetId).Name
            : "";

    /// <summary>
    /// Record the actor's own action outcome into their memory at high
    /// salience (your own deeds outlive overheard chatter). An
    /// affordance-level `salience` overrides the boost — conversation
    /// verbs declare a high one so your side of an exchange sticks. Idle
    /// filler is the exception: waiting carries a small penalty instead,
    /// so idle turns age out before even ambient chatter. Look is too
    /// verbose to store verbatim; look and examine are state snapshots —
    /// keyed so only the freshest of each subject survives.
    /// </summary>
    private void RecordOutcome(WorldObject agent, AvailableAction action, string message) =>
        _engine.Memory.Record(agent,
            action.Verb == "look" ? "You look around." : message,
            action.Verb switch
            {
                "look" => "look",
                "examine" => $"examine:{action.TargetId}",
                _ => null,
            },
            salience: action.Verb == "wait"
                ? IdleSaliencePenalty
                : LookupAffordance(action)?.Salience
                  ?? _engine.Memory.SalienceBoostOf(agent));

    /// <summary>Idle turns age out before ambient chatter (salience 0).</summary>
    private const int IdleSaliencePenalty = -2;

    /// <summary>Execute a handler by id without advancing the turn.</summary>
    public ActionResult Execute(
        WorldObject agent, string handlerId, string? targetId = null, string? text = null,
        string? verb = null, Modules.ReactionOptionSpec? reaction = null, string? auxTargetId = null,
        IReadOnlyDictionary<string, string>? affordanceData = null)
    {
        lock (_engine.SyncRoot)
        {
            var handler = _engine.HandlerRegistry.Get(handlerId);
            return handler.Execute(
                BuildContext(agent, targetId, text, verb, auxTargetId, reaction, affordanceData));
        }
    }

    private ActionContext BuildContext(
        WorldObject agent, string? targetId, string? text, string? verb,
        string? auxTargetId, Modules.ReactionOptionSpec? reaction,
        IReadOnlyDictionary<string, string>? affordanceData = null) =>
        new()
        {
            World = _engine.World,
            Modules = _engine.ModuleRegistry,
            Signals = _engine.SignalBus,
            Memory = _engine.Memory,
            Agent = agent,
            Target = targetId is not null && _engine.World.HasObject(targetId)
                ? _engine.World.GetObject(targetId)
                : null,
            AuxTarget = auxTargetId is not null && _engine.World.HasObject(auxTargetId)
                ? _engine.World.GetObject(auxTargetId)
                : null,
            Verb = verb,
            Random = _engine.Random,
            Reaction = reaction,
            Data = affordanceData,
            Args = text is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["text"] = text },
        };

    /// <summary>
    /// Evaluate an affordance's execution gates. Returns null when there
    /// are no gates or all pass; a blocked gate returns a Failure result
    /// (failText to the actor, turn consumed via the caller's normal
    /// failure tail, failSignals emitted). Gates run before reaction
    /// parking and the check roll — prerequisites before dice.
    /// </summary>
    private ActionResult? EvaluateGates(
        WorldObject agent, AvailableAction action, string? text,
        Modules.AffordanceDefinition? affordance)
    {
        if (affordance?.Gates is not { Count: > 0 } specs)
            return null;
        var ctx = BuildContext(agent, action.TargetId, text, action.Verb, action.AuxTargetId, null);
        foreach (var spec in specs)
        {
            var gate = _engine.GateRegistry.Get(spec.Kind);
            if (!gate.Blocks(ctx, spec))
                continue;
            var targetName = ctx.Target is not null
                ? $" {Perception.WithDefiniteArticle(ctx.Target.Name)}"
                : "";
            return ActionResult.Fail(
                spec.FailText ?? $"You can't {action.Verb}{targetName} right now.");
        }
        return null;
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
            if (_engine.GameOver is not null)
                return; // the game has ended — nobody else acts
            _engine.Reactions.PollPolicies(); // NPC defenders' reaction choices land here
            var fullLodRooms = FullLodRooms();
            var lodFactor = NpcLodFactor();
            foreach (var agentId in NpcAgentIds())
            {
                if (!_engine.World.HasObject(agentId))
                {
                    _inFlightSelections.Remove(agentId); // destroyed mid-selection
                    continue;
                }
                var agent = _engine.World.GetObject(agentId);
                if (Actions.Health.IsIncapacitated(_engine.World, _engine.ModuleRegistry, agent))
                    continue; // unconscious agents get no turn
                // "about to say something" only lasts while the speech
                // track is actually occupied
                if (Turn >= _speechBusyUntil.GetValueOrDefault(agentId))
                    ClearPendingSpeech(agentId);
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
                        a.Verb == chosen.Verb && a.TargetId == chosen.TargetId &&
                        a.AuxTargetId == chosen.AuxTargetId);
                    if (action is null)
                        continue; // stale choice — discard, fresh selection next turn
                    PerformAction(agent, action, chosen.Text);
                    // turn-based: the round allows one speech AND one body
                    // action — start the companion slot (a random policy
                    // resolves inline; an LLM plan lands on a later pump)
                    if (_engine.TimeMode == TimeMode.TurnBased)
                        StartCompanionSlot(policy, agent, speechSlot: !IsSpeech(action));
                    continue;
                }
                // the companion slot (say-alongside-act): complete it the
                // same way the primary selection completes. A slot that
                // yielded (nothing to say) doesn't consume anything —
                // the round-start flow below still runs
                if (_inFlightSlots.TryGetValue(agentId, out var slot))
                {
                    if (!slot.Selection.IsCompleted)
                        continue;
                    _inFlightSlots.Remove(agentId);
                    var chosen = slot.Selection.IsCompletedSuccessfully ? slot.Selection.Result : null;
                    var match = chosen is null ? null : SlotChoices(agent, slot.SpeechSlot)
                        .FirstOrDefault(a => a.Verb == chosen.Verb && a.TargetId == chosen.TargetId &&
                                             a.AuxTargetId == chosen.AuxTargetId);
                    if (match is not null)
                    {
                        PerformAction(agent, match, chosen!.Text);
                        continue;
                    }
                }

                // LOD: agents nobody can perceive (not in a player's room or
                // an adjacent one) start new work only every npcLodFactor
                // turns — in-flight decisions always run to completion. With
                // no player in the world, everything is full LOD. The
                // stagger keys on the round clock (the turn counter jumps
                // several times per round now that every action advances it)
                if (lodFactor > 1 && fullLodRooms.Count > 0 &&
                    !fullLodRooms.Contains(_engine.World.RoomOf(agentId).Id) &&
                    RoundClock % lodFactor != StableOffset(agentId, lodFactor))
                    continue;

                // no selection in flight: busy agents skip (idle backoff is
                // interruptible — new signals wake the agent to start deciding)
                if (IsBusy(agentId) && !CanWake(agentId))
                    continue;
                // one NPC round per player action (turn-based) or per turn
                // (real-time): once an agent has started their selection
                // this round, later pumps only complete what's in flight
                if (RoundConsumed(agentId))
                    continue;
                var availableActions = _engine.ActionResolver.Resolve(agent);
                ConsumeRound(agentId);
                _inFlightSelections[agentId] =
                    policy.ChooseActionAsync(_engine, agent, availableActions, CancellationToken.None);
                // deciding counts as this agent's turn
            }
        }
    }

    /// <summary>Per-agent companion-slot selections, keyed by agent id.</summary>
    private sealed record SlotSelection(
        Task<AvailableAction?> Selection, bool SpeechSlot);

    private readonly Dictionary<string, SlotSelection> _inFlightSlots = new(StringComparer.Ordinal);

    /// <summary>
    /// The NPC round each agent last consumed: in turn-based mode the
    /// value is the round epoch (bumped by <see cref="NewNpcRound"/> once
    /// per player action — NPC actions advance the turn counter but never
    /// grant new NPC rounds); in real-time mode it is the turn itself
    /// (each tick is a round).
    /// </summary>
    private readonly Dictionary<string, int> _roundStartedAt = new(StringComparer.Ordinal);

    private int _roundEpoch;

    /// <summary>
    /// Grant a fresh NPC round (turn-based): called once per player
    /// action — a typed command, a Wait, an auto-played step — so every
    /// autonomous agent gets exactly one round of (up to) one speech and
    /// one body action between player inputs.
    /// </summary>
    public void NewNpcRound()
    {
        lock (_engine.SyncRoot)
        {
            _roundEpoch++;
        }
    }

    private bool RoundConsumed(string agentId) =>
        _roundStartedAt.TryGetValue(agentId, out var at) &&
        at == RoundClock;

    private void ConsumeRound(string agentId) =>
        _roundStartedAt[agentId] = RoundClock;

    /// <summary>
    /// The clock rounds stagger on: the round epoch in turn-based mode
    /// (bumped once per player action), the turn itself in real-time
    /// (each tick is a round).
    /// </summary>
    private int RoundClock =>
        _engine.TimeMode == TimeMode.TurnBased ? _roundEpoch : Turn;

    private bool IsSpeech(AvailableAction action) =>
        LookupAffordance(action)?.Speech == true;

    /// <summary>
    /// The actions offered to a companion slot: only speech, or only
    /// non-speech, depending on what the primary action was.
    /// </summary>
    private List<AvailableAction> SlotChoices(WorldObject agent, bool speechSlot) =>
        _engine.ActionResolver.Resolve(agent)
            .Where(a => IsSpeech(a) == speechSlot)
            .ToList();

    private void StartCompanionSlot(Policies.IAgentPolicy policy, WorldObject agent, bool speechSlot)
    {
        // the handler may have destroyed the actor (an NPC's own depart)
        if (!_engine.World.HasObject(agent.Id))
            return;
        var choices = SlotChoices(agent, speechSlot);
        if (choices.Count == 0)
            return;
        var selection = policy.ChooseActionAsync(_engine, agent, choices, CancellationToken.None);
        _inFlightSlots[agent.Id] = new SlotSelection(selection, speechSlot);
        // synchronous policies (random, an already-decided LLM line) land
        // at once; async ones complete on a later pump
        if (selection.IsCompletedSuccessfully && selection.Result is { } chosen)
        {
            var match = choices.FirstOrDefault(a =>
                a.Verb == chosen.Verb && a.TargetId == chosen.TargetId &&
                a.AuxTargetId == chosen.AuxTargetId);
            if (match is not null)
            {
                _inFlightSlots.Remove(agent.Id);
                PerformAction(agent, match, chosen.Text);
            }
        }
    }

    /// <summary>
    /// Rooms at full level of detail: every player-controlled agent's room
    /// plus adjacent rooms (linked by a portal in either direction). No
    /// players in the world → everything is full LOD.
    /// </summary>
    private HashSet<string> FullLodRooms()
    {
        var rooms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var obj in _engine.World.Objects.Values)
        {
            if (!obj.HasModule("agent") ||
                (_engine.ModuleRegistry.ResolveString(obj, "agent", "policy") ?? "player") != "player")
                continue;
            var roomId = _engine.World.RoomOf(obj.Id).Id;
            rooms.Add(roomId);
            foreach (var portal in _engine.World.Objects.Values)
            {
                if (!portal.HasModule("portal"))
                    continue;
                var to = _engine.ModuleRegistry.ResolveString(portal, "portal", "to");
                if (portal.Parent == roomId && to is not null)
                    rooms.Add(to);
                if (to == roomId && portal.Parent.Length > 0)
                    rooms.Add(portal.Parent); // one-way portal inbound to a player's room
            }
        }
        return rooms;
    }

    /// <summary>The scenario's remote-NPC action divisor (rules module field npcLodFactor, default 10).</summary>
    private int NpcLodFactor()
    {
        var host = Actions.Checks.RulesHost(_engine.World);
        return host is null ? 10
            : Math.Max(1, _engine.ModuleRegistry.ResolveInt(host, "rules", "npcLodFactor", 10));
    }

    /// <summary>A deterministic per-agent stagger (string hashes are process-randomized).</summary>
    private static int StableOffset(string agentId, int factor)
    {
        var hash = 0;
        foreach (var c in agentId)
            hash = hash * 31 + c;
        return (hash & 0x7fffffff) % factor;
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
    private void EmitSignals(
        WorldObject agent, AvailableAction action, string? text,
        string departureRoomId, string? holderBefore = null, string? targetName = null)
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
        _engine.SignalBus.Emit(agent, target, affordance.Signals, text, traversal,
            Extras(holderBefore, action.AuxTargetId), targetName);
    }

    /// <summary>
    /// Extra template placeholders: {container} — " from the cupboard" when
    /// the action's target came out of a thing (container, furniture), not
    /// off the floor (a room) or out of somebody (an agent); {item} — the
    /// name of a two-object verb's aux target (the gift for give, the
    /// stowed item for put). Both format to empty when unset.
    /// </summary>
    private IReadOnlyDictionary<string, string>? Extras(string? holderId, string? auxTargetId)
    {
        Dictionary<string, string>? extra = null;
        if (holderId is not null && _engine.World.HasObject(holderId))
        {
            var holder = _engine.World.GetObject(holderId);
            if (!holder.HasModule("room") && !holder.HasModule("agent"))
                (extra ??= new(StringComparer.Ordinal))["{container}"] = $" from the {holder.Name}";
        }
        if (auxTargetId is not null && _engine.World.HasObject(auxTargetId))
            (extra ??= new(StringComparer.Ordinal))["{item}"] = _engine.World.GetObject(auxTargetId).Name;
        return extra;
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

    /// <summary>
    /// Emit the affordance's failSignals for a failed action (a failed
    /// check or a failed handler — e.g. a missed attack, a botched
    /// pickpocketing). Failures are otherwise silent to observers.
    /// </summary>
    private void EmitFailSignals(WorldObject agent, AvailableAction action)
    {
        var affordance = LookupAffordance(action);
        if (affordance is null || affordance.FailSignals.Count == 0)
            return;
        var target = action.TargetId is not null && _engine.World.HasObject(action.TargetId)
            ? _engine.World.GetObject(action.TargetId)
            : null;
        _engine.SignalBus.Emit(agent, target, affordance.FailSignals, null, null,
            Extras(null, action.AuxTargetId));
    }

    /// <summary>The turn until which the agent is busy with its current action (0 = free). For tooling/tests.</summary>
    public int BusyUntilTurn(string agentId) =>
        _busyUntil.TryGetValue(agentId, out var until) ? until : 0;

    /// <summary>The turn until which the agent's speech track is busy (0 = free). For policies/tooling/tests.</summary>
    public int SpeechBusyUntilTurn(string agentId) =>
        _speechBusyUntil.TryGetValue(agentId, out var until) ? until : 0;

    /// <summary>Return and clear the agent's queued action-outcome messages (their own results, for auto-play spectating).</summary>
    public IReadOnlyList<string> DrainOutcomes(string agentId)
    {
        if (!_outcomes.TryGetValue(agentId, out var queue) || queue.Count == 0)
            return [];
        var drained = queue.ToArray();
        queue.Clear();
        return drained;
    }

    private void QueueOutcome(string agentId, string message)
    {
        if (!_outcomes.TryGetValue(agentId, out var queue))
            _outcomes[agentId] = queue = new Queue<string>();
        if (queue.Count >= MaxQueuedOutcomes)
            queue.Dequeue();
        queue.Enqueue(message);
    }

    /// <summary>
    /// Evaluate the affordance's stat/skill check, if any. Returns null
    /// when there is no check or the check passes (the handler then runs);
    /// a failed check returns a Failure result without running the handler
    /// — it still consumes the turn and records memory via the caller.
    /// Opposed checks roll the defender (the target agent, or the agent
    /// holding the target item) against the actor.
    /// </summary>
    private ActionResult? EvaluateCheck(
        WorldObject agent, AvailableAction action, Modules.ReactionOptionSpec? reaction = null)
    {
        if (LookupAffordance(action)?.Check is not { } check)
            return null;
        var target = action.TargetId is not null && _engine.World.HasObject(action.TargetId)
            ? _engine.World.GetObject(action.TargetId)
            : null;
        int margin;
        if (check.Opposed is not null)
        {
            var defender = Checks.OpposedDefender(_engine.World, agent, target);
            if (defender is null)
                return ActionResult.Fail("Nothing opposes your attempt.");
            margin = Checks.EvaluateOpposed(
                _engine.World, _engine.ModuleRegistry, _engine.Random, agent, check, defender,
                reaction);
        }
        else
        {
            margin = Checks.Evaluate(_engine.World, _engine.ModuleRegistry, _engine.Random, agent, check);
        }
        if (margin >= 0)
            return null;
        return ActionResult.Fail(check.FailText ?? (target is not null
            ? $"You try to {action.Verb} {Perception.WithDefiniteArticle(target.Name)}, but fail."
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

    /// <summary>
    /// Advance ambient emission timers (a cursed mark burning, a charm
    /// tingling) by <paramref name="seconds"/> and fire any that come due.
    /// An object with the `ambient` module periodically sends one of its
    /// `texts` variants to the agent holding it. Timing is in seconds of
    /// time actually passing: real-time ticks advance every object, while
    /// in turn-based mode each action advances only the acting agent's own
    /// held objects by the action's duration — so adding more NPCs doesn't
    /// speed up the player's emissions. When <paramref name="onlyHolderId"/>
    /// is set, only objects held by that agent advance. The delay between
    /// emissions is re-rolled each time from the `interval` spec.
    /// </summary>
    private void AdvanceAmbient(int seconds, string? onlyHolderId)
    {
        foreach (var obj in _engine.World.Objects.Values)
        {
            if (!obj.HasModule("ambient"))
                continue;
            WorldObject? holder = obj.Parent.Length > 0 && _engine.World.HasObject(obj.Parent)
                ? _engine.World.GetObject(obj.Parent)
                : null;
            if (holder is null || !holder.HasModule("agent"))
                continue; // the timer only runs while an agent holds it
            if (onlyHolderId is not null && holder.Id != onlyHolderId)
                continue;
            if (!_ambientNextDue.TryGetValue(obj.Id, out var due))
                due = RollAmbientDelay(obj); // first fire after one interval
            var elapsed = _ambientElapsed.GetValueOrDefault(obj.Id) + seconds;
            if (elapsed < due)
            {
                _ambientElapsed[obj.Id] = elapsed;
                _ambientNextDue[obj.Id] = due;
                continue;
            }
            _ambientElapsed[obj.Id] = 0;
            _ambientNextDue[obj.Id] = RollAmbientDelay(obj);
            var texts = _engine.ModuleRegistry.ResolveStringList(obj, "ambient", "texts") ?? [];
            if (texts.Count > 0)
                _engine.SignalBus.SendTo(holder, texts[_engine.Random.Next(texts.Count)]);
        }
    }

    /// <summary>
    /// Roll the next ambient delay from the `interval` spec:
    /// a plain number (fixed period) or { "min": n, "max": n } (uniform
    /// random between them, re-rolled per emission).
    /// </summary>
    private int RollAmbientDelay(WorldObject obj)
    {
        var min = 8;
        var max = 8;
        switch (_engine.ModuleRegistry.ResolveField(obj, "ambient", "interval"))
        {
            case { ValueKind: System.Text.Json.JsonValueKind.Number } e:
                min = max = e.GetInt32();
                break;
            case { ValueKind: System.Text.Json.JsonValueKind.Object } e:
                if (e.TryGetProperty("min", out var mn) && mn.ValueKind == System.Text.Json.JsonValueKind.Number)
                    min = mn.GetInt32();
                if (e.TryGetProperty("max", out var mx) && mx.ValueKind == System.Text.Json.JsonValueKind.Number)
                    max = mx.GetInt32();
                break;
        }
        min = Math.Max(1, min);
        max = Math.Max(min, max);
        return min + _engine.Random.Next(max - min + 1);
    }

    /// <summary>
    /// Record what the agent is busy doing, as an observable world field
    /// (agent-module `activity`) — "Maya (massaging Alex's shoulders)" in
    /// listings, "Maya is massaging Alex's shoulders." on examine. Only
    /// actions of 3+ seconds register: briefer gestures are over before
    /// they're worth reporting. Cleared when the busy spell expires.
    /// </summary>
    private void RecordActivity(
        WorldObject agent, AvailableAction action,
        Modules.AffordanceDefinition? affordance, int duration)
    {
        // the handler may have destroyed the actor (an NPC's own depart)
        if (duration < 3 || !agent.HasModule("agent") || !_engine.World.HasObject(agent.Id))
            return;
        var target = action.TargetId is not null && _engine.World.HasObject(action.TargetId)
            ? _engine.World.GetObject(action.TargetId)
            : null;
        // part targets render possessively ("Sam's shoulders"); agents by
        // the name the actor can print; plain objects with an article
        string targetName;
        if (target is null)
            targetName = "";
        else if (target.HasModule("bodypart") && target.Parent.Length > 0 &&
                 _engine.World.HasObject(target.Parent) &&
                 _engine.World.GetObject(target.Parent).HasModule("agent"))
            targetName = Actions.Knowledge.NameFor(
                _engine.ModuleRegistry, agent, _engine.World.GetObject(target.Parent)) +
                $"'s {target.Name}";
        else if (target.HasModule("agent"))
            targetName = Actions.Knowledge.NameFor(_engine.ModuleRegistry, agent, target);
        else
            targetName = Actions.Perception.WithDefiniteArticle(target.Name);
        var described = affordance?.Activity is { Length: > 0 } custom
            ? custom.Replace("{target}", targetName, StringComparison.Ordinal)
            : Gerund(action.Verb) + (targetName.Length > 0 ? " " + targetName : "");
        _engine.World.SetFieldOverride(agent.Id, "agent", "activity",
            World.World.ToJson(described));
        _activityUntil[agent.Id] = Turn + duration;
    }

    /// <summary>The activity a body-track action is keeping the agent busy with, or null when free.</summary>
    public string? ActivityOf(string agentId)
    {
        if (!_engine.World.HasObject(agentId))
            return null;
        var agent = _engine.World.GetObject(agentId);
        var activity = _engine.ModuleRegistry.ResolveString(agent, "agent", "activity") ?? "";
        return activity.Length > 0 ? activity : null;
    }

    /// <summary>
    /// A naive English gerund for the fallback activity text:
    /// drop a trailing 'e' ("massage" → "massaging"), turn trailing "ie"
    /// into "ying" ("lie" → "lying"), and double a single trailing
    /// consonant after a single vowel ("sit" → "sitting"). Data's
    /// <c>activity</c> overrides always win for irregular verbs.
    /// </summary>
    private static string Gerund(string verb)
    {
        if (verb.EndsWith("ie", StringComparison.Ordinal))
            return verb[..^2] + "ying";
        if (verb.EndsWith('e'))
            return verb[..^1] + "ing";
        if (verb.Length >= 3 &&
            !IsVowel(verb[^3]) && IsVowel(verb[^2]) && !IsVowel(verb[^1]) &&
            verb[^1] is not ('w' or 'x' or 'y'))
            return verb + verb[^1] + "ing";
        return verb + "ing";
    }

    private static bool IsVowel(char c) => c is 'a' or 'e' or 'i' or 'o' or 'u';

    /// <summary>
    /// Mark that the agent is holding words back until their speech track
    /// frees (an LLM plan's next line is speech; the previous line is
    /// still pacing) — rendered as "about to say something" rather than
    /// claiming they're talking, which isn't interesting.
    /// </summary>
    public void NotePendingSpeech(string agentId)
    {
        if (_engine.World.HasObject(agentId) &&
            _engine.World.GetObject(agentId).HasModule("agent"))
            _engine.World.SetFieldOverride(agentId, "agent", "speakingSoon",
                World.World.ToJson(true));
    }

    private void ClearPendingSpeech(string agentId)
    {
        if (_engine.World.HasObject(agentId) &&
            _engine.World.GetObject(agentId).HasModule("agent"))
            _engine.World.SetFieldOverride(agentId, "agent", "speakingSoon",
                World.World.ToJson(false));
    }

    private void ExpireActivity()
    {
        foreach (var (agentId, until) in _activityUntil.ToArray())
        {
            if (Turn < until)
                continue;
            _activityUntil.Remove(agentId);
            if (_engine.World.HasObject(agentId))
                _engine.World.SetFieldOverride(agentId, "agent", "activity",
                    World.World.ToJson(""));
        }
    }

    private void AdvanceTurn()
    {
        Turn++;
        ExpireActivity();
        _engine.Reactions.ExpireDue(Turn); // reaction deadlines pick the default
        // in real-time mode each tick is one second for everyone; in
        // turn-based mode ambient time is advanced per actor by
        // PerformAction (see AdvanceAmbient) so NPC count doesn't matter
        if (_engine.TimeMode == TimeMode.RealTime)
        {
            AdvanceAmbient(1, onlyHolderId: null);
            Metabolism.Advance(_engine, 1);
            Chatter.Advance(_engine, 1);
        }
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
