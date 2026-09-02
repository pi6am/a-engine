using AEngine.Core.Actions;
using AEngine.Core.Modules;

namespace AEngine.Core.Runtime;

/// <summary>
/// A telegraphed action waiting on the target agent's reaction. The actor
/// is already committed (busy); the parked action resolves when the
/// defender picks an option, when their policy answers (NPCs), or when the
/// deadline passes (the default option).
/// </summary>
public sealed class PendingReaction
{
    public required int Id { get; init; }
    public required string ActorId { get; init; }
    public required string DefenderId { get; init; }
    /// <summary>The parked action, resolved when the reaction lands.</summary>
    public required AvailableAction Action { get; init; }
    public string? Text { get; init; }
    /// <summary>Defender-relative announcement ("the arena duelist swings at you!").</summary>
    public required string Announcement { get; init; }
    /// <summary>The options the defender can pick (availability-filtered).</summary>
    public required IReadOnlyList<ReactionOptionSpec> Options { get; init; }
    public required ReactionOptionSpec DefaultOption { get; init; }
    /// <summary>The turn at which the default option applies.</summary>
    public required int DeadlineTurn { get; init; }
    /// <summary>An NPC defender's in-flight policy selection (option id; null = default).</summary>
    public Task<string?>? PolicySelection { get; set; }
}

/// <summary>
/// Quick-time events / reactions. Affordances with a `reaction` spec park
/// in <see cref="TurnManager.PerformAction"/> instead of resolving at
/// once; this manager tracks the pending reactions and drives resolution
/// — by explicit choice (the player's UI), by policy task (NPCs), or by
/// deadline (the data-driven default). All methods take SyncRoot so UI
/// threads can call them directly; the engine calls them with the lock
/// already held (reentrant).
/// </summary>
public sealed class ReactionManager
{
    private readonly GameEngine _engine;
    private readonly List<PendingReaction> _pending = [];
    private readonly List<(string ActorId, string Message)> _resolved = [];
    // the last option each defender explicitly chose per (verb, actor) —
    // becomes the effective default while it's still available
    private readonly Dictionary<(string DefenderId, string Verb, string ActorId), string> _remembered = new();
    private int _nextId;

    public ReactionManager(GameEngine engine) => _engine = engine;

    /// <summary>Snapshot of all pending reactions (for tooling).</summary>
    public IReadOnlyList<PendingReaction> Pending
    {
        get { lock (_engine.SyncRoot) return _pending.ToArray(); }
    }

    /// <summary>
    /// Outcome messages of resolved reactions ("You hit the arena duelist
    /// for 6 damage."). Signals exclude the actor, so without these the
    /// actor never sees how their telegraphed action landed. UIs drain
    /// this and show the actor their own results.
    /// </summary>
    public IReadOnlyList<(string ActorId, string Message)> DrainResolved()
    {
        lock (_engine.SyncRoot)
        {
            var drained = _resolved.ToArray();
            _resolved.Clear();
            return drained;
        }
    }

    // called by TurnManager.ResolveParked with the lock held
    internal void RecordResolved(string actorId, string message) =>
        _resolved.Add((actorId, message));

    /// <summary>The oldest pending reaction awaiting this defender, if any.</summary>
    public PendingReaction? PendingFor(string defenderId)
    {
        lock (_engine.SyncRoot)
            return _pending.FirstOrDefault(p => p.DefenderId == defenderId);
    }

    internal PendingReaction Add(
        string actorId, string defenderId, AvailableAction action, string? text,
        string announcement, IReadOnlyList<ReactionOptionSpec> options, int deadlineTurn)
    {
        var pending = new PendingReaction
        {
            Id = ++_nextId,
            ActorId = actorId,
            DefenderId = defenderId,
            Action = action,
            Text = text,
            Announcement = announcement,
            Options = options,
            DefaultOption = options.FirstOrDefault(o => o.Default) ?? options[0],
            DeadlineTurn = deadlineTurn,
        };
        _pending.Add(pending);
        return pending;
    }

    /// <summary>
    /// The option that applies when the defender doesn't choose: their
    /// remembered last choice for this (verb, actor) when it's still
    /// available, else the first option whose DefaultWhen field condition
    /// holds on the defender (dynamic defaults — a date melts into a
    /// touch when comfortable and warmed up), else the configured default.
    /// </summary>
    public ReactionOptionSpec EffectiveDefault(PendingReaction pending)
    {
        lock (_engine.SyncRoot)
        {
            if (_remembered.TryGetValue(
                    (pending.DefenderId, pending.Action.Verb, pending.ActorId), out var id) &&
                pending.Options.FirstOrDefault(o => o.Id == id) is { } remembered)
                return remembered;
            if (_engine.World.HasObject(pending.DefenderId))
            {
                var defender = _engine.World.GetObject(pending.DefenderId);
                var conditional = pending.Options.FirstOrDefault(o =>
                    o.DefaultWhen is { } when &&
                    WhenSpecEval.Matches(_engine.ModuleRegistry, defender, when));
                if (conditional is not null)
                    return conditional;
            }
            return pending.DefaultOption;
        }
    }

    /// <summary>Resolve a pending reaction with the given option; false if already resolved.</summary>
    public bool Choose(int id, string optionId)
    {
        lock (_engine.SyncRoot)
        {
            var pending = _pending.FirstOrDefault(p => p.Id == id);
            var option = pending?.Options.FirstOrDefault(o =>
                o.Id.Equals(optionId, StringComparison.OrdinalIgnoreCase));
            if (pending is null || option is null)
                return false;
            _pending.Remove(pending);
            // explicit choices are remembered as the effective default
            _remembered[(pending.DefenderId, pending.Action.Verb, pending.ActorId)] = option.Id;
            _engine.TurnManager.ResolveParked(pending, option);
            return true;
        }
    }

    /// <summary>Resolve a pending reaction with its default option (timeout fallbacks).</summary>
    public void ForceDefault(int id)
    {
        lock (_engine.SyncRoot)
        {
            var pending = _pending.FirstOrDefault(p => p.Id == id);
            if (pending is null)
                return;
            _pending.Remove(pending);
            _engine.TurnManager.ResolveParked(pending, EffectiveDefault(pending));
        }
    }

    /// <summary>Apply the default to every pending reaction past its deadline.</summary>
    public void ExpireDue(int turn)
    {
        lock (_engine.SyncRoot)
        {
            foreach (var pending in _pending.Where(p => p.DeadlineTurn <= turn).ToArray())
            {
                _pending.Remove(pending);
                _engine.TurnManager.ResolveParked(pending, EffectiveDefault(pending));
            }
        }
    }

    /// <summary>
    /// Resolve NPC defenders whose policy selection has completed (the
    /// option the policy picked, or the default when it passed/failed).
    /// Policies that complete synchronously resolve immediately.
    /// </summary>
    public void PollPolicies()
    {
        lock (_engine.SyncRoot)
        {
            foreach (var pending in _pending.Where(p => p.PolicySelection is { } t && t.IsCompleted).ToArray())
            {
                _pending.Remove(pending);
                var chosenId = pending.PolicySelection!.IsCompletedSuccessfully
                    ? pending.PolicySelection.Result
                    : null;
                var option = chosenId is not null
                    ? pending.Options.FirstOrDefault(o =>
                        o.Id.Equals(chosenId, StringComparison.OrdinalIgnoreCase))
                    : null;
                _engine.TurnManager.ResolveParked(pending, option ?? pending.DefaultOption);
            }
        }
    }
}
