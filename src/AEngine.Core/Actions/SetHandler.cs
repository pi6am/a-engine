using System.Text.Json;
using AEngine.Core.Modules;
using AEngine.Core.World;

namespace AEngine.Core.Actions;

/// <summary>
/// A field-setting verb driven entirely by its affordance Data payload
/// ("module", "field", "value" — bools and numbers parsed as JSON, else
/// strings), with an optional "self" message template carrying {target}.
/// Powers fixture controls like a television's power and channel: the
/// scenario's data describes the knob, no handler code required.
/// </summary>
public sealed class SetHandler : IActionHandler
{
    public string Id => "set";

    public ActionResult Execute(ActionContext ctx)
    {
        var target = ctx.Target ?? throw new InvalidOperationException("set requires a target.");
        string Module() => ctx.Data is not null && ctx.Data.TryGetValue("module", out var m) ? m : "";
        string Field() => ctx.Data is not null && ctx.Data.TryGetValue("field", out var f) ? f : "";
        var module = Module();
        var field = Field();
        if (module.Length == 0 || field.Length == 0)
            throw new InvalidOperationException("set requires Data module/field.");
        var value = ctx.Data is not null && ctx.Data.TryGetValue("value", out var v) ? v : "";
        // accept bool-looking and number-looking values as JSON, else string
        JsonElement json = value is "true" or "false"
            ? World.World.ToJson(value == "true")
            : double.TryParse(value, out var n) ? World.World.ToJson(n) : World.World.ToJson(value);
        ctx.World.SetFieldOverride(target.Id, module, field, json);
        var self = (ctx.Data is not null && ctx.Data.TryGetValue("self", out var s) ? s : "Done.")
            .Replace("{target}",
                target.HasModule("agent")
                    ? Knowledge.NameFor(ctx.Modules, ctx.Agent, target)
                    : Perception.WithDefiniteArticle(target.Name),
                StringComparison.Ordinal);
        return ActionResult.Ok(
            char.ToUpperInvariant(self[0]) + self[1..]);
    }
}
