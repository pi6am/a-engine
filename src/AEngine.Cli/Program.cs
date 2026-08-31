using System.Text;
using AEngine.Cli;
using AEngine.Core.Actions;
using AEngine.Core.Modules;
using AEngine.Core.Runtime;
using AEngine.Core.Scenarios;
using AEngine.Core.Signals;
using AEngine.DebugServer;
using AEngine.Llm;

// CLI entry point: loads a scenario (scenarios/mvp by default, or e.g.
// scenarios/npc for the NPC demo) and runs a text-first REPL.
// Usage: AEngine.Cli [scenarioDirOrFile] [--debug-api[=PORT]] [--debug-port N]
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
// request: the extracted plan is printed (/showplan, default off) and
// executed stepwise. Action numbers keep working either way. In real-time mode (--real-time or
// /realtime) a per-second timer advances the world on its own — NPCs act
// without waiting for the player, and signals the player observes print
// as they happen; /turnbased switches back.

const int defaultDebugPort = 5050;

var debugApi = false;
var debugPort = defaultDebugPort;
var realTime = false;
string? scenarioArg = null;
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
        scenarioArg = arg;
    }
}

// a scenario is a directory of JSON files or a packaged file (zip, any extension)
var scenarioPath = scenarioArg ?? FindScenarioDir("scenarios/mvp");
if (scenarioPath is null)
{
    Console.Error.WriteLine("Could not find the scenario 'scenarios/mvp'.");
    return 1;
}

var engine = GameEngine.CreateWithBuiltinHandlers();
string scenarioName;
try
{
    scenarioName = ScenarioLoader.LoadFrom(engine, scenarioPath);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to load scenario: {ex.Message}");
    return 1;
}

var player = engine.World.GetObject("player");
// The full room description prints on arrival (the player's room changed
// since the last render) — while you stay put, only action results and
// observations print. Tracked across the main loop and the real-time
// timer (an NPC could carry you elsewhere while you're idle).
string? lastRoomId = null;
Console.WriteLine($"=== {scenarioName} ===");

LlmPlanner? planner = null;
Narrator? narrator = null;
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
    narrator = new Narrator(llmClient, player.Name);
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
var output = new OutputSettings();
var slash = new SlashCommandRegistry();
slash.Register("showplan", [], "Control whether the plan is logged (/showplan on|off)", args =>
{
    if (args.Length == 1 && args[0].Equals("on", StringComparison.OrdinalIgnoreCase))
        output.ShowPlan = true;
    else if (args.Length == 1 && args[0].Equals("off", StringComparison.OrdinalIgnoreCase))
        output.ShowPlan = false;
    else
    {
        Console.WriteLine($"Plan logging is {(output.ShowPlan ? "on" : "off")}. Usage: /showplan on|off");
        return false;
    }
    Console.WriteLine($"Plan logging {(output.ShowPlan ? "on" : "off")}.");
    return false;
});
slash.Register("narrate", [], "Expand narration via LLM (/narrate all|room|actions|off)", args =>
{
    if (args.Length == 1 && Enum.TryParse<NarrateScope>(args[0], ignoreCase: true, out var scope))
    {
        output.Narrate = scope;
        Console.WriteLine($"Narration: {args[0].ToLowerInvariant()}." +
            (scope != NarrateScope.Off && narrator is null
                ? " (No LLM endpoint configured — no effect.)"
                : ""));
        return false;
    }
    Console.WriteLine($"Narration is {output.Narrate.ToString().ToLowerInvariant()}. Usage: /narrate all|room|actions|off");
    return false;
});
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
console.Completions = slash.CompletionItems();

// quick-time reactions: F2 opens a modal popup for a pending reaction
// (the status line announces it in real-time mode; turn-based prompts
// inline — see ResolvePendingReactions)
console.ReactionMenuProvider = () =>
{
    lock (engine.SyncRoot)
    {
        var pr = engine.Reactions.PendingFor(player.Id);
        if (pr is null)
            return null;
        var effectiveDefault = engine.Reactions.EffectiveDefault(pr);
        return new ReactionMenu(
            pr.Announcement,
            pr.Options.Select(o => o.Label).ToList(),
            pr.Options.TakeWhile(o => !ReferenceEquals(o, effectiveDefault)).Count(),
            Math.Max(0, pr.DeadlineTurn - engine.TurnManager.Turn));
    }
};
console.ReactionChosen = index =>
{
    lock (engine.SyncRoot)
    {
        var pr = engine.Reactions.PendingFor(player.Id);
        if (pr is not null && index < pr.Options.Count)
            engine.Reactions.Choose(pr.Id, pr.Options[index].Id);
    }
};

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

