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

    /// <summary>The object carrying the scenario's rules module: the world root, or a top-level object.</summary>
    public static WorldObject? RulesHost(World.World world)
    {
        var root = world.GetObject(World.World.RootId);
        return root.HasModule("rules")
            ? root
            : world.ChildrenOf(World.World.RootId).FirstOrDefault(c => c.HasModule("rules"));
    }

    /// <summary>The scenario's dice formula: n d m from the rules module.</summary>
    public static (int Count, int Sides) DiceFormula(World.World world, ModuleRegistry modules)
    {
        var rulesHost = RulesHost(world);
        return rulesHost is null
            ? (1, 20)
            : (modules.ResolveInt(rulesHost, "rules", "diceCount", 1),
               modules.ResolveInt(rulesHost, "rules", "diceSides", 20));
    }

    /// <summary>
    /// The actor's bonus for a check: stat value + skill value + active
    /// condition modifiers (<see cref="Conditions.StatMod"/> — a drunk
    /// brawler's agility penalty and brawling bonus apply live).
    /// </summary>
    public static int Bonus(World.World world, ModuleRegistry modules, WorldObject actor, string? stat, string? skill)
    {
        var bonus = 0;
        if (stat is not null && actor.HasModule("stats"))
            bonus += Stats.Get(modules, actor, "stats", stat);
        if (skill is not null && actor.HasModule("skills"))
            bonus += Stats.Get(modules, actor, "skills", skill);
        return bonus + Conditions.StatMod(world, modules, actor, stat)
                     + Conditions.StatMod(world, modules, actor, skill);
    }

    /// <summary>
    /// Evaluate an uncontested check for the actor: dice + bonus −
    /// difficulty. Margin >= 0 is a success.
    /// </summary>
    public static int Evaluate(
        World.World world, ModuleRegistry modules, Random random,
        WorldObject actor, CheckSpec spec)
    {
        var (count, sides) = DiceFormula(world, modules);
        return RollDice(random, count, sides) + Bonus(world, modules, actor, spec.Stat, spec.Skill)
               - spec.Difficulty;
    }

    /// <summary>
    /// Evaluate an opposed check: both sides roll dice + bonus; the actor
    /// must beat the defender by the difficulty (default 0, ties win).
    /// Margin >= 0 is a success for the actor. An incapacitated defender
    /// can't resist: they contribute nothing (no roll, no bonus). When the
    /// defender chose a quick-time reaction, it replaces their side: the
    /// reaction's stat/skill plus its flat bonus, or nothing at all when
    /// the reaction accepts the action (NoResist).
    /// </summary>
    public static int EvaluateOpposed(
        World.World world, ModuleRegistry modules, Random random,
        WorldObject actor, CheckSpec spec, WorldObject defender,
        ReactionOptionSpec? reaction = null)
    {
        var (count, sides) = DiceFormula(world, modules);
        var attack = RollDice(random, count, sides) + Bonus(world, modules, actor, spec.Stat, spec.Skill);
        var defend = reaction switch
        {
            { NoResist: true } => 0,
            not null => RollDice(random, count, sides) +
                        Bonus(world, modules, defender, reaction.Stat, reaction.Skill) + reaction.Bonus,
            _ when Health.IsIncapacitated(world, modules, defender) => 0,
            _ => RollDice(random, count, sides) +
                 Bonus(world, modules, defender, spec.Opposed?.Stat, spec.Opposed?.Skill),
        };
        return attack - defend - spec.Difficulty;
    }

    /// <summary>
    /// The defender of an opposed check against a target: the target
    /// itself when it's an agent, else the agent holding it (picking a
    /// pocket is opposed by the pocket's owner).
    /// </summary>
    public static WorldObject? OpposedDefender(World.World world, WorldObject actor, WorldObject? target)
    {
        if (target is null)
            return null;
        if (target.HasModule("agent"))
            return target;
        if (target.Parent.Length > 0 && target.Parent != actor.Id && world.HasObject(target.Parent))
        {
            var holder = world.GetObject(target.Parent);
            if (holder.HasModule("agent"))
                return holder;
        }
        return null;
    }
}
