using System.Text.Json;
using AEngine.Core.Modules;
using AEngine.Core.World;

namespace AEngine.Core.Actions;

/// <summary>
/// The shared condition evaluator for automation rules and individual
/// effects: an array of specs that must ALL match (any one flippable
/// with <c>negate</c>). Kinds: a module field on any object
/// (<c>{ of, module, field, equals/min/max }</c>), a carried condition
/// kind (<c>{ of, hasCondition }</c>), containment
/// (<c>{ holder, holds }</c>), and presence (<c>{ of, inRoom }</c>).
/// Selectors resolve object ids or the context names
/// (actor/target/aux/self). A null/missing list matches unconditionally.
/// </summary>
internal static class RuleConditions
{
    public static bool Evaluate(
        World.World world, ModuleRegistry modules, JsonElement? list, EffectContext ctx)
    {
        if (list is not { ValueKind: JsonValueKind.Array } specs)
            return true;
        foreach (var spec in specs.EnumerateArray())
        {
            if (spec.ValueKind != JsonValueKind.Object)
                continue;
            var negate = spec.TryGetProperty("negate", out var n) &&
                         n.ValueKind == JsonValueKind.True;
            bool matched;
            if (Str(spec, "hasCondition") is { } kind)
            {
                var agent = Effects.Resolve(world, Str(spec, "of") ?? "self", ctx);
                matched = agent is not null && agent.HasModule("agent") &&
                          Conditions.Has(world, modules, agent, kind);
            }
            else if (Str(spec, "holds") is { } itemId)
            {
                var holder = Effects.Resolve(world, Str(spec, "holder") ?? "self", ctx);
                matched = holder is not null && world.HasObject(itemId) &&
                          world.GetObject(itemId).Parent == holder.Id;
            }
            else if (Str(spec, "inRoom") is { } roomId)
            {
                var obj = Effects.Resolve(world, Str(spec, "of") ?? "self", ctx);
                matched = obj is not null && world.HasObject(roomId) &&
                          world.RoomOf(obj.Id).Id == roomId;
            }
            else
            {
                var module = Str(spec, "module");
                var field = Str(spec, "field");
                var obj = Effects.Resolve(world, Str(spec, "of") ?? "self", ctx);
                matched = module is not null && field is not null &&
                          obj is not null && obj.HasModule(module) &&
                          FieldMatch.Matches(
                              modules.ResolveField(obj, module, field),
                              spec.TryGetProperty("equals", out var eq) ? eq : null,
                              Dbl(spec, "min"), Dbl(spec, "max"));
            }
            if (matched == negate)
                return false;
        }
        return true;
    }

    private static string? Str(JsonElement obj, string name) =>
        GateArgs.String(obj, name);

    private static double? Dbl(JsonElement obj, string name) =>
        GateArgs.Number(obj, name);
}
