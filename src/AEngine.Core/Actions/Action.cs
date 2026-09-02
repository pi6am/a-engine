using AEngine.Core.Modules;
using AEngine.Core.World;

namespace AEngine.Core.Actions;

/// <summary>Context handed to an action handler.</summary>
public sealed class ActionContext
{
    public required World.World World { get; init; }
    public required ModuleRegistry Modules { get; init; }
    /// <summary>Signal delivery — handlers can emit observable outcomes beyond the affordance's declared specs (a trader's spoken refusal).</summary>
    public required Signals.SignalBus Signals { get; init; }
    /// <summary>Agent memory — handlers can record outcomes for agents other than the actor (a trader remembering their own refusal).</summary>
    public required Runtime.AgentMemory Memory { get; init; }
    public required WorldObject Agent { get; init; }
    public WorldObject? Target { get; init; }
    /// <summary>
    /// The affordance verb that was performed ("touch", "go", ...). Null
    /// when the handler was invoked without an affordance (e.g. scheduled
    /// actions via TurnManager.Execute).
    /// </summary>
    public string? Verb { get; init; }
    /// <summary>The engine's randomness source (checks, damage rolls).</summary>
    public Random? Random { get; init; }
    /// <summary>
    /// The reaction the target chose to this action (quick-time events),
    /// when the action was telegraphed and answered. Handler-rolled opposed
    /// checks (attack, remove) pass it to Checks.EvaluateOpposed.
    /// </summary>
    public ReactionOptionSpec? Reaction { get; init; }
    /// <summary>
    /// The second object of a two-object verb: the item for give/put
    /// (whose Target is the recipient/container). Null for one-object verbs.
    /// </summary>
    public WorldObject? AuxTarget { get; init; }
    /// <summary>
    /// The affordance's free string payload (Data) — answers for ask
    /// verbs, intensities for stimulation, targets for set-style verbs.
    /// Null when invoked without an affordance (scheduled actions).
    /// </summary>
    public IReadOnlyDictionary<string, string>? Data { get; init; }
    public IReadOnlyDictionary<string, string> Args { get; init; } =
        new Dictionary<string, string>();
}

/// <summary>How an action attempt ended.</summary>
public enum ActionOutcome
{
    /// <summary>The action changed the world.</summary>
    Success,
    /// <summary>
    /// The intended end state already held (e.g. unlocking an unlocked
    /// door): nothing happened, no time passes, not a failure.
    /// </summary>
    Noop,
    /// <summary>The action was attempted and failed.</summary>
    Failure,
}

/// <summary>Outcome of executing an action.</summary>
public sealed record ActionResult(ActionOutcome Outcome, string Message)
{
    /// <summary>True only when the action actually changed the world.</summary>
    public bool Success => Outcome == ActionOutcome.Success;

    /// <summary>
    /// Dynamic duration override (seconds/turns), e.g. say scales with the
    /// length of the speech. Null = the affordance's declared duration.
    /// </summary>
    public int? Duration { get; init; }

    /// <summary>
    /// True when this result ends the game (a completed rite's epilogue);
    /// TurnManager records the message on <see cref="GameEngine.GameOver"/>.
    /// </summary>
    public bool EndsGame { get; init; }

    public static ActionResult Ok(string message, int? duration = null) =>
        new(ActionOutcome.Success, message) { Duration = duration };
    public static ActionResult Noop(string message) => new(ActionOutcome.Noop, message);
    public static ActionResult Fail(string message) => new(ActionOutcome.Failure, message);
}

/// <summary>An action handler, addressable by string id.</summary>
public interface IActionHandler
{
    string Id { get; }
    ActionResult Execute(ActionContext context);
}

/// <summary>A menu entry produced by the ActionResolver.</summary>
public sealed record AvailableAction(
    string Verb, string? TargetId, string Label, string HandlerId,
    string ModuleId, string? Prompt = null)
{
    /// <summary>
    /// Optional free-text argument for prompted verbs (e.g. the words for
    /// 'say'), supplied by a UI or a policy; surfaced to the handler as
    /// ActionContext.Args["text"].
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    /// The second object of a two-object verb: the item for give/put
    /// (TargetId is the recipient/container). Surfaced to the handler as
    /// ActionContext.AuxTarget and to signal templates as {item}.
    /// </summary>
    public string? AuxTargetId { get; init; }
}
