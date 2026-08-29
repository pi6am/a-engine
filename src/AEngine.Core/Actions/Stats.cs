using AEngine.Core.Modules;
using AEngine.Core.World;

namespace AEngine.Core.Actions;

/// <summary>
/// Stat and skill access. The set of stats/skills is scenario data, so
/// values live in map fields (`stats.values`, `skills.values`) rather than
/// declared fields; undeclared names read as 0.
/// </summary>
public static class Stats
{
    /// <summary>The object's value for a stat/skill (0 when undeclared).</summary>
    public static int Get(ModuleRegistry modules, WorldObject obj, string moduleId, string name) =>
        modules.ResolveIntMap(obj, moduleId, "values") is { } map &&
        map.TryGetValue(name, out var value)
            ? value
            : 0;

    /// <summary>Set a single stat/skill value, preserving the rest of the map.</summary>
    public static void Set(
        World.World world, ModuleRegistry modules, WorldObject obj,
        string moduleId, string name, int value)
    {
        var map = modules.ResolveIntMap(obj, moduleId, "values")
                  ?? new Dictionary<string, int>(StringComparer.Ordinal);
        map[name] = value;
        world.SetFieldOverride(obj.Id, moduleId, "values", World.World.ToJson(map));
    }
}
