using System.Text.Json;
using AEngine.Core.Modules;
using AEngine.Core.World;

namespace AEngine.Core.Actions;

/// <summary>
/// Shared perception helpers: what the world looks like to an agent.
/// Used by the look/open handlers and the LLM context builder so every
/// consumer describes the world identically. Visibility rules: open/closed
/// state of openables is observable; a container's contents are visible
/// while it is open — and a container with no openable mechanism (a
/// bird's nest, no lid to close) is always open and never annotated.
/// Lock state is never observable.
/// </summary>
public static class Perception
{
    /// <summary>
    /// Whether an object is currently hidden from perception: a
    /// `hideable` module with <c>concealed: true</c> — invisible under
    /// the rug, buried in the sand, not yet won. Effects flip the flag
    /// to reveal (see the effect vocabulary); until then the resolver
    /// offers nothing targeting it and look skips it entirely.
    /// </summary>
    public static bool IsConcealed(ModuleRegistry modules, WorldObject obj) =>
        obj.HasModule("hideable") &&
        modules.ResolveBool(obj, "hideable", "concealed");

    /// <summary>
    /// Whether a portal side offers open/close at all — any attached
    /// module with an `open` or `close` affordance. Bare passages (a
    /// staircase, a path) never show state; real doors do.
    /// </summary>
    public static bool IsClosable(ModuleRegistry modules, WorldObject portal) =>
        portal.Modules
            .Where(a => modules.Has(a.ModuleId))
            .SelectMany(a => modules.Get(a.ModuleId).Affordances)
            .Any(affordance => affordance.Verb is "open" or "close");

