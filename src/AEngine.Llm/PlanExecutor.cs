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
    /// ("Say to X: ...") are parsed generously: quotes optional, the
    /// addressee
    /// optional.
    /// </summary>
    public static AvailableAction? MatchAvailableOrPotential(
        GameEngine engine, WorldObject agent, string line)
    {
        // an exact label match always wins: a planner faithfully echoing
        // an action's label ("Say goodnight and go home" — an exit, not a
        // sentence to speak) is running that action
        var exact = engine.ActionResolver.Resolve(agent).FirstOrDefault(a =>
            string.Equals(a.Label, line, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;
        if (TryParseSpeech(line, out var verb, out var addressee, out var speech))
        {
            var spoken = FindSpeechAction(engine, agent, verb, addressee);
            if (spoken is not null)
                return spoken with { Text = speech };
        }
        if (TryParseTouch(engine, agent, line) is { } touch)
            return touch;
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
    /// Parse a speech line: "Say to X: \"...\"", "Say: ...", "Say ..." —
    /// the speech-first variant "Say: \"...\" to X" — and the legacy
    /// bracketed "Say [to X]: ...". The leading verb may be say, shout,
    /// or whisper (shout is broadcast — a stray addressee is ignored;
    /// whisper needs one). Quotation marks are optional; a bracketless
    /// addressee runs to the colon (or the opening quote), since names
    /// can be multi-word ("Nix the goblin"); the trailing "to X" form is
    /// only recognized with quoted speech, where the closing quote
    /// disambiguates it from the utterance itself.
    /// </summary>
    public static bool TryParseSpeech(string line, out string verb, out string? addressee, out string speech)
    {
        verb = "say";
        addressee = null;
        speech = "";
        var stem = SpeechStem(line);
        if (stem is null)
            return false;
        verb = stem;
        var rest = line[stem.Length..].Trim();
        if (rest.StartsWith("[to", StringComparison.OrdinalIgnoreCase))
        {
            var close = rest.IndexOf(']');
            if (close < 0)
                return false;
            var name = rest[3..close].Trim();
            addressee = name.Length > 0 ? name : null;
            rest = rest[(close + 1)..].Trim();
        }
        else if (rest.StartsWith("to ", StringComparison.OrdinalIgnoreCase))
        {
            // bracketless addressee: the name runs to the first colon or
            // opening quote (whichever comes first, with room for a name);
            // without either delimiter it can't be told from the utterance
            // ("say to be honest" is itself speech) and stays broadcast
            var colon = rest.IndexOf(':');
            var quote = rest.IndexOfAny(['"', '“']);
            var cut = new[] { colon, quote }.Where(i => i > 3).DefaultIfEmpty(-1).Min();
            if (cut > 3)
            {
                addressee = rest[3..cut].Trim();
                rest = rest[cut..].Trim();
            }
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
    /// The touch-verb stem a line starts with (kiss, massage, stroke,
    /// lick), or null. Word boundaries matter ("kissing" is not a
    /// command).
    /// </summary>
    private static string? TouchStem(string line) =>
        new[] { "kiss", "massage", "stroke", "lick" }.FirstOrDefault(v =>
            line.StartsWith(v, StringComparison.OrdinalIgnoreCase) &&
            (line.Length == v.Length || line[v.Length] is ' '));

    /// <summary>
    /// Parse a touch line aimed at a body part: "Kiss Maya's neck",
    /// "massage her shoulders", "stroke her thigh". The part resolves on
    /// the other agents present (possessives stripped generously — her,
    /// his, their, the, or an owner's name); the action must exist in
    /// the actor's current list (so exposure and intimacy filtering
    /// still decide).
    /// </summary>
    public static AvailableAction? TryParseTouch(GameEngine engine, WorldObject agent, string line)
    {
        var stem = TouchStem(line);
        if (stem is null)
            return null;
        var rest = line[stem.Length..].Trim();
        if (rest.Length == 0)
            return null;
        var actions = engine.ActionResolver.Resolve(agent)
            .Where(a => a.Verb == stem && a.TargetId is not null)
            .ToList();
        if (actions.Count == 0)
            return null;
        var roomId = engine.World.RoomOf(agent.Id).Id;
        foreach (var other in engine.World.Objects.Values
                     .Where(o => o.Id != agent.Id && o.HasModule("agent") &&
                                 engine.World.RoomOf(o.Id).Id == roomId))
        {
            var prefixes = new List<string> { "the ", "her ", "his ", "their ", other.Name + "'s " };
            foreach (var proper in Knowledge.ProperNames(engine.ModuleRegistry, other))
                prefixes.Add(proper + "'s ");
            foreach (var prefix in prefixes.Append(""))
            {
                if (!rest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                var partName = rest[prefix.Length..].Trim();
                if (partName.Length == 0)
                    continue;
                var part = BodyParts.FindByName(engine.World, other, partName);
                if (part is null)
                    continue;
                return actions.FirstOrDefault(a => a.TargetId == part.Id);
            }
        }
        return null;
    }

    /// <summary>
    /// The speech verb a line starts with ("say", "shout", "whisper" —
    /// case-insensitive, followed by end-of-line, space, colon, or the
    /// legacy "["), or null. Word-boundary matters: "saying", "shouts",
    /// "whispers" are not speech commands.
    /// </summary>
    private static string? SpeechStem(string line) =>
        new[] { "say", "shout", "whisper" }.FirstOrDefault(v =>
            line.StartsWith(v, StringComparison.OrdinalIgnoreCase) &&
            (line.Length == v.Length || line[v.Length] is ' ' or ':' or '['));

    /// <summary>
    /// Find the {speech}-parameterized action for a verb: with an
    /// addressee, the entry whose target's name matches (loosely) — or
    /// whose PROPER name matches, which works regardless of what the
    /// speaker could print (you can address "Nix" by name the moment
    /// you've heard it, before you could pick her out of a lineup).
    /// Without one, the undirected entry; a directed-only verb (whisper)
    /// has none, so it falls to its single entry when exactly one
    /// listener is present and stays unmatched when the addressee is
    /// ambiguous — there is no undirected whisper.
    /// </summary>
    private static AvailableAction? FindSpeechAction(
        GameEngine engine, WorldObject agent, string verb, string? addressee)
    {
        var verbs = engine.ActionResolver.Resolve(agent)
            .Where(a => a.Verb == verb).ToList();
        if (verbs.Count == 0)
            return null;
        var undirected = verbs.FirstOrDefault(a => a.TargetId == agent.Id);
        if (addressee is not null)
        {
            var needle = Normalize(addressee);
            var matched = verbs.FirstOrDefault(a =>
            {
                if (a.TargetId is null)
                    return false;
                var target = engine.World.GetObject(a.TargetId);
                // match the raw name, the knowledge-rendered label (the
                // plan line usually echoes the advertised label — for a
                // stranger that's their incognito rendering, "a halfling
                // woman with a loaded tray"), or their proper name
                var name = Normalize(target.Name);
                var rendered = Normalize(
                    Knowledge.NameFor(engine.ModuleRegistry, agent, target));
                if (name.Contains(needle, StringComparison.Ordinal) ||
                    needle.Contains(name, StringComparison.Ordinal) ||
                    rendered.Contains(needle, StringComparison.Ordinal) ||
                    needle.Contains(rendered, StringComparison.Ordinal))
                    return true;
                return Knowledge.ProperNames(engine.ModuleRegistry, target).Any(p =>
                {
                    var proper = Normalize(p);
                    return proper == needle ||
                           proper.Contains(needle, StringComparison.Ordinal) ||
                           needle.Contains(proper, StringComparison.Ordinal);
                });
            });
            if (matched is not null)
                return matched;
            // a broadcast entry ignores a stray addressee ("shout to X"
            // is still just a shout; "say to X" with X unmatched keeps
            // say's old fall-through to the undirected entry)
            if (undirected is not null)
                return undirected;
        }
        return undirected ?? (verbs.Count == 1 ? verbs[0] : null);
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
