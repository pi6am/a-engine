namespace AEngine.Cli;

/// <summary>
/// Registry for REPL slash commands ("/actions", "/quit", …). Slash
/// commands are meta actions: they never consume game time or turns.
/// Extensible — register new commands (e.g. /save, /load) at startup.
/// </summary>
public sealed class SlashCommandRegistry
{
    /// <summary>Handler for a slash command; returns true to exit the REPL.</summary>
    public delegate bool SlashHandler(string[] args);

    private sealed record Entry(string Name, string[] Aliases, string Description, SlashHandler Handler);

    private readonly List<Entry> _entries = [];

    public void Register(string name, string[] aliases, string description, SlashHandler handler) =>
        _entries.Add(new Entry(name, aliases, description, handler));

    /// <summary>True when the input is a slash command.</summary>
    public static bool IsSlashCommand(string input) =>
        input.StartsWith("/", StringComparison.Ordinal);

    /// <summary>
    /// All commands and aliases ("/quit", "/exit", …) with their
    /// descriptions, for tab completion in the console prompt.
    /// </summary>
    public IReadOnlyList<(string Name, string Description)> CompletionItems()
    {
        var list = new List<(string, string)>();
        foreach (var entry in _entries)
        {
            list.Add(("/" + entry.Name, entry.Description));
            foreach (var alias in entry.Aliases)
                list.Add(("/" + alias, entry.Description));
        }
        return list;
    }

    /// <summary>
    /// Dispatch a slash command. Returns true to exit the REPL, false to
    /// continue. Unknown commands (and a bare "/") print a hint and
    /// return false.
    /// </summary>
    public bool Dispatch(string input)
    {
        var parts = input[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            Console.WriteLine("Type /help for commands.");
            return false;
        }
        var name = parts[0];
        var args = parts[1..];
        foreach (var entry in _entries)
        {
            if (entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                entry.Aliases.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return entry.Handler(args);
        }
        Console.WriteLine($"Unknown command '/{name}'. Try /help.");
        return false;
    }

    /// <summary>Print the registered commands.</summary>
    public void PrintHelp()
    {
        Console.WriteLine("Commands:");
        foreach (var entry in _entries)
        {
            var aliases = entry.Aliases.Length > 0 ? $" (aliases: {string.Join(", ", entry.Aliases.Select(a => "/" + a))})" : "";
            Console.WriteLine($"  /{entry.Name}{aliases} — {entry.Description}");
        }
    }
}
