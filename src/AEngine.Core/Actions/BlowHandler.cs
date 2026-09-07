using AEngine.Core;
using AEngine.Core.Modules;
using AEngine.Core.Runtime;
using AEngine.Core.World;

namespace AEngine.Core.Actions;

/// <summary>
/// Wound-level melee (the classic-adventure fight): attacker and
/// defender are `duelist`s, the weapon is any held item with the
/// `weapon` module. Strength comes from the duelist module plus live
/// condition statMods ("strength") — wounds weaken — plus, when the
/// rules module sets <c>strengthFromScore</c>, one point per that many
/// score points for scorecard-carrying heroes (the original's
/// 2 + score/70). A defender's <c>weakTo</c> weapon list names the
/// blades that fight them at an advantage (the sword against the troll,
/// the knife against the thief): wielding one lowers their effective
/// strength by one.
/// <para>
/// One roll decides the blow: d9 + (attacker − defender strength),
/// clamped, against the outcome ladder — 0–1 miss, 2/4 light wound
/// (defender strength −1), 3/6 stagger, 5 serious wound (−2), 7
/// unconscious, 8+ dead. A miss still consumes the turn (the swing
/// happened) and reports as success so plans and walkthroughs keep
/// stepping. Unconscious attaches the condition named by Data
/// <c>unconsciousCondition</c> (engine-agnostic — a scenario's own
/// out-cold), marks the duelist `out` (the resolver withdraws the
/// attack), and sets a randomized `wakeIn` countdown for the scenario's
/// wake automation. Dead attaches Data <c>deadCondition</c> and drops
/// the fallen duelist's portable belongings where they fell.
/// </para>
/// </summary>
public sealed class BlowHandler : IActionHandler
{
    public string Id => "blow";

    public ActionResult Execute(ActionContext ctx)
    {
        var target = ctx.Target ?? throw new InvalidOperationException("blow requires a target.");
        if (!target.HasModule("duelist"))
            return ActionResult.Fail(
                $"There is no point in attacking {Perception.WithDefiniteArticle(target.Name)}.");
        if (ctx.Modules.ResolveBool(target, "duelist", "out"))
            return ActionResult.Noop(
                $"{Text.Capitalize(target.Name)} is in no shape to fight.");
        // the wielded weapon: a held item with the weapon module,
        // preferring the defender's own weakTo blade (the elvish sword
        // against the troll, the nasty knife against the thief) — the
        // best-weapon rule — before whatever else is at hand
        var weakTo = ctx.Modules.ResolveStringList(target, "duelist", "weakTo") ?? [];
        var weapon = ctx.World.ChildrenOf(ctx.Agent.Id)
            .FirstOrDefault(w => weakTo.Contains(w.Id) && w.HasModule("weapon")) ??
            ctx.World.ChildrenOf(ctx.Agent.Id)
                .FirstOrDefault(w => w.HasModule("weapon"));
        if (weapon is null)
            return ActionResult.Fail(
                $"Bare-handed combat against {target.Name} would be suicidal.");

        var random = ctx.Random ?? new Random();
        var attack = Strength(ctx, ctx.Agent);
        var defense = Strength(ctx, target);
        if (weakTo.Contains(weapon.Id))
            defense -= 1;

        var roll = Math.Clamp(
            random.Next(9) + (attack - defense), 0, 11);
        string Data(string key) =>
            ctx.Data is not null && ctx.Data.TryGetValue(key, out var v) ? v : "";

        switch (roll)
        {
            case <= 1:
                ctx.Signals.Emit(ctx.Agent, target,
                    [new Signals.SignalSpec
                    {
                        Sense = Signals.SignalSense.Visual, Priority = 5,
                        Text = "{agent} swings at the {target} and misses.",
                    }]);
                return ActionResult.Ok($"You swing at the {target.Name} and miss!");
            case 2 or 4:
                Wound(ctx, target, 1);
                return ActionResult.Ok(
                    $"{Perception.WithDefiniteArticle(target.Name)} staggers under a light wound from the {weapon.Name}.");
            case 3 or 6:
                return ActionResult.Ok(
                    $"{Perception.WithDefiniteArticle(target.Name)} staggers from the force of your blow.");
            case 5:
                Wound(ctx, target, 2);
                return ActionResult.Ok(
                    $"{Perception.WithDefiniteArticle(target.Name)} takes a serious wound from the {weapon.Name}!");
            case 7:
            {
                if (Data("unconsciousCondition") is { Length: > 0 } template &&
                    ctx.World.HasObject(template))
                    Conditions.Attach(ctx.World, ctx.Modules, target, template);
                ctx.World.SetFieldOverride(target.Id, "duelist", "out", World.World.ToJson(true));
                ctx.World.SetFieldOverride(target.Id, "duelist", "wakeIn",
                    World.World.ToJson(1 + random.Next(3)));
                ctx.Signals.Emit(ctx.Agent, target,
                    [new Signals.SignalSpec
                    {
                        Sense = Signals.SignalSense.Visual, Priority = 10,
                        Text = "The {target} collapses, out cold.",
                    }]);
                return ActionResult.Ok(
                    $"A mighty blow! The {target.Name} collapses, out cold.");
            }
            default:
            {
                if (Data("deadCondition") is { Length: > 0 } template &&
                    ctx.World.HasObject(template))
                    Conditions.Attach(ctx.World, ctx.Modules, target, template);
                ctx.World.SetFieldOverride(target.Id, "duelist", "out", World.World.ToJson(true));
                // the fallen drop what they held
                foreach (var itemId in target.Children.ToArray())
                {
                    if (ctx.World.HasObject(itemId) &&
                        ctx.World.GetObject(itemId).HasModule("portable"))
                        ctx.World.MoveObject(itemId, ctx.World.RoomOf(target.Id).Id);
                }
                ctx.Signals.Emit(ctx.Agent, target,
                    [new Signals.SignalSpec
                    {
                        Sense = Signals.SignalSense.Visual, Priority = 10,
                        Text = "The {target} is dead!",
                    }]);
                return ActionResult.Ok(
                    $"You deliver the death blow! The {target.Name} is dead!");
            }
        }
    }

