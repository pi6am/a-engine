using System.Text.Json;
using AEngine.Core.Actions;
using AEngine.Core.Modules;
using AEngine.Core.World;

namespace AEngine.Core.Runtime;

/// <summary>
/// The world-clock pass for data-driven <b>automations</b>: conditional
/// rules and timers authored as top-level objects carrying the
/// `automation` module. Each pass (once per player turn, tick by tick in
/// real-time) evaluates every rule's conditions and fires its
/// <c>effects</c> (the shared <see cref="Actions.Effects"/> vocabulary —
/// the same one the effect handler uses).
/// <para>
/// Conditions (<c>when</c>, an array, ALL must match):
/// <c>{ of, module, field, equals/min/max }</c> — a module field on any
/// object; <c>{ of, hasCondition: kind }</c> — a condition carried by an
/// agent; <c>{ holder, holds: itemId }</c> — containment; and
/// <c>{ of, inRoom: roomId }</c> — presence. The selectors "actor",
/// "target", "aux" are absent here (no action context) — ids or "self".
/// </para>
/// <para>
/// Timing: a plain rule fires every pass while its conditions hold.
/// <c>once: true</c> makes it edge-triggered — it fires when the
/// conditions become true and re-arms once they go false again (die,
/// resurrect, die again). <c>delay: n</c> waits n turns after the
/// conditions turn true before firing (the reservoir drains eight turns
/// after the bolt turns). <c>every: n</c> re-fires every n turns while
/// the conditions hold (the river's drift, a rising flood).
/// </para>
/// </summary>
public static class Automations
{
    /// <summary>
    /// One pass over every automation-carrying object, in id order
    /// (deterministic under a seeded Random).
    /// </summary>
    public static void Advance(GameEngine engine, int turns)
    {
        var world = engine.World;
        var modules = engine.ModuleRegistry;
        foreach (var obj in world.Objects.Values
                     .Where(o => o.HasModule("automation"))
                     .OrderBy(o => o.Id, StringComparer.Ordinal)
                     .ToList())
        {
            if (!world.HasObject(obj.Id))
                continue; // an earlier rule destroyed this one
            var conditionsHold = ConditionsHold(world, modules, obj);
            var once = modules.ResolveBool(obj, "automation", "once");
            var every = Math.Max(0, modules.ResolveInt(obj, "automation", "every"));
            var delay = Math.Max(0, modules.ResolveInt(obj, "automation", "delay"));
            var armed = modules.ResolveBool(obj, "automation", "armed", true);
            var countdown = modules.ResolveInt(obj, "automation", "countdown", 0);

            if (!conditionsHold)
            {
                // state left: re-arm and reset any pending countdown
                if (!armed)
                    world.SetFieldOverride(obj.Id, "automation", "armed",
                        World.World.ToJson(true));
                if (countdown != 0)
                    world.SetFieldOverride(obj.Id, "automation", "countdown",
                        World.World.ToJson(0));
                continue;
            }

            if (once && !armed)
                continue; // already fired for this stretch of truth

            if (delay > 0 && countdown <= 0)
            {
                // truth just began (or re-began): start the delay clock
                // (decremented below, so delay n fires on the n-th pass)
                world.SetFieldOverride(obj.Id, "automation", "countdown",
                    World.World.ToJson(delay));
                countdown = delay;
            }

            if (countdown > 0)
            {
                countdown -= turns;
                world.SetFieldOverride(obj.Id, "automation", "countdown",
                    World.World.ToJson(Math.Max(0, countdown)));
                if (countdown > 0)
                    continue;
            }

            if (world.HasObject(obj.Id))
                Effects.Apply(engine,
                    modules.ResolveField(obj, "automation", "effects"),
                    new EffectContext(Self: obj, Random: engine.Random));

            if (once)
                world.SetFieldOverride(obj.Id, "automation", "armed",
                    World.World.ToJson(false));
            if (every > 0 && world.HasObject(obj.Id))
                world.SetFieldOverride(obj.Id, "automation", "countdown",
                    World.World.ToJson(every));
        }
    }

    /// <summary>Evaluate an automation's when conditions (all must match).</summary>
    private static bool ConditionsHold(
        World.World world, ModuleRegistry modules, WorldObject rule)
    {
        if (modules.ResolveField(rule, "automation", "when") is not
                { ValueKind: JsonValueKind.Array } list)
            return true; // unconditional: a pure timer
        var ctx = new EffectContext(Self: rule, Random: null);
        foreach (var spec in list.EnumerateArray())
        {
            if (spec.ValueKind != JsonValueKind.Object)
                continue;
            // { of, hasCondition } — an agent carries a condition kind
            if (Str(spec, "hasCondition") is { } kind)
            {
                var agent = Effects.Resolve(world, Str(spec, "of") ?? "self", ctx);
                if (agent is null || !agent.HasModule("agent") ||
                    !Conditions.Has(world, modules, agent, kind))
                    return false;
                continue;
            }
            // { holder, holds } — an object currently inside another
            if (Str(spec, "holds") is { } itemId)
            {
                var holder = Effects.Resolve(world, Str(spec, "holder") ?? "self", ctx);
                if (holder is null || !world.HasObject(itemId) ||
                    world.GetObject(itemId).Parent != holder.Id)
                    return false;
                continue;
            }
            // { of, inRoom } — an object's room-granular location
            if (Str(spec, "inRoom") is { } roomId)
            {
                var obj = Effects.Resolve(world, Str(spec, "of") ?? "self", ctx);
                if (obj is null || !world.HasObject(roomId) ||
                    world.RoomOf(obj.Id).Id != roomId)
                    return false;
                continue;
            }
            // { of, module, field, equals/min/max } — a field comparison
            var module = Str(spec, "module");
            var field = Str(spec, "field");
            var obj2 = Effects.Resolve(world, Str(spec, "of") ?? "self", ctx);
            if (module is null || field is null || obj2 is null || !obj2.HasModule(module))
                return false;
            JsonElement? equals = spec.TryGetProperty("equals", out var eq) ? eq : null;
            if (!FieldMatch.Matches(
                    modules.ResolveField(obj2, module, field), equals,
                    Dbl(spec, "min"), Dbl(spec, "max")))
                return false;
        }
        return true;
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static double? Dbl(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : null;
}
