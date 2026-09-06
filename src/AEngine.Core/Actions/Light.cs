using AEngine.Core.Modules;
using AEngine.Core.Runtime;
using AEngine.Core.World;

namespace AEngine.Core.Actions;

/// <summary>
/// Darkness and light. A room carrying <c>dark: true</c> on its room
/// module is unlit unless a light source is present: any object with the
/// `lightsource` module (<c>on</c>, not <c>dead</c>) that is visible in
/// the room — lying there, on a surface, in an OPEN container, held by
/// anyone present, or in the acting agent's own pockets — lights it. An
/// agent with <c>alwaysLit</c> (a ghost) sees regardless. While unlit,
/// the resolver strips the room's affordances away (see
/// <see cref="ActionResolver"/>), look prints the scenario's darkText
/// (default "It is pitch black."), and moving through an unlit dark room
/// risks a data-defined hazard (rules module: darkHazardChance, a
/// percentage per move, and its darkHazardText/Condition — the engine is
/// generic; a scenario that does not opt in has no hazard).
/// </summary>
public static class Light
{
    /// <summary>
    /// Whether the agent can see: always-lit agents (ghosts), lit rooms,
    /// or a live light source present in the room or the agent's own
    /// belongings. One implementation — the room query carries it.
    /// </summary>
    public static bool IsLit(World.World world, ModuleRegistry modules, WorldObject agent)
    {
        if (modules.ResolveBool(agent, "agent", "alwaysLit"))
            return true;
        return RoomIsLit(world, modules, world.RoomOf(agent.Id), agent);
    }

    /// <summary>
    /// Whether a room would be lit for an agent standing in it — the
    /// shared room query behind IsLit and the go handler's
    /// destination check.
    /// </summary>
    public static bool RoomIsLit(
        World.World world, ModuleRegistry modules, WorldObject room, WorldObject agent) =>
        !modules.ResolveBool(room, "room", "dark") ||
        modules.ResolveBool(agent, "agent", "alwaysLit") ||
        LightsPresent(world, modules, room, agent);

    private static bool LightsPresent(
        World.World world, ModuleRegistry modules, WorldObject room, WorldObject agent)
    {
        // the agent's own belongings: direct children plus one level into
        // open containers (a lamp inside a closed sack lights nothing)
        if (LightsOnTree(world, modules, agent))
            return true;
        // the room: children, their open containers and surfaces, and
        // other agents' belongings
        foreach (var child in world.ChildrenOf(room.Id))
            if (LightsOnTree(world, modules, child))
                return true;
        return false;
    }

    private static bool LightsOnTree(World.World world, ModuleRegistry modules, WorldObject obj)
    {
        if (IsBurning(modules, obj))
            return true;
        foreach (var child in world.ChildrenOf(obj.Id))
        {
            // descend into open containers, surfaces, and agents (pockets
            // of someone present); closed containers stay dark inside
            if (!obj.HasModule("container") ||
                Perception.IsOpen(world, modules, obj) ||
                obj.HasModule("surface") ||
                obj.HasModule("agent"))
            {
                if (LightsOnTree(world, modules, child))
                    return true;
            }
        }
        return false;
    }

    /// <summary>A live light source: the module attached, on, and not burned out.</summary>
    public static bool IsBurning(ModuleRegistry modules, WorldObject obj) =>
        obj.HasModule("lightsource") &&
        modules.ResolveBool(obj, "lightsource", "on") &&
        !modules.ResolveBool(obj, "lightsource", "dead");

    /// <summary>
    /// Burn fuel on every burning light source with a finite fuel count,
    /// by whole turns of world time. Each burnStages entry
    /// ("<c>threshold|message</c>") announces itself to the light's
    /// holder the first time the remaining fuel drops below its
    /// threshold; fuel reaching zero snuffs the light (on=false,
    /// dead=true) with the module's <c>outText</c> (default: "The {name}
    /// has gone out."). Infinite fuel (-1, the default) never burns.
    /// </summary>
    public static void Advance(GameEngine engine, int turns)
    {
        var world = engine.World;
        var modules = engine.ModuleRegistry;
        foreach (var obj in world.Objects.Values.OrderBy(o => o.Id, StringComparer.Ordinal))
        {
            if (!obj.HasModule("lightsource"))
                continue;
            var fuel = modules.ResolveInt(obj, "lightsource", "fuel", -1);
            if (fuel < 0 || !IsBurning(modules, obj))
                continue;
            var before = fuel;
            var after = Math.Max(0, fuel - turns);
            world.SetFieldOverride(obj.Id, "lightsource", "fuel", World.World.ToJson(after));
            var holder = obj.Parent.Length > 0 && world.HasObject(obj.Parent)
                ? world.GetObject(obj.Parent)
                : null;
            foreach (var stage in modules.ResolveStringList(obj, "lightsource", "burnStages") ?? [])
            {
                var bar = stage.IndexOf('|');
                if (bar <= 0 ||
                    !int.TryParse(stage[..bar], out var threshold) ||
                    !(before >= threshold && after < threshold))
                    continue;
                if (holder is not null && holder.HasModule("agent"))
                    engine.SignalBus.SendTo(holder, stage[(bar + 1)..]);
            }
            if (after == 0 && before > 0)
            {
                world.SetFieldOverride(obj.Id, "lightsource", "on", World.World.ToJson(false));
                world.SetFieldOverride(obj.Id, "lightsource", "dead", World.World.ToJson(true));
                if (holder is not null && holder.HasModule("agent"))
                    engine.SignalBus.SendTo(holder,
                        modules.ResolveString(obj, "lightsource", "outText") is { Length: > 0 } outText
                            ? outText.Replace("{name}", obj.Name, StringComparison.Ordinal)
                            : $"The {obj.Name} has gone out.");
            }
        }
    }
}
