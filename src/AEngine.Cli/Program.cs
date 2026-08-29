using System.Text;
using AEngine.Cli;
using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.Scenarios;
using AEngine.Core.Signals;
using AEngine.DebugServer;
using AEngine.Llm;

// CLI entry point: loads a scenario (scenarios/mvp by default, or e.g.
// scenarios/npc for the NPC demo) and runs a text-first REPL.
// Usage: AEngine.Cli [scenarioDir] [--debug-api[=PORT]] [--debug-port N]
//        [--llm-endpoint URL] [--llm-model NAME] [--llm-api-key KEY]
//        [--real-time]
// The action list is shown on demand via the /actions slash command;
// slash commands (see SlashCommandRegistry) are meta actions that never
// consume a turn. The debug API is off by default; it is an
// unauthenticated loopback-only REST endpoint for inspecting and mutating
// the world while the game runs. Input that exactly matches an action
// label ("wait", "go north", ...) runs directly without an LLM call.
// With an LLM endpoint configured (or AENGINE_LLM_ENDPOINT/MODEL/API_KEY
// set), other non-numeric free text is sent to the LLM as a planning
// request: the extracted plan is printed and executed stepwise. Action
// numbers keep working either way. In real-time mode (--real-time or
// /realtime) a per-second timer advances the world on its own — NPCs act
// without waiting for the player, and signals the player observes print
// as they happen; /turnbased switches back.

const int defaultDebugPort = 5050;

var debugApi = false;
var debugPort = defaultDebugPort;
var realTime = false;
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
    else if (arg == "--real-time")
    {
        realTime = true;
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
}

// Slash commands are meta actions: they never consume a turn.
var console = new ConsolePrompt();
CancellationTokenSource? realTimeCts = null;
// real-time clock speed: 1.0 = one game second per real second; 0.5 makes
// a 2s action take 4s of real time, 2.0 takes 1s. Read/written across
// threads (the timer loop), so use Interlocked.
var timescale = 1.0;
var slash = new SlashCommandRegistry();
slash.Register("actions", [], "List the actions currently available to you", _ =>
{
    var list = engine.ActionResolver.Resolve(player);
    for (var i = 0; i < list.Count; i++)
        Console.WriteLine($"  {i + 1}. {list[i].Label}");
    return false;
});
slash.Register("help", [], "List the slash commands", _ =>
{
    slash.PrintHelp();
    Console.WriteLine("Anything else you type is an in-world action" +
        (planner is null ? " number (see /actions)." : " (free text or a number from /actions)."));
    return false;
});
slash.Register("realtime", ["rt"], "Real-time mode: the world advances on its own", _ =>
{
    SetTimeMode(TimeMode.RealTime);
    return false;
});
slash.Register("turnbased", ["tb"], "Turn-based mode: time advances with your actions", _ =>
{
    SetTimeMode(TimeMode.TurnBased);
    return false;
});
slash.Register("timescale", ["ts"], "Set the real-time clock speed (1.0 = normal, 2 = twice as fast, 0.5 = half)", args =>
{
    if (args.Length == 0)
    {
        Console.WriteLine($"Timescale is {Interlocked.CompareExchange(ref timescale, 0.0, 0.0)}x.");
        return false;
    }
    if (!double.TryParse(args[0], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var factor) || factor <= 0)
    {
        Console.WriteLine("Usage: /timescale <factor> — e.g. /timescale 2 or /timescale 0.5");
        return false;
    }
    Interlocked.Exchange(ref timescale, factor);
    Console.WriteLine($"Timescale set to {factor}x — one real second advances {factor}s of game time." +
        (engine.TimeMode == TimeMode.TurnBased ? " (Takes effect in real-time mode.)" : ""));
    return false;
});
slash.Register("quit", ["exit"], "Leave the game", _ => true);

Console.WriteLine(planner is null
    ? "Type /actions to see what you can do, /help for commands."
    : "Describe what you want to do; /actions lists commands, /help for meta commands.");

using var debugServer = debugApi ? new DebugServer(engine, debugPort) : null;
if (debugServer is not null)
{
    debugServer.Start();
    Console.WriteLine($"Debug API listening on {debugServer.Address}");
}