    /// <summary>Effective combat strength: base field, live wound mods, and the hero's score scaling.</summary>
    private static int Strength(ActionContext ctx, WorldObject duelist)
    {
        var strength = ctx.Modules.ResolveInt(duelist, "duelist", "strength", 2) +
                       Conditions.StatMod(ctx.World, ctx.Modules, duelist, "strength");
        var rules = Checks.RulesHost(ctx.World);
        if (rules is not null && duelist.HasModule("scorecard") &&
            ctx.Modules.ResolveInt(rules, "rules", "strengthFromScore") is > 0 and var per)
            strength += Score.Of(ctx.World, ctx.Modules, duelist) / per;
        return strength;
    }

    private static void Wound(ActionContext ctx, WorldObject target, int damage)
    {
        var strength = ctx.Modules.ResolveInt(target, "duelist", "strength", 2);
        ctx.World.SetFieldOverride(target.Id, "duelist", "strength",
            World.World.ToJson(strength - damage));
    }

}

/// <summary>
/// The world-clock pass for proximity senses: an object with the `sense`
/// module (held, usually — a glowing sword) watches the agents named in
/// its <c>senses</c> list; the glow field reads 2 while one shares the
/// holder's room, 1 while one is a portal away, 0 otherwise. Level
/// changes message the holder with <c>onFaint</c>/<c>onBright</c>/
/// <c>onDim</c>. Watched agents carrying any condition named in
/// <c>ignoresConditions</c> don't count (a sword that dims over the
/// fallen). The engine knows nothing about what the sense means —
/// warnings, detections, and ancient elvish enchantments are all data.
/// </summary>
public static class Senses
{
    public static void Advance(GameEngine engine)
    {
        var world = engine.World;
        var modules = engine.ModuleRegistry;
        foreach (var obj in world.Objects.Values
                     .Where(o => o.HasModule("sense"))
                     .OrderBy(o => o.Id, StringComparer.Ordinal))
        {
            var watched = modules.ResolveStringList(obj, "sense", "senses") ?? [];
            if (watched.Count == 0)
                continue;
            var ignores = modules.ResolveStringList(obj, "sense", "ignoresConditions") ?? [];
            var holder = obj.Parent.Length > 0 && world.HasObject(obj.Parent)
                ? world.GetObject(obj.Parent)
                : null;
            if (holder is null || !holder.HasModule("agent"))
                continue;
            var room = world.RoomOf(holder.Id);
            var adjacent = new HashSet<string>(StringComparer.Ordinal);
            foreach (var portal in world.ChildrenOf(room.Id).Where(p => p.HasModule("portal")))
                if (modules.ResolveString(portal, "portal", "to") is { Length: > 0 } to)
                    adjacent.Add(to);
            var glow = 0;
            foreach (var id in watched)
            {
                if (!world.HasObject(id) || !world.GetObject(id).HasModule("agent"))
                    continue;
                var agent = world.GetObject(id);
                if (ignores.Any(kind =>
                        Conditions.Has(world, modules, agent, kind)))
                    continue; // out of the fight — no longer worth sensing
                var where = world.RoomOf(id);
                if (where.Id == room.Id)
                    glow = 2;
                else if (adjacent.Contains(where.Id))
                    glow = Math.Max(glow, 1);
            }
            var current = modules.ResolveInt(obj, "sense", "glow");
            if (glow == current)
                continue;
            world.SetFieldOverride(obj.Id, "sense", "glow", World.World.ToJson(glow));
            var text = glow switch
            {
                1 => modules.ResolveString(obj, "sense", "onFaint"),
                2 => modules.ResolveString(obj, "sense", "onBright"),
                _ => modules.ResolveString(obj, "sense", "onDim"),
            };
            if (text is { Length: > 0 })
                engine.SignalBus.SendTo(holder, text);
        }
    }
}
