using AEngine.Core.Modules;
using AEngine.Core.World;

namespace AEngine.Core.Actions;

/// <summary>
/// Enumerates the actions currently available to an agent by scanning its
/// current room, the room's children (including contents of open
/// containers), portals, and the agent's inventory. Emits structured
/// (verb, target, label) menu entries.
/// </summary>
public sealed class ActionResolver
{
    private readonly World.World _world;
    private readonly ModuleRegistry _modules;

    public ActionResolver(World.World world, ModuleRegistry modules)
    {
        _world = world;
        _modules = modules;
    }

    /// <summary>
    /// The actions currently available to an agent, state-filtered for
    /// display: open/close follow the visible open state, take/drop follow
    /// held. (unlock/lock are always listed — lock state is not observable.)
    /// </summary>
    public IReadOnlyList<AvailableAction> Resolve(WorldObject agent) =>
        Resolve(agent, stateFiltered: true);

    /// <summary>
    /// All affordances on reachable objects without open/close state
    /// filtering — for plan matching: a generated but currently redundant
    /// "open"/"close" line still resolves here and noops at runtime.
    /// </summary>
    public IReadOnlyList<AvailableAction> ResolvePotential(WorldObject agent) =>
        Resolve(agent, stateFiltered: false);

    private IReadOnlyList<AvailableAction> Resolve(WorldObject agent, bool stateFiltered)
    {
        // room-granular location: a carried agent acts from the carrier's room
        var room = _world.RoomOf(agent.Id);
        var actions = new List<AvailableAction>();

        // other agents in the room, for directed-speech entries
        var others = room.Children
            .Where(id => id != agent.Id && _world.GetObject(id).HasModule("agent"))
            .Select(_world.GetObject)
            .ToList();

        // agent's own affordances (look, inventory, say, wait)
        AddFromModules(actions, agent, agent, stateFiltered, others);

        // things in the room (items, furniture, portals)
        foreach (var childId in room.Children)
        {
            if (childId == agent.Id)
                continue;
            var child = _world.GetObject(childId);
            AddFromModules(actions, agent, child, stateFiltered, others);

            // contents of open containers are reachable too
            if (child.HasModule("container") && IsOpenState(child))
            {
                foreach (var innerId in child.Children)
                    AddFromModules(actions, agent, _world.GetObject(innerId), stateFiltered, others);
            }
        }

        // inventory
        foreach (var itemId in agent.Children)
            AddFromModules(actions, agent, _world.GetObject(itemId), stateFiltered, others);

        return actions;
    }

    private void AddFromModules(
        List<AvailableAction> actions, WorldObject agent, WorldObject target, bool stateFiltered,
        IReadOnlyList<WorldObject> others)
    {
        foreach (var attachment in target.Modules)
        {
            if (!_modules.Has(attachment.ModuleId))
                continue;
            foreach (var affordance in _modules.Get(attachment.ModuleId).Affordances)
            {
                if (!Applies(affordance.Verb, agent, target, stateFiltered))
                    continue;
                // speech is parameterized: the label carries a {speech}
                // placeholder, plus an addressee when several agents are
                // present ("Say [to the old cook]: {speech}")
                if (affordance.Verb == "say" && target.Id == agent.Id)
                {
                    if (others.Count > 1)
                    {
                        foreach (var other in others)
                            actions.Add(new AvailableAction(
                                "say", other.Id, $"Say [to {other.Name}]: {{speech}}",
                                affordance.Handler, attachment.ModuleId, affordance.Prompt));
                    }
                    else
                    {
                        actions.Add(new AvailableAction(
                            "say", agent.Id, "Say: {speech}",
                            affordance.Handler, attachment.ModuleId, affordance.Prompt));
                    }
                    continue;
                }
                var label = LabelFor(affordance.Verb, target);
                actions.Add(new AvailableAction(
                    affordance.Verb, target.Id, label, affordance.Handler,
                    attachment.ModuleId, affordance.Prompt));
            }
        }
    }

    /// <summary>
    /// State-based filtering. Observability stays: take/drop by held.
    /// Lock state is not observable, so unlock/lock are always listed for
    /// lockable targets. open/close follow the visible open state in
    /// listings (stateFiltered) but are always present in the potential
    /// set so generated-but-redundant lines resolve and noop at runtime.
    /// </summary>
    private bool Applies(string verb, WorldObject agent, WorldObject target, bool stateFiltered)
    {
        bool held = target.Parent == agent.Id;
        return verb switch
        {
            "look" => target.Id == agent.Id,
            "inventory" => target.Id == agent.Id,
            "wait" => target.Id == agent.Id,
            "say" => target.Id == agent.Id, // speech comes from your own can_speak
            "take" => !held && target.Id != agent.Id, // can't pick up yourself
            "drop" => held && target.Id != agent.Id,
            "open" => HasOpenState(target) && (!stateFiltered || !IsOpenState(target)),
            "close" => HasOpenState(target) && (!stateFiltered || IsOpenState(target)),
            "unlock" => HasLockState(target),
            "lock" => HasLockState(target),
            _ => true,
        };
    }

    private bool HasOpenState(WorldObject target) => PortalOrSelf(target) is not null;

    private bool HasLockState(WorldObject target) =>
        target.HasModule("lockable");

    private bool IsOpenState(WorldObject target) => PortalOrSelf(target) is { } s &&
        _modules.ResolveBool(s.StateObject, s.ModuleId, "open");

    private (WorldObject StateObject, string ModuleId)? PortalOrSelf(WorldObject target)
    {
        if (target.HasModule("portal"))
        {
            var stateRef = _modules.ResolveString(target, "portal", "stateRef");
            if (stateRef is not null && _world.HasObject(stateRef))
                return (_world.GetObject(stateRef), "doorstate");
            return null;
        }
        return target.HasModule("openable") ? (target, "openable") : null;
    }

    private string LabelFor(string verb, WorldObject target) => verb switch
    {
        "look" => "Look around",
        "inventory" => "Check inventory",
        "wait" => "Wait",
        "go" => $"Go {_modules.ResolveString(target, "portal", "direction") ?? target.Name}",
        _ => $"{Capitalize(verb)} the {target.Name}",
    };

    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
