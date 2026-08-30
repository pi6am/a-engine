namespace AEngine.Cli;

/// <summary>
/// Player-facing output toggles (meta settings, no game state), changed
/// at runtime via slash commands: /showplan, /narrate.
/// </summary>
public sealed class OutputSettings
{
    /// <summary>Whether LLM action plans print before execution (/showplan on|off).</summary>
    public bool ShowPlan { get; set; }

    /// <summary>LLM narration scope (/narrate). Room|All narrate arrival descriptions; action narration is not implemented yet.</summary>
    public NarrateScope Narrate { get; set; } = NarrateScope.Off;
}

/// <summary>What the LLM narrator expands: rooms, action results, both (all), or nothing.</summary>
public enum NarrateScope
{
    Off,
    Room,
    Actions,
    All,
}
