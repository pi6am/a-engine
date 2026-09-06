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

    /// <summary>
    /// Optional failure message sourced from the world (data on the
    /// target object), used when the spec carries no failText — the seam
    /// for gates whose parameters live per-object (the exit gate's
    /// blockedText on a portal side).
    /// </summary>
    string? Message(ActionContext ctx, GateSpec spec) => null;
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
    public static IEnumerable<IActionGate> Builtins() =>
        [new ConditionGate(), new FieldGate(), new ExposedGate(), new CoveredGate(),
         new EmbracedGate(), new PartsFreeGate(), new ExitGate(), new BarredGate(),
         new GuardGate(),
         new CarryingGate(), new NotCarryingGate(), new LoadUnderGate(), new AllowOnlyGate()];
}

/// <summary>
/// The guard gate: an item's own fields say who protects it. While the
/// agent named in <c>guardedBy</c> is still in the fight — carrying none
/// of the condition kinds listed in <c>guardConditions</c> — the item
/// cannot be taken ("You'd be stabbed in the back first."). An optional
/// <c>needsFlag</c> (a flag object that must be true) guards on world
/// state instead: the platinum bar stays untakeable until its room is
/// quieted. The message comes from <c>guardedText</c> through
/// <see cref="Message"/>.
/// </summary>
public sealed class GuardGate : IActionGate
{
    public string Id => "guard";

    public bool Blocks(ActionContext ctx, GateSpec spec)
    {
        var item = ctx.Target;
        if (item is null || !item.HasModule("portable"))
            return false;
        var needsFlag = ctx.Modules.ResolveString(item, "portable", "needsFlag");
        if (needsFlag is { Length: > 0 } && ctx.World.HasObject(needsFlag))
        {
            var flag = ctx.World.GetObject(needsFlag);
            if (flag.HasModule("flag") &&
                !ctx.Modules.ResolveBool(flag, "flag", "value"))
                return true;
        }
        var guardId = ctx.Modules.ResolveString(item, "portable", "guardedBy");
        if (guardId is not { Length: > 0 } || !ctx.World.HasObject(guardId))
            return false;
        var guard = ctx.World.GetObject(guardId);
        var releases = ctx.Modules.ResolveStringList(item, "portable", "guardConditions") ?? [];
        return releases.Count == 0 ||
               releases.All(kind => !Conditions.Has(ctx.World, ctx.Modules, guard, kind));
    }

    public string? Message(ActionContext ctx, GateSpec spec) =>
        ctx.Target is { } item &&
        ctx.Modules.ResolveString(item, "portable", "guardedText") is { Length: > 0 } text
            ? text
            : "Someone would stop you.";
}

/// <summary>
/// The barred gate for one-sided doors: blocks open on a portal side
/// carrying <c>barred: true</c> — a door that only opens from its other
/// side (the slammed trap door: "The door is locked from above."). The
/// message comes from the side's own <c>barredText</c> field through
/// <see cref="Message"/>. Close stays ungated: a door ajar can still be
/// shut from anywhere.
/// </summary>
public sealed class BarredGate : IActionGate
{
    public string Id => "barred";

    public bool Blocks(ActionContext ctx, GateSpec spec) =>
        ctx.Target is { } portal &&
        portal.HasModule("portal") &&
        ctx.Modules.ResolveBool(portal, "portal", "barred");

    public string? Message(ActionContext ctx, GateSpec spec) =>
        ctx.Target is { } portal &&
        ctx.Modules.ResolveString(portal, "portal", "barredText") is { Length: > 0 } text
            ? text
            : "It is barred from the other side.";
}

/// <summary>
/// The conditional-exit gate: all of its parameters live as fields on
/// the TARGET portal side (so one affordance definition serves every
/// portal), checked against the actor who would pass through. Fields,
/// all optional: <c>requires</c> — a flag object (flag.value) that must
/// be true ("The troll fends you off"); <c>requiresCarrying</c> — item
/// ids the actor must hold; <c>notCarrying</c> — ids that bar the way
/// (the coffin that won't fit); <c>allowOnly</c> — the actor may carry
/// nothing outside this list (the lamp-only chimney), softened by
/// <c>allowPlus</c> extra items (the lamp and one more thing);
/// <c>loadUnder</c>
/// — a maximum carried weight (the empty-handed crawl); and
/// <c>blockedText</c> — the failure message, read through
/// <see cref="Message"/> so each blocked passage speaks for itself
/// (default: "You can't go that way.").
/// </summary>
public sealed class ExitGate : IActionGate
{
    public string Id => "exit";

