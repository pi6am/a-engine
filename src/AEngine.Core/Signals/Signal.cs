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
/// (traversal specs may also use {exitPortal} / {exitDirection} /
/// {entryPortal} / {entryDirection}).
/// </summary>
public sealed class SignalSpec
{
    public required SignalSense Sense { get; init; }
    public int Priority { get; init; }
    public SignalScope Scope { get; init; } = SignalScope.None;
    public required string Text { get; init; }
}

/// <summary>A formatted signal delivered to an observer's queue.</summary>
public sealed record Signal(
    SignalSense Sense, int Priority, string Text, string OriginRoomId, string? TargetId = null);
