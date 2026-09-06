using System.Text.Json;
using AEngine.Core.Modules;
using AEngine.Core.World;

namespace AEngine.Core.Actions;

/// <summary>
/// The generic verb engine: applies a data-defined effect list (see
/// <see cref="Effects"/>) to the world. The affordance names the effects
/// either inline (Data key "effects" — a JSON array in a string) or as a
/// field on the target's own module (Data key "effectsField" — the field
/// holds the array, so per-object overrides work: every bell rings, this
/// bell's ring does something particular). Verbs live entirely in data:
/// move/ring/wave/dig/wind/pray/press/turn/tie/raise are all affordances
/// over this one handler. Data "self" is the actor's message template
/// ({target} substituted).
/// </summary>
public sealed class EffectHandler : IActionHandler
{
    public string Id => "effect";

    public ActionResult Execute(ActionContext ctx)
    {
        var target = ctx.Target ?? throw new InvalidOperationException("effect requires a target.");
        string Data(string key) =>
            ctx.Data is not null && ctx.Data.TryGetValue(key, out var v) ? v : "";
        JsonElement? list = null;
        if (Data("effects") is { Length: > 0 } inline)
        {
            try
            {
                list = JsonSerializer.Deserialize<JsonElement>(inline);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"effect Data 'effects' is not valid JSON: {ex.Message}");
            }
        }
        else if (Data("effectsField") is { Length: > 0 } field && ctx.ModuleId is not null)
        {
            list = ctx.Modules.ResolveField(target, ctx.ModuleId, field);
        }
        if (list is not { ValueKind: JsonValueKind.Array })
            throw new InvalidOperationException(
                "effect requires Data 'effects' (inline JSON) or 'effectsField' naming an array field.");
        Effects.Apply(ctx.Engine, list,
            new EffectContext(ctx.Agent, target, ctx.AuxTarget, target, ctx.Random));
        var self = Data("self") is { Length: > 0 } s
            ? s
            : "You {verb} {target}.";
        return ActionResult.Ok(char.ToUpperInvariant(self[0]) + self[1..]
            .Replace("{verb}", ctx.Verb ?? "act", StringComparison.Ordinal)
            .Replace("{target}",
                target.HasModule("agent")
                    ? Knowledge.NameFor(ctx.Modules, ctx.Agent, target)
                    : Perception.WithDefiniteArticle(target.Name),
                StringComparison.Ordinal));
    }
}

/// <summary>
/// A prompted verb whose free-text argument is matched against a
/// data-defined answer table ("Say to the cyclops: ..."). The answers
/// live in a field on the target's own module (Data key "answersField";
/// the field maps a normalized word or phrase to an effect array) so
/// answers are per-object data. A matched answer applies its effects; a
/// wrong one fails with Data "onWrong" (default "Nothing happens.").
/// </summary>
public sealed class AnswerHandler : IActionHandler
{
    public string Id => "answer";

    public ActionResult Execute(ActionContext ctx)
    {
        var target = ctx.Target ?? throw new InvalidOperationException("answer requires a target.");
        string Data(string key) =>
            ctx.Data is not null && ctx.Data.TryGetValue(key, out var v) && v.Length > 0 ? v : "";
        var field = Data("answersField");
        if (field.Length == 0 || ctx.ModuleId is null)
            throw new InvalidOperationException("answer requires Data 'answersField'.");
        var said = ctx.Args.TryGetValue("text", out var text) ? text : "";
        var answers = ctx.Modules.ResolveField(target, ctx.ModuleId, field);
        if (answers is not { ValueKind: JsonValueKind.Object } map)
            throw new InvalidOperationException(
                $"Field '{field}' is not an answer table.");
        foreach (var answer in map.EnumerateObject())
        {
            if (!string.Equals(Normalize(answer.Name), Normalize(said), StringComparison.Ordinal) ||
                answer.Value.ValueKind != JsonValueKind.Array)
                continue;
            Effects.Apply(ctx.Engine, answer.Value,
                new EffectContext(ctx.Agent, target, ctx.AuxTarget, target, ctx.Random));
            var self = Data("self") is { Length: > 0 } s ? s : "You say: \"{arg}\"";
            return ActionResult.Ok(char.ToUpperInvariant(self[0]) + self[1..]
                .Replace("{arg}", said, StringComparison.Ordinal)
                .Replace("{target}",
                    target.HasModule("agent")
                        ? Knowledge.NameFor(ctx.Modules, ctx.Agent, target)
                        : Perception.WithDefiniteArticle(target.Name),
                    StringComparison.Ordinal));
        }
        return ActionResult.Fail(Data("onWrong") is { Length: > 0 } wrong
            ? wrong
            : "Nothing happens.");
    }

    private static string Normalize(string s) =>
        string.Join(' ', s.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Trim('.', '!', '?', '"', '\'');
}

/// <summary>
/// The read verb: prints the target's readable `text` field, verbatim
/// (the leaflet, the prayer, the commands of the gods).
/// </summary>
public sealed class ReadHandler : IActionHandler
{
    public string Id => "read";

    public ActionResult Execute(ActionContext ctx)
    {
        var target = ctx.Target ?? throw new InvalidOperationException("read requires a target.");
        var text = ctx.Modules.ResolveString(target, "readable", "text");
        if (string.IsNullOrWhiteSpace(text))
            return ActionResult.Fail(
                $"There is nothing written on {Perception.WithDefiniteArticle(target.Name)}.");
        return ActionResult.Ok(text!);
    }
}
