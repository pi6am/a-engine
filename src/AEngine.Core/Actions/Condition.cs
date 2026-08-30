using AEngine.Core.Modules;
using AEngine.Core.World;

namespace AEngine.Core.Actions;

/// <summary>
/// Crunch-level configuration and condition rendering (RPG stage 6). The
/// scenario's rules module sets `crunch` ("numeric", the default, reports
/// damage numbers and hp fractions | "descriptive", categorized blows and
/// condition words), the band tables (`blowBands` / `conditionBands` —
/// maps of label → percent, engine defaults when absent), and
/// `shockThreshold`. All damage and status reporting flows through
/// here so reports and status displays stay at one crunch level.
/// </summary>
public static class Condition
{
    /// <summary>The scenario's crunch level: "numeric" (default) or "descriptive".</summary>
    public static string CrunchMode(World.World world, ModuleRegistry modules) =>
        Checks.RulesHost(world) is { } host
            ? modules.ResolveString(host, "rules", "crunch") ?? "numeric"
            : "numeric";

    /// <summary>True when the scenario reports wounds descriptively rather than numerically.</summary>
    public static bool Descriptive(World.World world, ModuleRegistry modules) =>
        CrunchMode(world, modules) == "descriptive";

    /// <summary>
    /// Percent of total part damage that incapacitates by shock
    /// (rules.shockThreshold; 0/absent = off — only vital parts incapacitate).
    /// </summary>
    public static int ShockThreshold(World.World world, ModuleRegistry modules) =>
        Checks.RulesHost(world) is { } host
            ? modules.ResolveInt(host, "rules", "shockThreshold", 0)
            : 0;

    /// <summary>
    /// Blow categories, ascending by upper percent bound: damage as a
    /// percent of the hit part's maxHp takes the first band it fits
    /// (defaults: glancing ≤15, solid ≤40, severe above).
    /// </summary>
    public static IReadOnlyList<(string Label, int Pct)> BlowBands(
        World.World world, ModuleRegistry modules) =>
        Bands(world, modules, "blowBands", [("glancing", 15), ("solid", 40), ("severe", 100)]);

    /// <summary>
    /// Overall-condition bands, ascending by remaining-hp percent: the
    /// strictest band the agent still fits applies (defaults: severely
    /// wounded ≤30, wounded ≤60, slightly wounded ≤90).
    /// </summary>
    public static IReadOnlyList<(string Label, int Pct)> ConditionBands(
        World.World world, ModuleRegistry modules) =>
        Bands(world, modules, "conditionBands",
            [("severely wounded", 30), ("wounded", 60), ("slightly wounded", 90)]);

    private static List<(string Label, int Pct)> Bands(
        World.World world, ModuleRegistry modules, string field,
        List<(string Label, int Pct)> defaults)
    {
        var map = Checks.RulesHost(world) is { } host
            ? modules.ResolveIntMap(host, "rules", field)
            : null;
        var bands = map is { Count: > 0 }
            ? map.Select(kv => (kv.Key, kv.Value)).ToList()
            : defaults;
        bands.Sort((a, b) => a.Item2.CompareTo(b.Item2));
        return bands;
    }

    /// <summary>The blow category for damage dealt to a part ("glancing"/"solid"/"severe").</summary>
    public static string BlowCategory(
        World.World world, ModuleRegistry modules, WorldObject part, int damage)
    {
        var maxHp = modules.ResolveInt(part, "health", "maxHp");
        var pct = maxHp > 0 ? damage * 100 / maxHp : 100;
        return BlowBands(world, modules).FirstOrDefault(b => pct <= b.Pct).Label ?? "severe";
    }

    /// <summary>
    /// The agent's total hp pool: parts summed when present, else the
    /// monolithic health module; null when the agent has neither.
    /// </summary>
    public static (int Hp, int Max)? Pool(World.World world, ModuleRegistry modules, WorldObject agent)
    {
        var parts = BodyParts.Of(world, agent);
        if (parts.Count > 0)
            return (parts.Sum(p => modules.ResolveInt(p, "health", "hp")),
                    parts.Sum(p => modules.ResolveInt(p, "health", "maxHp")));
        return agent.HasModule("health")
            ? (modules.ResolveInt(agent, "health", "hp"),
               modules.ResolveInt(agent, "health", "maxHp"))
            : null;
    }

