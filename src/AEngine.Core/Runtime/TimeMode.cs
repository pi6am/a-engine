namespace AEngine.Core.Runtime;

/// <summary>
/// Time mode of the engine. Turn-based: each action advances the turn.
/// Real-time: a driver (the CLI's per-second timer) advances turns via
/// TurnManager.Tick and NPCs act on their own; actions take their
/// affordance's data-driven duration in turns/seconds.
/// </summary>
public enum TimeMode
{
    TurnBased,
    RealTime,
}
