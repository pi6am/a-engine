using AEngine.Core.Modules;
using AEngine.Core.World;

namespace AEngine.Core.Actions;

/// <summary>
/// Data-driven conditions (buffs and debuffs): child objects of an agent
/// carrying a `condition` module — fields `kind` (unique id, e.g.
/// "tipsy"), `label` (display word, default the kind), `visible` (shown in
/// room listings and examine, default true), `selfText` (the holder's felt
/// line, "You feel tipsy."), `traits`/`goals` (additive text shifting
/// LLM-driven behavior), `statMods` (string→int map summed into
/// <see cref="Checks.Bonus"/> — drunk: agility −2, brawling +1).
/// Conditions are attached by cloning a scenario template object
/// (<see cref="Attach"/>) — from a metabolism pass, a one-shot handler,
/// or (stage 2) timed chains — and gate affordances through
/// requires/excludes in the resolver and execution-time gates. Like body
/// parts, conditions are part of the agent, not belongings: listings,
/// pocket scans, and inventory skip them (<see cref="IsInternal"/>).
/// </summary>
public static class Conditions
{
    /// <summary>
    /// Objects that are part of an agent rather than belongings: body
    /// parts and conditions. Skipped wherever inventory is listed or
    /// scanned for action targets.
    /// </summary>
    public static bool IsInternal(WorldObject obj) =>
        obj.HasModule("bodypart") || obj.HasModule("condition");

    /// <summary>The agent's active conditions (children with the condition module).</summary>
    public static List<WorldObject> Active(World.World world, WorldObject agent) =>
        world.ChildrenOf(agent.Id).Where(c => c.HasModule("condition")).ToList();

    /// <summary>A condition object's kind: its `kind` field, or its id when unset.</summary>
    public static string KindOf(ModuleRegistry modules, WorldObject condition) =>
        modules.ResolveString(condition, "condition", "kind") is { Length: > 0 } kind
            ? kind
            : condition.Id;

    public static bool Has(World.World world, ModuleRegistry modules, WorldObject agent, string kind) =>
        Active(world, agent).Any(c => KindOf(modules, c) == kind);

    /// <summary>
    /// Attach a condition by cloning its template (a top-level object
    /// referenced by id) under the agent. Idempotent: an agent already
    /// carrying the kind keeps the existing instance.
    /// </summary>
    public static WorldObject Attach(
        World.World world, ModuleRegistry modules, WorldObject agent, string templateId)
    {
        var template = world.GetObject(templateId);
        if (!template.HasModule("condition"))
            throw new InvalidOperationException(
                $"Condition template '{templateId}' has no condition module.");
        var kind = KindOf(modules, template);
        if (Active(world, agent).FirstOrDefault(c => KindOf(modules, c) == kind) is { } existing)
            return existing;
        return world.CloneTree(templateId, agent.Id, $"{kind}_{agent.Id}");
    }

    /// <summary>Remove the agent's condition of a kind. False when absent.</summary>
    public static bool Detach(World.World world, ModuleRegistry modules, WorldObject agent, string kind)
    {
        foreach (var condition in Active(world, agent))
            if (KindOf(modules, condition) == kind)
            {
                world.DestroyObject(condition.Id);
                return true;
            }
        return false;
    }

    /// <summary>
    /// Sum of a stat's modifiers across the agent's active conditions.
    /// Applied additively in <see cref="Checks.Bonus"/> on top of stats
    /// and skills, so every check re-reads it live.
    /// </summary>
    public static int StatMod(World.World world, ModuleRegistry modules, WorldObject agent, string? stat)
    {
        if (stat is null)
            return 0;
        var total = 0;
        foreach (var condition in Active(world, agent))
            if (modules.ResolveIntMap(condition, "condition", "statMods") is { } mods &&
                mods.TryGetValue(stat, out var value))
                total += value;
        return total;
    }

    /// <summary>
    /// Additive trait text from active conditions ("flirty,
    /// loose-tongued"), appended to the agent's own traits for
    /// LLM-driven policies. Empty string when nothing applies.
    /// </summary>
    public static string TraitText(World.World world, ModuleRegistry modules, WorldObject agent) =>
        JoinText(world, modules, agent, "traits");

    /// <summary>Additive goal text from active conditions ("get another drink").</summary>
    public static string GoalText(World.World world, ModuleRegistry modules, WorldObject agent) =>
        JoinText(world, modules, agent, "goals");

    private static string JoinText(World.World world, ModuleRegistry modules, WorldObject agent, string field)
    {
        var parts = Active(world, agent)
            .Select(c => modules.ResolveString(c, "condition", field))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
        return parts.Count == 0 ? "" : string.Join(" ", parts);
    }

    /// <summary>
    /// Display words for the agent's visible conditions ("tipsy",
    /// "drunk"), for room listings and examine — rendered alongside
    /// posture ("the elf (drunk, sitting in the booth)").
    /// </summary>
    public static List<string> VisibleWords(World.World world, ModuleRegistry modules, WorldObject agent) =>
        Active(world, agent)
            .Where(c => modules.ResolveBool(c, "condition", "visible", true))
            .Select(c => LabelOf(modules, c))
            .ToList();

    /// <summary>A condition's display word: its `label` field, or its kind.</summary>
    public static string LabelOf(ModuleRegistry modules, WorldObject condition)
    {
        var label = modules.ResolveString(condition, "condition", "label");
        return string.IsNullOrWhiteSpace(label) ? KindOf(modules, condition) : label!;
    }

    /// <summary>
    /// The agent's own felt lines ("You feel tipsy.", "You need to pee."),
    /// for look, inventory, and the LLM context. Authored `selfText`, or
    /// "You feel {label}." when unset.
    /// </summary>
    public static List<string> SelfLines(World.World world, ModuleRegistry modules, WorldObject agent) =>
        Active(world, agent)
            .Select(c => modules.ResolveString(c, "condition", "selfText") is { Length: > 0 } text
                ? text
                : $"You feel {LabelOf(modules, c)}.")
            .Select(Capitalize)
            .ToList();

    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
