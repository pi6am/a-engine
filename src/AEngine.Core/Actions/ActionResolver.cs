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
        var posture = Postures.Of(_world, _modules, agent);
        var actions = new List<AvailableAction>();

        // other agents in the room, for directed-speech entries — including
        // agents seated on furniture (grandchildren of the room)
        var others = new List<WorldObject>();
        foreach (var childId in room.Children)
        {
            if (childId == agent.Id)
                continue;
            var child = _world.GetObject(childId);
            if (child.HasModule("agent"))
                others.Add(child);
            foreach (var occupantId in child.Children)
            {
                if (occupantId != agent.Id && _world.GetObject(occupantId).HasModule("agent"))
                    others.Add(_world.GetObject(occupantId));
            }
        }

        // agent's own affordances (look, inventory, say, wait)
        var examinable = new List<WorldObject>();
        AddFromModules(actions, agent, agent, stateFiltered, others, examinable);

        // an incapacitated agent can only look
        if (Health.IsIncapacitated(_modules, agent))
        {
            actions.RemoveAll(a => a.Verb != "look");
            return actions;
        }

        // a carried agent can only use its own verbs (look/say/wait/...) —
        // no escape until the carrier puts it down
        if (posture == Postures.Carried)
            return actions;

        // things in the room (items, furniture, portals)
        foreach (var childId in room.Children)
        {
            if (childId == agent.Id)
                continue;
            var child = _world.GetObject(childId);
            AddFromModules(actions, agent, child, stateFiltered, others, examinable);

            // other agents' pockets are scan targets too (steal), as are
            // their worn garments (remove); Applies restricts items held by
            // another agent to those verbs only
            if (child.HasModule("agent"))
            {
                foreach (var pocketId in child.Children)
                    AddFromModules(actions, agent, _world.GetObject(pocketId), stateFiltered, others, examinable);
            }

            // occupants of furniture are reachable (cuddle a bed-mate, talk
            // to a seated agent), as are contents of open containers
            if (child.HasModule("sittable") || child.HasModule("lyable"))
            {
                foreach (var occupantId in child.Children)
                {
                    var occupant = _world.GetObject(occupantId);
                    AddFromModules(actions, agent, occupant, stateFiltered, others, examinable);
                    if (occupant.HasModule("agent"))
                    {
                        foreach (var pocketId in occupant.Children)
                            AddFromModules(actions, agent, _world.GetObject(pocketId), stateFiltered, others, examinable);
                    }
                }
            }
            if (child.HasModule("container") && IsOpenState(child))
            {
                foreach (var innerId in child.Children)
                    AddFromModules(actions, agent, _world.GetObject(innerId), stateFiltered, others, examinable);
            }
        }

        // inventory
        foreach (var itemId in agent.Children)
            AddFromModules(actions, agent, _world.GetObject(itemId), stateFiltered, others, examinable);

        // everything visible can be examined in detail — a universal verb
        // with no module/affordance of its own (moduleId "" skips
        // affordance lookups: no signals, default duration, no check)
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in examinable)
        {
            if (target.Id == agent.Id || !seen.Add(target.Id))
                continue;
            actions.Add(new AvailableAction(
                "examine", target.Id, $"Examine {The(target)}", "examine", ""));
        }

        return actions;
    }

    private void AddFromModules(
        List<AvailableAction> actions, WorldObject agent, WorldObject target, bool stateFiltered,
        IReadOnlyList<WorldObject> others, List<WorldObject> examinable)
    {
        examinable.Add(target);
        foreach (var attachment in target.Modules)
        {
            if (!_modules.Has(attachment.ModuleId))
                continue;
            foreach (var affordance in _modules.Get(attachment.ModuleId).Affordances)
            {
                if (!Applies(affordance, agent, target, stateFiltered))
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
                var label = LabelFor(affordance.Verb, agent, target);
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
    /// Posture rules come from the affordance: its posture allow-list
    /// (go requires standing, stand requires sitting/lying) and its
    /// same-support requirement, checked against the derived posture.
    /// </summary>
    private bool Applies(
        Modules.AffordanceDefinition affordance, WorldObject agent, WorldObject target, bool stateFiltered)
    {
        bool held = target.Parent == agent.Id;
        // items held by another agent are only reachable via steal (their
        // pockets) or remove (a garment they're wearing) — their
        // take/drop/open/close affordances don't apply to you
        bool heldByOther = target.Id != agent.Id &&
                           target.Parent.Length > 0 && target.Parent != agent.Id &&
                           _world.HasObject(target.Parent) &&
                           _world.GetObject(target.Parent).HasModule("agent");
        if (heldByOther && affordance.Verb is not ("steal" or "remove"))
            return false;
        var applies = affordance.Verb switch
        {
            "look" => target.Id == agent.Id,
            "inventory" => target.Id == agent.Id,
            "wait" => target.Id == agent.Id,
            "say" => target.Id == agent.Id, // speech comes from your own can_speak
            "take" => !held && target.Id != agent.Id, // can't pick up yourself
            "drop" => held && target.Id != agent.Id && !Clothing.IsWorn(_modules, target),
            "steal" => heldByOther && !Clothing.IsWorn(_modules, target),
            "shove" => target.HasModule("agent") && target.Id != agent.Id,
            "attack" => target.HasModule("attackable") && target.Id != agent.Id,
            "wear" => held && target.HasModule("wearable") &&
                      !Clothing.IsWorn(_modules, target) && agent.HasModule("body"),
            "remove" => target.HasModule("wearable") && Clothing.IsWorn(_modules, target),
            "open" => HasOpenState(target) && (!stateFiltered || !IsOpenState(target)),
            "close" => HasOpenState(target) && (!stateFiltered || IsOpenState(target)),
            "unlock" => HasLockState(target),
            "lock" => HasLockState(target),
            "pick" => HasLockState(target),
            "sit" => target.HasModule("sittable"),
            "lie" => target.HasModule("lyable"),
            // stand: from furniture (target = what you're on) or from
            // prone (target = yourself)
            "stand" => target.Id == agent.Parent || target.Id == agent.Id,
            _ => true,
        };
        return applies && Postures.CanUse(_world, _modules, affordance, agent, target);
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

    private string LabelFor(string verb, WorldObject agent, WorldObject target) => verb switch
    {
        "look" => "Look around",
        "inventory" => "Check inventory",
        "wait" => "Wait",
        "go" => $"Go {_modules.ResolveString(target, "portal", "direction") ?? target.Name}",
        "sit" => $"Sit on {The(target)}",
        "lie" => $"Lie down on {The(target)}",
        // stand from prone targets yourself; stand from furniture targets it
        "stand" => target.Id == agent.Id ? "Stand up" : $"Get off {The(target)}",
        "wear" => $"Wear {The(target)}",
        "remove" => $"Take off {The(target)}",
        _ => $"{Capitalize(verb)} {The(target)}",
    };

    /// <summary>The target's name with a definite article, unless it already carries one.</summary>
    private static string The(WorldObject target) => Perception.WithDefiniteArticle(target.Name);

    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
