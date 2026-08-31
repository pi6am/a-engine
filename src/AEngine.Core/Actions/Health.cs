using AEngine.Core.Modules;
using AEngine.Core.Signals;
using AEngine.Core.World;

namespace AEngine.Core.Actions;

/// <summary>
/// Health model (RPG stages 3+6): an agent with the `health` module has a
/// monolithic hp pool (`hp`/`maxHp`) and is incapacitated when hp falls to
/// `incapacitatedAt` (default 0). An agent with body parts (children with
/// the `bodypart` module, each with their own `health` pool) has no global
/// pool: they are incapacitated when a `vital` part is crippled, or when
/// total part damage reaches the rules module's `shockThreshold` percent.
/// Incapacitated agents can't act — the resolver offers them only `look`
/// and policies skip their turns.
/// </summary>
public static class Health
{
    /// <summary>True if the object is incapacitated (parts-aware; see class doc).</summary>
    public static bool IsIncapacitated(World.World world, ModuleRegistry modules, WorldObject obj)
    {
        var parts = BodyParts.Of(world, obj);
        if (parts.Count > 0)
        {
            if (parts.Any(p => BodyParts.IsVital(modules, p) && BodyParts.IsCrippled(modules, p)))
                return true;
            var shock = Condition.ShockThreshold(world, modules);
            if (shock > 0 && Condition.Pool(world, modules, obj) is { } pool && pool.Max > 0)
                return (pool.Max - pool.Hp) * 100 >= shock * pool.Max;
            return false;
        }
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
    /// Apply damage to the target's monolithic health pool (clamped at 0).
    /// Returns a message fragment when the target is incapacitated by the
    /// blow ("The goblin collapses, incapacitated!"), null otherwise. A
    /// standing agent is knocked prone — they crumple where they stood;
    /// seated, lying, or carried agents stay where they are. Targets
    /// without a health module (or with body parts — use
    /// <see cref="ApplyToPart"/>) are unaffected and return null.
    /// </summary>
    public static string? Apply(
        World.World world, ModuleRegistry modules, WorldObject target, int amount,
        SignalBus? signals = null)
    {
        if (!target.HasModule("health") || BodyParts.Of(world, target).Count > 0 || amount <= 0)
            return null;
        var wasIncapacitated = Health.IsIncapacitated(world, modules, target);
        var hpBefore = modules.ResolveInt(target, "health", "hp");
        var hp = System.Math.Max(hpBefore - amount, 0);
        world.SetFieldOverride(target.Id, "health", "hp", World.World.ToJson(hp));
        if (signals is not null && target.HasModule("agent"))
            WarnBand(world, modules, signals, target, "You",
                hpBefore, hp, modules.ResolveInt(target, "health", "maxHp"));
        return IncapacitationFragment(world, modules, target, wasIncapacitated, signals);
    }

    /// <summary>
    /// Apply damage to a body part's pool (clamped at 0). On the cripple
    /// transition the part's `crippleEffects` fire: disarm/disarm_offhand
    /// drop the owner's worn held-region items to the floor, prone knocks a
    /// standing owner down (no_stand is passive — the stand handler checks
    /// it). When the blow incapacitates the owner (vital part or shock),
    /// they collapse as in <see cref="Apply"/>. Returns the message
    /// fragments for what visibly happened, null for an unremarkable hit.
    /// </summary>
    public static string? ApplyToPart(
        World.World world, ModuleRegistry modules, WorldObject part, int amount,
        SignalBus? signals = null)
    {
        if (!part.HasModule("health") || amount <= 0)
            return null;
        var owner = world.GetObject(part.Parent);
        var wasCrippled = BodyParts.IsCrippled(modules, part);
        var wasIncapacitated = Health.IsIncapacitated(world, modules, owner);
        var hpBefore = modules.ResolveInt(part, "health", "hp");
        var hp = System.Math.Max(hpBefore - amount, 0);
        world.SetFieldOverride(part.Id, "health", "hp", World.World.ToJson(hp));

        var fragments = new List<string>();
        var newlyCrippled = !wasCrippled && BodyParts.IsCrippled(modules, part);
        if (signals is not null && owner.HasModule("agent"))
        {
            // victim-facing wound reports, escalating: overall condition,
            // then the part itself (a cripple or a worsening band) — felt
            // (and remembered) even when the blow was someone else's action
            if (Condition.Pool(world, modules, owner) is { } pool && pool.Max > 0)
                WarnBand(world, modules, signals, owner, "You",
                    pool.Hp + (hpBefore - hp), pool.Hp, pool.Max);
            if (newlyCrippled)
                signals.SendTo(owner, $"Your {part.Name} is crippled!");
            else
                WarnBand(world, modules, signals, owner, $"Your {part.Name}",
                    hpBefore, hp, modules.ResolveInt(part, "health", "maxHp"));
        }
        if (newlyCrippled)
        {
            fragments.Add(
                $"{Capitalize(owner.Name)}'s {part.Name} is crippled!");
            foreach (var effect in BodyParts.CrippleEffects(modules, part))
                switch (effect)
                {
                    case "disarm":
                        DropHeldRegion(world, modules, owner, fragments, "held");
                        break;
                    case "disarm_offhand":
                        DropHeldRegion(world, modules, owner, fragments, "held_off_hand");
                        break;
                    case "prone":
                        if (owner.HasModule("agent") &&
                            Postures.Of(world, modules, owner) == Postures.Standing)
                        {
                            world.SetFieldOverride(
                                owner.Id, "agent", "posture", World.World.ToJson(Postures.Prone));
                            fragments.Add($"{Capitalize(owner.Name)} topples to the ground.");
                        }
                        break;
                        // no_stand: passive — the stand handler refuses while crippled
                }
        }
        if (IncapacitationFragment(world, modules, owner, wasIncapacitated, signals) is { } fragment)
            fragments.Add(fragment);
        return fragments.Count > 0 ? string.Join(" ", fragments) : null;
    }

    /// <summary>
    /// Victim-facing condition warning: when a wound crosses into a worse
    /// condition band (rules.conditionBands), the victim feels it — "Your
    /// left arm is wounded." / "You are severely wounded." Only crossings
    /// to a strictly worse band report, so healthy scrapes stay quiet.
    /// </summary>
    private static void WarnBand(
        World.World world, ModuleRegistry modules, SignalBus signals,
        WorldObject victim, string subject, int hpBefore, int hpAfter, int maxHp)
    {
        if (maxHp <= 0 || hpAfter >= hpBefore)
            return;
        var bands = Condition.ConditionBands(world, modules);
        // bands ascend by remaining-hp percent: lower index = worse shape
        int BandIndex(int value)
        {
            var pct = value * 100 / maxHp;
            for (var i = 0; i < bands.Count; i++)
                if (pct <= bands[i].Pct)
                    return i;
            return -1; // healthier than the loosest band
        }
        var before = BandIndex(hpBefore);
        var after = BandIndex(hpAfter);
        if (after >= 0 && (before < 0 || after < before))
            signals.SendTo(victim, $"{subject} {(subject == "You" ? "are" : "is")} {bands[after].Label}.");
    }

    // a disarmed item unwears and falls to the owner's room floor
    private static void DropHeldRegion(
        World.World world, ModuleRegistry modules, WorldObject owner,
        List<string> fragments, string region)
    {
        var room = world.RoomOf(owner.Id);
        foreach (var item in Clothing.WornItems(world, modules, owner)
                     .Where(w => Clothing.GarmentRegions(modules, w).Contains(region)))
        {
            world.SetFieldOverride(item.Id, "wearable", "worn", World.World.ToJson(false));
            world.MoveObject(item.Id, room.Id);
            fragments.Add($"{Capitalize(item.Name)} clatters to the ground.");
        }
    }

    /// <summary>
    /// The one-time incapacitation report: when the blow newly
    /// incapacitates the target, a standing agent crumples prone and the
    /// fragment announces it; the victim feels it as a private sensation
    /// ("You collapse, incapacitated!"); otherwise null.
    /// </summary>
    private static string? IncapacitationFragment(
        World.World world, ModuleRegistry modules, WorldObject target, bool wasIncapacitated,
        SignalBus? signals = null)
    {
        if (wasIncapacitated || !Health.IsIncapacitated(world, modules, target))
            return null;
        var collapses = target.HasModule("agent") &&
                        Postures.Of(world, modules, target) == Postures.Standing;
        if (collapses)
            world.SetFieldOverride(target.Id, "agent", "posture", World.World.ToJson(Postures.Prone));
        if (signals is not null && target.HasModule("agent"))
            signals.SendTo(target, collapses ? "You collapse, incapacitated!" : "You are incapacitated!");
        var capitalized = Capitalize(target.Name);
        return collapses ? $"{capitalized} collapses, incapacitated!" : $"{capitalized} is incapacitated!";
    }

    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