    /// <summary>
    /// The agent's overall condition ("wounded"), null when unhurt or
    /// pool-less. Incapacitation is reported separately.
    /// </summary>
    public static (string Label, int Hp, int Max)? Overall(
        World.World world, ModuleRegistry modules, WorldObject agent)
    {
        if (Pool(world, modules, agent) is not { } pool || pool.Max <= 0 || pool.Hp >= pool.Max)
            return null;
        var pct = pool.Hp * 100 / pool.Max;
        var band = ConditionBands(world, modules).FirstOrDefault(b => pct <= b.Pct);
        return band.Label is null ? null : (band.Label, pool.Hp, pool.Max);
    }

    /// <summary>The agent's crippled parts (hp at/below their incapacitated threshold).</summary>
    public static List<WorldObject> CrippledParts(
        World.World world, ModuleRegistry modules, WorldObject agent) =>
        BodyParts.Of(world, agent).Where(p => BodyParts.IsCrippled(modules, p)).ToList();

    /// <summary>
    /// Self status lines for inventory and the LLM context. Numeric mode
    /// shows the same detail examine gives others — a per-part breakdown
    /// ("Health: head 6/6, torso 7/10, left arm 0/5 (crippled).") — or the
    /// pool fraction for part-less agents ("Health: 7/10."). Descriptive
    /// mode shows the condition word and crippled parts ("You are
    /// wounded." / "Your left arm is crippled."), nothing while unhurt.
    /// </summary>
    public static List<string> SelfLines(World.World world, ModuleRegistry modules, WorldObject agent)
    {
        if (!Descriptive(world, modules))
        {
            var parts = BodyParts.Of(world, agent);
            if (parts.Count > 0)
                return ["Health: " + string.Join(", ", parts.Select(p =>
                    $"{p.Name} {modules.ResolveInt(p, "health", "hp")}/{modules.ResolveInt(p, "health", "maxHp")}" +
                    (BodyParts.IsCrippled(modules, p) ? " (crippled)" : ""))) + "."];
            if (Pool(world, modules, agent) is { } pool)
                return [$"Health: {pool.Hp}/{pool.Max}."];
            return [];
        }
        var lines = new List<string>();
        if (Overall(world, modules, agent) is { } o)
            lines.Add($"You are {o.Label}.");
        lines.AddRange(CrippledParts(world, modules, agent).Select(p => $"Your {p.Name} is crippled."));
        return lines;
    }

    /// <summary>
    /// Health detail lines for examining an agent: per-part fractions in
    /// numeric mode ("Health: head 6/6, torso 7/10, left arm 0/5
    /// (crippled), …"), condition words in descriptive mode.
    /// </summary>
    public static List<string> ExamineLines(
        World.World world, ModuleRegistry modules, WorldObject target)
    {
        var lines = new List<string>();
        var parts = BodyParts.Of(world, target);
        if (Descriptive(world, modules))
        {
            if (Overall(world, modules, target) is { } o &&
                !Health.IsIncapacitated(world, modules, target))
                lines.Add($"{Capitalize(target.Name)} is {o.Label}.");
            foreach (var part in CrippledParts(world, modules, target))
                lines.Add($"{Capitalize(target.Name)}'s {part.Name} is crippled.");
            return lines;
        }
        if (parts.Count > 0)
            lines.Add("Health: " + string.Join(", ", parts.Select(p =>
                $"{p.Name} {modules.ResolveInt(p, "health", "hp")}/{modules.ResolveInt(p, "health", "maxHp")}" +
                (BodyParts.IsCrippled(modules, p) ? " (crippled)" : ""))) + ".");
        else if (Pool(world, modules, target) is { } pool)
            lines.Add($"Health: {pool.Hp}/{pool.Max}.");
        return lines;
    }

    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
