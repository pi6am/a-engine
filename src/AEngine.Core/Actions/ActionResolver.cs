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

    public IReadOnlyList<AvailableAction> Resolve(WorldObject agent)
    {
        var room = _world.GetObject(agent.Parent);
        var actions = new List<AvailableAction>();

        // agent's own affordances (look, inventory)
        AddFromModules(actions, agent, agent, room);

        // things in the room (items, furniture, portals)
        foreach (var childId in room.Children)
        {
            if (childId == agent.Id)
                continue;
            var child = _world.GetObject(childId);
            AddFromModules(actions, agent, child, room);

            // contents of open containers are reachable too
            if (child.HasModule("container") && IsOpenState(child))
            {
                foreach (var innerId in child.Children)
                    AddFromModules(actions, agent, _world.GetObject(innerId), room);
            }
        }

        // inventory
        foreach (var itemId in agent.Children)
            AddFromModules(actions, agent, _world.GetObject(itemId), room);

        return actions;
    }

    private void AddFromModules(
        List<AvailableAction> actions, WorldObject agent, WorldObject target, WorldObject room)
    {
        foreach (var attachment in target.Modules)
        {
            if (!_modules.Has(attachment.ModuleId))
                continue;
            foreach (var affordance in _modules.Get(attachment.ModuleId).Affordances)
            {
                if (!Applies(affordance.Verb, agent, target))
                    continue;
                var label = LabelFor(affordance.Verb, target);
                actions.Add(new AvailableAction(affordance.Verb, target.Id, label, affordance.Handler));
            }
        }
    }

    /// <summary>State-based filtering so menus only show sensible verbs.</summary>
    private bool Applies(string verb, WorldObject agent, WorldObject target)
    {
        bool held = target.Parent == agent.Id;
        return verb switch
        {
            "look" => target.Id == agent.Id,
            "inventory" => target.Id == agent.Id,
            "take" => !held,
            "drop" => held,
            "open" => !IsOpenState(target) && !IsLockedState(target),
            "close" => IsOpenState(target),
            "unlock" => IsLockedState(target),
            "lock" => HasLockState(target) && !IsLockedState(target),
            _ => true,
        };
    }

    private bool HasLockState(WorldObject target) =>
        target.HasModule("lockable");

    private bool IsOpenState(WorldObject target) => PortalOrSelf(target) is { } s &&
        _modules.ResolveBool(s.StateObject, s.ModuleId, "open");

    private bool IsLockedState(WorldObject target) => PortalOrSelf(target) is { } s &&
        _modules.ResolveBool(s.StateObject, s.ModuleId, "locked");

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
        "go" => $"Go {_modules.ResolveString(target, "portal", "direction") ?? target.Name}",
        _ => $"{Capitalize(verb)} the {target.Name}",
    };

    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
