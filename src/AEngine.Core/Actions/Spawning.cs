using AEngine.Core.Modules;
using AEngine.Core.World;

namespace AEngine.Core.Actions;

/// <summary>
/// Shared helpers for prefab spawning: where a spawner's clones land
/// (<see cref="SpawnTarget"/> — the object named by the spawner's
/// `spawnTo` ref, e.g. the bar counter, or the spawner host itself when
/// unset) and how many clones of a template already sit there
/// (<see cref="CloneCount"/> — the single-slot anti-flood measure,
/// counted per prefab so an empty ale mug blocks the ale tap without
/// blocking the whiskey bottle).
/// </summary>
public static class Spawning
{
    /// <summary>
    /// Where this spawner's clones land: the object named by its `spawnTo`
    /// field (a counter or table — usually a `surface`), or the spawner
    /// host itself when unset.
    /// </summary>
    public static WorldObject SpawnTarget(World.World world, ModuleRegistry modules, WorldObject host)
    {
        var to = modules.ResolveString(host, "spawner", "spawnTo");
        return to is not null && world.HasObject(to) ? world.GetObject(to) : host;
    }

    /// <summary>
    /// How many clones of a template id sit under a parent (spawned ids
    /// are the template id or "{templateId}_N"). Ids are stable, so a
    /// prefix match reliably identifies a template's instances.
    /// </summary>
    public static int CloneCount(World.World world, WorldObject parent, string templateId) =>
        parent.Children.Count(id =>
            id == templateId || id.StartsWith(templateId + "_", StringComparison.Ordinal));

    /// <summary>
    /// Event-driven emission — the spawner's sibling, fired by motive
    /// events instead of actions: clone the template to a rule-chosen
    /// place. A worn garment carrying <paramref name="collectModule"/>
    /// collects (the clone lands inside it, it gains <c>filled: true</c>,
    /// renames to its <c>filledName</c> when authored, and claims its
    /// <c>fillRegions</c> as covered — a filled condom bars the hips);
    /// else the orifice an ongoing embrace holds the emitter's probe
    /// part in receives it (the partner's occupied part whose
    /// <c>receives</c> accepts the probe), or the orifice an in-flight
    /// touch engages (<paramref name="engagedOrifice"/> — a mouth
    /// mid-suck); else it lands beside the emitter — their room, or
    /// whatever support they're on. Returns the created object, or
    /// null when the template doesn't exist.
    /// </summary>
    public static WorldObject? Emit(
        World.World world, ModuleRegistry modules, WorldObject agent,
        string templateId, string? probe = null, string? collectModule = null,
        WorldObject? engagedOrifice = null)
    {
        if (!world.HasObject(templateId))
            return null;
        // 1. a collecting garment intercepts everything
        if (collectModule is { Length: > 0 })
            foreach (var garment in Clothing.WornItems(world, modules, agent))
                if (garment.HasModule(collectModule))
                {
                    var collected = CloneFor(world, templateId, garment);
                    world.SetFieldOverride(garment.Id, collectModule, "filled",
                        World.World.ToJson(true));
                    if (modules.ResolveString(garment, collectModule, "filledName") is
                            { Length: > 0 } filledName)
                        garment.Name = filledName;
                    // the garment may claim extra coverage once filled —
                    // a spent condom bars the region it protects
                    var regions = Clothing.GarmentRegions(modules, garment)
                        .Concat(modules.ResolveStringList(garment, collectModule, "fillRegions") ?? [])
                        .Distinct()
                        .ToList();
                    world.SetFieldOverride(
                        garment.Id, "wearable", "regions", World.World.ToJson(regions));
                    return collected;
                }
        // 2. the orifice an in-flight touch engages (a mouth mid-suck)
        if (engagedOrifice is not null && world.HasObject(engagedOrifice.Id))
            return CloneFor(world, templateId, engagedOrifice);
        // 3. an ongoing embrace holding the emitter's probe: the
        // partner's occupied part that receives the probe collects it
        if (probe is { Length: > 0 } &&
            BodyParts.InstrumentOf(world, modules, agent, probe) is { } instrument &&
            Embraces.Of(world, modules, agent) is { } embrace)
        {
            var members = Embraces.Members(world, modules, embrace);
            var busy = Embraces.OccupiedParts(world, modules, agent);
            if (busy.Contains(instrument.Name))
                foreach (var otherId in members)
                {
                    if (otherId == agent.Id ||
                        !world.HasObject(otherId) ||
                        !world.GetObject(otherId).HasModule("agent"))
                        continue;
                    var partner = world.GetObject(otherId);
                    foreach (var partId in partner.Children)
                    {
                        if (!world.HasObject(partId))
                            continue;
                        var part = world.GetObject(partId);
                        if (part.HasModule("bodypart") &&
                            Embraces.OccupiedParts(world, modules, partner).Contains(part.Name) &&
                            BodyParts.Receives(modules, part).Contains(probe))
                            return CloneFor(world, templateId, part);
                    }
                }
        }
        // 4. beside the emitter — floor or furniture
        var beside = agent.Parent.Length > 0 && world.HasObject(agent.Parent)
            ? world.GetObject(agent.Parent)
            : world.GetObject(World.World.RootId);
        return CloneFor(world, templateId, beside);
    }

    private static WorldObject CloneFor(World.World world, string templateId, WorldObject parent)
    {
        var id = templateId;
        for (var n = 1; world.HasObject(id); n++)
            id = $"{templateId}_{n}";
        return world.CloneTree(templateId, parent.Id, id);
    }
}
