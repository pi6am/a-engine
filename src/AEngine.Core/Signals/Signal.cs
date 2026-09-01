namespace AEngine.Core.Signals;

/// <summary>A sensory channel a signal can be perceived on (extensible).</summary>
public enum SignalSense
{
    Visual,
    Audible,
}

/// <summary>
/// Which room-perspective a spec is delivered from. None = normal
/// propagation from the origin room. Departure/Arrival are only delivered
/// when the action moved the actor through a portal (traversal): Departure
/// goes to observers in the room the actor left, Arrival to observers in
/// the room the actor entered.
/// </summary>
public enum SignalScope
{
    None,
    Departure,
    Arrival,
}

/// <summary>
/// Who may receive a spec, relative to the action's target: Everyone
/// (default), OnlyTarget (delivered solely to the agent the action
/// targets — directed speech, "X says to you: …"), or ExceptTarget
/// (everyone but the target — a bystander's murmur). Composes with the
/// sense and portal rules; an OnlyTarget spec on a targetless or
/// non-agent-targeted action reaches nobody.
/// </summary>
public enum SignalAudience
{
    Everyone,
    OnlyTarget,
    ExceptTarget,
}

/// <summary>
/// Portal-traversal context for a "go" action: the room the actor left,
/// the room entered, and the portal sides on each end (the entry side may
/// be null for one-way portals with no return side).
/// </summary>
public sealed record TraversalContext(
    string DepartureRoomId, string ArrivalRoomId,
    World.WorldObject ExitSide, World.WorldObject? EntrySide);

/// <summary>
/// A signal template declared on a module affordance: sense, priority,
/// scope, and a text template with {agent} / {target} / {arg} placeholders
/// (plus {container} / {item} — see SignalBus.Format; traversal specs may
/// also use {exitPortal} / {exitDirection} / {entryPortal} /
/// {entryDirection}).
/// </summary>
public sealed class SignalSpec
{
    public required SignalSense Sense { get; init; }
    public int Priority { get; init; }
    public SignalScope Scope { get; init; } = SignalScope.None;
    /// <summary>Who may receive this spec, relative to the action's target.</summary>
    public SignalAudience Audience { get; init; } = SignalAudience.Everyone;
    public required string Text { get; init; }
}

/// <summary>
/// A formatted signal delivered to an observer's queue. ThroughPortal is
/// true when the signal crossed a portal to reach the observer (or is a
/// traversal departure/arrival report) — renderers use it to keep the
/// "You hear:" framing for remote sounds while printing same-room speech
/// bare.
/// </summary>
public sealed record Signal(
    SignalSense Sense, int Priority, string Text, string OriginRoomId,
    string? TargetId = null, bool ThroughPortal = false);
