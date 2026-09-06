using System.Text.Json;
using AEngine.Core.Modules;
using AEngine.Core.Runtime;
using AEngine.Core.Signals;
using AEngine.Core.World;

namespace AEngine.Core.Actions;

/// <summary>
/// Context an effect evaluation resolves selectors against: the acting
/// agent (for effects triggered by an action), the action's target and
/// aux item, and the object carrying the effect list (the automation
/// rule, or the target whose module field holds it). Nulls are simply
/// unusable selectors — an effect naming "actor" from an automation has
/// no actor and resolves nothing.
/// </summary>
public sealed record EffectContext(
    WorldObject? Actor = null, WorldObject? Target = null, WorldObject? Aux = null,
    WorldObject? Self = null, Random? Random = null);

/// <summary>
/// The shared effect vocabulary: one JSON shape, applied by the
/// <c>effect</c> handler (an affordance's declared effects — the generic
/// verb engine, where all the scenario's small verbs live as data) and
/// by the world-clock automation pass (conditional rules and timers).
/// Effects address objects by id or by the context selectors
/// ("actor", "target", "aux", "self"); unrecognized ids and missing
/// selectors are skipped, so effect lists degrade gracefully in a world
/// that has moved on.
/// <para>
/// Kinds: <c>set</c>/<c>adjust</c> (module fields), <c>attach</c>/
/// <c>detach</c> (conditions), <c>move</c> (reparent — an agent's
/// arrival awards room-entry points), <c>teleport</c> (move to a room,
/// ditto), <c>scatter</c> (contents matching a filter to destinations —
/// death drops, robberies, curses), <c>destroy</c>, <c>spawn</c> (clone
/// a template), <c>transform</c> (replace an object with a template at
/// the same parent under the same id, so references survive),
/// <c>open</c>/<c>close</c>/<c>lock</c>/<c>unlock</c> (through shared
/// doorstates), <c>conceal</c>/<c>reveal</c> (the hideable flag),
/// <c>rename</c>, <c>addModule</c>/<c>removeModule</c>, <c>say</c> (a
/// private sensation), <c>signal</c> (a room observation),
/// <c>endsGame</c>.
/// </para>
/// </summary>
public static class Effects
{
    /// <summary>
    /// Apply a JSON array of effects. Stops for nothing: each effect
    /// resolves independently and missing objects are skipped.
    /// </summary>
    public static void Apply(
        GameEngine engine, JsonElement? list, EffectContext ctx)
    {
        if (list is not { ValueKind: JsonValueKind.Array } effects)
            return;
        foreach (var effect in effects.EnumerateArray())
            ApplyOne(engine, effect, ctx);
    }

