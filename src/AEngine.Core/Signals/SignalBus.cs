using System.Text.RegularExpressions;
using AEngine.Core.Modules;
using AEngine.Core.World;

namespace AEngine.Core.Signals;

/// <summary>
/// Delivers sensory signals to agents. Each successful action may emit
/// signal specs (declared on the affordance); every other agent that can
/// perceive the action receives the single highest-priority receivable
/// signal (ties: first listed) in an ephemeral per-agent queue. Delivered
/// signals are also recorded into the observer's
/// <see cref="Runtime.AgentMemory"/>. Formatting is observer-relative: when
/// the observer IS the target, {target} renders as "you" (with the
/// following verb dropping its third-person -s in subject position) —
/// "the old cook gives the bread to you", never "... to the guest".
///
/// Propagation: an observer in the origin room receives all senses; an
/// observer one portal away receives a sense only if the portal side in
/// the origin room transmits it (portal fields transmitVisual /
/// transmitAudio: always | whenOpen | never — whenOpen reads the shared
/// doorstate via the side's own stateRef), and the delivered text gains a
/// directional suffix ("… through the wooden door to the south.") naming
/// the portal side in the observer's room, suppressed when the signal's
/// target is that same door. Anything farther gets nothing. An action
/// targeting a portal (e.g. closing a door) manifests on both sides of the
/// door: observers in the other side's room perceive it as a same-room
/// event.
///
/// Portal traversal (a successful "go") is delivered from scoped specs
/// instead: Departure specs to observers in the room the actor left
/// ("{agent} exits through the {exitPortal} to the {exitDirection}."),
/// Arrival specs to observers in the room entered ("{agent} enters from
/// the {entryPortal} to the {entryDirection}.").
///
/// Location is room-granular (see World.RoomOf): a carried agent or an
/// agent inside a container acts from and observes in the room containing
/// their carrier — a parrot in your pocket can be heard.
/// </summary>
public sealed class SignalBus
{
    private readonly World.World _world;
    private readonly ModuleRegistry _modules;
    private readonly Runtime.AgentMemory _memory;
    private readonly Dictionary<string, Queue<Signal>> _queues = new(StringComparer.Ordinal);

    public SignalBus(World.World world, ModuleRegistry modules, Runtime.AgentMemory memory)
    {
        _world = world;
        _modules = modules;
        _memory = memory;
    }

    /// <summary>
    /// Format and deliver the given specs for an action performed by
    /// <paramref name="actor"/>. <paramref name="extra"/> supplies
    /// additional template placeholders (e.g. {container} naming the holder
    /// the target was taken from). <paramref name="targetName"/> overrides
    /// how {target} renders — the target's name as it was BEFORE the
    /// handler ran, when a handler renames its target (a consumed drink
    /// becoming "empty mug"): observers should hear what was drunk, not
    /// what it became.
    /// </summary>
    public void Emit(
        WorldObject actor, WorldObject? target, IReadOnlyList<SignalSpec> specs,
        string? arg = null, TraversalContext? traversal = null,
        IReadOnlyDictionary<string, string>? extra = null, string? targetName = null)
    {
        if (specs.Count == 0)
            return;
        if (traversal is not null)
        {
            EmitTraversal(actor, specs, traversal);
            return;
        }
        var originRoomId = _world.RoomOf(actor.Id).Id;
        // A portal action (e.g. closing a door) manifests on both sides of
        // the door: observers in the other side's room perceive it as if it
        // happened in their room, transmission rules notwithstanding.
        var otherSideRoomId = OtherSideRoom(target, originRoomId);
        var normalSpecs = specs.Where(s => s.Scope == SignalScope.None).ToList();
        // per-sense room reach: the minimum attenuation cost from the origin
        // to every room (spec strength pays that cost at delivery)
        var reachCache = new Dictionary<SignalSense, Dictionary<string, RoomHop>>();
        Dictionary<string, RoomHop> ReachFor(SignalSense sense)
        {
            if (!reachCache.TryGetValue(sense, out var reach))
                reachCache[sense] = reach = RoomReach(originRoomId, otherSideRoomId, sense);
            return reach;
        }
        foreach (var observer in _world.Objects.Values)
        {
            if (observer.Id == actor.Id || !observer.HasModule("agent"))
                continue;
            var best = BestReceivable(
                observer, originRoomId, actor, target, normalSpecs, arg, extra,
                targetName, ReachFor);
            if (best is null)
                continue;
            Enqueue(observer, best);
        }
    }

