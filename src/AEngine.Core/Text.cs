namespace AEngine.Core;

/// <summary>
/// The shared text helpers (sentence capitalization, word
/// normalization) — one implementation, used by handlers, the
/// resolver, the plan matcher, and the CLI alike. Duplicated private
/// copies of these have historically drifted apart (the nest's
/// always-open bug took three edits because three private IsOpen
/// copies each broke differently); nothing in this class has a
/// second implementation anywhere.
/// </summary>
public static class Text
{
    /// <summary>Sentence-case the first character; empty strings pass through.</summary>
    public static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>
    /// Lowercase and collapse runs of whitespace into single spaces —
    /// the shared shape of every label-matching and answer-matching
    /// comparison. Punctuation and article handling stay with the
    /// callers that need them.
    /// </summary>
    public static string NormalizeWords(string s) =>
        string.Join(' ', s.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