    private static void ApplyOne(GameEngine engine, JsonElement effect, EffectContext ctx)
    {
        if (effect.ValueKind != JsonValueKind.Object)
            return;
        // a per-effect `when` guard (same condition kinds as automations):
        // the effect simply doesn't happen while it doesn't hold
        if (!RuleConditions.Evaluate(engine.World, engine.ModuleRegistry,
                effect.TryGetProperty("when", out var when) ? when : null, ctx))
            return;
        var world = engine.World;
        var modules = engine.ModuleRegistry;

        WorldObject? Sel(JsonElement? spec, string key) =>
            spec is not { } s || !s.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String
                ? null
                : Resolve(world, v.GetString()!, ctx);

        foreach (var prop in effect.EnumerateObject())
        {
            var args = prop.Value;
            switch (prop.Name)
            {
                case "set":
                {
                    var obj = Sel(args, "of") ?? Sel(args, "object");
                    var module = Str(args, "module");
                    var field = Str(args, "field");
                    if (obj is null || module is null || field is null ||
                        !obj.HasModule(module) || !args.TryGetProperty("value", out var value))
                        break;
                    world.SetFieldOverride(obj.Id, module, field, value.Clone());
                    break;
                }
                case "toggle":
                {
                    // flip a bool in place — the no-race way to reverse
                    // state from an effect list (conditional set+set pairs
                    // re-evaluate mid-list and undo themselves)
                    var obj = Sel(args, "of") ?? Sel(args, "object");
                    var module = Str(args, "module");
                    var field = Str(args, "field");
                    if (obj is null || module is null || field is null || !obj.HasModule(module))
                        break;
                    if (modules.ResolveField(obj, module, field) is
                            { ValueKind: JsonValueKind.True or JsonValueKind.False } current)
                        world.SetFieldOverride(obj.Id, module, field,
                            World.World.ToJson(!current.GetBoolean()));
                    break;
                }
                case "adjust":
                {
                    var obj = Sel(args, "of") ?? Sel(args, "object");
                    var module = Str(args, "module");
                    var field = Str(args, "field");
                    var by = Num(args, "by");
                    if (obj is null || module is null || field is null || by is null ||
                        !obj.HasModule(module))
                        break;
                    var current = modules.ResolveDouble(obj, module, field);
                    world.SetFieldOverride(obj.Id, module, field,
                        World.World.ToJson(current + by.Value));
                    break;
                }
                case "attach":
                {
                    var agent = Sel(args, "to");
                    var template = Str(args, "condition");
                    if (agent is not null && agent.HasModule("agent") &&
                        template is not null && world.HasObject(template))
                        Conditions.Attach(world, modules, agent, template);
                    break;
                }
                case "detach":
                {
                    var agent = Sel(args, "from");
                    var kind = Str(args, "kind");
                    if (agent is not null && agent.HasModule("agent") && kind is not null)
                        Conditions.Detach(world, modules, agent, kind);
                    break;
                }
                case "move":
                {
                    var obj = Sel(args, "object");
                    var dest = Sel(args, "to");
                    // "actorRoom" drops the thing at the acting agent's
                    // feet — the songbird's gift lands, it isn't handed over
                    if (dest is null && Str(args, "to") == "actorRoom" &&
                        ctx.Actor is { } actor)
                        dest = world.RoomOf(actor.Id);
                    if (obj is not null && dest is not null && obj.Id != World.World.RootId)
                        MoveAndAward(engine, obj, dest.Id, ctx);
                    break;
                }
                case "teleport":
                {
                    var obj = Sel(args, "object");
                    if (obj is null)
                        break;
                    // "to" is one room, or a list the engine picks from
                    // (seeded Random — the vampire bat's abduction)
                    if (args.TryGetProperty("to", out var toArray) &&
                        toArray.ValueKind == JsonValueKind.Array)
                    {
                        var rooms = toArray.EnumerateArray()
                            .Where(x => x.ValueKind == JsonValueKind.String)
                            .Select(x => x.GetString()!)
                            .Where(world.HasObject)
                            .ToList();
                        if (rooms.Count > 0)
                            MoveAndAward(engine, obj,
                                rooms[(ctx.Random ?? engine.Random).Next(rooms.Count)], ctx);
                    }
                    else if (Sel(args, "to") is { } room)
                    {
                        MoveAndAward(engine, obj, room.Id, ctx);
                    }
                    break;
                }
                case "scatter":
                {
                    Scatter(engine, args, ctx);
                    break;
                }
                case "destroy":
                {
                    var obj = Sel(args, "object");
                    if (obj is not null && obj.Id != World.World.RootId)
                        world.DestroyObject(obj.Id);
                    break;
                }
                case "spawn":
                {
                    var template = Str(args, "template");
                    var dest = Sel(args, "to");
                    if (template is not null && dest is not null && world.HasObject(template))
                    {
                        var id = template;
                        for (var n = 1; world.HasObject(id); n++)
                            id = $"{template}_{n}";
                        world.CloneTree(template, dest.Id, id);
                    }
                    break;
                }
                case "transform":
                {
                    var obj = Sel(args, "object");
                    var template = Str(args, "into");
                    if (obj is null || template is null || !world.HasObject(template) ||
                        obj.Id == World.World.RootId)
                        break;
                    var parent = obj.Parent;
                    var id = obj.Id;
                    world.DestroyObject(id);
                    world.CloneTree(template, parent, id);
                    break;
                }
                case "open":
                case "close":
                case "unlock":
                case "lock":
                {
                    var obj = Sel(args, "object");
                    if (obj is null)
                        break;
                    if (Perception.GetOpenState(world, modules, obj) is not { } state)
                        break;
                    if (prop.Name is "open" or "close")
                        world.SetFieldOverride(state.StateObject.Id, state.ModuleId, "open",
                            World.World.ToJson(prop.Name == "open"));
                    else
                        world.SetFieldOverride(state.StateObject.Id, state.ModuleId, "locked",
                            World.World.ToJson(prop.Name == "lock"));
                    break;
                }
                case "conceal":
                case "reveal":
                {
                    var obj = Sel(args, "object");
                    if (obj is not null && obj.HasModule("hideable"))
                        world.SetFieldOverride(obj.Id, "hideable", "concealed",
                            World.World.ToJson(prop.Name == "conceal"));
                    break;
                }
                case "rename":
                {
                    var obj = Sel(args, "object");
                    if (obj is null)
                        break;
                    if (Str(args, "name") is { } name)
                        obj.Name = name;
                    if (Str(args, "description") is { } description)
                        obj.Description = description;
                    break;
                }
                case "addModule":
                {
                    var obj = Sel(args, "object");
                    var module = Str(args, "module");
                    if (obj is not null && module is not null)
                        world.AddModule(obj.Id, module);
                    break;
                }
                case "removeModule":
                {
                    var obj = Sel(args, "object");
                    var module = Str(args, "module");
                    if (obj is not null && module is not null)
                        world.RemoveModule(obj.Id, module);
                    break;
                }
                case "say":
                {
                    var agent = Sel(args, "to");
                    if (agent is not null && agent.HasModule("agent") &&
                        Str(args, "text") is { } text)
                        engine.SignalBus.SendTo(agent, Interpolate(text, ctx));
                    break;
                }
                case "signal":
                {
                    var from = Sel(args, "from") ?? ctx.Actor;
                    if (from is not null && Str(args, "text") is { } text)
                        engine.SignalBus.Emit(from, ctx.Target,
                            [new SignalSpec
                            {
                                Sense = Str(args, "sense") == "audible"
                                    ? SignalSense.Audible
                                    : SignalSense.Visual,
                                Priority = 10,
                                Text = Interpolate(text, ctx),
                            }]);
                    break;
                }
                case "endsGame":
                {
                    engine.GameOver ??= Interpolate(Str(args, "text") ?? "It is over.", ctx);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Move an object (awarding the arrival room's entry points when the
    /// object is a scorecard-carrying agent) — the shared tail of the
    /// move and teleport effects.
    /// </summary>
    private static void MoveAndAward(
        GameEngine engine, WorldObject obj, string destId, EffectContext ctx)
    {
        engine.World.MoveObject(obj.Id, destId);
        if (obj.HasModule("agent") && obj.HasModule("scorecard") &&
            engine.World.HasObject(destId))
        {
            var room = engine.World.RoomOf(destId);
            var points = Score.AwardRoom(engine.World, engine.ModuleRegistry, room);
            if (points != 0)
                Score.Adjust(engine.World, engine.ModuleRegistry, obj, points);
        }
    }

    /// <summary>
    /// Scatter an object's contents to destinations: every child
    /// carrying the filter module (default portable, worn garments and
    /// internal anatomy excepted) moves out — to the room the holder is
    /// in ("here"), to a data map of per-item destinations
    /// (<c>special</c>), or to a random pick from a list of rooms
    /// (deterministic under a seeded Random). Death drops, thief
    /// robberies, and the skeleton's curse are all this one effect.
    /// </summary>
    private static void Scatter(GameEngine engine, JsonElement args, EffectContext ctx)
    {
        var world = engine.World;
        var modules = engine.ModuleRegistry;
        var from = ResolveSpec(world, args, "from", ctx);
        if (from is null)
            return;
        var filter = Str(args, "filterModule") ?? "portable";
        var dests = args.TryGetProperty("to", out var toArray) && toArray.ValueKind == JsonValueKind.Array
            ? toArray.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!)
                .Where(world.HasObject)
                .ToList()
            : [];
        Dictionary<string, string>? special = null;
        if (args.TryGetProperty("special", out var specialEl) &&
            specialEl.ValueKind == JsonValueKind.Object)
        {
            special = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var specialProp in specialEl.EnumerateObject())
                if (specialProp.Value.ValueKind == JsonValueKind.String &&
                    world.HasObject(specialProp.Value.GetString()!))
                    special[specialProp.Name] = specialProp.Value.GetString()!;
        }
        var here = world.RoomOf(from.Id).Id;
        var random = ctx.Random ?? engine.Random;
        foreach (var child in world.ChildrenOf(from.Id).ToList())
        {
            if (!child.HasModule(filter) || Clothing.IsWorn(modules, child) ||
                Conditions.IsInternal(child))
                continue;
            string dest;
            if (special is not null && special.TryGetValue(child.Id, out var mapped))
                dest = mapped;
            else if (Str(args, "to") == "here")
                dest = here;
            else if (dests.Count > 0)
                dest = dests[random.Next(dests.Count)];
            else
                continue;
            world.MoveObject(child.Id, dest);
        }
    }

    /// <summary>Resolve a selector: context names, else a literal object id.</summary>
    public static WorldObject? Resolve(
        World.World world, string selector, EffectContext ctx) =>
        selector switch
        {
            "actor" => ctx.Actor,
            "target" => ctx.Target,
            "aux" or "item" => ctx.Aux,
            "self" => ctx.Self,
            _ => world.HasObject(selector) ? world.GetObject(selector) : null,
        };

    private static WorldObject? ResolveSpec(
        World.World world, JsonElement args, string key, EffectContext ctx) =>
        args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? Resolve(world, v.GetString()!, ctx)
            : null;

    /// <summary>
    /// Interpolate {actor}/{target}/{item} name placeholders in effect
    /// texts (definite articles, empty when the referent is absent).
    /// Effects have no observer, so names render plainly.
    /// </summary>
    private static string Interpolate(string text, EffectContext ctx)
    {
        if (!text.Contains('{'))
            return text;
        return text
            .Replace("{actor}", PlainName(ctx.Actor), StringComparison.Ordinal)
            .Replace("{target}", PlainName(ctx.Target), StringComparison.Ordinal)
            .Replace("{item}", PlainName(ctx.Aux), StringComparison.Ordinal);
    }

    private static string PlainName(WorldObject? obj) =>
        obj is null ? "" : Perception.WithDefiniteArticle(obj.Name);

    private static string? Str(JsonElement obj, string name) =>
        GateArgs.String(obj, name);

    private static double? Num(JsonElement obj, string name) =>
        GateArgs.Number(obj, name);
}
