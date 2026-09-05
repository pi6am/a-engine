using System.Text.Json;
using AEngine.Core.Modules;
using AEngine.Core.World;

namespace AEngine.Core.Actions;

/// <summary>
/// A field-setting verb driven entirely by its affordance Data payload:
/// "module", "field", "value" (bools and numbers parsed as JSON, else
/// strings), an optional "on" of "actor" to write the actor's field
/// instead of the target's (relieving oneself writes the actor's
/// bladder, not the toilet's), an optional "self" message template
/// carrying {target}, and an optional "sensationField" naming a field
/// of the target's affordance-owning module delivered to the actor as
/// a private sensation ("sensationFallback" when it is unset) — the
/// toilet's per-object reliefText. Powers fixture controls like a
/// television's power and channel, and state resets like a visit to
/// the restroom: the scenario's data describes the knob, no handler
/// code required.
/// </summary>
public sealed class SetHandler : IActionHandler
{
    public string Id => "set";

    public ActionResult Execute(ActionContext ctx)
    {
        var target = ctx.Target ?? throw new InvalidOperationException("set requires a target.");
        string Data(string key) =>
            ctx.Data is not null && ctx.Data.TryGetValue(key, out var v) ? v : "";
        var module = Data("module");
        var field = Data("field");
        if (module.Length == 0 || field.Length == 0)
            throw new InvalidOperationException("set requires Data module/field.");
        var value = Data("value");
        // accept bool-looking and number-looking values as JSON, else string
        JsonElement json = value is "true" or "false"
            ? World.World.ToJson(value == "true")
            : double.TryParse(value, out var n) ? World.World.ToJson(n) : World.World.ToJson(value);
        var holder = Data("on") == "actor" ? ctx.Agent : target;
        ctx.World.SetFieldOverride(holder.Id, module, field, json);
        // a private sensation authored per-object on the target (the
        // toilet's reliefText), falling back to the data's own text
        if (Data("sensationField") is { Length: > 0 } senseField && ctx.ModuleId is { } owner)
        {
            var sensation =
                ctx.Modules.ResolveString(target, owner, senseField) is { Length: > 0 } authored
                    ? authored
                    : Data("sensationFallback");
            if (sensation.Length > 0)
                ctx.Signals.SendTo(ctx.Agent, sensation);
        }
        var self = (Data("self") is { Length: > 0 } s ? s : "Done.")
            .Replace("{target}",
                target.HasModule("agent")
                    ? Knowledge.NameFor(ctx.Modules, ctx.Agent, target)
                    : Perception.WithDefiniteArticle(target.Name),
                StringComparison.Ordinal);
        return ActionResult.Ok(
            char.ToUpperInvariant(self[0]) + self[1..]);
    }
}
