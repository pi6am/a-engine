using AEngine.Core.Modules;
using AEngine.Core.World;

namespace AEngine.Core.Actions;

/// <summary>
/// Granular body parts (RPG stage 6): a part is a child object of the
/// agent with the data-defined `bodypart` module (`region` — the wear
/// region armor must cover to protect it; `vital`; `crippleEffects`, a
/// list of engine-known ids: disarm, disarm_offhand, prone, no_stand)
/// plus a `health` module for its own hp pool. Parts are anatomy, not
/// items: the resolver and item listings skip them. Agents without parts
/// keep the monolithic `health` pool (slimes, training dummies).
/// </summary>
public static class BodyParts
{
    /// <summary>The agent's body parts (children with the bodypart module).</summary>
    public static List<WorldObject> Of(World.World world, WorldObject agent) =>
        world.ChildrenOf(agent.Id).Where(c => c.HasModule("bodypart")).ToList();

    /// <summary>
    /// Find a part by name, tolerating articles and case ("the head" →
    /// "head"), then loose containment either way. Ambiguous names ("arm"
    /// matches both "left arm" and "right arm") resolve to a uniformly
    /// random match when <paramref name="random"/> is given, otherwise the
    /// first. Null when nothing matches.
    /// </summary>
    public static WorldObject? FindByName(
        World.World world, WorldObject agent, string name, Random? random = null)
    {
        var needle = Normalize(name);
        if (needle.Length == 0)
            return null;
        var parts = Of(world, agent);
        var matches = parts.Where(p => Normalize(p.Name) == needle).ToList();
        if (matches.Count == 0)
            matches = parts.Where(p =>
                Normalize(p.Name).Contains(needle, StringComparison.Ordinal) ||
                needle.Contains(Normalize(p.Name), StringComparison.Ordinal)).ToList();
        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => random is not null ? matches[random.Next(matches.Count)] : matches[0],
        };
    }

    /// <summary>True when the part's hp pool is at/below its incapacitated threshold.</summary>
    public static bool IsCrippled(ModuleRegistry modules, WorldObject part) =>
        part.HasModule("health") &&
        modules.ResolveInt(part, "health", "hp") <=
        modules.ResolveInt(part, "health", "incapacitatedAt", 0);

    /// <summary>The wear region the part belongs to ("" when undeclared).</summary>
    public static string Region(ModuleRegistry modules, WorldObject part) =>
        modules.ResolveString(part, "bodypart", "region") ?? "";

    /// <summary>Vital parts incapacitate their owner when crippled (head, torso).</summary>
    public static bool IsVital(ModuleRegistry modules, WorldObject part) =>
        modules.ResolveBool(part, "bodypart", "vital");

    /// <summary>
    /// To-hit penalty for an attack aimed at this part
    /// (bodypart.aimedPenalty, default 4) — big easy targets get less,
    /// small vital ones more.
    /// </summary>
    public static int AimedPenalty(ModuleRegistry modules, WorldObject part) =>
        modules.ResolveInt(part, "bodypart", "aimedPenalty", 4);

    /// <summary>Engine-known cripple effect ids (disarm, disarm_offhand, prone, no_stand).</summary>
    public static List<string> CrippleEffects(ModuleRegistry modules, WorldObject part) =>
        modules.ResolveStringList(part, "bodypart", "crippleEffects") ?? [];

    /// <summary>
    /// Resolve the actor's instrument for a probe tag ("tongue",
    /// "fingers", "penis"): the body part declaring that
    /// <c>bodypart.probe</c>, else a held object providing it on any of
    /// its modules (a toy's <c>probe</c> field). Null when the actor has
    /// nothing for it.
    /// </summary>
    public static WorldObject? InstrumentOf(
        World.World world, ModuleRegistry modules, WorldObject agent, string probe)
    {
        foreach (var part in Of(world, agent))
            if (modules.ResolveString(part, "bodypart", "probe") == probe)
                return part;
        foreach (var itemId in agent.Children)
        {
            if (!world.HasObject(itemId))
                continue;
            var item = world.GetObject(itemId);
            if (Conditions.IsInternal(item))
                continue;
            foreach (var attachment in item.Modules)
                if (modules.Has(attachment.ModuleId) &&
                    modules.ResolveString(item, attachment.ModuleId, "probe") == probe)
                    return item;
        }
        return null;
    }

    /// <summary>
    /// The probe tags a target part receives (<c>bodypart.receives</c> —
    /// an orifice's compatibility list; empty for surfaces).
    /// </summary>
    public static List<string> Receives(ModuleRegistry modules, WorldObject part) =>
        modules.ResolveStringList(part, "bodypart", "receives") ?? [];

    private static string Normalize(string s)
    {
        s = s.Trim();
        if (s.StartsWith("the ", StringComparison.OrdinalIgnoreCase))
            s = s[4..];
        return s.Trim().ToLowerInvariant();
    }
}
