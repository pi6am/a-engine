using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.Scenarios;
using AEngine.Core.Signals;
using AEngine.DebugServer;
using AEngine.Llm;

// CLI entry point: loads a scenario (scenarios/mvp by default, or e.g.
// scenarios/npc for the NPC demo) and runs a menu-driven, turn-based REPL.
// Usage: AEngine.Cli [scenarioDir] [--debug-api[=PORT]] [--debug-port N]
//        [--llm-endpoint URL] [--llm-model NAME] [--llm-api-key KEY]
// The debug API is off by default; it is an unauthenticated loopback-only
// REST endpoint for inspecting and mutating the world while the game runs.
// With an LLM endpoint configured (or AENGINE_LLM_ENDPOINT/MODEL/API_KEY
// set), non-numeric free text is sent to the LLM as a planning request:
// the extracted plan is printed and executed stepwise. Menu numbers keep
// working either way.

const int defaultDebugPort = 5050;

var debugApi = false;
var debugPort = defaultDebugPort;
string? scenarioDirArg = null;
string? llmEndpoint = Environment.GetEnvironmentVariable("AENGINE_LLM_ENDPOINT");
string? llmModel = Environment.GetEnvironmentVariable("AENGINE_LLM_MODEL");
string? llmApiKey = Environment.GetEnvironmentVariable("AENGINE_LLM_API_KEY");
for (var i = 0; i < args.Length; i++)
{
    var arg = args[i];
    if (arg == "--debug-api")
    {
        debugApi = true;
    }
    else if (arg.StartsWith("--debug-api=", StringComparison.Ordinal))
    {
        debugApi = true;
        debugPort = int.Parse(arg["--debug-api=".Length..]);
    }
    else if (arg == "--debug-port" && i + 1 < args.Length)
    {
        debugApi = true;
        debugPort = int.Parse(args[++i]);
    }
    else if (arg.StartsWith("--debug-port=", StringComparison.Ordinal))
    {
        debugApi = true;
        debugPort = int.Parse(arg["--debug-port=".Length..]);
    }
    else if (arg.StartsWith("--llm-endpoint=", StringComparison.Ordinal))
    {
        llmEndpoint = arg["--llm-endpoint=".Length..];
    }
    else if (arg == "--llm-endpoint" && i + 1 < args.Length)
    {
        llmEndpoint = args[++i];
    }
    else if (arg.StartsWith("--llm-model=", StringComparison.Ordinal))
    {
        llmModel = arg["--llm-model=".Length..];
    }
    else if (arg == "--llm-model" && i + 1 < args.Length)
    {
        llmModel = args[++i];
    }
    else if (arg.StartsWith("--llm-api-key=", StringComparison.Ordinal))
    {
        llmApiKey = arg["--llm-api-key=".Length..];
    }
    else if (arg == "--llm-api-key" && i + 1 < args.Length)
    {
        llmApiKey = args[++i];
    }
    else
    {
        scenarioDirArg = arg;
    }
}

var scenarioDir = scenarioDirArg ?? FindScenarioDir("scenarios/mvp");
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

LlmPlanner? planner = null;
if (!string.IsNullOrWhiteSpace(llmEndpoint))
{
    var llmOptions = new LlmOptions
    {
        BaseUrl = llmEndpoint,
        Model = string.IsNullOrWhiteSpace(llmModel) ? "default" : llmModel,
        ApiKey = llmApiKey,
    };
    var llmClient = new OpenAiCompatibleClient(llmOptions);
    planner = new LlmPlanner(llmClient, engine);
    engine.PolicyRegistry.Register(new LlmPolicy(planner));
    Console.WriteLine($"LLM planning enabled ({llmOptions.BaseUrl}, model '{llmOptions.Model}').");
    Console.WriteLine("Type a menu number, free text, or 'quit'.");
}
else
{
    Console.WriteLine("Type a menu number (or 'quit') and press Enter.");
}

using var debugServer = debugApi ? new DebugServer(engine, debugPort) : null;
if (debugServer is not null)
{
    debugServer.Start();
    Console.WriteLine($"Debug API listening on {debugServer.Address}");
}

while (true)
{
    var look = engine.TurnManager.Execute(player, "look", player.Id);
    Console.WriteLine();
    Console.WriteLine(look.Message);
    Console.WriteLine();

    // sensory signals from other agents' actions (e.g. NPCs)
    foreach (var signal in engine.SignalBus.Drain(player.Id))
    {
        Console.WriteLine(signal.Sense == SignalSense.Visual
            ? $"You see: {signal.Text}"
            : $"You hear: {signal.Text}");
    }

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
    if (!int.TryParse(input, out var choice))
    {
        if (planner is null)
        {
            Console.WriteLine("Invalid choice.");
            continue;
        }
        // free text -> LLM plan -> stepwise execution (NPC turns after each step)
        IReadOnlyList<string> plan;
        try
        {
            plan = await planner.CreatePlanAsync(player, input, npc: false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"LLM request failed: {ex}");
            continue;
        }
        if (plan.Count == 0)
        {
            Console.WriteLine("The LLM returned no usable actions.");
            continue;
        }
        Console.WriteLine("Plan:");
        foreach (var line in plan)
            Console.WriteLine($"  - {line}");
        var executor = new PlanExecutor(engine, player);
        var steps = executor.Execute(plan, step =>
        {
            Console.WriteLine(step.Result!.Message);
            if (step.Result.Success)
                engine.TurnManager.RunNpcTurns();
        });
        var lastStep = steps[^1];
        if (lastStep.Note is not null)
            Console.WriteLine(lastStep.Note);
        else if (lastStep.Result is { Outcome: ActionOutcome.Failure })
            Console.WriteLine("Plan stopped.");
        continue;
    }
    if (choice < 1 || choice > actions.Count)
    {
        Console.WriteLine("Invalid choice.");
        continue;
    }

    var action = actions[choice - 1];
    string? text = null;
    if (action.Prompt is not null)
    {
        // prompted verbs (e.g. say) take a free-text argument
        Console.Write(action.Prompt + " ");
        text = Console.ReadLine();
        if (text is null) // EOF
        {
            Console.WriteLine();
            Console.WriteLine("Goodbye.");
            return 0;
        }
    }

    var result = engine.TurnManager.PerformAction(player, action, text);
    Console.WriteLine(result.Message);
    engine.TurnManager.RunNpcTurns();
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
