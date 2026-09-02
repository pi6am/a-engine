using System.Text.Json;
using AEngine.Core.Actions;
using AEngine.Core.Modules;
using AEngine.Core.World;

namespace AEngine.Core.Runtime;

/// <summary>
/// World-clock upkeep for agents with a `metabolism` module: alcohol
/// decays per second (the burned amount flowing into the bladder), and
/// threshold bands re-evaluate — attaching/detaching condition templates
/// ("tipsy" at 25% of capacity, "drunk" at 50%...) with private
/// sensations on transitions. Bands are fractions of the agent's
/// `capacity` (or of the raw 0..1 bladder), so an orc out-drinks a
/// goblin on the same bands. Time semantics: turn-based mode advances
/// everyone by each performed action's duration; real-time ticks advance
/// one second per tick. Zero seconds re-evaluates bands without decaying
/// — the initial sync after a scenario loads (the drunk elf is drunk on
/// turn 0).
/// </summary>
public static class Metabolism
{
    /// <summary>
    /// A threshold band: while the value reaches Min, the condition
    /// template named by Condition is attached; below it, detached. Bands
    /// are exclusive — the highest reached band wins, lower ones detach.
    /// </summary>
    private sealed record Stage(double Min, string TemplateId);

    /// <summary>
    /// Advance every metabolizing agent by seconds of world time. In
    /// turn-based mode pass <paramref name="onlyAgentId"/> so each action
    /// advances only its ACTOR's metabolism — otherwise N agents acting
    /// once per round would burn N× the seconds per action, and everyone
    /// sobers up (and fills up) at the cast size's pace instead of their
    /// own (the same simultaneity rule the ambient timers follow).
    /// </summary>
    public static void Advance(GameEngine engine, double seconds, string? onlyAgentId = null)
    {
        var agents = engine.World.Objects.Values
            .Where(o => o.HasModule("agent") && o.HasModule("metabolism"))
            .Where(o => onlyAgentId is null || o.Id == onlyAgentId)
            .ToList();
        foreach (var agent in agents)
        {
            var modules = engine.ModuleRegistry;
            var alcohol = modules.ResolveDouble(agent, "metabolism", "alcohol");
            var bladder = modules.ResolveDouble(agent, "metabolism", "bladder");
            if (seconds > 0)
            {
                var decay = modules.ResolveDouble(agent, "metabolism", "alcoholDecayPerSec", 0.002);
                var burned = Math.Min(alcohol, decay * seconds);
                alcohol -= burned;
                bladder = Math.Min(1.0, bladder + burned *
                    modules.ResolveDouble(agent, "metabolism", "bladderFromAlcohol", 1.0));
                engine.World.SetFieldOverride(
                    agent.Id, "metabolism", "alcohol", World.World.ToJson(alcohol));
                engine.World.SetFieldOverride(
                    agent.Id, "metabolism", "bladder", World.World.ToJson(bladder));
            }
            var capacity = Math.Max(0.000001, modules.ResolveDouble(agent, "metabolism", "capacity", 1.0));
            ApplyBands(engine, agent, Stages(modules, agent, "stages"), alcohol / capacity);
            ApplyBands(engine, agent, Stages(modules, agent, "bladderStages"), bladder);
        }
    }

    private static List<Stage> Stages(ModuleRegistry modules, WorldObject agent, string field)
    {
        if (modules.ResolveField(agent, "metabolism", field) is not { ValueKind: JsonValueKind.Array } e)
            return [];
        var stages = new List<Stage>();
        foreach (var item in e.EnumerateArray())
        {
            if (!item.TryGetProperty("min", out var min) || min.ValueKind != JsonValueKind.Number)
                continue;
            if (!item.TryGetProperty("condition", out var condition) ||
                condition.ValueKind != JsonValueKind.String)
                continue;
            stages.Add(new Stage(min.GetDouble(), condition.GetString()!));
        }
        return stages;
    }

    /// <summary>
    /// Attach the highest band the ratio reaches, detach every other
    /// band's condition, and notify the agent on transitions (attach:
    /// the condition's selfText, "You feel tipsy."; detach: its
    /// clearText when authored).
    /// </summary>
    private static void ApplyBands(GameEngine engine, WorldObject agent, List<Stage> stages, double ratio)
    {
        var world = engine.World;
        var modules = engine.ModuleRegistry;
        var active = stages.Where(s => ratio >= s.Min)
            .OrderByDescending(s => s.Min)
            .FirstOrDefault();
        foreach (var stage in stages)
        {
            if (!world.HasObject(stage.TemplateId))
                continue;
            var template = world.GetObject(stage.TemplateId);
            var kind = Conditions.KindOf(modules, template);
            if (ReferenceEquals(stage, active))
            {
                var wasPresent = Conditions.Has(world, modules, agent, kind);
                Conditions.Attach(world, modules, agent, stage.TemplateId);
                if (!wasPresent && TransitionText(modules, template, attach: true) is { } arrived)
                    engine.SignalBus.SendTo(agent, arrived);
            }
            else if (Conditions.Detach(world, modules, agent, kind))
            {
                if (TransitionText(modules, template, attach: false) is { } faded)
                    engine.SignalBus.SendTo(agent, faded);
            }
        }
    }

    private static string? TransitionText(ModuleRegistry modules, WorldObject template, bool attach)
    {
        if (attach)
        {
            return modules.ResolveString(template, "condition", "selfText") is { Length: > 0 } arrived
                ? arrived
                : $"You feel {Conditions.LabelOf(modules, template)}.";
        }
        return modules.ResolveString(template, "condition", "clearText") is { Length: > 0 } faded
            ? faded
            : null;
    }
}
