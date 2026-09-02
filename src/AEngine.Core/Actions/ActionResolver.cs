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
        if (Health.IsIncapacitated(_world, _modules, agent))
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
                    // the acting agent's own verbs and pockets were already
                    // handled above and in the inventory scan — scanning
                    // them again as an occupant duplicates every entry
                    if (occupantId == agent.Id)
                        continue;
                    var occupant = _world.GetObject(occupantId);
                    AddFromModules(actions, agent, occupant, stateFiltered, others, examinable);
                    if (occupant.HasModule("agent"))
                    {
                        foreach (var pocketId in occupant.Children)
                            AddFromModules(actions, agent, _world.GetObject(pocketId), stateFiltered, others, examinable);
                    }
                }
            }
            if (child.HasModule("container") && IsOpenState(child) || child.HasModule("surface"))
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
                "examine", target.Id, $"Examine {The(agent, target)}", "examine", ""));
        }

        // collapse identical (verb, label) entries: interchangeable
        // objects sharing a name (three "empty mug"s) read as one action
        // — the LLM and the menus can't tell them apart anyway. The first
        // occurrence wins, so choices made from this list always
        // reference an entry that survives later re-resolution
        return actions.DistinctBy(a => (a.Verb, a.Label)).ToList();
    }

    private void AddFromModules(
        List<AvailableAction> actions, WorldObject agent, WorldObject target, bool stateFiltered,
        IReadOnlyList<WorldObject> others, List<WorldObject> examinable)
    {
        // body parts are anatomy and conditions are states, not items: no
        // affordances, no pocket scans, no examine entries — body parts get
        // wounded, conditions get attached and detached
        if (Conditions.IsInternal(target))
            return;
        examinable.Add(target);
        foreach (var attachment in target.Modules)
        {
            if (!_modules.Has(attachment.ModuleId))
                continue;
            foreach (var affordance in _modules.Get(attachment.ModuleId).Affordances)
            {
                if (!Applies(affordance, agent, target, stateFiltered))
                    continue;
                // othersOnly: a service is never aimed at its own owner
                // (the sorcerer does not "ask the sorcerer")
                if (affordance.OthersOnly && target.Id == agent.Id)
                    continue;
                // targetOthers: the performer-facing direction of a service,
                // offered from the agent's own modules, one entry per other
                // agent present ("Perform the unbinding rite on {target}")
                if (affordance.TargetOthers)
                {
                    if (target.Id != agent.Id)
                        continue;
                    foreach (var other in others)
                        actions.Add(new AvailableAction(
                            affordance.Verb, other.Id, LabelFor(affordance, agent, other),
                            affordance.Handler, attachment.ModuleId, affordance.Prompt));
                    continue;
                }
                // speech is parameterized: the label carries a {speech}
                // placeholder, aimed per SpeechTargets. Both (the say
                // default): the undirected broadcast ("Say: {speech}") is
                // always offered, and with several other agents present
                // each addressee gets a directed entry too ("Say to Nix
                // the goblin: {speech}") — addressing is a choice, not a
                // requirement. Broadcast (shout): exactly one undirected
                // entry. Directed (whisper): one entry per other agent,
                // none when alone — there is no undirected whisper.
                if (target.Id == agent.Id &&
                    (affordance.SpeechTargets is not null || affordance.Verb == "say"))
                {
                    var cap = char.ToUpperInvariant(affordance.Verb[0]) + affordance.Verb[1..];
                    var targeting = affordance.SpeechTargets ?? Modules.SpeechTargeting.Both;
                    if (targeting != Modules.SpeechTargeting.Directed)
                        actions.Add(new AvailableAction(
                            affordance.Verb, agent.Id, $"{cap}: {{speech}}",
                            affordance.Handler, attachment.ModuleId, affordance.Prompt));
                    if (targeting != Modules.SpeechTargeting.Broadcast &&
                        (targeting == Modules.SpeechTargeting.Directed || others.Count > 1))
                        foreach (var other in others)
                            actions.Add(new AvailableAction(
                                affordance.Verb, other.Id, $"{cap} to {NameFor(agent, other)}: {{speech}}",
                                affordance.Handler, attachment.ModuleId, affordance.Prompt));
                    continue;
                }
                // give is a two-object verb on the held item: one entry per
                // recipient, the item riding as AuxTargetId (the recipient
                // stays TargetId so the accept/decline reaction finds them)
                if (affordance.Verb == "give")
                {
                    foreach (var other in others)
                        actions.Add(new AvailableAction(
                            "give", other.Id, $"Give {The(agent, target)} to {NameFor(agent, other)}",
                            affordance.Handler, attachment.ModuleId, affordance.Prompt)
                        { AuxTargetId = target.Id });
                    continue;
                }
                // put is a two-object verb on the container: one entry per
                // held item, the item riding as AuxTargetId
                if (affordance.Verb == "put")
                {
                    foreach (var itemId in agent.Children)
                    {
                        var item = _world.GetObject(itemId);
                        if (!item.HasModule("portable") || Conditions.IsInternal(item) ||
                            Clothing.IsWorn(_modules, item))
                            continue;
                        var prep = target.HasModule("surface") ? "onto" : "into";
                        actions.Add(new AvailableAction(
                            "put", target.Id, $"Put {The(item)} {prep} {The(target)}",
                            affordance.Handler, attachment.ModuleId, affordance.Prompt)
                        { AuxTargetId = item.Id });
                    }
                    continue;
                }
                var label = LabelFor(affordance, agent, target);
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
        // pockets), remove (a garment they're wearing), or trade (a ware
        // they're selling) — their take/drop/open/close affordances don't
        // apply to you
        bool heldByOther = target.Id != agent.Id &&
                           target.Parent.Length > 0 && target.Parent != agent.Id &&
                           _world.HasObject(target.Parent) &&
                           _world.GetObject(target.Parent).HasModule("agent");
        if (heldByOther && affordance.Verb is not ("steal" or "remove" or "trade"))
            return false;
        // policy gating: some affordances belong to one audience — the
        // game-ending "Go home" is the player's alone (an NPC picking it
        // would end the player's game), and NPCs get their own quieter
        // departure instead
        var policy = _modules.ResolveString(agent, "agent", "policy") ?? "player";
        if (affordance.PlayerOnly && policy != "player")
            return false;
        if (affordance.NpcOnly && policy == "player")
            return false;
        // status conditions on the actor gate the affordance: Requires
        // lists kinds any of which the actor must carry ("use the urinal"
        // while needing to pee or bursting), Excludes lists kinds that
        // suppress the verb
        if (!ConditionKindsApply(affordance.Requires, all: true, agent))
            return false;
        if (!ConditionKindsApply(affordance.Excludes, all: false, agent))
            return false;
        // observable state of the target (or actor): hide "Drink the ale"
        // once the vessel is empty, show "Clear the mug" only once it is
        if (!WhenApplies(affordance, agent, target))
            return false;
        // spawner slots: the spawn handler's affordance hides while the
        // spawn target already holds maxChildren clones of the spawner's
        // prefab (default 1) — the anti-flood rule; a new drink can only
        // be drawn once the last one was picked up. The spawner host and
        // the spawn target are decoupled (spawnTo), so a tap can pour
        // onto the shared counter without being a container itself
        if (affordance.Handler == "spawn")
        {
            if (!target.HasModule("spawner"))
                return false;
            var templateId = _modules.ResolveString(target, "spawner", "prefab");
            if (templateId is not null)
            {
                var spawnTarget = Spawning.SpawnTarget(_world, _modules, target);
                if (Spawning.CloneCount(_world, spawnTarget, templateId) >=
                    _modules.ResolveInt(target, "spawner", "maxChildren", 1))
                    return false;
            }
        }
        // speech verbs are parameterized from the agent's OWN attachments
        // (see the speech entry building in AddFromModules): another
        // agent's can_speak never offers you "Shout the Lythienne"
        if ((affordance.SpeechTargets is not null || affordance.Verb == "say") &&
            target.Id != agent.Id)
            return false;
        var applies = affordance.Verb switch
        {
            "look" => target.Id == agent.Id,
            "inventory" => target.Id == agent.Id,
            "wait" => target.Id == agent.Id,
            "take" => !held && target.Id != agent.Id, // can't pick up yourself
            "drop" => held && target.Id != agent.Id && !Clothing.IsWorn(_modules, target),
            // give: a held, unworn item (entries are emitted per recipient)
            "give" => held && target.Id != agent.Id && !Clothing.IsWorn(_modules, target),
            // put: the open state of a container is observable — closed
            // containers are listed only in the potential set, like "open";
            // a surface is always open (no state to filter)
            "put" => target.HasModule("surface") ||
                     (target.HasModule("container") &&
                      (!stateFiltered || !HasOpenState(target) || IsOpenState(target))),
            "steal" => heldByOther && !Clothing.IsWorn(_modules, target),
            // trade: barter for a ware another agent is holding; a ware
            // with a `trader` sells only through that agent — once sold,
            // nobody can barter it back out of the buyer's hands
            "trade" => heldByOther &&
                       (_modules.ResolveString(target, "ware", "trader") is not { Length: > 0 } trader ||
                        trader == target.Parent),
            "shove" => target.HasModule("agent") && target.Id != agent.Id,
            "attack" => target.HasModule("attackable") && target.Id != agent.Id,
            // grappling: seize a free agent (not one already carried);
            // release/choke apply to a victim you're carrying; escape is
            // the carried victim's own break-out
            "grapple" => target.HasModule("agent") && target.Id != agent.Id &&
                         Postures.Of(_world, _modules, target) != Postures.Carried,
            "release" => target.HasModule("agent") && target.Parent == agent.Id,
            "escape" => target.Id == agent.Id &&
                        Postures.Of(_world, _modules, agent) == Postures.Carried,
            "choke" => target.HasModule("agent") && target.Parent == agent.Id,
            "hug" => target.HasModule("agent") && target.Id != agent.Id,
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

    /// <summary>
    /// Evaluate a comma-separated condition-kind list on the actor: with
    /// all=true the actor carries at least ONE listed kind (Requires —
    /// any-of, since condition kinds are often exclusive tiers like
    /// tipsy/drunk); with all=false none may be present (Excludes —
    /// any-listed blocks). An empty/null list always passes.
    /// </summary>
    private bool ConditionKindsApply(string? kinds, bool all, WorldObject agent)
    {
        if (string.IsNullOrWhiteSpace(kinds))
            return true;
        var split = kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return all
            ? split.Any(kind => Conditions.Has(_world, _modules, agent, kind))
            : split.All(kind => !Conditions.Has(_world, _modules, agent, kind));
    }

    /// <summary>
    /// Evaluate an affordance's When specs: observable module-field state
    /// of the target (default) or the actor. Every spec must match —
    /// comparison semantics are shared with field gates (FieldMatch).
    /// </summary>
    private bool WhenApplies(
        Modules.AffordanceDefinition affordance, WorldObject agent, WorldObject target)
    {
        if (affordance.When is not { Count: > 0 } specs)
            return true;
        foreach (var spec in specs)
        {
            var obj = spec.On == "actor" ? agent : target;
            if (!obj.HasModule(spec.Module))
                return false;
            var value = _modules.ResolveField(obj, spec.Module, spec.Field);
            if (!FieldMatch.Matches(value, spec.EqualsValue, spec.Min, spec.Max))
                return false;
        }
        return true;
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

    private string LabelFor(Modules.AffordanceDefinition affordance, WorldObject agent, WorldObject target)
    {
        // a data-driven label override wins; {target} names the target as
        // the acting agent can print it — the author owns the phrasing,
        // articles included
        if (affordance.Label is { } custom)
            return custom.Replace("{target}", NameFor(agent, target), StringComparison.Ordinal);
        return affordance.Verb switch
        {
        "look" => "Look around",
        "inventory" => "Check inventory",
        "wait" => "Wait",
        "go" => $"Go {_modules.ResolveString(target, "portal", "direction") ?? target.Name}",
        "sit" => $"Sit on {The(target)}",
        "lie" => $"Lie down on {The(target)}",
        // stand from prone targets yourself; stand from furniture targets it
        "stand" => target.Id == agent.Id ? "Stand up" : $"Get off {The(target)}",
        "escape" => "Break free",
        "wear" => $"Wear {The(target)}",
        // taking off (or stealing) another agent's property names the
        // holder — the label must say whose jeans they are (by the name
        // the acting agent can print)
        "remove" when target.Parent.Length > 0 && _world.HasObject(target.Parent) &&
                      _world.GetObject(target.Parent) is { } wearer &&
                      wearer.HasModule("agent") && wearer.Id != agent.Id =>
            $"Take off {The(target)} from {NameFor(agent, wearer)}",
        "remove" => $"Take off {The(target)}",
        "steal" when target.Parent.Length > 0 && _world.HasObject(target.Parent) &&
                     _world.GetObject(target.Parent) is { } holder &&
                     holder.HasModule("agent") && holder.Id != agent.Id =>
            $"Steal {The(target)} from {NameFor(agent, holder)}",
        // a part-ful target advertises the optional aimed syntax (parsed
        // like Say's [to X]): "Attack the arena duelist [in the {part}]"
        "attack" when BodyParts.Of(_world, target).Count > 0 =>
            $"Attack {The(agent, target)} [in the {{part}}]",
        _ => $"{Capitalize(affordance.Verb)} {The(agent, target)}",
        };
    }

    /// <summary>The name the acting agent can print for this object (incognito until learned).</summary>
    private string NameFor(WorldObject observer, WorldObject obj) =>
        Knowledge.NameFor(_modules, observer, obj);

    /// <summary>The target's name with a definite article, unless it already carries one.</summary>
    private static string The(WorldObject target) => Perception.WithDefiniteArticle(target.Name);

    /// <summary>Observer-relative: the name the agent can print, with a definite article.</summary>
    private string The(WorldObject observer, WorldObject target) =>
        Perception.WithDefiniteArticle(NameFor(observer, target));

    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