    public bool Blocks(ActionContext ctx, GateSpec spec)
    {
        var portal = ctx.Target;
        if (portal is null || !portal.HasModule("portal"))
            return false;
        var modules = ctx.Modules;

        var requires = modules.ResolveString(portal, "portal", "requires");
        if (requires is { Length: > 0 } && ctx.World.HasObject(requires))
        {
            var flag = ctx.World.GetObject(requires);
            if (flag.HasModule("flag") &&
                !modules.ResolveBool(flag, "flag", "value"))
                return true;
        }

        var held = new HashSet<string>(StringComparer.Ordinal);
        CarryingGate.CollectHeld(ctx, ctx.Agent.Id, held);
        foreach (var item in modules.ResolveStringList(portal, "portal", "requiresCarrying") ?? [])
            if (!held.Contains(item))
                return true;
        foreach (var item in modules.ResolveStringList(portal, "portal", "notCarrying") ?? [])
            if (held.Contains(item))
                return true;
        var allowOnly = modules.ResolveStringList(portal, "portal", "allowOnly");
        if (allowOnly is { Count: > 0 })
        {
            var extras = modules.ResolveInt(portal, "portal", "allowPlus");
            if (held.Except(allowOnly).Count() > Math.Max(0, extras))
                return true;
        }
        if (modules.ResolveInt(portal, "portal", "loadUnder") is > 0 and var cap)
        {
            var load = 0.0;
            void Weigh(string objId)
            {
                var obj = ctx.World.GetObject(objId);
                if (obj.HasModule("portable"))
                    load += ctx.Modules.ResolveDouble(obj, "portable", "weight");
                foreach (var childId in obj.Children)
                    Weigh(childId);
            }
            foreach (var childId in ctx.Agent.Children)
                Weigh(childId);
            if (load > cap)
                return true;
        }
        return false;
    }

    public string? Message(ActionContext ctx, GateSpec spec) =>
        ctx.Target is { } portal &&
        ctx.Modules.ResolveString(portal, "portal", "blockedText") is { Length: > 0 } text
            ? text
            : null;
}

/// <summary>
/// Exposure gate for body-part-targeted actions: blocks when the part's
/// wear region is covered by a worn garment on its owner ("You reach for
/// her chest, but her dress is in the way."). No args — the target's own
/// region decides. Parts without a region are never blocked.
/// </summary>
public sealed class ExposedGate : IActionGate
{
    public string Id => "exposed";

    public bool Blocks(ActionContext ctx, GateSpec spec)
    {
        var part = ctx.Target;
        if (part is null || !part.HasModule("bodypart"))
            return false;
        var region = BodyParts.Region(ctx.Modules, part);
        if (region.Length == 0 || !ctx.World.HasObject(part.Parent))
            return false;
        var owner = ctx.World.GetObject(part.Parent);
        return owner.HasModule("agent") &&
               Clothing.CoversRegion(ctx.World, ctx.Modules, owner, region);
    }
}

/// <summary>
/// Embrace gate for sub-actions of an ongoing pair state. Args:
/// <c>{ "kind"?, "position"?, "negate"? }</c> — blocks unless the
/// actor (and the target, when it is another agent) share an embrace
/// matching the kind/position; <c>negate</c> inverts it (blocks while
/// embraced — reserving new pairings for the unattached). Covers
/// stale plans the resolver's listing filter already hid.
/// </summary>
public sealed class EmbracedGate : IActionGate
{
    public string Id => "embraced";

    public bool Blocks(ActionContext ctx, GateSpec spec)
    {
        var kind = GateArgs.String(spec.Args, "kind");
        var position = GateArgs.String(spec.Args, "position");
        var other = ctx.Target is not null && ctx.Target.HasModule("agent") &&
                    ctx.Target.Id != ctx.Agent.Id
            ? ctx.Target
            : null;
        var embrace = other is null
            ? Embraces.Of(ctx.World, ctx.Modules, ctx.Agent)
            : Embraces.Find(ctx.World, ctx.Modules, ctx.Agent, other);
        var holds = embrace is not null &&
                    (kind is null || Embraces.Kind(ctx.Modules, embrace) == kind) &&
                    (position is null || Embraces.Position(ctx.Modules, embrace) == position);
        var negate = spec.Args is { } args &&
                     args.TryGetProperty("negate", out var n) && n.ValueKind == JsonValueKind.True;
        return negate ? holds : !holds;
    }
}

/// <summary>
/// The inverse of the exposed gate: blocks when the target part's wear
/// region is NOT covered — the execution-time guard for through-clothes
/// affordances (listed via <c>coveredParts</c>), keeping stale plans from
/// rubbing bare skin with the through-sweater verb.
/// </summary>
public sealed class CoveredGate : IActionGate
{
    public string Id => "covered";