Console.WriteLine(); // separate the intro from the first room description
// action narration (/narrate actions|all): raw outcome and observation
// lines buffer and flush as one LLM batch per player input — one call's
// latency per turn instead of one per line
var eventBuffer = new List<string>();
while (true)
{
    // game over: a handler ended it (epilogue), or the player was
    // incapacitated (defeat) — print the ending and leave
    string? gameOverText;
    lock (engine.SyncRoot)
    {
        if (engine.GameOver is null &&
            Health.IsIncapacitated(engine.World, engine.ModuleRegistry, player))
            engine.GameOver = engine.DefeatText;
        gameOverText = engine.GameOver;
    }
    if (gameOverText is not null)
    {
        Console.WriteLine();
        Console.WriteLine(Wrap(gameOverText));
        realTimeCts?.Cancel();
        return 0;
    }

    string? arrivalLook = null;
    string? arrivalRoomId = null;
    string? arrivalRoomName = null;
    lock (engine.SyncRoot)
    {
        var room = engine.World.RoomOf(player.Id);
        if (room.Id != lastRoomId)
        {
            lastRoomId = room.Id;
            arrivalRoomId = room.Id;
            arrivalRoomName = room.Name;
            arrivalLook = engine.TurnManager.Execute(player, "look", player.Id).Message;
        }
    }
    if (arrivalLook is not null)
        await PrintRoomAsync(arrivalRoomId!, arrivalRoomName!, arrivalLook);

    // sensory signals from other agents' actions (e.g. NPCs)
    foreach (var signal in engine.SignalBus.Drain(player.Id))
    {
        if (NarrateActions())
            eventBuffer.Add(RenderSignal(signal));
        else
            Console.WriteLine(RenderSignal(signal));
    }
    // safety net: a real-time race can land a signal here after the
    // action's own flush already ran
    await FlushEventsAsync();

    var input = console.ReadLine("> ");
    if (input is null) // EOF (e.g. piped input exhausted) — exit cleanly
    {
        // woken by the world clock (real-time mode): the game ended while
        // the player was idle — the loop top prints the ending
        if (console.WasWoken)
            continue;
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
            // an ending prints once, at the top of the loop — not inline
            if (!directResult.EndsGame)
                await PrintResultAsync(direct.Verb, directResult.Message);
            if (engine.TimeMode == TimeMode.TurnBased)
                RunNpcTurnsAndResolve(); // real-time: the timer drives NPCs
            await FinishActionAsync();
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
        if (output.ShowPlan)
        {
            Console.WriteLine("Plan:");
            foreach (var line in plan)
                Console.WriteLine($"  - {line}");
        }
        var executor = new PlanExecutor(engine, player);
        var steps = executor.Execute(plan, step =>
        {
            // sync-over-async is safe here: no synchronization context in a
            // console app, and the plan executor's callback is synchronous
            if (!step.Result!.EndsGame) // an ending prints once, at the top of the loop
                PrintResultAsync(step.Action!.Verb, step.Result!.Message).GetAwaiter().GetResult();
            if (step.Result.Success && engine.TimeMode == TimeMode.TurnBased)
                RunNpcTurnsAndResolve(); // real-time: the timer drives NPCs
        });
        var lastStep = steps[^1];
        var note = lastStep.Note
            ?? (lastStep.Result is { Outcome: ActionOutcome.Failure } && steps.Count < plan.Count
                ? "Plan stopped." // only when later steps were skipped
                : null);
        // narrated: the prose explains the failure, so the meta line
        // follows it; raw: the meta line precedes the closing blank
        if (NarrateActions())
        {
            await FinishActionAsync();
            if (note is not null)
                Console.WriteLine(note);
        }
        else
        {
            if (note is not null)
                Console.WriteLine(note);
            await FinishActionAsync();
        }
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
        text = console.ReadLine(action.Prompt + " ", completions: false);
        if (text is null) // EOF
        {
            realTimeCts?.Cancel();
            Console.WriteLine();
            Console.WriteLine("Goodbye.");
            return 0;
        }
    }

    var result = engine.TurnManager.PerformAction(player, action, text);
    if (!result.EndsGame) // an ending prints once, at the top of the loop
        await PrintResultAsync(action.Verb, result.Message);
    if (engine.TimeMode == TimeMode.TurnBased)
        RunNpcTurnsAndResolve(); // real-time: the timer drives NPCs
    await FinishActionAsync();
}

