using AEngine.Core.Modules;
using AEngine.Core.World;

namespace AEngine.Core.Actions;

/// <summary>
/// Shared perception helpers: what the world looks like to an agent.
/// Used by the look/open handlers and the LLM context builder so every
/// consumer describes the world identically. Visibility rules: open/closed
/// state of openables is observable; a container's contents are visible
/// only while it is open; lock state is never observable.
/// </summary>
public static class Perception
{
    /// <summary>
    /// Get the (stateObject, moduleId) that carries open/locked state for
    /// a target: the shared doorstate object for portals (via stateRef),
    /// the target itself for openables. Null when the target has no
    /// openable state.
    /// </summary>
    public static (WorldObject StateObject, string ModuleId)? GetOpenState(
        World.World world, ModuleRegistry modules, WorldObject target)
    {
        if (target.HasModule("portal"))
        {
            var stateRef = modules.ResolveString(target, "portal", "stateRef");
            if (stateRef is null || !world.HasObject(stateRef))
                return null;
            return (world.GetObject(stateRef), "doorstate");
        }
        if (target.HasModule("openable"))
            return (target, "openable");
        return null;
    }

    public static bool IsOpen(World.World world, ModuleRegistry modules, WorldObject target)
    {
        var state = GetOpenState(world, modules, target);
        return state is not null &&
               modules.ResolveBool(state.Value.StateObject, state.Value.ModuleId, "open");
    }

    /// <summary>
    /// State annotation for a room listing: "" for plain objects,
    /// " (closed)" / " (open)" for openables.
    /// </summary>
    public static string Annotate(World.World world, ModuleRegistry modules, WorldObject obj)
    {
        var state = GetOpenState(world, modules, obj);
        if (state is null)
            return "";
        return modules.ResolveBool(state.Value.StateObject, state.Value.ModuleId, "open")
            ? " (open)"
            : " (closed)";
    }

    /// <summary>
    /// The visible contents of a room as flat listing entries: each
    /// top-level object (with state annotation), plus the contents of open
    /// containers as separate entries ("brass key (in desk drawer)").
    /// </summary>
    public static List<string> DescribeRoomContents(
        World.World world, ModuleRegistry modules, WorldObject room, string agentId)
    {
        var observer = world.GetObject(agentId);
        var items = new List<string>();
        foreach (var child in world.ChildrenOf(room.Id))
        {
            if (child.Id == agentId || child.HasModule("portal"))
                continue;
            items.Add(NameFor(observer, child) + Annotate(world, modules, child));
            if (child.HasModule("container") && IsOpen(world, modules, child))
            {
                foreach (var inner in world.ChildrenOf(child.Id))
                    items.Add($"{inner.Name} (in {child.Name})");
            }
        }
        return items;
    }

    /// <summary>
    /// Sentence reporting a container's contents, for the open action:
    /// "There is a brass key inside." / "It's empty."
    /// </summary>
    public static string ContentsSentence(World.World world, WorldObject container)
    {
        var contents = world.ChildrenOf(container.Id).ToList();
        return contents.Count == 0
            ? "It's empty."
            : $"There {(contents.Count == 1 ? "is" : "are")} " +
              string.Join(", ", contents.Select(c => WithArticle(c.Name))) + " inside.";
    }

    /// <summary>"brass key" -> "a brass key"; names with an article stay as-is.</summary>
    public static string WithArticle(string name) =>
        name.StartsWith("the ", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("a ", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("an ", StringComparison.OrdinalIgnoreCase)
            ? name
            : "a " + name;

    /// <summary>
    /// Observer-relative naming: every agent is the protagonist of their
    /// own perception, so an agent's own name renders as "you"; everyone
    /// else renders by their descriptive name. (Today the self case is
    /// mostly latent — agents are excluded from their own room listings
    /// and receive no self-signals — but any observer-relative rendering
    /// should go through this.)
    /// </summary>
    public static string NameFor(WorldObject observer, WorldObject obj) =>
        obj.Id == observer.Id ? "you" : obj.Name;
}
