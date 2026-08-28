using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.World;

namespace AEngine.Llm;

/// <summary>The outcome of one plan line.</summary>
public sealed record PlanStepResult(
    string Line, AvailableAction? Action, ActionResult? Result, string? Note)
{
    public bool Executed => Result is not null;
}

/// <summary>
/// Executes a parsed plan line by line. Each line is matched against the
/// CURRENTLY available actions (case-insensitive label equality first,
/// then normalized containment), so conditional availability resolves
/// itself at execution time ("Open the wooden door" matches only after
/// "unlock" succeeded). Noop steps (already in the desired state, e.g. a
/// redundant "unlock") are skipped over without consequence. Stops at the
/// first line with no match or whose action fails.
/// </summary>
public sealed class PlanExecutor
{
    private readonly GameEngine _engine;
    private readonly WorldObject _agent;

    public PlanExecutor(GameEngine engine, WorldObject agent)
    {
        _engine = engine;
        _agent = agent;
    }

    /// <summary>
    /// Execute the plan, invoking <paramref name="afterStep"/> after each
    /// executed step (the CLI uses it to print the result and run NPC
    /// turns). Returns the ordered step results, ending at the first
    /// unmatched or failed line.
    /// </summary>
    public IReadOnlyList<PlanStepResult> Execute(
        IReadOnlyList<string> plan, Action<PlanStepResult>? afterStep = null)
    {
        var results = new List<PlanStepResult>();
        foreach (var line in plan)
        {
            var action = MatchAvailableOrPotential(_engine, _agent, line);
            if (action is null)
            {
                results.Add(new PlanStepResult(
                    line, null, null, $"I don't know how to '{line}' right now."));
                break;
            }
            var result = _engine.TurnManager.PerformAction(_agent, action);
            var step = new PlanStepResult(line, action, result, null);
            results.Add(step);
            afterStep?.Invoke(step);
            if (result.Outcome == ActionOutcome.Failure)
                break;
        }
        return results;
    }

    /// <summary>
    /// Match a plan line against the currently available actions, falling
    /// back to the state-unfiltered potential set so a generated but
    /// redundant line (e.g. "Open the desk drawer" when it is already open)
    /// still resolves — the handler then reports a noop.
    /// </summary>
    public static AvailableAction? MatchAvailableOrPotential(
        GameEngine engine, WorldObject agent, string line)
    {
        return MatchLine(engine.ActionResolver.Resolve(agent), line)
            ?? MatchLine(engine.ActionResolver.ResolvePotential(agent), line);
    }

    /// <summary>
    /// Match a plan line against the available actions: case-insensitive
    /// label equality first, then normalized containment in either
    /// direction.
    /// </summary>
    public static AvailableAction? MatchLine(IReadOnlyList<AvailableAction> actions, string line)
    {
        var exact = actions.FirstOrDefault(a =>
            string.Equals(a.Label, line, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;

        var needle = Normalize(line);
        if (needle.Length == 0)
            return null;
        return actions.FirstOrDefault(a =>
        {
            var label = Normalize(a.Label);
            return label.Contains(needle, StringComparison.Ordinal) ||
                   needle.Contains(label, StringComparison.Ordinal);
        });
    }

    private static string Normalize(string s) =>
        string.Join(' ', s.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