    public bool Blocks(ActionContext ctx, GateSpec spec)
    {
        var part = ctx.Target;
        if (part is null || !part.HasModule("bodypart"))
            return false;
        var region = BodyParts.Region(ctx.Modules, part);
        if (region.Length == 0 || !ctx.World.HasObject(part.Parent))
            return false;
        var owner = ctx.World.GetObject(part.Parent);
        return owner.HasModule("agent") &&
               !Clothing.CoversRegion(ctx.World, ctx.Modules, owner, region);
    }
}

/// <summary>
/// Busy-parts gate: blocks when a part this action engages is already
/// held by an ongoing embrace — the embrace's template declares
/// <c>occupiesA</c>/<c>occupiesB</c> part-name lists per side. The parts
/// checked: the targeted part (when the action targets a body part),
/// the actor's probe instrument (the affordance's <c>probe</c> data),
/// and any parts the data names in <c>usesParts</c> (a comma list — the
/// actor's mouth for a kiss). Quick touches while joined, lips while
/// giving oral: bodies don't contort that way.
/// </summary>
public sealed class PartsFreeGate : IActionGate
{
    public string Id => "partsFree";

    public bool Blocks(ActionContext ctx, GateSpec spec)
    {
        // the targeted part, on its owner's side
        if (ctx.Target is { } part && part.HasModule("bodypart") &&
            ctx.World.HasObject(part.Parent) &&
            ctx.World.GetObject(part.Parent).HasModule("agent") &&
            Embraces.OccupiedParts(ctx.World, ctx.Modules, ctx.World.GetObject(part.Parent))
                .Contains(part.Name))
            return true;
        // the actor's own engaged parts
        var busy = Embraces.OccupiedParts(ctx.World, ctx.Modules, ctx.Agent);
        if (ctx.Data is not null)
        {
            if (ctx.Data.TryGetValue("probe", out var probe) &&
                probe.Length > 0 &&
                BodyParts.InstrumentOf(ctx.World, ctx.Modules, ctx.Agent, probe) is { } instrument &&
                busy.Contains(instrument.Name))
                return true;
            if (ctx.Data.TryGetValue("usesParts", out var uses))
                foreach (var name in uses.Split(',',
                             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (busy.Contains(name))
                        return true;
        }
        return false;
    }
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
/// Field gate. Args: <c>{ on: "actor"|"target" (default target), of?,
/// module, field, equals?, min?, max? }</c> — blocks on a module-field
/// comparison (same matching rules as the resolver's When specs: equals
/// compares the literal verbatim, min/max bound a number). <c>of</c>
/// names any object by id (or "actor"/"target" selectors) and overrides
/// <c>on</c> — the gate for passages keyed on a third object's state:
/// "the troll fends you off" reads a shared troll_state object, "the
/// reservoir is drained" a dam_state one.
/// </summary>
public sealed class FieldGate : IActionGate
{
    public string Id => "field";

    public bool Blocks(ActionContext ctx, GateSpec spec)
    {
        var of = GateArgs.String(spec.Args, "of");
        WorldObject obj;
        if (of is not null)
            obj = Actions.Effects.Resolve(ctx.World, of,
                     new EffectContext(ctx.Agent, ctx.Target, ctx.AuxTarget)) ??
                 (GateArgs.String(spec.Args, "on") == "actor" ? ctx.Agent : ctx.Target ?? ctx.Agent);
        else
            obj = GateArgs.String(spec.Args, "on") == "actor" ? ctx.Agent : ctx.Target ?? ctx.Agent;
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

/// <summary>
/// Carrying gate. Args: <c>{ item: id | [ids], any?: bool }</c> — blocks
/// unless the actor holds the named item(s): all of them, or any one
/// when <c>any</c> is true. The "must wield the wrench to turn the bolt"
/// family; the held item may be anywhere in the actor's belongings
/// (a tool loose inside an open sack still turns a bolt).
/// </summary>
public sealed class CarryingGate : IActionGate
{
    public string Id => "carrying";

    public bool Blocks(ActionContext ctx, GateSpec spec)
    {
        var items = GateArgs.Strings(spec.Args, "item");
        if (items.Count == 0)
            return false;
        var held = new HashSet<string>(StringComparer.Ordinal);
        CollectHeld(ctx, ctx.Agent.Id, held);
        var any = spec.Args is { } args &&
                  args.TryGetProperty("any", out var a) && a.ValueKind == JsonValueKind.True;
        return any
            ? !items.Any(held.Contains)
            : !items.All(held.Contains);
    }

    internal static void CollectHeld(ActionContext ctx, string holderId,
        HashSet<string> into, string? skipId = null)
    {
        foreach (var childId in ctx.World.GetObject(holderId).Children)
        {
            if (childId == skipId)
                continue; // stowed in the thing being boarded — freight, not cargo
            into.Add(childId);
            CollectHeld(ctx, childId, into, skipId);
        }
    }
}

/// <summary>
/// NotCarrying gate. Args: <c>{ item: id | [ids] }</c> — blocks while the
/// actor holds ANY of the named items. The bulk gate: "the gold coffin
/// won't fit through the hole", "not with that inflated boat". Stowed
/// cargo doesn't count: the actor's subtree is walked EXCEPT the action's
/// own target — a sword riding in the boat being boarded is freight,
/// not a pocketknife.
/// </summary>
public sealed class NotCarryingGate : IActionGate
{
    public string Id => "notCarrying";

    public bool Blocks(ActionContext ctx, GateSpec spec)
    {
        var items = GateArgs.Strings(spec.Args, "item");
        if (items.Count == 0)
            return false;
        var held = new HashSet<string>(StringComparer.Ordinal);
        CarryingGate.CollectHeld(ctx, ctx.Agent.Id, held, ctx.Target?.Id);
        return items.Any(held.Contains);
    }
}

/// <summary>
/// Load gate. Args: <c>{ max?: number, maxField?: { module, field }?,
/// module?: "portable", field?: "weight", includeTarget?: bool,
/// includeAux?: bool }</c> — blocks when the summed weight field of the
/// actor's carried items (plus the action's target and/or aux item when
/// included) exceeds the cap. The cap is either a literal or read from
/// the actor's own module field (wounds reducing one's load). The
/// generic form of both Zork's 100-point load limit and the Timber
/// Room's empty-handed crawl.
/// </summary>
public sealed class LoadUnderGate : IActionGate
{
    public string Id => "loadUnder";

    public bool Blocks(ActionContext ctx, GateSpec spec)
    {
        var module = GateArgs.String(spec.Args, "module") ?? "portable";
        var field = GateArgs.String(spec.Args, "field") ?? "weight";
        double max;
        if (GateArgs.Element(spec.Args, "maxField") is { ValueKind: JsonValueKind.Object } cap)
        {
            var capModule = GateArgs.String(cap, "module") ?? "agent";
            var capField = GateArgs.String(cap, "field") ?? "loadMax";
            if (!ctx.Agent.HasModule(capModule))
                return false;
            max = ctx.Modules.ResolveDouble(ctx.Agent, capModule, capField, 100);
        }
        else
            max = GateArgs.Number(spec.Args, "max") ?? 100;

        var load = 0.0;
        void Add(WorldObject? obj)
        {
            if (obj is not null && obj.HasModule(module))
                load += ctx.Modules.ResolveDouble(obj, module, field);
        }
        foreach (var childId in ctx.World.GetObject(ctx.Agent.Id).Children)
            LoadOf(ctx, childId, module, field, ref load);
        if (spec.Args is { } args)
        {
            if (args.TryGetProperty("includeTarget", out var t) && t.ValueKind == JsonValueKind.True)
                Add(ctx.Target);
            if (args.TryGetProperty("includeAux", out var x) && x.ValueKind == JsonValueKind.True)
                Add(ctx.AuxTarget);
        }
        return load > max;
    }

    private static void LoadOf(
        ActionContext ctx, string objId, string module, string field, ref double load)
    {
        var obj = ctx.World.GetObject(objId);
        if (obj.HasModule(module))
            load += ctx.Modules.ResolveDouble(obj, module, field);
        foreach (var childId in obj.Children)
            LoadOf(ctx, childId, module, field, ref load);
    }
}

/// <summary>
/// Allow-only gate. Args: <c>{ items: [ids] }</c> — blocks while the
/// actor carries anything NOT in the list. The chimney rule ("the lamp
/// and nothing else"), in one declaration.
/// </summary>
public sealed class AllowOnlyGate : IActionGate
{
    public string Id => "allowOnly";

    public bool Blocks(ActionContext ctx, GateSpec spec)
    {
        var allowed = GateArgs.Strings(spec.Args, "items").ToHashSet(StringComparer.Ordinal);
        if (allowed.Count == 0)
            return false;
        var held = new HashSet<string>(StringComparer.Ordinal);
        CarryingGate.CollectHeld(ctx, ctx.Agent.Id, held);
        return held.Except(allowed).Any();
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

/// <summary>
/// Evaluate a When spec directly against an object (the resolver's
/// WhenApplies answers actor-vs-target internally; this is the seam for
/// other systems that already hold the object — reaction defaults).
/// </summary>
public static class WhenSpecEval
{
    public static bool Matches(ModuleRegistry modules, WorldObject obj, Modules.WhenSpec spec)
    {
        if (spec.Absent)
            return !obj.HasModule(spec.Module);
        if (spec.Field is null || !obj.HasModule(spec.Module))
            return false;
        return FieldMatch.Matches(
            modules.ResolveField(obj, spec.Module, spec.Field),
            spec.EqualsValue, spec.Min, spec.Max);
    }
}
