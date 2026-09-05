using System.Text.Json;
using AEngine.Core.Actions;
using AEngine.Core.Modules;
using AEngine.Core.Signals;
using AEngine.Core.World;

namespace AEngine.Core.Runtime;

/// <summary>
/// World-clock upkeep for data-driven <b>motives</b> — simulated
/// floating-point values on agents, declared entirely in scenario data.
/// Any module that declares a <c>motives</c> list field becomes a motive
/// group: each entry names one motive (whose value lives in the field of
/// the same name on that module, so overrides, <c>when</c> specs, gates,
/// the LLM context, and the debug API all read it like any field) plus
/// its dynamics:
/// <list type="bullet">
/// <item><c>drift</c> — ordered rules, first match wins: a rule is a
/// rate plus an optional target (a constant or another motive) and mode
/// (<c>linear</c>: constant speed toward the target, hard-stopping at
/// it; <c>proportional</c>: exponential approach integrated exactly as
/// <c>x += (target − x) · (1 − e^(−rate·dt))</c>, stable at any step
/// size). Rules may carry <c>when</c> conditions on other motives'
/// ranges (frustration rising while arousal runs high and pleasure
/// low) — a motive with no drift is static and moves only through
/// impulses.</item>
/// <item><c>routes</c> — the amount a motive <i>loses</i> to drift
/// flows into other motives as gain × factor (alcohol burning into a
/// bladder). One hop: routed gains do not route further.</item>
/// <item><c>bands</c> — exclusive threshold bands (highest reached
/// wins) attaching condition templates, with the templates'
/// selfText/clearText as private transition signals. Bands evaluate on
/// the value divided by <c>scaleField</c> when authored, so one band
/// table serves agents of different capacities.</item>
/// <item><c>onFull</c>/<c>onEmpty</c> — threshold events firing when
/// the motive reaches a finite bound: absolute <c>set</c>s and clamped
/// <c>adjust</c>s (the orgasm's resets), condition <c>attach</c>es, a
/// <c>self</c> text, and an ambient <c>signal</c> — the prose stays in
/// data. The event's sets must move the motive off its bound or it
/// re-fires on the next pass.</item>
/// </list>
/// Integration is Jacobi-style for stability: each sub-step evaluates
/// every rule and target against the pre-step snapshot, then applies
/// all deltas together — motive order can't change the outcome, and no
/// motive feeds back into its own step. Steps are capped at the
/// module's <c>maxStep</c> seconds (default 1, matching the real-time
/// tick, so turn-based and real-time trajectories coincide); every
/// drift mode either hard-stops at its target or integrates exactly,
/// so no step size overshoots. <see cref="Advance"/> is the world-clock
/// pass (0 seconds = band/event sync only — the initial state applies
/// on load); <see cref="Impulse"/> is the event-driven side handlers
/// call (clamped deltas, immediate band sync, returns the ids of fired
/// events). The engine knows nothing about specific motives — the
/// tavern's drunkenness sim and the intimacy sim are both just data.
/// </summary>
public static class Motives
{
    /// <summary>A drift-rule condition: a motive (or module field) against min/max/equals.</summary>
    private sealed record MotiveWhen(
        string? Module, string Field, JsonElement? EqualsValue, double? Min, double? Max);

    private sealed record DriftRule(
        List<MotiveWhen> When, bool Proportional, double Rate,
        double? TargetValue, string? TargetMotive);

    private sealed record RouteSpec(string To, double Factor);

    private sealed record BandSpec(double Min, string TemplateId);

    private sealed record EventSpec(
        string Id, Dictionary<string, double> Set, Dictionary<string, double> Adjust,
        List<string> Attach, string? Self, SignalSpec? Signal, bool SilentBands,
        EmitSpec? Emit);

    /// <summary>
    /// An event-driven object emission: on firing, a template is cloned
    /// to a rule-chosen location — collected into a worn garment
    /// carrying the named module (a condom fills), else into the
    /// orifice an ongoing embrace holds the emitter's probe in, else
    /// beside the emitter. The engine knows nothing about what is
    /// emitted; template, probe tag, and collector module are data.
    /// </summary>
    private sealed record EmitSpec(string Template, string? Probe, string? CollectModule);

    private sealed record MotiveDef(
        string ModuleId, string Id, double Min, double Max, string? ScaleField,
        List<DriftRule> Drift, List<RouteSpec> Routes, List<BandSpec> Bands,
        EventSpec? OnFull, EventSpec? OnEmpty);

    private sealed class MotiveState(MotiveDef def, double value)
    {
        internal MotiveDef Def = def;
        internal double Value = value;
        internal readonly double Start = value;
    }

