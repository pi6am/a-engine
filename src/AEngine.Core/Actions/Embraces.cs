using AEngine.Core.Modules;
using AEngine.Core.World;

namespace AEngine.Core.Actions;

/// <summary>
/// Ongoing pair states — hugs, cuddles, a lap to sit on, joined
/// lovers. An embrace is a shared world object at the tree root
/// (present in the world, listed in no room, like portal doorstates
/// and condition templates), cloned from a scenario template carrying
/// an <c>embrace</c> module with data fields: <c>kind</c> (scenario
/// vocabulary — "hug", "cuddled", "joined"), <c>positions</c> (valid
/// arrangements a reposition may set), <c>entryPosture</c> (posture
/// both take on entering, when the shared support allows it),
/// <c>exposeRequired</c> (wear region that must be uncovered on both
/// sides to enter), and <c>autoEnd</c> (fleeting kinds — a hug —
/// dissolve the moment the entering action completes, while lasting
/// kinds stay until disengaged and expose sub-actions). The live
/// instance carries <c>members</c> (agent ids, entry order) and
/// <c>position</c>. One embrace per agent — entering while either
/// party is held by one fails, answered by the entering action's data
/// prose. Members are a list and matching is "same embrace object",
/// so multi-agent groups later add entry/disengage flows, not a
/// rework. Lookups ignore (and prune) entries whose members are gone
/// — a member destroyed by the world takes the state with it.
/// </summary>
public static class Embraces
{
    /// <summary>All live embrace objects (root objects with the embrace module).</summary>
    public static List<WorldObject> All(World.World world) =>
        world.Objects.Values.Where(o => o.HasModule("embrace")).ToList();

    /// <summary>
    /// The one embrace both agents share, when they are members of the
    /// same object (optionally of the given kind).
    /// </summary>
    public static WorldObject? Find(
        World.World world, ModuleRegistry modules, WorldObject a, WorldObject b, string? kind = null)
    {
        foreach (var embrace in All(world))
        {
            var members = Members(world, modules, embrace);
            if (members.Count == 0 || !members.Contains(a.Id) || !members.Contains(b.Id))
                continue;
            if (kind is null || Kind(modules, embrace) == kind)
                return embrace;
        }
        return null;
    }

    /// <summary>The agent's current embrace, if any (one per agent).</summary>
    public static WorldObject? Of(World.World world, ModuleRegistry modules, WorldObject agent)
    {
        foreach (var embrace in All(world))
            if (Members(world, modules, embrace).Contains(agent.Id))
                return embrace;
        return null;
    }

    public static string Kind(ModuleRegistry modules, WorldObject embrace) =>
        modules.ResolveString(embrace, "embrace", "kind") ?? embrace.Id;

    public static string Position(ModuleRegistry modules, WorldObject embrace) =>
        modules.ResolveString(embrace, "embrace", "position") ?? "";

    public static List<string> Positions(ModuleRegistry modules, WorldObject embrace) =>
        modules.ResolveStringList(embrace, "embrace", "positions") ?? [];

    /// <summary>
    /// The members, pruning the embrace when any ref is gone (an
    /// orphaned pairing heals on the next lookup). An empty list —
    /// a not-yet-bound template — is inert, not pruned.
    /// </summary>
    public static List<string> Members(World.World world, ModuleRegistry modules, WorldObject embrace)
    {
        var ids = modules.ResolveStringList(embrace, "embrace", "members") ?? [];
        if (ids.Count == 0 || ids.All(world.HasObject))
            return ids;
        world.DestroyObject(embrace.Id);
        return [];
    }

    /// <summary>
    /// Clone the template and bind the pair. The initial position is
    /// the caller's data when given, else the template's own, else the
    /// first declared valid position.
    /// </summary>
    public static WorldObject Enter(
        World.World world, ModuleRegistry modules, string templateId,
        WorldObject initiator, WorldObject partner, string? position = null)
    {
        var id = $"embrace_{initiator.Id}_{partner.Id}";
        for (var n = 2; world.HasObject(id); n++)
            id = $"embrace_{initiator.Id}_{partner.Id}_{n}";
        var embrace = world.CloneTree(templateId, World.World.RootId, id);
        world.SetFieldOverride(embrace.Id, "embrace", "members",
            World.World.ToJson(new List<string> { initiator.Id, partner.Id }));
        if (position is { Length: > 0 })
            world.SetFieldOverride(embrace.Id, "embrace", "position", World.World.ToJson(position));
        else if ((modules.ResolveString(embrace, "embrace", "position") ?? "").Length == 0 &&
                 Positions(modules, embrace) is { Count: > 0 } valid)
            world.SetFieldOverride(embrace.Id, "embrace", "position",
                World.World.ToJson(valid[0]));
        return embrace;
    }

    public static void SetPosition(World.World world, WorldObject embrace, string position) =>
        world.SetFieldOverride(embrace.Id, "embrace", "position", World.World.ToJson(position));

    public static bool AutoEnd(ModuleRegistry modules, WorldObject embrace) =>
        modules.ResolveBool(embrace, "embrace", "autoEnd");

    /// <summary>
    /// The part names an ongoing embrace occupies for the agent — the
    /// template's <c>occupiesA</c>/<c>occupiesB</c> lists by member side
    /// (A = the initiator, B = the partner), matched against part names,
    /// case-insensitively. Empty when unembraced or the kind occupies
    /// nothing. The <c>partsFree</c> gate blocks touches whose engaged
    /// parts are in here.
    /// </summary>
    public static HashSet<string> OccupiedParts(
        World.World world, ModuleRegistry modules, WorldObject agent)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var embrace = Of(world, modules, agent);
        if (embrace is null)
            return result;
        var members = Members(world, modules, embrace);
        var side = members.IndexOf(agent.Id) == 0 ? "occupiesA" : "occupiesB";
        foreach (var name in modules.ResolveStringList(embrace, "embrace", side) ?? [])
            result.Add(name);
        return result;
    }

    public static void Dissolve(World.World world, WorldObject embrace) =>
        world.DestroyObject(embrace.Id);
}
