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
}