    /// <summary>
    /// Advance every motive-carrying agent by seconds of world time. In
    /// turn-based mode pass <paramref name="onlyAgentId"/> so each action
    /// advances only its ACTOR's motives — otherwise N agents acting once
    /// per round would run the world N× fast, and everyone sobers up (and
    /// fills up, and cools off) at the cast size's pace. Zero seconds
    /// re-evaluates bands and events without drifting — the initial sync
    /// after a scenario loads (the drunk elf is drunk on turn 0).
    /// </summary>
    public static void Advance(GameEngine engine, double seconds, string? onlyAgentId = null)
    {
        var agents = engine.World.Objects.Values
            .Where(o => o.HasModule("agent"))
            .Where(o => onlyAgentId is null || o.Id == onlyAgentId)
            .ToList();
        foreach (var agent in agents)
        {
            var modules = engine.ModuleRegistry;
            var groups = Groups(modules, agent);
            if (groups.Count == 0)
                continue;
            var motives = Load(modules, agent, groups);
            if (motives.Count == 0)
                continue;
            if (seconds > 0)
            {
                var maxStep = Math.Max(0.001,
                    groups.Min(g => modules.ResolveDouble(agent, g, "maxStep", 1.0)));
                var remaining = seconds;
                while (remaining > 1e-9)
                {
                    var dt = Math.Min(maxStep, remaining);
                    remaining -= dt;
                    Step(modules, agent, motives, dt);
                }
            }
            EvaluateBands(engine.World, modules, engine.SignalBus, agent, motives);
            FireEvents(engine.World, modules, engine.SignalBus, agent, motives);
            WriteChanged(engine.World, agent, motives);
        }
    }

    /// <summary>
    /// Apply an event-driven impulse: clamped deltas to named motives
    /// (the touch handlers' arousal/pleasure/comfort, a drink's alcohol),
    /// then the same band/event tail <see cref="Advance"/> ends with —
    /// bands attach the moment a value crosses, not next tick. Returns
    /// the ids of fired events so handlers can react (the touch
    /// handler's climax suffix).
    /// </summary>
    public static List<string> Impulse(
        World.World world, ModuleRegistry modules, SignalBus signals,
        WorldObject agent, Dictionary<string, double> deltas,
        WorldObject? engagedOrifice = null)
    {
        var fired = new List<string>();
        var groups = Groups(modules, agent);
        if (groups.Count == 0)
            return fired;
        var motives = Load(modules, agent, groups);
        if (motives.Count == 0)
            return fired;
        foreach (var (id, delta) in deltas)
            if (motives.TryGetValue(id, out var m))
                m.Value = Math.Clamp(m.Value + delta, m.Def.Min, m.Def.Max);
        EvaluateBands(world, modules, signals, agent, motives);
        fired.AddRange(FireEvents(world, modules, signals, agent, motives, engagedOrifice));
        WriteChanged(world, agent, motives);
        return fired;
    }

    /// <summary>
    /// The module id carrying a named motive on an agent (for handlers
    /// that read or write a motive field without knowing the scenario's
    /// module naming), or null when the agent doesn't track it.
    /// </summary>
    public static string? FindMotiveModule(ModuleRegistry modules, WorldObject agent, string motiveId)
    {
        foreach (var group in Groups(modules, agent))
            if (ParseDefs(modules, agent, group).Any(d => d.Id == motiveId))
                return group;
        return null;
    }

    /// <summary>Attached modules whose definitions declare a `motives` field.</summary>
    private static List<string> Groups(ModuleRegistry modules, WorldObject agent) =>
        agent.Modules
            .Where(a => modules.Has(a.ModuleId) &&
                        modules.Get(a.ModuleId).GetField("motives") is not null)
            .Select(a => a.ModuleId)
            .ToList();

    private static Dictionary<string, MotiveState> Load(
        ModuleRegistry modules, WorldObject agent, List<string> groups)
    {
        var motives = new Dictionary<string, MotiveState>(StringComparer.Ordinal);
        foreach (var group in groups)
            foreach (var def in ParseDefs(modules, agent, group))
                if (!motives.ContainsKey(def.Id)) // first group wins on duplicate ids
                    motives[def.Id] = new MotiveState(
                        def, modules.ResolveDouble(agent, group, def.Id));
        return motives;
    }

