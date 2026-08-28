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

/// <summary>Outcome of executing an action.</summary>
public sealed record ActionResult(bool Success, string Message)
{
    public static ActionResult Ok(string message) => new(true, message);
    public static ActionResult Fail(string message) => new(false, message);
}

/// <summary>An action handler, addressable by string id.</summary>
public interface IActionHandler
{
    string Id { get; }
    ActionResult Execute(ActionContext context);
}

/// <summary>A menu entry produced by the ActionResolver.</summary>
public sealed record AvailableAction(string Verb, string? TargetId, string Label, string HandlerId);