// Word-wrap narrated prose to the console width, capped at 80 columns,
// breaking at spaces (hard break only when a word exceeds the width).
static string Wrap(string text)
{
    var width = 80;
    try
    {
        width = Math.Min(Console.WindowWidth, 80);
    }
    catch
    {
        // output redirected — keep the cap
    }
    if (width < 20)
        width = 80; // degenerate console
    var sb = new StringBuilder();
    foreach (var rawLine in text.Split('\n'))
    {
        var line = rawLine.TrimEnd();
        while (line.Length > width)
        {
            var cut = line.LastIndexOf(' ', width);
            if (cut <= 0)
                cut = width;
            sb.AppendLine(line[..cut]);
            line = line[(cut + 1)..].TrimStart();
        }
        sb.AppendLine(line);
    }
    return sb.ToString().TrimEnd();
}

// Same-room events print bare ("The old cook opens the cupboard.");
// the "You see:"/"You hear:" framing is reserved for signals that
// crossed a portal — you were not simply present for those. Bare lines
// start the sentence, so capitalize.
// Room narration (/narrate room|all) is on when the narrator exists and
// the scope covers rooms.
bool NarrateRooms() =>
    narrator is not null && output.Narrate is NarrateScope.Room or NarrateScope.All;

// Action narration (/narrate actions|all) is on when the narrator exists
// and the scope covers action outcomes and observations.
bool NarrateActions() =>
    narrator is not null && output.Narrate is NarrateScope.Actions or NarrateScope.All;

// Narrate the buffered event lines as one prose block (falling back to
// the raw lines on any failure) and close with a blank line. No-op on an
// empty buffer.
async Task FlushEventsAsync()
{
    if (eventBuffer.Count == 0)
        return;
    var batch = eventBuffer.ToList();
    eventBuffer.Clear();
    string? narrated = null;
    try
    {
        narrated = await narrator!.NarrateEventsAsync(batch);
    }
    catch
    {
        // fall back to the raw lines
    }
    Console.WriteLine(narrated is not null ? Wrap(narrated) : string.Join('\n', batch));
    Console.WriteLine(); // outcomes end with a blank line
}

// Close out a player action: raw mode just ends the outcome with a blank
// line; narrated mode folds the NPC observations the action triggered
// into the batch and flushes it. A move narrates the transition here too
// ("You pass through the stone archway…") — the new room's description
// prints after, at the top of the loop.
async Task FinishActionAsync()
{
    if (!NarrateActions())
    {
        Console.WriteLine(); // outcomes end with a blank line
        return;
    }
    foreach (var signal in engine.SignalBus.Drain(player.Id))
        eventBuffer.Add(RenderSignal(signal));
    await FlushEventsAsync();
}

// Narrate a raw look render, or null when narration is off or failed —
// the caller then prints the raw text. Silent fallback by design: the raw
// render is always a correct description.
async Task<string?> TryNarrateAsync(string roomId, string raw)
{
    if (!NarrateRooms())
        return null;
    try
    {
        var narrated = await narrator!.NarrateRoomAsync(roomId, raw);
        return narrated == raw ? null : narrated;
    }
    catch
    {
        return null;
    }
}

// Print the room on arrival: the raw look render, or the room's name
// followed by narrated prose when room narration is on. No leading
// blank — the preceding outcome (or the intro) already ended with one.
async Task PrintRoomAsync(string roomId, string roomName, string raw)
{
    var narrated = await TryNarrateAsync(roomId, raw);
    if (narrated is not null)
    {
        Console.WriteLine(roomName);
        Console.WriteLine();
    }
    Console.WriteLine(narrated is not null ? Wrap(narrated) : raw);
    Console.WriteLine();
}

