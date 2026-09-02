using AEngine.Core.Modules;
using AEngine.Core.World;

namespace AEngine.Core.Actions;

/// <summary>
/// Clothing model: a worn garment is a child of the agent (same place
/// inventory lives) with <c>worn: true</c> on its <c>wearable</c>
/// attachment. Garments declare the body regions they occupy
/// (<c>wearable.regions</c>); at most one worn garment per region, so
/// conflict is set intersection — layering (shirt + coat) is the author's
/// choice of distinct region names. Fit is data-driven via the agent's
/// <c>body</c> module: the garment's regions must be a subset of
/// <c>body.regions</c>, and an agent with no body can't wear anything.
/// </summary>
public static class Clothing
{
    /// <summary>True if the object is a wearable currently being worn.</summary>
    public static bool IsWorn(ModuleRegistry modules, WorldObject garment) =>
        garment.HasModule("wearable") && modules.ResolveBool(garment, "wearable", "worn");

    /// <summary>The garments the agent is currently wearing.</summary>
    public static List<WorldObject> WornItems(World.World world, ModuleRegistry modules, WorldObject agent) =>
        world.ChildrenOf(agent.Id).Where(c => IsWorn(modules, c)).ToList();

    /// <summary>The regions a garment occupies (empty if undeclared).</summary>
    public static List<string> GarmentRegions(ModuleRegistry modules, WorldObject garment) =>
        modules.ResolveStringList(garment, "wearable", "regions") ?? [];

    /// <summary>
    /// The agent's body regions, or null when the agent has no body module
    /// (and therefore can't wear anything).
    /// </summary>
    public static List<string>? BodyRegions(ModuleRegistry modules, WorldObject agent) =>
        agent.HasModule("body")
            ? modules.ResolveStringList(agent, "body", "regions") ?? []
            : null;

    /// <summary>
    /// True when some worn garment covers the body region (a dress covers
    /// both top and bottom; a bra covers the top). Parts in a covered
    /// region are not exposed — the intimacy gating behind touching them.
    /// </summary>
    public static bool CoversRegion(
        World.World world, ModuleRegistry modules, WorldObject agent, string region) =>
        WornItems(world, modules, agent)
            .Any(g => GarmentRegions(modules, g).Contains(region, StringComparer.Ordinal));
}
