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
    /// <summary>
    /// Memory salience in events (additive, negatives allowed): a bomb
    /// blast +10 outlives being addressed to you; a jukebox −5 ages out
    /// faster than ambient chatter. See AgentMemory's aging eviction.
    /// </summary>
    public int Salience { get; init; }
    /// <summary>
    /// Perceptual strength on a log scale (decibel-like), so attenuation
    /// adds up. A signal is perceivable while its remaining strength is
    /// non-negative: with the default strength 1, portal attenuation 1,
    /// and room attenuation 0, a signal carries exactly one room away.
    /// Loud events (a gunshot 4) carry several rooms; whispers (0) stay
    /// in the room they were uttered in.
    /// </summary>
    public int Strength { get; init; } = 1;
    public required string Text { get; init; }
}

/// <summary>
/// A formatted signal delivered to an observer's queue. ThroughPortal is
/// true when the signal crossed a portal to reach the observer (or is a
/// traversal departure/arrival report) — renderers use it to keep the
/// "You hear:" framing for remote sounds while printing same-room speech
/// bare. Salience rides along into the observer's memory (combined there
/// with the addressed-to-you boost). Strength is the signal's remaining
/// perceptual strength at delivery — what's left after the attenuation
/// of the path it took (the hook for degrading a representation: speech
/// fading to "you hear someone talking" through a solid door).
/// </summary>
public sealed record Signal(
    SignalSense Sense, int Priority, string Text, string OriginRoomId,
    string? TargetId = null, bool ThroughPortal = false, int Salience = 0,
    int Strength = 1);
