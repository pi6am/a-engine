using AEngine.Core.Modules;
using AEngine.Core.World;

namespace AEngine.Core.Actions;

/// <summary>
/// Shared helpers for built-in handlers: resolving open/locked state
/// through a portal's shared doorstate object (via stateRef) or an
/// object's own openable module.
/// </summary>
internal static class HandlerState
{
    /// <summary>
    /// Get the (stateObject, moduleId) that carries open/locked state for
    /// a target: the doorstate object for portals, the target itself for
    /// openables. Returns null when the target has no openable state.
    /// </summary>
    public static (WorldObject StateObject, string ModuleId)? GetOpenState(
        ActionContext ctx, WorldObject target) =>
        Perception.GetOpenState(ctx.World, ctx.Modules, target);

    public static bool IsOpen(ActionContext ctx, WorldObject target)
    {
        var state = GetOpenState(ctx, target);
        return state is not null &&
               ctx.Modules.ResolveBool(state.Value.StateObject, state.Value.ModuleId, "open");
    }

    public static bool IsLocked(ActionContext ctx, WorldObject target)
    {
        var state = GetOpenState(ctx, target);
        return state is not null &&
               ctx.Modules.ResolveBool(state.Value.StateObject, state.Value.ModuleId, "locked");
    }

    public static void SetOpen(ActionContext ctx, WorldObject target, bool open)
    {
        var state = GetOpenState(ctx, target)
            ?? throw new InvalidOperationException($"'{target.Id}' has no openable state.");
        ctx.World.SetFieldOverride(state.StateObject.Id, state.ModuleId, "open", World.World.ToJson(open));
    }

    public static void SetLocked(ActionContext ctx, WorldObject target, bool locked)
    {
        var state = GetOpenState(ctx, target)
            ?? throw new InvalidOperationException($"'{target.Id}' has no openable state.");
        ctx.World.SetFieldOverride(state.StateObject.Id, state.ModuleId, "locked", World.World.ToJson(locked));
    }

    /// <summary>The agent's current room (its parent object).</summary>
    public static WorldObject RoomOf(ActionContext ctx) =>
        ctx.World.GetObject(ctx.Agent.Parent);

    /// <summary>True if the agent holds the object in its inventory.</summary>
    public static bool IsHeld(ActionContext ctx, WorldObject obj) =>
        obj.Parent == ctx.Agent.Id;
}