// Print an action result. An explicit look is a room description, so it
// routes through the room narrator (the cache makes repeat looks free) or
// prints raw — it never joins the event batch, keeping the room/actions
// scopes distinct.
async Task PrintResultAsync(string verb, string message)
{
    if (verb == "look")
    {
        if (NarrateRooms())
        {
            string roomId;
            string roomName;
            lock (engine.SyncRoot)
            {
                var room = engine.World.RoomOf(player.Id);
                roomId = room.Id;
                roomName = room.Name;
            }
            var narrated = await TryNarrateAsync(roomId, message);
            if (narrated is not null)
            {
                Console.WriteLine(roomName);
                Console.WriteLine();
                Console.WriteLine(Wrap(narrated));
                return;
            }
        }
        Console.WriteLine(message);
        return;
    }
    // action narration batches outcomes with the observations that follow
    if (NarrateActions())
        eventBuffer.Add(message);
    else
        Console.WriteLine(message);
}

static string RenderSignal(Signal signal) =>
    signal.ThroughPortal
        ? signal.Sense == SignalSense.Visual
            ? $"You see: {signal.Text}"
            : $"You hear: {signal.Text}"
        : Capitalize(signal.Text);

static string Capitalize(string s) =>
    s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

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

// NPC turns in turn-based mode, then resolve any reactions they
// telegraphed: the player is prompted inline; NPC defenders get a brief
// wall-clock grace period for their (possibly LLM) policy before the
// default reaction applies.
void RunNpcTurnsAndResolve()
{
    engine.TurnManager.RunNpcTurns();
    ResolvePendingReactions();
}

void ResolvePendingReactions()
{
    while (true)
    {
        PendingReaction? playerPending;
        PendingReaction? npcPending;
        lock (engine.SyncRoot)
        {
            engine.Reactions.PollPolicies();
            playerPending = engine.Reactions.PendingFor(player.Id);
            npcPending = engine.Reactions.Pending
                .FirstOrDefault(p => p.PolicySelection is { IsCompleted: false });
        }
        if (playerPending is not null)
        {
            PromptReactionInline(playerPending);
            continue;
        }
        if (npcPending is null)
        {
            PrintResolvedOutcomes();
            return;
        }
        if (!npcPending.PolicySelection!.Wait(TimeSpan.FromSeconds(15)))
        {
            lock (engine.SyncRoot)
                engine.Reactions.ForceDefault(npcPending.Id);
        }
    }
}

// resolution results of the player's own telegraphed actions ("You hit
// the arena duelist for 6 damage.") — signals never reach the actor
void PrintResolvedOutcomes()
{
    foreach (var (actorId, message) in engine.Reactions.DrainResolved())
        if (actorId == player.Id)
        {
            if (NarrateActions())
                eventBuffer.Add(message);
            else
                Console.WriteLine(message);
        }
}

