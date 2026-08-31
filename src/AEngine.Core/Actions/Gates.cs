using System.Text.Json;
using AEngine.Core.Modules;
using AEngine.Core.World;

namespace AEngine.Core.Actions;

/// <summary>
/// An execution-time gate kind. Affordances declare gates in data
/// (<see cref="GateSpec"/>); <c>TurnManager.PerformAction</c> resolves
/// each gate's kind through the <see cref="GateRegistry"/> and evaluates
/// it BEFORE reaction parking and the check roll — prerequisites before
/// dice. A blocked action fails loudly with the gate's failText and
/// consumes the turn (the affordance's failSignals fire, like a failed
/// check); the action stays LISTED, so agents can still try and be told
/// why not ("Your bladder is bursting — not another drop."). This is the
/// extensible hook seam: new gate kinds register at runtime, mirroring
/// <see cref="HandlerRegistry"/> and the policy registry.
/// </summary>
public interface IActionGate
{
    string Id { get; }

    /// <summary>True when this gate blocks the action.</summary>
    bool Blocks(ActionContext ctx, GateSpec spec);
}

/// <summary>
/// A gate declared on an affordance: the kind (a
/// <see cref="GateRegistry"/> id), the kind's raw parameters under
/// <c>args</c> (each gate owns its parameter schema), and the failText
/// reported to the actor when blocked.
/// </summary>
public sealed class GateSpec
{
    public required string Kind { get; init; }
    public JsonElement? Args { get; init; }
    public string? FailText { get; init; }
}

/// <summary>Registry of gate kinds by string id — replaceable at runtime.</summary>
public sealed class GateRegistry
{
    private readonly Dictionary<string, IActionGate> _gates = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, IActionGate> Gates => _gates;

    public IActionGate Get(string id) =>
        _gates.TryGetValue(id, out var gate)
            ? gate
            : throw new KeyNotFoundException($"No gate with id '{id}'.");

    public bool Has(string id) => _gates.ContainsKey(id);

    public void Register(IActionGate gate)
    {
        if (_gates.ContainsKey(gate.Id))
            throw new InvalidOperationException($"Gate '{gate.Id}' is already registered.");
        _gates[gate.Id] = gate;
    }

    public void Replace(IActionGate gate) => _gates[gate.Id] = gate;

    /// <summary>The built-in gate kinds.</summary>
    public static IEnumerable<IActionGate> Builtins() => [new ConditionGate(), new FieldGate()];
}

/// <summary>
/// Condition gate. Args: <c>{ "on": "actor"|"target" (default actor),
/// "requires": [kinds], "excludes": [kinds] }</c> — blocks when the
/// referenced agent carries none of the required kinds (any-of, matching
/// the resolver's Requires; condition kinds are often exclusive tiers)
/// or any of the excluded ones. Unlike the resolver's requires/excludes
/// (which hide the action), this fails the attempt with a message.
/// </summary>
public sealed class ConditionGate : IActionGate
{
    public string Id => "condition";

    public bool Blocks(ActionContext ctx, GateSpec spec)
    {
        var on = GateArgs.String(spec.Args, "on") ?? "actor";
        var obj = on == "target" ? ctx.Target ?? ctx.Agent : ctx.Agent;
        var required = GateArgs.Strings(spec.Args, "requires");
        if (required.Count > 0 &&
            required.All(kind => !Conditions.Has(ctx.World, ctx.Modules, obj, kind)))
            return true;
        foreach (var kind in GateArgs.Strings(spec.Args, "excludes"))
            if (Conditions.Has(ctx.World, ctx.Modules, obj, kind))
                return true;
        return false;
    }
}

/// <summary>
/// Field gate. Args: <c>{ "on": "actor"|"target" (default target),
/// "module", "field", "equals"?, "min"?, "max"? }</c> — blocks on a
/// module-field comparison (same matching rules as the resolver's When
/// specs: equals compares the literal verbatim, min/max bound a number).
/// </summary>
public sealed class FieldGate : IActionGate
{
    public string Id => "field";

    public bool Blocks(ActionContext ctx, GateSpec spec)
    {
        var on = GateArgs.String(spec.Args, "on") ?? "target";
        var obj = on == "actor" ? ctx.Agent : ctx.Target ?? ctx.Agent;
        var module = GateArgs.String(spec.Args, "module");
        var field = GateArgs.String(spec.Args, "field");
        if (module is null || field is null || !obj.HasModule(module))
            return false; // nothing to compare — other gates decide
        var value = ctx.Modules.ResolveField(obj, module, field);
        return !FieldMatch.Matches(
            value, GateArgs.Element(spec.Args, "equals"),
            GateArgs.Number(spec.Args, "min"), GateArgs.Number(spec.Args, "max"));
    }
}

/// <summary>Shared field-comparison semantics for When specs and field gates.</summary>
internal static class FieldMatch
{
    /// <summary>
    /// A field value against a comparison: Equals matches the raw literal
    /// (bool/number/string, verbatim), Min/Max bound a number. A value
    /// that is unset or non-numeric fails any comparison.
    /// </summary>
    internal static bool Matches(JsonElement? value, JsonElement? equals, double? min, double? max)
    {
        if (value is not { } e || e.ValueKind == JsonValueKind.Null)
            return false; // unset never satisfies a comparison
        if (equals is { } eq)
        {
            if (e.ValueKind != eq.ValueKind || e.GetRawText() != eq.GetRawText())
                return false;
        }
        if (min is not null || max is not null)
        {
            if (e.ValueKind != JsonValueKind.Number)
                return false;
            var n = e.GetDouble();
            if (min is { } lo && n < lo)
                return false;
            if (max is { } hi && n > hi)
                return false;
        }
        return true;
    }
}

/// <summary>Typed readers over a gate's raw args object.</summary>
internal static class GateArgs
{
    internal static string? String(JsonElement? args, string name)
    {
        if (args is not { } e || e.ValueKind != JsonValueKind.Object)
            return null;
        return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
    }

    internal static List<string> Strings(JsonElement? args, string name)
    {
        if (args is not { } e || e.ValueKind != JsonValueKind.Object)
            return [];
        if (!e.TryGetProperty(name, out var v))
            return [];
        if (v.ValueKind == JsonValueKind.Array)
            return v.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!)
                .ToList();
        // tolerate a single string
        return v.ValueKind == JsonValueKind.String ? [v.GetString()!] : [];
    }

    internal static double? Number(JsonElement? args, string name)
    {
        if (args is not { } e || e.ValueKind != JsonValueKind.Object)
            return null;
        return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : null;
    }

    internal static JsonElement? Element(JsonElement? args, string name)
    {
        if (args is not { } e || e.ValueKind != JsonValueKind.Object)
            return null;
        return e.TryGetProperty(name, out var v) ? v : null;
    }
}