    /// <summary>
    /// One exit entry for a look/context exit listing:
    /// "north (front door, open)" for a closable side,
    /// "down (staircase)" for a passage that simply leads on. Lock state
    /// is never observable.
    /// </summary>
    public static string ExitLabel(
        World.World world, ModuleRegistry modules, WorldObject portal)
    {
        var dir = modules.ResolveString(portal, "portal", "direction") ?? "somewhere";
        if (!IsClosable(modules, portal))
            return $"{dir} ({portal.Name})";
        var state = IsOpen(world, modules, portal) ? "open" : "closed";
        return $"{dir} ({portal.Name}, {state})";
    }

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
        // a container with no openable mechanism has no lid to close —
        // a bird's nest is always open (surfaces already behave so)
        return state is null
            ? target.HasModule("container")
            : modules.ResolveBool(state.Value.StateObject, state.Value.ModuleId, "open");
    }

    /// <summary>
    /// Lock observability's mirror: whether the shared doorstate (or
    /// openable's own state) is currently locked. Like IsOpen, this is
    /// the one implementation — HandlerState delegates here.
    /// </summary>
    public static bool IsLocked(World.World world, ModuleRegistry modules, WorldObject target)
    {
        var state = GetOpenState(world, modules, target);
        return state is not null &&
               modules.ResolveBool(state.Value.StateObject, state.Value.ModuleId, "locked");
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
    /// <summary>
    /// The room's item listing lines, the original's model: every visible
    /// object that isn't scenery announces itself on every look — its
    /// FDESC while it still sits at its loaded spot (the scene-setting
    /// line reads true exactly as long as the scene holds), then its
    /// LDESC once the world has moved it, then the generic
    /// "There is a … here." Objects flagged <c>scenery</c> (the ZIL
    /// NDESCBIT — the trophy case, the rug: described by the room's own
    /// prose) never list. An open container groups its visible contents
    /// under "The … contains:" as bare article-names; a surface's
    /// contents list directly. Agents, portals, concealed objects, and
    /// the observer are never listed.
    /// </summary>
    public static List<string> RoomItemLines(
        World.World world, ModuleRegistry modules, WorldObject room, string agentId)
    {
        var lines = new List<string>();
        void Emit(WorldObject item)
        {
            var atOrigin = item.Attributes.TryGetValue("originParent", out var origin) &&
                           origin.ValueKind == JsonValueKind.String &&
                           origin.GetString() == item.Parent;
            lines.Add(atOrigin && item.FirstDescription is { Length: > 0 } first
                ? first
                : item.Description.Length > 0
                    ? item.Description
                    : $"There is a {item.Name} here.");
            if (item.HasModule("container") && IsOpen(world, modules, item))
            {
                var contents = world.ChildrenOf(item.Id)
                    .Where(c => !IsConcealed(modules, c)).ToList();
                if (contents.Count > 0)
                {
                    lines.Add($"The {item.Name} contains:");
                    foreach (var inner in contents)
                        lines.Add("  " + CapitalizedArticle(inner.Name));
                }
            }
        }
        void Walk(WorldObject holder)
        {
            foreach (var child in world.ChildrenOf(holder.Id))
            {
                if (child.Id == agentId || child.HasModule("portal") ||
                    child.HasModule("agent") || IsConcealed(modules, child))
                    continue;
                // scenery itself never lists, but its surface still holds
                // what's on it — the kitchen table is silent, the sack on
                // it is not
                var scenery = child.Attributes.TryGetValue("scenery", out var flag) &&
                              flag.ValueKind == JsonValueKind.True;
                if (!scenery)
                    Emit(child);
                if (child.HasModule("surface"))
                    Walk(child);
            }
        }
        Walk(room);
        return lines;
    }

    /// <summary>"a leaflet" / "quantity of water" → "A leaflet" / "A quantity of water".</summary>
    private static string CapitalizedArticle(string name)
    {
        var article = name.StartsWith("a ", StringComparison.Ordinal) ||
                      name.StartsWith("an ", StringComparison.Ordinal) ||
                      name.StartsWith("some ", StringComparison.Ordinal)
            ? ""
            : "a ";
        return char.ToUpperInvariant((article + name)[0]) + (article + name)[1..];
    }

    /// <summary>
    /// The objects visible in a room listing, in listing order: room
    /// children plus the contents of open containers and surfaces,
    /// recursively — the same set <see cref="DescribeRoomContents"/>
    /// renders as name entries. Excludes agents, portals, concealed
    /// objects, and the observer.
    /// </summary>
    public static List<WorldObject> VisibleObjects(
        World.World world, ModuleRegistry modules, WorldObject room, string agentId)
    {
        var visible = new List<WorldObject>();
        void Collect(WorldObject holder)
        {
            foreach (var child in world.ChildrenOf(holder.Id))
            {
                if (child.Id == agentId || child.HasModule("portal") ||
                    child.HasModule("agent") || IsConcealed(modules, child))
                    continue;
                visible.Add(child);
                if (child.HasModule("container") && IsOpen(world, modules, child))
                    Collect(child);
                else if (child.HasModule("surface"))
                    Collect(child);
            }
        }
        Collect(room);
        return visible;
    }

    public static List<string> DescribeRoomContents(
        World.World world, ModuleRegistry modules, WorldObject room, string agentId)
    {
        var observer = world.GetObject(agentId);
        var items = new List<string>();
        foreach (var child in world.ChildrenOf(room.Id))
        {
            if (child.Id == agentId || child.HasModule("portal") || IsConcealed(modules, child))
                continue;
            var entry = NameFor(modules, observer, child) + Annotate(world, modules, child);
            // agent conditions gather into one parenthetical list:
            // "the arena duelist (prone, incapacitated)"; descriptive
            // crunch adds the overall condition word ("wounded"), and
            // visible status conditions append theirs ("tipsy", "drunk").
            // A busy agent leads with their activity — what someone is
            // doing outranks how they're positioned
            var conditions = AgentStateWords(world, modules, child);
            if (conditions.Count > 0)
                entry += $" ({string.Join(", ", conditions)})";
            items.Add(entry);
            // contents of open containers ("in") and surfaces ("on"),
            // recursive: an opened sack on the table shows its garlic
            AddContents(child, "in", "container", IsOpen(world, modules, child));
            AddContents(child, "on", "surface", true);
            // occupants of furniture (or of a carrier) list like container
            // contents: "the old cook (sitting on the chair)" — visible
            // status conditions ride along ("(sitting on the chair, drunk)")
            foreach (var occupant in world.ChildrenOf(child.Id))
            {
                if (occupant.Id == agentId || !occupant.HasModule("agent"))
                    continue;
                var posture = Postures.Of(world, modules, occupant);
                var where = posture == Postures.Carried
                    ? $"carried by {child.Name}"
                    : $"{posture} on the {child.Name}";
                var words = Conditions.VisibleWords(world, modules, occupant);
                var activity = modules.ResolveString(occupant, "agent", "activity");
                var midActivity = activity is { Length: > 0 } ? activity + ", " : "";
                var suffix = words.Count > 0 ? ", " + string.Join(", ", words) : "";
                items.Add($"{NameFor(modules, observer, occupant)} ({midActivity}{where}{suffix})");
            }
        }
        // agents the observer is carrying (a grappled victim) list like
        // furniture occupants: "the arena duelist (carried by you)"
        foreach (var carried in world.ChildrenOf(agentId))
        {
            if (carried.HasModule("agent"))
                items.Add($"{NameFor(modules, observer, carried)} (carried by you)");
        }
        return items;

        void AddContents(WorldObject obj, string prep, string module, bool open)
        {
            if (!open || !obj.HasModule(module))
                return;
            foreach (var inner in world.ChildrenOf(obj.Id))
            {
                if (IsConcealed(modules, inner))
                    continue;
                items.Add($"{inner.Name} ({prep} {obj.Name})");
                // nested reachability: an open container or surface
                // inside lists its own contents too (never an agent's
                // pockets — occupants render separately)
                if (inner.HasModule("agent"))
                    continue;
                if (inner.HasModule("container") && IsOpen(world, modules, inner))
                    AddContents(inner, "in", "container", true);
                if (inner.HasModule("surface"))
                    AddContents(inner, "on", "surface", true);
            }
        }
    }

    /// <summary>
    /// One line per visibly dressed agent in the room (top-level agents and
    /// furniture occupants, the observer excluded — what you're wearing is
    /// on the inventory screen): "the old cook is wearing an apron, a
    /// chef's hat." Placeholder for per-agent detail until an examine verb
    /// exists; the "You see:" listing stays compact.
    /// </summary>
    /// <summary>
    /// An agent's observable state words, shared by the compact listing
    /// and the who's-here prose lines: what they're busy with, crunch
    /// condition bands, visible status conditions, prone, incapacitated.
    /// </summary>
    public static List<string> AgentStateWords(
        World.World world, ModuleRegistry modules, WorldObject agent)
    {
        var conditions = new List<string>();
        if (!agent.HasModule("agent"))
            return conditions;
        if (modules.ResolveString(agent, "agent", "activity") is { Length: > 0 } busy)
            conditions.Add(busy);
        if (Condition.Descriptive(world, modules) &&
            !Health.IsIncapacitated(world, modules, agent) &&
            Condition.Overall(world, modules, agent) is { } condition)
            conditions.Add(condition.Label);
        conditions.AddRange(Conditions.VisibleWords(world, modules, agent));
        if (Postures.Of(world, modules, agent) == Postures.Prone)
            conditions.Add("prone");
        if (Health.IsIncapacitated(world, modules, agent))
            conditions.Add("incapacitated");
        return conditions;
    }

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
            if (obj.Id == observerId || !obj.HasModule("agent"))
                return;
            // scenery agents are covered by the room's own prose (the
            // ZIL NDESCBIT: the cyclops "blocks the staircase" in the
            // room description, and never lists on his own)
            if (obj.Attributes.TryGetValue("scenery", out var flag) &&
                flag.ValueKind == JsonValueKind.True)
                return;
            var observer = world.GetObject(observerId);
            var name = NameFor(modules, observer, obj);
            // who's here reads like the item listing: the agent's
            // description (the troll's "A nasty-looking troll, brandishing
            // a bloody axe, blocks all passages out of the room."), or
            // "The … is here." when it has none, with observable states
            // riding along in parentheses
            var description = Knowledge.DescriptionFor(modules, observer, obj);
            var line = description.Length > 0
                ? description
                : $"{char.ToUpperInvariant(name[0]) + name[1..]} is here.";
            var conditions = AgentStateWords(world, modules, obj);
            if (conditions.Count > 0)
                line += $" ({string.Join(", ", conditions)})";
            lines.Add(line);
            var worn = Clothing.WornItems(world, modules, obj);
            if (worn.Count > 0)
            {
                var list = string.Join(", ", worn.Select(w => WithArticle(w.Name)));
                lines.Add($"{name} is wearing {list}.");
            }
        }
    }

    /// <summary>
    /// Sentence reporting a container's/surface's contents, for the open
    /// and examine verbs: "There is a brass key inside." / "It's empty."
    /// A surface passes its own preposition ("on it").
    /// </summary>
    public static string ContentsSentence(World.World world, WorldObject container, string prep = "inside")
    {
        var contents = world.ChildrenOf(container.Id).ToList();
        return contents.Count == 0
            ? "It's empty."
            : $"There {(contents.Count == 1 ? "is" : "are")} " +
              string.Join(", ", contents.Select(c => WithArticle(c.Name))) + $" {prep}.";
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
        // a proper name takes no article ("Maya eases back", "Examine
        // Nix the goblin") — capitalized leading word reads as one
        name.StartsWith("the ", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("a ", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("an ", StringComparison.OrdinalIgnoreCase) ||
        (name.Length > 0 && char.IsUpper(name[0]))
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
    /// else renders by the name the observer can print — their real name,
    /// or their incognito description until the observer has learned it
    /// (see <see cref="Knowledge"/>).
    /// </summary>
    public static string NameFor(ModuleRegistry modules, WorldObject observer, WorldObject obj) =>
        obj.Id == observer.Id ? "you" : Knowledge.NameFor(modules, observer, obj);
}
