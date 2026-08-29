using AEngine.Core.Modules;
using AEngine.Core.World;

namespace AEngine.Core.Actions;

/// <summary>
/// Stat/skill check resolution. The dice formula is scenario data: a
/// `rules` module (on the world root object or a top-level "rules"
/// object) declares `diceCount`/`diceSides` (defaults 1d20; 0d0 is
/// diceless and deterministic, useful in tests). A check rolls
/// n d m + stat + skill against the difficulty and returns the margin
/// (>= 0 is a success).
/// </summary>
public static class Checks
{
    /// <summary>Roll n dice of m sides and sum them (0d0 = 0).</summary>
    public static int RollDice(Random random, int count, int sides)
    {
        var total = 0;
        for (var i = 0; i < count; i++)
            total += random.Next(1, sides + 1);
        return total;
    }

    /// <summary>The scenario's dice formula: n d m from the rules module.</summary>
    public static (int Count, int Sides) DiceFormula(World.World world, ModuleRegistry modules)
    {
        var root = world.GetObject(World.World.RootId);
        var rulesHost = root.HasModule("rules")
            ? root
            : world.ChildrenOf(World.World.RootId).FirstOrDefault(c => c.HasModule("rules"));
        return rulesHost is null
            ? (1, 20)
            : (modules.ResolveInt(rulesHost, "rules", "diceCount", 1),
               modules.ResolveInt(rulesHost, "rules", "diceSides", 20));
    }

    /// <summary>The actor's bonus for a check: stat value + skill value.</summary>
    public static int Bonus(ModuleRegistry modules, WorldObject actor, CheckSpec spec)
    {
        var bonus = 0;
        if (spec.Stat is not null && actor.HasModule("stats"))
            bonus += Stats.Get(modules, actor, "stats", spec.Stat);
        if (spec.Skill is not null && actor.HasModule("skills"))
            bonus += Stats.Get(modules, actor, "skills", spec.Skill);
        return bonus;
    }

    /// <summary>
    /// Evaluate a check for the actor: dice + bonus − difficulty.
    /// Margin >= 0 is a success.
    /// </summary>
    public static int Evaluate(
        World.World world, ModuleRegistry modules, Random random,
        WorldObject actor, CheckSpec spec)
    {
        var (count, sides) = DiceFormula(world, modules);
        return RollDice(random, count, sides) + Bonus(modules, actor, spec) - spec.Difficulty;
    }
}
