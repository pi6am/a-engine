namespace AEngine.Llm;

/// <summary>
/// Tolerant extraction of candidate action lines from an LLM reply:
/// strips code fences, bullets, and numbering, and drops lines that don't
/// look like actions (prose padding, headings like "Plan:"). A line is
/// kept when its first word is a known verb (covers prerequisite lines for
/// actions not currently listed, e.g. "Take the brass key" while the
/// drawer is closed) or when it matches/starts with a known action label
/// (covers labels whose first word is not the verb, e.g. "Check
/// inventory" for the "inventory" verb).
/// </summary>
public static class PlanParser
{
    private static readonly string[] DefaultVerbs =
        ["look", "go", "open", "close", "take", "drop", "unlock", "lock", "inventory", "say"];

    public static IReadOnlyList<string> Parse(
        string response,
        IReadOnlyList<string>? knownVerbs = null,
        IReadOnlyList<string>? knownLabels = null)
    {
        var verbs = knownVerbs ?? DefaultVerbs;
        var lines = new List<string>();
        foreach (var raw in response.Split('\n'))
        {
            var line = StripDecorations(raw);
            if (line.Length == 0)
                continue;
            var firstWord = FirstWord(line);
            var verbMatch = firstWord.Length > 0 &&
                verbs.Contains(firstWord, StringComparer.OrdinalIgnoreCase);
            var labelMatch = knownLabels is not null && knownLabels.Any(label =>
                line.Equals(label, StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith(label + " ", StringComparison.OrdinalIgnoreCase));
            if (!verbMatch && !labelMatch)
                continue;
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>Trim, drop code fences, strip leading bullets/numbering.</summary>
    private static string StripDecorations(string raw)
    {
        var line = raw.Trim();
        if (line.StartsWith("```", StringComparison.Ordinal))
            return "";
        // bullets
        if (line.Length >= 2 && (line[0] is '-' or '*' or '•' or '+') && line[1] == ' ')
            line = line[2..].TrimStart();
        // numbering: "1. ", "2) ", "3: "
        var i = 0;
        while (i < line.Length && char.IsDigit(line[i]))
            i++;
        if (i > 0 && i + 1 < line.Length && line[i] is '.' or ')' or ':' && line[i + 1] == ' ')
            line = line[(i + 2)..].TrimStart();
        // stray markdown emphasis around the line
        return line.Trim().Trim('*').TrimEnd('.').Trim();
    }

    private static string FirstWord(string line)
    {
        var i = 0;
        while (i < line.Length && char.IsLetter(line[i]))
            i++;
        return line[..i];
    }
}
