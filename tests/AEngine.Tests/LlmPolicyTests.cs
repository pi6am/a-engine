using AEngine.Core.Runtime;
using AEngine.Core.Scenarios;
using AEngine.Core.World;
using AEngine.Llm;

namespace AEngine.Tests;

/// <summary>
/// LlmPolicy on the npc scenario with a FakeLlmClient: the cook (switched
/// to the "llm" policy via field override) executes a canned plan across
/// RunNpcTurns calls, and a step invalidated mid-plan discards the plan
/// remainder (a fresh plan is requested next selection).
/// </summary>
public class LlmPolicyTests
{
    private static GameEngine NewEngine(FakeLlmClient llm)
    {
        var engine = GameEngine.CreateWithBuiltinHandlers();
        var dir = FindScenarioDir();
        ScenarioLoader.LoadInto(
            engine,
            Path.Combine(dir, "modules.json"),
            Path.Combine(dir, "world.json"));
        var planner = new LlmPlanner(llm, engine);
        engine.PolicyRegistry.Register(new LlmPolicy(planner));
        engine.World.SetFieldOverride("cook", "agent", "policy", World.ToJson("llm"));
        return engine;
    }

    private static string FindScenarioDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scenarios", "npc");
            if (File.Exists(Path.Combine(candidate, "world.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate scenarios/npc.");
    }

    private static void RunTurns(GameEngine engine, int count)
    {
        // one NPC round per batch: the first call starts the (fake-LLM,
        // already-complete) selection, the next completes it; later
        // batches need a fresh round
        engine.TurnManager.NewNpcRound();
        for (var i = 0; i < count; i++)
            engine.TurnManager.RunNpcTurns();
    }

    // the scenario's action durations leave the cook busy for a turn or two
    // after acting; in turn-based mode only performed actions advance the
    // turn, so the player waits to let time pass
    private static void PassTurns(GameEngine engine, int count)
    {
        var player = engine.World.GetObject("player");
        var wait = engine.ActionResolver.Resolve(player).First(a => a.Verb == "wait");
        for (var i = 0; i < count; i++)
            engine.TurnManager.PerformAction(player, wait);
    }

    [Fact]
    public void Cook_ExecutesCannedPlan_AcrossTurns()
    {
        var llm = new FakeLlmClient();
        llm.Enqueue("""
            Take the loaf of bread
            Open the cupboard
            Take the carving knife
            """);
        var engine = NewEngine(llm);

        // each step takes two RunNpcTurns calls (start selection, execute)
        RunTurns(engine, 2);
        Assert.Equal("cook", engine.World.GetObject("bread").Parent);

        PassTurns(engine, 2); // ride out the take's busy turns
        RunTurns(engine, 2);
        Assert.True(engine.ModuleRegistry.ResolveBool(
            engine.World.GetObject("cupboard"), "openable", "open"));

        // "Take the carving knife" only became available after the cupboard opened
        PassTurns(engine, 2);
        RunTurns(engine, 2);
        Assert.Equal("cook", engine.World.GetObject("knife").Parent);

        // the plan is exhausted; no further LLM call was needed
        Assert.Equal(0, llm.Remaining);
    }

    [Fact]
    public void InvalidatedStep_DiscardsPlanRemainder_AndReplans()
    {
        var llm = new FakeLlmClient();
        llm.Enqueue("""
            Open the cupboard
            Take the carving knife
            """);
        llm.Enqueue("Take the loaf of bread");
        var engine = NewEngine(llm);

        RunTurns(engine, 2); // cook opens the cupboard
        Assert.True(engine.ModuleRegistry.ResolveBool(
            engine.World.GetObject("cupboard"), "openable", "open"));

        // the cupboard gets closed before the next step: "Take the carving
        // knife" no longer matches the available actions
        var cook = engine.World.GetObject("cook");
        Assert.True(engine.TurnManager.Execute(cook, "close", "cupboard").Success);

        PassTurns(engine, 2); // ride out the open's busy turns
        RunTurns(engine, 2); // stale step discarded (cook passes)
        Assert.Equal("cupboard", engine.World.GetObject("knife").Parent);

        // a fresh plan was requested (second canned response) and executed
        RunTurns(engine, 2);
        Assert.Equal("cook", engine.World.GetObject("bread").Parent);
        Assert.Equal(0, llm.Remaining);
    }

    [Fact]
    public void PolicyContext_ForNpc_IncludesCharacterAndGoals()
    {
        var llm = new FakeLlmClient();
        llm.Enqueue("Look around");
        var engine = NewEngine(llm);

        RunTurns(engine, 2);

        var messages = llm.LastMessages!;
        Assert.Equal(2, messages.Count);
        // the system prompt frames the NPC's identity: who "you" IS
        Assert.Contains("You are the old cook", messages[0].Content);
        Assert.Contains("grizzled old cook", messages[0].Content);
        Assert.Contains("Goals:", messages[1].Content);
        Assert.Contains("Available actions", messages[1].Content);
        // the request nudges idle agents to Wait instead of polling with Look
        Assert.Contains("prefer Wait", messages[1].Content);
    }

    [Fact]
    public void NewObservation_InterruptsCachedPlan_AndTriggersReplan()
    {
        var llm = new FakeLlmClient();
        llm.Enqueue("""
            Take the loaf of bread
            Open the cupboard
            """);
        llm.Enqueue("Say: \"What was that?\"");
        var engine = NewEngine(llm);

        RunTurns(engine, 2); // cook plans (LLM call 1) and takes the bread
        Assert.Equal("cook", engine.World.GetObject("bread").Parent);

        RunTurns(engine, 2); // the cached second step pops without an LLM call
        Assert.Equal(1, llm.Remaining);

        // the guest speaks; the audio crosses the closed kitchen door
        var player = engine.World.GetObject("player");
        var say = engine.ActionResolver.Resolve(player).First(a => a.Verb == "say");
        Assert.True(engine.TurnManager.PerformAction(player, say, "Hey cook!").Success);

        // the pending observation interrupts the cached plan: the cook
        // re-plans immediately and the interruption is in the prompt
        RunTurns(engine, 2);
        Assert.Equal(0, llm.Remaining);
        Assert.Contains("Recent observations and actions", llm.LastMessages![1].Content);
        Assert.Contains("Hey cook!", llm.LastMessages[1].Content);
    }

    [Fact]
    public void Speech_CachedActionStep_ExecutesWhileSpeechPlaysOut()
    {
        var llm = new FakeLlmClient();
        llm.Enqueue("""
            Say: "What a fine day it is, indeed!"
            Open the cupboard
            """);
        var engine = NewEngine(llm);
        engine.TimeMode = TimeMode.RealTime; // speech pacing is real-time

        RunTurns(engine, 2); // cook plans (LLM call 1) and says the line
        Assert.True(engine.TurnManager.Turn < engine.TurnManager.SpeechBusyUntilTurn("cook"));

        // the cached non-speech step pops and executes mid-utterance —
        // talking doesn't block doing (a couple of ticks pass: new
        // real-time rounds, the line still pacing)
        for (var i = 0; i < 2; i++)
            engine.TurnManager.Tick();
        RunTurns(engine, 2);
        Assert.True(engine.TurnManager.Turn < engine.TurnManager.SpeechBusyUntilTurn("cook"));
        Assert.True(engine.ModuleRegistry.ResolveBool(
            engine.World.GetObject("cupboard"), "openable", "open"));
        Assert.Equal(0, llm.Remaining); // no new LLM call was needed
    }

    [Fact]
    public void Speech_InterruptionWaitsForSpeechTrack_ThenReplans()
    {
        var llm = new FakeLlmClient();
        llm.Enqueue("Say: \"What a fine day it is, indeed!\"");
        llm.Enqueue("Take the loaf of bread");
        var engine = NewEngine(llm);
        engine.TimeMode = TimeMode.RealTime; // speech pacing is real-time

        RunTurns(engine, 2); // cook plans (LLM call 1) and says the line
        Assert.True(engine.TurnManager.Turn < engine.TurnManager.SpeechBusyUntilTurn("cook"));

        // the guest speaks; the observation is pending while the cook is
        // mid-utterance — it does NOT interrupt yet (no new LLM call)
        var player = engine.World.GetObject("player");
        var say = engine.ActionResolver.Resolve(player).First(a => a.Verb == "say");
        Assert.True(engine.TurnManager.PerformAction(player, say, "Hey cook!").Success);
        RunTurns(engine, 4); // even count: no dangling in-flight selection
        Assert.Equal(1, llm.Remaining);

        // once the speech track clears, the pending signal interrupts as usual
        for (var i = 0; i < 8; i++)
            engine.TurnManager.Tick();
        RunTurns(engine, 2);
        Assert.Equal(0, llm.Remaining);
        Assert.Equal("cook", engine.World.GetObject("bread").Parent);
        Assert.Contains("Hey cook!", llm.LastMessages![1].Content);
    }
}