    /// <summary>
    /// One integration sub-step, Jacobi-style: every motive's drift is
    /// computed from the pre-step snapshot, routed gains flow from the
    /// actual (post-clamp) losses, and only then are values committed —
    /// so motive order never changes the outcome and nothing feeds back
    /// into its own step.
    /// </summary>
    private static void Step(
        ModuleRegistry modules, WorldObject agent,
        Dictionary<string, MotiveState> motives, double dt)
    {
        var staged = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (id, m) in motives)
        {
            var next = m.Value;
            if (FirstRule(modules, agent, m, motives) is { } rule)
            {
                var target = rule.TargetValue ??
                    (rule.TargetMotive is { } name &&
                     motives.TryGetValue(name, out var other) ? other.Value : null);
                if (rule.Proportional && target is { } pt)
                    // exact exponential approach: stable, never overshoots
                    next = m.Value + (pt - m.Value) *
                        (1 - Math.Exp(-Math.Max(0, rule.Rate) * dt));
                else if (target is { } lt)
                {
                    // constant speed toward the target, hard-stopping at it
                    var step = Math.Abs(rule.Rate) * dt;
                    next = m.Value < lt ? Math.Min(lt, m.Value + step)
                         : m.Value > lt ? Math.Max(lt, m.Value - step)
                         : lt;
                }
                else
                    next = m.Value + rule.Rate * dt;
            }
            staged[id] = Math.Clamp(next, m.Def.Min, m.Def.Max);
        }
        // drift losses flow through routes (one hop, gains clamped)
        foreach (var m in motives.Values)
        {
            if (m.Def.Routes.Count == 0)
                continue;
            var loss = m.Value - staged[m.Def.Id];
            if (loss <= 0)
                continue;
            foreach (var route in m.Def.Routes)
                if (staged.TryGetValue(route.To, out var gain))
                    staged[route.To] = Math.Clamp(
                        gain + loss * route.Factor,
                        motives[route.To].Def.Min, motives[route.To].Def.Max);
        }
        foreach (var (id, value) in staged)
            motives[id].Value = value;
    }

    private static DriftRule? FirstRule(
        ModuleRegistry modules, WorldObject agent,
        MotiveState m, Dictionary<string, MotiveState> motives)
    {
        foreach (var rule in m.Def.Drift)
            if (WhenMatches(rule.When, m.Def, modules, agent, motives))
                return rule;
        return null;
    }

    /// <summary>
    /// A drift rule's conditions. Specs naming a foreign module resolve
    /// through the registry (same matching rules as the resolver's When
    /// specs); module-less specs read the agent's motives from the live
    /// snapshot, falling back to a plain field of the owning module.
    /// </summary>
    private static bool WhenMatches(
        List<MotiveWhen> when, MotiveDef def, ModuleRegistry modules,
        WorldObject agent, Dictionary<string, MotiveState> motives)
    {
        foreach (var spec in when)
        {
            if (spec.Module is { Length: > 0 } other)
            {
                if (!agent.HasModule(other) ||
                    !FieldMatch.Matches(
                        modules.ResolveField(agent, other, spec.Field),
                        spec.EqualsValue, spec.Min, spec.Max))
                    return false;
                continue;
            }
            if (motives.TryGetValue(spec.Field, out var m))
            {
                if (!NumberMatches(m.Value, spec))
                    return false;
            }
            else if (!FieldMatch.Matches(
                         modules.ResolveField(agent, def.ModuleId, spec.Field),
                         spec.EqualsValue, spec.Min, spec.Max))
                return false;
        }
        return true;
    }

    /// <summary>A snapshot motive value against a condition (equals compares numerically).</summary>
    private static bool NumberMatches(double value, MotiveWhen spec)
    {
        if (spec.EqualsValue is { } eq)
        {
            if (eq.ValueKind != JsonValueKind.Number || eq.GetDouble() != value)
                return false;
        }
        if (spec.Min is { } lo && value < lo)
            return false;
        if (spec.Max is { } hi && value > hi)
            return false;
        return true;
    }

    /// <summary>Attach/detach every motive's band conditions (highest reached wins).</summary>
    private static void EvaluateBands(
        World.World world, ModuleRegistry modules, SignalBus signals,
        WorldObject agent, Dictionary<string, MotiveState> motives, bool silent = false)
    {
        foreach (var m in motives.Values)
            ApplyBands(world, modules, signals, agent, m, silent);
    }

    private static void ApplyBands(
        World.World world, ModuleRegistry modules, SignalBus signals,
        WorldObject agent, MotiveState m, bool silent)
    {
        if (m.Def.Bands.Count == 0)
            return;
        var scale = 1.0;
        if (m.Def.ScaleField is { } field)
            scale = Math.Max(0.000001,
                modules.ResolveDouble(agent, m.Def.ModuleId, field, 1.0));
        var ratio = m.Value / scale;
        BandSpec? active = null;
        foreach (var band in m.Def.Bands)
            if (ratio >= band.Min && (active is null || band.Min > active.Min))
                active = band;
        foreach (var band in m.Def.Bands)
        {
            if (!world.HasObject(band.TemplateId))
                continue;
            var template = world.GetObject(band.TemplateId);
            var kind = Conditions.KindOf(modules, template);
            if (ReferenceEquals(band, active))
            {
                var wasPresent = Conditions.Has(world, modules, agent, kind);
                Conditions.Attach(world, modules, agent, band.TemplateId);
                if (!wasPresent && !silent)
                {
                    var arrived = modules.ResolveString(template, "condition", "selfText") is
                        { Length: > 0 } text
                            ? text
                            : $"You feel {Conditions.LabelOf(modules, template)}.";
                    signals.SendTo(agent, arrived);
                }
            }
            else if (Conditions.Detach(world, modules, agent, kind) && !silent)
            {
                if (modules.ResolveString(template, "condition", "clearText") is { Length: > 0 } faded)
                    signals.SendTo(agent, faded);
            }
        }
    }

    /// <summary>
    /// Fire every motive's threshold event whose bound it sits at, in
    /// motive order: apply the event's sets/adjusts (clamped), re-derive
    /// bands from the new values (silently when the event asks — the
    /// orgasm's falling-away bands shouldn't talk over the moment), then
    /// attach its conditions and deliver its texts.
    /// </summary>
    private static List<string> FireEvents(
        World.World world, ModuleRegistry modules, SignalBus signals,
        WorldObject agent, Dictionary<string, MotiveState> motives,
        WorldObject? engagedOrifice = null)
    {
        var fired = new List<string>();
        foreach (var m in motives.Values)
        {
            var ev = m.Def.Max < double.MaxValue && m.Value >= m.Def.Max - 1e-9
                ? m.Def.OnFull
                : m.Def.Min > double.MinValue && m.Value <= m.Def.Min + 1e-9
                    ? m.Def.OnEmpty
                    : null;
            if (ev is null)
                continue;
            fired.Add(ev.Id);
            foreach (var (id, value) in ev.Set)
                if (motives.TryGetValue(id, out var t))
                    t.Value = Math.Clamp(value, t.Def.Min, t.Def.Max);
            foreach (var (id, delta) in ev.Adjust)
                if (motives.TryGetValue(id, out var t))
                    t.Value = Math.Clamp(t.Value + delta, t.Def.Min, t.Def.Max);
            EvaluateBands(world, modules, signals, agent, motives, ev.SilentBands);
            foreach (var templateId in ev.Attach)
                if (world.HasObject(templateId))
                    Conditions.Attach(world, modules, agent, templateId);
            if (ev.Self is { Length: > 0 })
                signals.SendTo(agent, ev.Self);
            if (ev.Signal is { } spec)
                signals.Emit(agent, null, [spec], null);
            if (ev.Emit is { } emit)
                Actions.Spawning.Emit(world, modules, agent, emit.Template,
                    emit.Probe, emit.CollectModule, engagedOrifice);
        }
        return fired;
    }

    private static void WriteChanged(
        World.World world, WorldObject agent, Dictionary<string, MotiveState> motives)
    {
        foreach (var m in motives.Values)
            if (Math.Abs(m.Value - m.Start) > 1e-12)
                world.SetFieldOverride(
                    agent.Id, m.Def.ModuleId, m.Def.Id, World.World.ToJson(m.Value));
    }

    // ------------------------------------------------------------------
    // parsing: motive definitions from a module's `motives` list field
    // (per-object override -> module default), tolerant of junk entries
    // ------------------------------------------------------------------

    private static List<MotiveDef> ParseDefs(
        ModuleRegistry modules, WorldObject agent, string group)
    {
        var defs = new List<MotiveDef>();
        if (modules.ResolveField(agent, group, "motives") is not
                { ValueKind: JsonValueKind.Array } list)
            return defs;
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("id", out var idEl) ||
                idEl.ValueKind != JsonValueKind.String)
                continue;
            var min = Bound(item, "min", 0.0, double.MinValue);
            var max = Bound(item, "max", 1.0, double.MaxValue);
            if (min > max)
                min = max; // a malformed range pins the motive instead of throwing
            defs.Add(new MotiveDef(
                group, idEl.GetString()!, min, max,
                Str(item, "scaleField"),
                ParseDrift(item),
                ParseList(item, "routes", r => new RouteSpec(
                    Str(r, "to") ?? "", Num(r, "factor") ?? 1.0)),
                ParseList(item, "bands", b => new BandSpec(
                    Num(b, "min") ?? 0.0, Str(b, "condition") ?? "")),
                ParseEvent(item, "onFull"),
                ParseEvent(item, "onEmpty")));
        }
        return defs;
    }

    /// <summary>A bound with a default; an explicit JSON null means unbounded.</summary>
    private static double Bound(JsonElement obj, string name, double fallback, double unbounded) =>
        obj.TryGetProperty(name, out var e)
            ? e.ValueKind == JsonValueKind.Number ? e.GetDouble() : unbounded
            : fallback;

    private static List<DriftRule> ParseDrift(JsonElement obj)
    {
        var rules = new List<DriftRule>();
        if (!obj.TryGetProperty("drift", out var list) || list.ValueKind != JsonValueKind.Array)
            return rules;
        foreach (var r in list.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object)
                continue;
            var when = new List<MotiveWhen>();
            if (r.TryGetProperty("when", out var conditions) &&
                conditions.ValueKind == JsonValueKind.Array)
                foreach (var c in conditions.EnumerateArray())
                {
                    if (c.ValueKind != JsonValueKind.Object ||
                        !c.TryGetProperty("field", out var f) ||
                        f.ValueKind != JsonValueKind.String)
                        continue;
                    when.Add(new MotiveWhen(
                        Str(c, "module"), f.GetString()!,
                        c.TryGetProperty("equals", out var eq) &&
                            eq.ValueKind is JsonValueKind.Number or JsonValueKind.True
                                or JsonValueKind.False or JsonValueKind.String
                            ? eq : null,
                        Num(c, "min"), Num(c, "max")));
                }
            double? targetValue = null;
            string? targetMotive = null;
            if (r.TryGetProperty("target", out var t))
            {
                if (t.ValueKind == JsonValueKind.Number)
                    targetValue = t.GetDouble();
                else if (t.ValueKind == JsonValueKind.String)
                    targetMotive = t.GetString()!;
            }
            rules.Add(new DriftRule(
                when, Str(r, "mode") == "proportional", Num(r, "rate") ?? 0,
                targetValue, targetMotive));
        }
        return rules;
    }

    private static List<T> ParseList<T>(JsonElement obj, string name, Func<JsonElement, T> parse)
    {
        var result = new List<T>();
        if (!obj.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var item in list.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Object)
                result.Add(parse(item));
        return result;
    }

    private static EventSpec? ParseEvent(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.Object)
            return null;
        var id = Str(e, "id") ?? $"{Str(obj, "id")}.{name[2..].ToLowerInvariant()}";
        var attach = new List<string>();
        if (e.TryGetProperty("attach", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String &&
                    item.GetString() is { Length: > 0 } templateId)
                    attach.Add(templateId);
                else if (item.ValueKind == JsonValueKind.Object &&
                         (Str(item, "condition") ?? Str(item, "template")) is { Length: > 0 } refd)
                    attach.Add(refd);
            }
        return new EventSpec(
            id, NumberMap(e, "set"), NumberMap(e, "adjust"),
            attach, Str(e, "self"), ParseSignal(e),
            e.TryGetProperty("silentBands", out var sb) && sb.ValueKind == JsonValueKind.True,
            ParseEmit(e));
    }

    private static EmitSpec? ParseEmit(JsonElement obj)
    {
        if (!obj.TryGetProperty("emit", out var e) || e.ValueKind != JsonValueKind.Object)
            return null;
        var template = Str(e, "template");
        if (template is not { Length: > 0 })
            return null;
        return new EmitSpec(template, Str(e, "probe"), Str(e, "collectModule"));
    }

    private static Dictionary<string, double> NumberMap(JsonElement obj, string name)
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        if (obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Object)
            foreach (var prop in e.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.Number)
                    map[prop.Name] = prop.Value.GetDouble();
        return map;
    }

    private static SignalSpec? ParseSignal(JsonElement obj)
    {
        if (!obj.TryGetProperty("signal", out var e) || e.ValueKind != JsonValueKind.Object ||
            !e.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
            return null;
        var sense = SignalSense.Audible;
        if (e.TryGetProperty("sense", out var s) && s.ValueKind == JsonValueKind.String &&
            Enum.TryParse(s.GetString(), ignoreCase: true, out SignalSense parsed))
            sense = parsed;
        return new SignalSpec
        {
            Sense = sense,
            Text = text.GetString()!,
            Priority = (int)(Num(e, "priority") ?? 5),
            Salience = (int)(Num(e, "salience") ?? 0),
        };
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString()
            : null;

    private static double? Num(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number
            ? e.GetDouble()
            : null;
}