    /// <summary>
    /// The room of the other side of the targeted portal (same shared
    /// doorstate), or null when the target is not a two-sided portal.
    /// </summary>
    private string? OtherSideRoom(WorldObject? target, string originRoomId)
    {
        if (target is null || !target.HasModule("portal"))
            return null;
        var stateRef = _modules.ResolveString(target, "portal", "stateRef");
        if (stateRef is null)
            return null;
        foreach (var obj in _world.Objects.Values)
        {
            if (obj.Id != target.Id && obj.HasModule("portal") &&
                _modules.ResolveString(obj, "portal", "stateRef") == stateRef &&
                obj.Parent != originRoomId)
                return obj.Parent;
        }
        return null;
    }

    /// <summary>Deliver scoped specs for a portal traversal to the departure and arrival rooms.</summary>
    private void EmitTraversal(
        WorldObject actor, IReadOnlyList<SignalSpec> specs, TraversalContext traversal)
    {
        var extra = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["{exitPortal}"] = traversal.ExitSide.Name,
            ["{exitDirection}"] = DirectionPhrase(DirectionOf(traversal.ExitSide)),
            ["{entryPortal}"] = traversal.EntrySide?.Name ?? traversal.ExitSide.Name,
            ["{entryDirection}"] = DirectionPhrase(DirectionOf(traversal.EntrySide ?? traversal.ExitSide)),
        };
        foreach (var observer in _world.Objects.Values)
        {
            if (observer.Id == actor.Id || !observer.HasModule("agent"))
                continue;
            var observerRoomId = _world.RoomOf(observer.Id).Id;
            var scope = observerRoomId == traversal.DepartureRoomId ? SignalScope.Departure
                : observerRoomId == traversal.ArrivalRoomId ? SignalScope.Arrival
                : SignalScope.None;
            if (scope == SignalScope.None)
                continue;
            Signal? best = null;
            foreach (var spec in specs)
            {
                if (spec.Scope != scope)
                    continue;
                if (best is null || spec.Priority > best.Priority)
                {
                    best = new Signal(
                        spec.Sense, spec.Priority,
                        Format(spec.TextAt(spec.Strength), actor, null, null, extra, observer),
                        scope == SignalScope.Departure
                            ? traversal.DepartureRoomId
                            : traversal.ArrivalRoomId,
                        traversal.ExitSide.Id,
                        ThroughPortal: true, Salience: spec.Salience, Strength: spec.Strength);
                }
            }
            if (best is not null)
                Enqueue(observer, best);
        }
    }

    /// <summary>Return and clear the agent's pending signals.</summary>
    public IReadOnlyList<Signal> Drain(string agentId)
    {
        if (!_queues.TryGetValue(agentId, out var queue) || queue.Count == 0)
            return [];
        var drained = queue.ToArray();
        queue.Clear();
        return drained;
    }

    /// <summary>Return the agent's pending signals without clearing (for tooling).</summary>
    public IReadOnlyList<Signal> Peek(string agentId) =>
        _queues.TryGetValue(agentId, out var queue) ? queue.ToArray() : [];

    /// <summary>
    /// Deliver a private sensation to one agent (ambient module emissions —
    /// a curse burning, a charm tingling). No propagation, no observer
    /// formatting: the text is authored for the perceiver, and it lands in
    /// memory at high salience (it's about them).
    /// </summary>
    public void SendTo(WorldObject observer, string text) =>
        Enqueue(observer, new Signal(
            SignalSense.Visual, 0, text, _world.RoomOf(observer.Id).Id),
            _memory.SalienceBoostOf(observer));

    /// <summary>
    /// The cheapest attenuation cost from the origin to each reachable
    /// room, for one sense — a small Dijkstra over portal edges. Crossing
    /// a portal side costs its per-sense attenuation plus the average of
    /// the two rooms' per-sense attenuation (room attenuation is NOT
    /// applied to listeners in the origin room — that's reserved for a
    /// future spatial refinement). The transmit gates
    /// (always/whenOpen/never) are hard edges: a closed door's visual
    /// never becomes a cost, it blocks the edge outright. A portal action
    /// manifests at full strength on both sides of its door, so the other
    /// side's room seeds at cost 0.
    /// </summary>
    private Dictionary<string, RoomHop> RoomReach(
        string originRoomId, string? otherSideRoomId, SignalSense sense)
    {
        var reach = new Dictionary<string, RoomHop>(StringComparer.Ordinal)
        {
            [originRoomId] = new RoomHop(0, null),
        };
        if (otherSideRoomId is not null && !reach.ContainsKey(otherSideRoomId))
            reach[otherSideRoomId] = new RoomHop(0, null);
        // (cost, room) pairs pending relaxation — rooms are few, a linear
        // scan for the minimum keeps this dependency-free and simple
        var pending = new List<(int Cost, string Room)>(reach.Select(r => (r.Value.Cost, r.Key)));
        while (pending.Count > 0)
        {
            var best = pending[0];
            for (var i = 1; i < pending.Count; i++)
                if (pending[i].Cost < best.Cost)
                    best = pending[i];
            pending.Remove(best);
            if (best.Cost > reach.GetValueOrDefault(best.Room).Cost)
                continue; // a cheaper path already settled
            var room = _world.GetObject(best.Room);
            foreach (var side in _world.ChildrenOf(best.Room).Where(c => c.HasModule("portal")))
            {
                var toRoom = _modules.ResolveString(side, "portal", "to");
                if (toRoom is null || toRoom == best.Room || !_world.HasObject(toRoom))
                    continue;
                if (!Transmits(side, sense))
                    continue; // hard gate: closed to this sense outright
                var crossing = Math.Max(0, PortalAttenuation(side, sense) +
                    (RoomAttenuation(best.Room, sense) + RoomAttenuation(toRoom, sense)) / 2);
                var cost = best.Cost + crossing;
                if (reach.TryGetValue(toRoom, out var known) && known.Cost <= cost)
                    continue;
                reach[toRoom] = new RoomHop(cost, EntrySideIn(side, best.Room, toRoom));
                pending.Add((cost, toRoom));
            }
        }
        return reach;
    }

    /// <summary>
    /// The portal side a signal enters a room through — used for the
    /// directional suffix ("through the wooden door to the south"): the
    /// side living in the entered room that shares the crossed side's
    /// doorstate (or points back), or the crossed side itself for one-way
    /// portals with no return side.
    /// </summary>
    private WorldObject EntrySideIn(WorldObject crossed, string fromRoomId, string toRoomId) =>
        _world.ChildrenOf(toRoomId).FirstOrDefault(p =>
            p.HasModule("portal") &&
            (SameDoor(p, crossed) ||
             _modules.ResolveString(p, "portal", "to") == fromRoomId))
        ?? crossed;

    private int PortalAttenuation(WorldObject side, SignalSense sense) =>
        _modules.ResolveInt(side, "portal",
            sense == SignalSense.Visual ? "attenuateVisual" : "attenuateAudio", 1);

    private int RoomAttenuation(string roomId, SignalSense sense)
    {
        var room = _world.GetObject(roomId);
        return room.HasModule("room")
            ? _modules.ResolveInt(room, "room",
                sense == SignalSense.Visual ? "attenuateVisual" : "attenuateAudio", 0)
            : 0;
    }

    /// <summary>How a signal entered a room: the accumulated cost, and the portal side it came through.</summary>
    private sealed record RoomHop(int Cost, WorldObject? EntrySide);

    private Signal? BestReceivable(
        WorldObject observer, string originRoomId,
        WorldObject actor, WorldObject? target,
        IReadOnlyList<SignalSpec> specs, string? arg,
        IReadOnlyDictionary<string, string>? extra = null, string? targetName = null,
        Func<SignalSense, Dictionary<string, RoomHop>>? reach = null)
    {
        var observerRoomId = _world.RoomOf(observer.Id).Id;
        var throughPortal = observerRoomId != originRoomId;

        Signal? best = null;
        foreach (var spec in specs)
        {
            // audience filters run before sense/portal rules: a spec can be
            // reserved for the action's target (directed speech) or barred
            // from it (a bystander's murmur)
            if (spec.Audience == SignalAudience.OnlyTarget &&
                (target is null || target.Id != observer.Id))
                continue;
            if (spec.Audience == SignalAudience.ExceptTarget &&
                target is not null && target.Id == observer.Id)
                continue;
            // attenuation: the spec's strength pays the cheapest cost to
            // the observer's room; a negative remainder is imperceptible
            var hop = reach is not null
                ? reach(spec.Sense).GetValueOrDefault(observerRoomId)
                : observerRoomId == originRoomId ? new RoomHop(0, null) : null;
            if (hop is null || spec.Strength - hop.Cost < 0)
                continue;
            if (best is null || spec.Priority > best.Priority)
            {
                var remaining = spec.Strength - hop.Cost;
                // the representation degrades with surviving strength: full
                // text up close, rungs of the ladder at range
                var text = Format(spec.TextAt(remaining), actor, target, arg, extra, observer, targetName);
                if (throughPortal && hop.EntrySide is not null && !SameDoor(target, hop.EntrySide))
                    text = text.TrimEnd('.') + Suffix(hop.EntrySide);
                best = new Signal(
                    spec.Sense, spec.Priority, text, originRoomId, target?.Id,
                    ThroughPortal: throughPortal, Salience: spec.Salience,
                    Strength: remaining);
            }
        }
        return best;
    }

    /// <summary>
    /// True when the action's target is the very door the observer is
    /// perceiving through (same portal side, or the other side of the same
    /// shared doorstate) — the directional suffix is redundant then.
    /// </summary>
    private bool SameDoor(WorldObject? target, WorldObject observerSide)
    {
        if (target is null || !target.HasModule("portal"))
            return false;
        if (target.Id == observerSide.Id)
            return true;
        var targetRef = _modules.ResolveString(target, "portal", "stateRef");
        return targetRef is not null &&
            targetRef == _modules.ResolveString(observerSide, "portal", "stateRef");
    }

    /// <summary>Directional suffix for signals observed through a portal (" through the wooden door to the south.").</summary>
    private string Suffix(WorldObject observerSide)
    {
        var direction = DirectionOf(observerSide);
        return direction.Length > 0
            ? $" through the {observerSide.Name} to the {DirectionPhrase(direction)}."
            : $" through the {observerSide.Name}.";
    }

    private string DirectionOf(WorldObject portalSide) =>
        _modules.ResolveString(portalSide, "portal", "direction") ?? "";

    /// <summary>
    /// A direction rendered for "to the ..." phrasing: up/down are not
    /// cardinals ("to the up" is not English), so they render as relative
    /// floors ("to the floor above").
    /// </summary>
    private static string DirectionPhrase(string direction) => direction switch
    {
        "up" => "floor above",
        "down" => "floor below",
        _ => direction,
    };

    private bool Transmits(WorldObject portalSide, SignalSense sense)
    {
        var field = sense == SignalSense.Visual ? "transmitVisual" : "transmitAudio";
        var fallback = sense == SignalSense.Visual ? "whenOpen" : "always";
        var mode = _modules.ResolveString(portalSide, "portal", field) ?? fallback;
        return mode switch
        {
            "always" => true,
            "never" => false,
            "whenOpen" => IsOpen(portalSide),
            _ => false,
        };
    }

    private bool IsOpen(WorldObject portalSide)
    {
        var stateRef = _modules.ResolveString(portalSide, "portal", "stateRef");
        return stateRef is not null && _world.HasObject(stateRef) &&
            _modules.ResolveBool(_world.GetObject(stateRef), "doorstate", "open");
    }

    private void Enqueue(WorldObject observer, Signal signal, int? salienceOverride = null)
    {
        if (!_queues.TryGetValue(observer.Id, out var queue))
            _queues[observer.Id] = queue = new Queue<Signal>();
        queue.Enqueue(signal);
        // observed signals are also remembered, so later plans/conversations
        // keep continuity even after the pending queue is drained. Salience:
        // being the action's target (addressed!) buys the agent's boost, on
        // top of any per-spec override riding the signal
        var salience = salienceOverride ??
                       (signal.TargetId == observer.Id
                           ? _memory.SalienceBoostOf(observer)
                           : 0) + signal.Salience;
        _memory.Record(observer, signal.Text, salience: salience);
    }

    private static string Format(
        string template, WorldObject actor, WorldObject? target, string? arg,
        IReadOnlyDictionary<string, string>? extra = null, WorldObject? observer = null,
        string? targetName = null)
    {
        var text = template
            .Replace("{agent}", actor.Name, StringComparison.Ordinal)
            .Replace("{arg}", arg ?? "", StringComparison.Ordinal);
        // observer-relative naming: every agent is the protagonist of their
        // own perception, so when the observer IS the target it renders as
        // "you" ("the old cook gives the bread to you"), never by name
        if (target is not null && observer is not null && target.Id == observer.Id)
            text = ReplaceTargetAsYou(text);
        else
            text = text.Replace("{target}", targetName ?? target?.Name ?? "", StringComparison.Ordinal);
        if (extra is not null)
            foreach (var (placeholder, value) in extra)
                text = text.Replace(placeholder, value, StringComparison.Ordinal);
        // {container} defaults to empty when the target wasn't in a holder;
        // {item} likewise for one-object verbs
        text = text.Replace("{container}", "", StringComparison.Ordinal);
        text = text.Replace("{item}", "", StringComparison.Ordinal);
        return CollapseDoubledArticles(text);
    }

    /// <summary>
    /// Render {target} as the observing target itself: object/possessive
    /// positions ("to the {target}") become "you"; at sentence start the
    /// placeholder is the subject, so the verb that follows it drops its
    /// third-person -s ("{target} declines" → "you decline").
    /// </summary>
    private static string ReplaceTargetAsYou(string template)
    {
        var text = template
            .Replace("the {target}", "you", StringComparison.Ordinal)
            .Replace("The {target}", "you", StringComparison.Ordinal);
        return SubjectTarget.Replace(text, match =>
        {
            var before = text[..match.Index].TrimEnd();
            var sentenceStart = before.Length == 0 ||
                before.EndsWith('.') || before.EndsWith('!') || before.EndsWith('?');
            var word = match.Groups[1].Value;
            return sentenceStart && word.Length > 0
                ? "you " + SecondPerson(word)
                : "you" + (word.Length > 0 ? " " + word : "");
        });
    }

    private static readonly Regex SubjectTarget =
        new(@"\{target\}(?: (\w+))?", RegexOptions.Compiled);

    /// <summary>Third-person singular verb → second person: declines → decline, tries → try, watches → watch.</summary>
    private static string SecondPerson(string verb)
    {
        if (verb.EndsWith("ies", StringComparison.Ordinal) && verb.Length > 3)
            return verb[..^3] + "y";
        if (verb.EndsWith("shes", StringComparison.Ordinal) ||
            verb.EndsWith("ches", StringComparison.Ordinal) ||
            verb.EndsWith("sses", StringComparison.Ordinal) ||
            verb.EndsWith("xes", StringComparison.Ordinal) ||
            verb.EndsWith("zes", StringComparison.Ordinal) ||
            verb.EndsWith("oes", StringComparison.Ordinal))
            return verb[..^2];
        if (verb.EndsWith('s') && !verb.EndsWith("ss", StringComparison.Ordinal))
            return verb[..^1];
        return verb;
    }

    /// <summary>
    /// Templates habitually write "the {target}" while descriptive names
    /// already carry their article ("the arena duelist") — collapse the
    /// resulting "the the" / "a an" doublings.
    /// </summary>
    private static readonly Regex DoubledArticles =
        new(@"\b(?:the|a|an) (the|a|an) ", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string CollapseDoubledArticles(string text)
    {
        string collapsed;
        while ((collapsed = DoubledArticles.Replace(text, "$1 ")) != text)
            text = collapsed;
        return text;
    }
}