void PromptReactionInline(PendingReaction pr)
{
    ReactionOptionSpec effectiveDefault;
    lock (engine.SyncRoot)
        effectiveDefault = engine.Reactions.EffectiveDefault(pr);
    Console.WriteLine();
    Console.WriteLine(pr.Announcement + " How do you react?");
    for (var i = 0; i < pr.Options.Count; i++)
        Console.WriteLine($"  {i + 1}. {pr.Options[i].Label}" +
            (ReferenceEquals(pr.Options[i], effectiveDefault) ? " (default)" : ""));
    var answer = console.ReadLine("> ")?.Trim() ?? "";
    var chosen = int.TryParse(answer, out var n) && n >= 1 && n <= pr.Options.Count
        ? pr.Options[n - 1]
        : pr.Options.FirstOrDefault(o => o.Label.Equals(answer, StringComparison.OrdinalIgnoreCase))
          ?? effectiveDefault;
    lock (engine.SyncRoot)
        engine.Reactions.Choose(pr.Id, chosen.Id);
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
    // narrated events accumulate over a short window before flushing as one
    // batch (see the tick body)
    var pendingEvents = new List<string>();
    var pendingSince = DateTime.UtcNow;
    var eventNarrationWindow = TimeSpan.FromSeconds(2);
    var gameOverWakeSent = false;
    try
    {
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                IReadOnlyList<Signal> signals;
                IReadOnlyList<(string ActorId, string Message)> resolved;
                string? status;
                string? arrival = null;
                string? arrivalRoomId = null;
                string? arrivalRoomName = null;
                bool gameOver;
                lock (engine.SyncRoot)
                {
                    pending += Interlocked.CompareExchange(ref timescale, 0.0, 0.0);
                    while (pending >= 1.0)
                    {
                        engine.TurnManager.Tick();
                        pending -= 1.0;
                    }
                    engine.TurnManager.RunNpcTurns();
                    // an NPC action may have ended the game (a rite, a
                    // defeat): the main loop is blocked in ReadLine and
                    // would never notice — wake it so it prints the ending
                    gameOver = engine.GameOver is not null ||
                        Health.IsIncapacitated(engine.World, engine.ModuleRegistry, player);
                    signals = engine.SignalBus.Drain(player.Id);
                    resolved = engine.Reactions.DrainResolved();
                    // the player arrived somewhere new without acting
                    // (e.g. carried): print the room description
                    var room = engine.World.RoomOf(player.Id);
                    if (room.Id != lastRoomId)
                    {
                        lastRoomId = room.Id;
                        arrivalRoomId = room.Id;
                        arrivalRoomName = room.Name;
                        arrival = engine.TurnManager.Execute(player, "look", player.Id).Message;
                    }
                    // announce a pending quick-time reaction on the status line
                    status = engine.Reactions.PendingFor(player.Id) is { } pr
                        ? $"{pr.Announcement} F2 to react " +
                          $"(default: {engine.Reactions.EffectiveDefault(pr).Label}, " +
                          $"{Math.Max(0, pr.DeadlineTurn - engine.TurnManager.Turn)}s)"
                        : null;
                }
                console.SetStatus(status);
                if (!gameOver &&
                    arrival is null && signals.Count == 0 && resolved.Count == 0 && pendingEvents.Count == 0)
                    continue;
                var sb = new StringBuilder();
                if (arrival is not null)
                {
                    if (NarrateRooms())
                    {
                        // never block the world clock on the LLM: narrate in
                        // the background and print above the prompt when ready
                        var (narrateRoomId, narrateRoomName, narrateRaw) =
                            (arrivalRoomId!, arrivalRoomName!, arrival);
                        _ = Task.Run(async () =>
                        {
                            var narrated = await TryNarrateAsync(narrateRoomId, narrateRaw);
                            console.WriteAbove(narrated is not null
                                ? $"{narrateRoomName}\n\n{Wrap(narrated)}"
                                : narrateRaw);
                        });
                    }
                    else
                    {
                        sb.AppendLine(arrival);
                    }
                }
                // the tick's events — observed signals plus the player's
                // own telegraphed actions resolving (signals never reach
                // the actor) — batch into one narration call
                var eventLines = new List<string>();
                foreach (var signal in signals)
                    eventLines.Add(RenderSignal(signal));
                foreach (var (actorId, message) in resolved)
                    if (actorId == player.Id)
                        eventLines.Add(message);
                if (NarrateActions() && !gameOver)
                {
                    // batch over a short window: a burst of world activity
                    // costs one LLM call instead of one per tick. The window
                    // trades a little latency for far fewer calls (the LLM
                    // server also serves NPC planning, serialized).
                    if (eventLines.Count > 0)
                    {
                        if (pendingEvents.Count == 0)
                            pendingSince = DateTime.UtcNow;
                        pendingEvents.AddRange(eventLines);
                    }
                    if (pendingEvents.Count >= 6 ||
                        (pendingEvents.Count > 0 && DateTime.UtcNow - pendingSince >= eventNarrationWindow))
                    {
                        var batch = pendingEvents.ToList();
                        pendingEvents.Clear();
                        // never block the world clock on the LLM: narrate in
                        // the background and print above the prompt when ready
                        _ = Task.Run(async () =>
                        {
                            string? narrated = null;
                            try
                            {
                                narrated = await narrator!.NarrateEventsAsync(batch);
                            }
                            catch
                            {
                                // fall back to the raw lines
                            }
                            console.WriteAbove(narrated is not null
                                ? Wrap(narrated)
                                : string.Join('\n', batch));
                        });
                    }
                }
                else
                {
                    foreach (var line in pendingEvents) // narrate toggled off mid-batch
                        sb.AppendLine(line);
                    pendingEvents.Clear();
                    foreach (var line in eventLines)
                        sb.AppendLine(line);
                }
                // prints above the input line; the prompt and partial input
                // are redrawn underneath
                var text = sb.ToString().TrimEnd();
                if (text.Length > 0)
                    console.WriteAbove(text);
                // the world ended on this tick: wake the blocked ReadLine —
                // the main loop re-checks and prints the ending (single
                // print path; never printed from the timer thread)
                if (gameOver && !gameOverWakeSent)
                {
                    gameOverWakeSent = true;
                    console.Wake();
                }
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
