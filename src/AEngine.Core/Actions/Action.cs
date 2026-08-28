using AEngine.Core.Modules;
using AEngine.Core.World;

namespace AEngine.Core.Actions;

/// <summary>Context handed to an action handler.</summary>
public sealed class ActionContext
{
    public required World.World World { get; init; }
    public required ModuleRegistry Modules { get; init; }
    public required WorldObject Agent { get; init; }
    public WorldObject? Target { get; init; }
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

    public static ActionResult Ok(string message) => new(ActionOutcome.Success, message);
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
}