if (realTime)
    SetTimeMode(TimeMode.RealTime);

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

    var input = console.ReadLine("> ");
    if (input is null) // EOF (e.g. piped input exhausted) — exit cleanly
    {
        realTimeCts?.Cancel();
        Console.WriteLine();
        Console.WriteLine("Goodbye.");
        return 0;
    }
    input = input.Trim();
    if (input.Length == 0)
        continue;

    if (SlashCommandRegistry.IsSlashCommand(input))
    {
        if (slash.Dispatch(input))
        {
            realTimeCts?.Cancel();
            Console.WriteLine("Goodbye.");
            return 0;
        }
        continue; // meta command: no turn consumed
    }

    if (!int.TryParse(input, out var choice))
    {
        // an exact action label ("wait", "go north", ...) runs directly —
        // no LLM round-trip needed
        var direct = engine.ActionResolver.Resolve(player)
            .FirstOrDefault(a => string.Equals(a.Label, input, StringComparison.OrdinalIgnoreCase));
        if (direct is not null)
        {
            var directResult = engine.TurnManager.PerformAction(player, direct);
            Console.WriteLine(directResult.Message);
            if (engine.TimeMode == TimeMode.TurnBased)
                engine.TurnManager.RunNpcTurns(); // real-time: the timer drives NPCs
            continue;
        }
        if (planner is null)
        {
            Console.WriteLine("I didn't understand that. Try /actions to see what you can do.");
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
            if (step.Result.Success && engine.TimeMode == TimeMode.TurnBased)
                engine.TurnManager.RunNpcTurns(); // real-time: the timer drives NPCs
        });
        var lastStep = steps[^1];
        if (lastStep.Note is not null)
            Console.WriteLine(lastStep.Note);
        else if (lastStep.Result is { Outcome: ActionOutcome.Failure })
            Console.WriteLine("Plan stopped.");
        continue;
    }
    // numeric selection from the current action list (see /actions)
    var actions = engine.ActionResolver.Resolve(player);
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
        text = console.ReadLine(action.Prompt + " ");
        if (text is null) // EOF
        {
            realTimeCts?.Cancel();
            Console.WriteLine();
            Console.WriteLine("Goodbye.");
            return 0;
        }
    }

    var result = engine.TurnManager.PerformAction(player, action, text);
    Console.WriteLine(result.Message);
    if (engine.TimeMode == TimeMode.TurnBased)
        engine.TurnManager.RunNpcTurns(); // real-time: the timer drives NPCs
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

// Switch between turn-based and real-time mode on the fly. Real-time runs
// a per-second background timer that advances the world and prints the
// signals the player observes as they happen.
void SetTimeMode(TimeMode mode)
{
    if (mode == engine.TimeMode)
        return;
    engine.TimeMode = mode;
    if (mode == TimeMode.RealTime)
    {
        realTimeCts = new CancellationTokenSource();
        _ = RealTimeLoop(realTimeCts.Token);
        Console.WriteLine("Real-time mode: the world advances on its own.");
    }
    else
    {
        realTimeCts?.Cancel();
        realTimeCts = null;
        Console.WriteLine("Turn-based mode: time advances with your actions.");
    }
}

async Task RealTimeLoop(CancellationToken ct)
{
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
    // fractional game seconds carried between ticks: each real second the
    // timescale accumulates, and whole game seconds tick off as they fill
    var pending = 0.0;
    try
    {
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                IReadOnlyList<Signal> signals;
                lock (engine.SyncRoot)
                {
                    pending += Interlocked.CompareExchange(ref timescale, 0.0, 0.0);
                    while (pending >= 1.0)
                    {
                        engine.TurnManager.Tick();
                        pending -= 1.0;
                    }
                    engine.TurnManager.RunNpcTurns();
                    signals = engine.SignalBus.Drain(player.Id);
                }
                if (signals.Count == 0)
                    continue;
                var sb = new StringBuilder();
                foreach (var signal in signals)
                    sb.AppendLine(signal.Sense == SignalSense.Visual
                        ? $"You see: {signal.Text}"
                        : $"You hear: {signal.Text}");
                // prints above the input line; the prompt and partial input
                // are redrawn underneath
                console.WriteAbove(sb.ToString().TrimEnd());
            }
            catch (OperationCanceledException)
            {
                throw; // mode switched or shutting down — handled outside
            }
            catch (Exception ex)
            {
                // a faulty handler/policy must not kill the world clock
                console.WriteAbove($"[engine error] {ex.Message}");
            }
        }
    }
    catch (OperationCanceledException)
    {
        // mode switched or shutting down
    }
}
