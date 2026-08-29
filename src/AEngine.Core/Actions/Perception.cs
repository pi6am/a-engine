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
            var entry = NameFor(observer, child) + Annotate(world, modules, child);
            // agent conditions gather into one parenthetical list:
            // "the arena duelist (prone, incapacitated)"
            var conditions = new List<string>();
            if (child.HasModule("agent") &&
                Postures.Of(world, modules, child) == Postures.Prone)
                conditions.Add("prone");
            if (Health.IsIncapacitated(modules, child))
                conditions.Add("incapacitated");
            if (conditions.Count > 0)
                entry += $" ({string.Join(", ", conditions)})";
            items.Add(entry);
            if (child.HasModule("container") && IsOpen(world, modules, child))
            {
                foreach (var inner in world.ChildrenOf(child.Id))
                    items.Add($"{inner.Name} (in {child.Name})");
            }
            // occupants of furniture (or of a carrier) list like container
            // contents: "the old cook (sitting on the chair)"
            foreach (var occupant in world.ChildrenOf(child.Id))
            {
                if (occupant.Id == agentId || !occupant.HasModule("agent"))
                    continue;
                var posture = Postures.Of(world, modules, occupant);
                var where = posture == Postures.Carried
                    ? $"carried by {child.Name}"
                    : $"{posture} on the {child.Name}";
                items.Add($"{NameFor(observer, occupant)} ({where})");
            }
        }
        // agents the observer is carrying (a grappled victim) list like
        // furniture occupants: "the arena duelist (carried by you)"
        foreach (var carried in world.ChildrenOf(agentId))
        {
            if (carried.HasModule("agent"))
                items.Add($"{NameFor(observer, carried)} (carried by you)");
        }
        return items;
    }

    /// <summary>
    /// One line per visibly dressed agent in the room (top-level agents and
    /// furniture occupants, the observer included):
    /// "the old cook is wearing an apron, a chef's hat." /
    /// "You are wearing an apron." Placeholder for per-agent detail until an
    /// examine verb exists; the "You see:" listing stays compact.
    /// </summary>
    public static List<string> DressedLines(
        World.World world, ModuleRegistry modules, WorldObject room, string observerId)
    {
        var lines = new List<string>();
        foreach (var child in world.ChildrenOf(room.Id))
        {
            if (child.HasModule("portal"))
                continue;
            AddLine(child);
            foreach (var occupant in world.ChildrenOf(child.Id))
                AddLine(occupant);
        }
        return lines;

        void AddLine(WorldObject obj)
        {
            if (!obj.HasModule("agent"))
                return;
            var worn = Clothing.WornItems(world, modules, obj);
            if (worn.Count == 0)
                return;
            var list = string.Join(", ", worn.Select(w => WithArticle(w.Name)));
            lines.Add(obj.Id == observerId
                ? $"You are wearing {list}."
                : $"{obj.Name} is wearing {list}.");
        }
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

    /// <summary>"brass key" -> "a brass key"; "apron" -> "an apron"; names with an article stay as-is.</summary>
    public static string WithArticle(string name) =>
        name.StartsWith("the ", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("a ", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("an ", StringComparison.OrdinalIgnoreCase)
            ? name
            : (name.Length > 0 && "aeiou".Contains(char.ToLowerInvariant(name[0])) ? "an " : "a ") + name;

    /// <summary>"brass key" -> "the brass key"; names with an article stay as-is.</summary>
    public static string WithDefiniteArticle(string name) =>
        name.StartsWith("the ", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("a ", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("an ", StringComparison.OrdinalIgnoreCase)
            ? name
            : "the " + name;

    /// <summary>
    /// Sentence reporting the agent's own posture when not standing:
    /// "You are sitting on the chair." / "You are being carried by the
    /// guest." Null while standing (the unmarked default).
    /// </summary>
    public static string? PostureLine(World.World world, ModuleRegistry modules, WorldObject agent)
    {
        var posture = Postures.Of(world, modules, agent);
        if (posture == Postures.Standing)
            return null;
        var parent = world.GetObject(agent.Parent);
        return posture switch
        {
            Postures.Carried => $"You are being carried by {parent.Name}.",
            Postures.Prone => "You are prone on the ground.",
            _ => $"You are {posture} on the {parent.Name}.",
        };
    }

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
