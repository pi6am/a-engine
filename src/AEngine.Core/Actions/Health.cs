using AEngine.Core.Modules;
using AEngine.Core.World;

namespace AEngine.Core.Actions;

/// <summary>
/// Health model (RPG stage 3): an agent with the `health` module has an
/// hp pool (`hp`/`maxHp`) and is incapacitated when hp falls to
/// `incapacitatedAt` (default 0). Incapacitated agents can't act — the
/// resolver offers them only `look` and policies skip their turns.
/// </summary>
public static class Health
{
    /// <summary>True if the object has a health module and is at/below the threshold.</summary>
    public static bool IsIncapacitated(ModuleRegistry modules, WorldObject obj)
    {
        if (!obj.HasModule("health"))
            return false;
        var hp = modules.ResolveInt(obj, "health", "hp");
        var at = modules.ResolveInt(obj, "health", "incapacitatedAt", 0);
        return hp <= at;
    }
}

/// <summary>Applying damage to health pools.</summary>
public static class Damage
{
    /// <summary>
    /// Apply damage to the target's health pool (clamped at 0). Returns a
    /// message fragment when the target is incapacitated by the blow
    /// ("The goblin is incapacitated!"), null otherwise. Targets without a
    /// health module are unaffected and return null.
    /// </summary>
    public static string? Apply(
        World.World world, ModuleRegistry modules, WorldObject target, int amount)
    {
        if (!target.HasModule("health") || amount <= 0)
            return null;
        var wasIncapacitated = Health.IsIncapacitated(modules, target);
        var hp = modules.ResolveInt(target, "health", "hp");
        hp = System.Math.Max(hp - amount, 0);
        world.SetFieldOverride(target.Id, "health", "hp", World.World.ToJson(hp));
        if (wasIncapacitated || !Health.IsIncapacitated(modules, target))
            return null;
        var name = target.Name;
        return $"{char.ToUpperInvariant(name[0])}{name[1..]} is incapacitated!";
    }
}
