using AEngine.Core.Modules;
using AEngine.Core.World;

namespace AEngine.Core.Actions;

/// <summary>
/// Posture model: an agent's posture is derived from its position in the
/// world tree — parent is a room → standing; parent is an agent → carried;
/// parent is anything else (furniture) → the agent module's stored
/// <c>posture</c> field (set by sit/lie, cleared by stand/take/drop), so a
/// bed can offer both sitting and lying. Action compatibility is declared
/// on the affordance: <see cref="AffordanceDefinition.Postures"/> is an
/// allow-list (null = any posture) and
/// <see cref="AffordanceDefinition.SameSupport"/> requires the target to
/// share the agent's parent (occupants of the same bed).
/// </summary>
public static class Postures
{
    public const string Standing = "standing";
    public const string Sitting = "sitting";
    public const string Lying = "lying";
    public const string Carried = "carried";
    public const string Prone = "prone";

    /// <summary>
    /// The agent's current posture, derived from the tree. On the floor of
    /// a room the stored posture field applies (standing or prone); on
    /// furniture it records sitting/lying; under another agent the agent
    /// is carried (and the stored field is ignored).
    /// </summary>
    public static string Of(World.World world, ModuleRegistry modules, WorldObject agent)
    {
        if (agent.Parent.Length == 0 || !world.HasObject(agent.Parent))
            return Standing;
        var parent = world.GetObject(agent.Parent);
        if (parent.HasModule("agent"))
            return Carried;
        return modules.ResolveString(agent, "agent", "posture") ??
               (parent.HasModule("room") ? Standing : Sitting);
    }

    /// <summary>
    /// Whether the affordance is usable by the agent against the target in
    /// the agent's current posture: the affordance's posture allow-list and
    /// its same-support requirement (target shares the agent's parent).
    /// </summary>
    public static bool CanUse(
        World.World world, ModuleRegistry modules, AffordanceDefinition affordance,
        WorldObject agent, WorldObject target)
    {
        if (affordance.Postures is { } postures && !postures.Contains(Of(world, modules, agent)))
            return false;
        if (affordance.SameSupport && target.Parent != agent.Parent)
            return false;
        return true;
    }
}
