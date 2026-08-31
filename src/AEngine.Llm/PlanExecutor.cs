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
            var result = _engine.TurnManager.PerformAction(_agent, action, action.Text);
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
    /// still resolves — the handler then reports a noop. Speech lines
    /// ("Say [to X]: ...") are parsed generously: quotes optional, [to X]
    /// optional.
    /// </summary>
    public static AvailableAction? MatchAvailableOrPotential(
        GameEngine engine, WorldObject agent, string line)
    {
        if (TryParseSpeech(line, out var addressee, out var speech))
        {
            var say = FindSayAction(engine, agent, addressee);
            if (say is not null)
                return say with { Text = speech };
        }
        if (TryParseAttack(engine, agent, line) is { } attack)
            return attack;
        return MatchLine(engine.ActionResolver.Resolve(agent), line)
            ?? MatchLine(engine.ActionResolver.ResolvePotential(agent), line);
    }

    /// <summary>
    /// Parse an attack line, optionally aimed at a body part: "Attack the
    /// X", "Attack the X [in the head]", "Attack the X in the head", or
    /// the advertised label verbatim ("... [in the {part}]" — the raw
    /// placeholder means unaimed). Returns the matching attack action with
    /// Text carrying the part name (null when unaimed); null when the line
    /// matches no attack action.
    /// </summary>
    public static AvailableAction? TryParseAttack(GameEngine engine, WorldObject agent, string line)
    {
        if (!line.StartsWith("attack", StringComparison.OrdinalIgnoreCase))
            return null;
        if (line.Length > 6 && line[6] is not (' ' or '['))
            return null; // "attacking" — not an attack command
        var rest = line[6..].Trim();
        string? part = null;
        if (rest.EndsWith(']'))
        {
            // bracketed suffix: "... [in the head]" / "... [in head]"
            var open = rest.LastIndexOf("[in ", StringComparison.OrdinalIgnoreCase);
            if (open >= 0)
            {
                part = rest[(open + 4)..^1].Trim();
                rest = rest[..open].Trim();
            }
        }
        else
        {
            // bare suffix: "... in the head" / "... in head"
            var idx = rest.LastIndexOf(" in the ", StringComparison.OrdinalIgnoreCase);
            var cut = idx >= 0 ? idx : rest.LastIndexOf(" in ", StringComparison.OrdinalIgnoreCase);
            if (cut >= 0)
            {
                part = rest[(cut + (idx >= 0 ? 8 : 4))..].Trim();
                rest = rest[..cut].Trim();
            }
        }
        if (part is not null)
        {
            if (part.StartsWith("the ", StringComparison.OrdinalIgnoreCase))
                part = part[4..].Trim();
            if (part.Length == 0 || part == "{part}")
                part = null; // the advertised label verbatim = unaimed
        }
        if (rest.Length == 0)
            return null;

        var needle = Normalize(rest);
        foreach (var action in engine.ActionResolver.Resolve(agent)
                     .Concat(engine.ActionResolver.ResolvePotential(agent))
                     .Where(a => a.Verb == "attack" && a.TargetId is not null))
        {
            var name = Normalize(engine.World.GetObject(action.TargetId!).Name);
            if (name == needle ||
                name.Contains(needle, StringComparison.Ordinal) ||
                needle.Contains(name, StringComparison.Ordinal))
                return action with { Text = part };
        }
        return null;
    }

    /// <summary>
    /// Parse a speech line: "Say [to X]: \"...\"", "Say: ...", "Say ..." —
    /// and the speech-first variant "Say: \"...\" to X". Quotation marks
    /// and the [to X] addressee are optional; the trailing "to X" form is
    /// only recognized with quoted speech, where the closing quote
    /// disambiguates it from the utterance itself.
    /// </summary>
    public static bool TryParseSpeech(string line, out string? addressee, out string speech)
    {
        addressee = null;
        speech = "";
        if (!line.StartsWith("say", StringComparison.OrdinalIgnoreCase))
            return false;
        if (line.Length > 3 && line[3] is not (' ' or ':' or '['))
            return false; // "saying", "says" — not a speech command
        var rest = line[3..].Trim();
        if (rest.StartsWith("[to", StringComparison.OrdinalIgnoreCase))
        {
            var close = rest.IndexOf(']');
            if (close < 0)
                return false;
            var name = rest[3..close].Trim();
            addressee = name.Length > 0 ? name : null;
            rest = rest[(close + 1)..].Trim();
        }
        if (rest.StartsWith(':'))
            rest = rest[1..].Trim();
        if (rest.Length > 1 && rest[0] is '"' or '“')
        {
            var closeQuote = rest.IndexOfAny(['"', '”'], 1);
            if (closeQuote > 0)
            {
                speech = rest[1..closeQuote].Trim();
                var tail = rest[(closeQuote + 1)..].Trim();
                if (tail.StartsWith("to ", StringComparison.OrdinalIgnoreCase) && tail.Length > 3)
                {
                    var name = tail[3..].Trim().Trim('"', '“', '”').TrimEnd('.').Trim();
                    if (name.Length > 0)
                        addressee ??= name;
                }
                return speech.Length > 0;
            }
        }
        speech = rest.Trim().Trim('"').Trim();
        return speech.Length > 0;
    }

    /// <summary>
    /// Find the say action for an addressee: with one, the say entry whose
    /// target's name matches (loosely); without one — or when the
    /// addressee doesn't match any directed entry (e.g. the LLM added
    /// "[to X]" though only one other agent is present) — the undirected
    /// entry (or, when only directed entries exist, the first one).
    /// </summary>
    private static AvailableAction? FindSayAction(
        GameEngine engine, WorldObject agent, string? addressee)
    {
        var says = engine.ActionResolver.Resolve(agent)
            .Where(a => a.Verb == "say").ToList();
        if (says.Count == 0)
            return null;
        var undirected = says.FirstOrDefault(a => a.TargetId == agent.Id);
        if (addressee is null)
            return undirected ?? says[0];
        var needle = Normalize(addressee);
        return says.FirstOrDefault(a =>
        {
            if (a.TargetId is null)
                return false;
            var name = Normalize(engine.World.GetObject(a.TargetId).Name);
            return name.Contains(needle, StringComparison.Ordinal) ||
                   needle.Contains(name, StringComparison.Ordinal);
        }) ?? undirected ?? says[0];
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
