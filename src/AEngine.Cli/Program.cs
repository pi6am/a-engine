using AEngine.Core.Runtime;
using AEngine.Core.Scenarios;

// CLI entry point: loads the MVP scenario and runs a menu-driven,
// turn-based REPL. Usage: AEngine.Cli [scenarioDir]

var scenarioDir = args.Length > 0 ? args[0] : FindScenarioDir("scenarios/mvp");
if (scenarioDir is null)
{
    Console.Error.WriteLine("Could not find the scenario directory 'scenarios/mvp'.");
    return 1;
}

var engine = GameEngine.CreateWithBuiltinHandlers();
string scenarioName;
try
{
    scenarioName = ScenarioLoader.LoadInto(
        engine,
        Path.Combine(scenarioDir, "modules.json"),
        Path.Combine(scenarioDir, "world.json"));
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to load scenario: {ex.Message}");
    return 1;
}

var player = engine.World.GetObject("player");
Console.WriteLine($"=== {scenarioName} ===");
Console.WriteLine("Type a menu number (or 'quit') and press Enter.");

while (true)
{
    var look = engine.TurnManager.Execute(player, "look", player.Id);
    Console.WriteLine();
    Console.WriteLine(look.Message);
    Console.WriteLine();

    var actions = engine.ActionResolver.Resolve(player);
    for (var i = 0; i < actions.Count; i++)
        Console.WriteLine($"  {i + 1}. {actions[i].Label}");
    Console.WriteLine("  0. Quit");
    Console.Write("> ");

    var input = Console.ReadLine();
    if (input is null) // EOF (e.g. piped input exhausted) — exit cleanly
    {
        Console.WriteLine();
        Console.WriteLine("Goodbye.");
        return 0;
    }
    input = input.Trim();
    if (input.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("q", StringComparison.OrdinalIgnoreCase) ||
        input == "0")
    {
        Console.WriteLine("Goodbye.");
        return 0;
    }
    if (!int.TryParse(input, out var choice) || choice < 1 || choice > actions.Count)
    {
        Console.WriteLine("Invalid choice.");
        continue;
    }

    var result = engine.TurnManager.PerformAction(player, actions[choice - 1]);
    Console.WriteLine(result.Message);
}

static string? FindScenarioDir(string relative)
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        var candidate = Path.Combine(dir.FullName, relative);
        if (File.Exists(Path.Combine(candidate, "world.json")))
            return candidate;
        dir = dir.Parent;
    }
    // fall back to the current working directory
    var cwdCandidate = Path.GetFullPath(relative);
    return File.Exists(Path.Combine(cwdCandidate, "world.json")) ? cwdCandidate : null;
}
