using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Llm;

namespace AEngine.Cli;

/// <summary>The outcome of a walkthrough run.</summary>
public sealed record WalkthroughResult(
    bool Success, string? Error, int Line, IReadOnlyList<string> Transcript)
{
    public static WalkthroughResult Ok(IReadOnlyList<string> transcript) =>
        new(true, null, 0, transcript);
    public static WalkthroughResult Fail(string error, int line, IReadOnlyList<string> transcript) =>
        new(false, error, line, transcript);
}

/// <summary>
/// Walkthrough verification: replays a list of command lines against a
/// live engine, deterministically, with no LLM attached. Lines are exact
/// action labels ("Go north", "Put the elvish sword into the trophy
/// case"), speech lines in the plan syntax ("Say: echo"), blank lines
/// and #-comments, and "<label> :: <text>" for other prompted verbs
/// ("Speak to the cyclops :: odysseus"). Matching goes through
/// PlanExecutor.MatchAvailableOrPotential — the same deterministic
/// matcher LLM plan execution uses. After each step the NPCs get their
/// round and any pending reactions resolve to their effective defaults,
/// mirroring the interactive CLI's flow. The run stops (and fails) on
/// the first unrecognized or failed command, reporting the line number;
/// a GameOver mid-script ends the run successfully (the script claims
/// the ending — assert on engine.GameOver in the caller).
/// </summary>
public static class Walkthrough
{
    public static WalkthroughResult Run(
        GameEngine engine, string playerAgentId, IEnumerable<string> lines,
        Action<string>? log = null)
    {
        var transcript = new List<string>();
        var player = engine.World.GetObject(playerAgentId);
        var lineNo = 0;
        foreach (var raw in lines)
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            // "<label> :: <free text>" feeds a prompted verb its argument
            string? text = null;
            var sep = line.IndexOf("::", StringComparison.Ordinal);
            if (sep >= 0)
            {
                text = line[(sep + 2)..].Trim();
                line = line[..sep].Trim();
            }
            // "<label> xN" performs the command up to N times, stopping
            // early without error once it stops resolving — combat under
            // a frozen seed takes as many swings as it takes
            var repeat = 1;
            var xMatch = System.Text.RegularExpressions.Regex.Match(line, @"\s+[x×](\d+)$");
            if (xMatch.Success)
            {
                repeat = int.Parse(xMatch.Groups[1].Value);
                line = line[..xMatch.Index].Trim();
            }
            if (line.Length == 0)
                continue;
            for (var swing = 0; swing < repeat; swing++)
            {
                var action = PlanExecutor.MatchAvailableOrPotential(engine, player, line);
                if (action is null)
                {
                    if (swing > 0)
                        break; // the repeat exhausted the action (it died)
                    return WalkthroughResult.Fail(
                        $"Line {lineNo}: unrecognized command '{line}'.", lineNo, transcript);
                }
                if (text is not null)
                    action = action with { Text = text };
                var result = engine.TurnManager.PerformAction(player, action, action.Text);
                if (swing == 0)
                {
                    log?.Invoke($"> {raw.Trim()}" + (repeat > 1 ? $" ({repeat}x)" : ""));
                }
                log?.Invoke(result.Message);
                transcript.Add(result.Message);
                if (result.Outcome == ActionOutcome.Failure)
                    return WalkthroughResult.Fail(
                        $"Line {lineNo}: command failed: {line} — {result.Message}", lineNo, transcript);
                // the interactive flow: one NPC round per player action,
                // reactions resolving to their effective defaults (the
                // deterministic defender)
                engine.TurnManager.NewNpcRound();
                engine.TurnManager.RunNpcTurns();
                ResolveReactionsToDefaults(engine);
                if (engine.GameOver is not null)
                    return WalkthroughResult.Ok(transcript);
            }
        }
        return WalkthroughResult.Ok(transcript);
    }

    /// <summary>
    /// Resolve every pending reaction to its effective default — the
    /// data's static default or its defaultWhen state picks. With no
    /// policies deciding, this is what the deadline would do anyway.
    /// </summary>
    public static void ResolveReactionsToDefaults(GameEngine engine)
    {
        while (true)
        {
            lock (engine.SyncRoot)
            {
                var pending = engine.Reactions.Pending;
                if (pending.Count == 0)
                    return;
                engine.Reactions.ForceDefault(pending[0].Id);
            }
        }
    }
}
