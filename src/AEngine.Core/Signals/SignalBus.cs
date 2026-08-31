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
    /// the target was taken from).
    /// </summary>
    public void Emit(
        WorldObject actor, WorldObject? target, IReadOnlyList<SignalSpec> specs,
        string? arg = null, TraversalContext? traversal = null,
        IReadOnlyDictionary<string, string>? extra = null)
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
        foreach (var observer in _world.Objects.Values)
        {
            if (observer.Id == actor.Id || !observer.HasModule("agent"))
                continue;
            var best = BestReceivable(
                observer, originRoomId, otherSideRoomId, actor, target, normalSpecs, arg, extra);
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
                        Format(spec.Text, actor, null, null, extra, observer),
                        scope == SignalScope.Departure
                            ? traversal.DepartureRoomId
                            : traversal.ArrivalRoomId,
                        traversal.ExitSide.Id,
                        ThroughPortal: true);
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
    /// formatting: the text is authored for the perceiver.
    /// </summary>
    public void SendTo(WorldObject observer, string text) =>
        Enqueue(observer, new Signal(
            SignalSense.Visual, 0, text, _world.RoomOf(observer.Id).Id));

    private Signal? BestReceivable(
        WorldObject observer, string originRoomId, string? otherSideRoomId,
        WorldObject actor, WorldObject? target,
        IReadOnlyList<SignalSpec> specs, string? arg,
        IReadOnlyDictionary<string, string>? extra = null)
    {
        var observerRoomId = _world.RoomOf(observer.Id).Id;
        WorldObject? portalSide = null;
        WorldObject? observerSide = null;
        if (observerRoomId != originRoomId && observerRoomId != otherSideRoomId)
        {
            // adjacent-room observer: find the portal side in the origin
            // room leading toward the observer — that side's transmission
            // fields decide what gets through (one-way by data).
            if (!_world.HasObject(originRoomId))
                return null;
            portalSide = _world.ChildrenOf(originRoomId).FirstOrDefault(c =>
                c.HasModule("portal") &&
                _modules.ResolveString(c, "portal", "to") == observerRoomId);
            if (portalSide is null)
                return null; // not adjacent
            // the side in the observer's own room, for the directional suffix
            observerSide = _world.ChildrenOf(observerRoomId).FirstOrDefault(c =>
                c.HasModule("portal") &&
                _modules.ResolveString(c, "portal", "to") == originRoomId);
        }

        Signal? best = null;
        foreach (var spec in specs)
        {
            if (portalSide is not null && !Transmits(portalSide, spec.Sense))
                continue;
            if (best is null || spec.Priority > best.Priority)
            {
                var text = Format(spec.Text, actor, target, arg, extra, observer);
                if (observerSide is not null && !SameDoor(target, observerSide))
                    text = text.TrimEnd('.') + Suffix(observerSide);
                best = new Signal(
                    spec.Sense, spec.Priority, text, originRoomId, target?.Id,
                    ThroughPortal: observerSide is not null);
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

    private void Enqueue(WorldObject observer, Signal signal)
    {
        if (!_queues.TryGetValue(observer.Id, out var queue))
            _queues[observer.Id] = queue = new Queue<Signal>();
        queue.Enqueue(signal);
        // observed signals are also remembered, so later plans/conversations
        // keep continuity even after the pending queue is drained
        _memory.Record(observer, signal.Text);
    }

    private static string Format(
        string template, WorldObject actor, WorldObject? target, string? arg,
        IReadOnlyDictionary<string, string>? extra = null, WorldObject? observer = null)
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
            text = text.Replace("{target}", target?.Name ?? "", StringComparison.Ordinal);
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
