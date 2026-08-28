namespace AEngine.Core.Signals;

/// <summary>A sensory channel a signal can be perceived on (extensible).</summary>
public enum SignalSense
{
    Visual,
    Audible,
}

/// <summary>
/// A signal template declared on a module affordance: sense, priority, and
/// a text template with {agent} / {target} / {arg} placeholders.
/// </summary>
public sealed class SignalSpec
{
    public required SignalSense Sense { get; init; }
    public int Priority { get; init; }
    public required string Text { get; init; }
}

/// <summary>A formatted signal delivered to an observer's queue.</summary>
public sealed record Signal(SignalSense Sense, int Priority, string Text, string OriginRoomId);
