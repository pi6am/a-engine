using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.Scenarios;
using AEngine.Llm;

namespace AEngine.Tests;

/// <summary>
/// PlanExecutor on the MVP scenario: a full playthrough plan (including
/// the conditional unlock -> open -> go sequence), a plan missing "open"
/// that stops at "go" with the failure message, and an unknown line that
/// stops with "don't know how".
/// </summary>
public class PlanExecutorTests
{
    private static GameEngine NewEngine()
    {
        var engine = GameEngine.CreateWithBuiltinHandlers();
        var dir = FindScenarioDir();
        ScenarioLoader.LoadInto(
            engine,
            Path.Combine(dir, "modules.json"),
            Path.Combine(dir, "world.json"));
        return engine;
    }

    private static string FindScenarioDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scenarios", "mvp");
            if (File.Exists(Path.Combine(candidate, "world.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate scenarios/mvp.");
    }

    [Fact]
    public void FullPlaythroughPlan_Succeeds()
    {
        var engine = NewEngine();
        var player = engine.World.GetObject("player");
        var executor = new PlanExecutor(engine, player);

        var steps = executor.Execute(
        [
            "Open the desk drawer",
            "Take the brass key",
            "Unlock the wooden door",
            "Open the wooden door",
            "Go north",
        ]);

        Assert.Equal(5, steps.Count);
        Assert.All(steps, s => Assert.True(s.Result!.Success, $"{s.Line}: {s.Result!.Message}"));
        Assert.Equal("room_b", engine.World.GetObject("player").Parent);
    }

    [Fact]
    public void PlanMissingOpen_StopsAtGoWithFailureMessage()
    {
        var engine = NewEngine();
        var player = engine.World.GetObject("player");
        var executor = new PlanExecutor(engine, player);

        var steps = executor.Execute(
        [
            "Open the desk drawer",
            "Take the brass key",
            "Unlock the wooden door",
            "Go north",
            "Look around", // never reached
        ]);

        Assert.Equal(4, steps.Count);
        var go = steps[^1];
        Assert.False(go.Result!.Success);
        Assert.Contains("closed", go.Result.Message);
        Assert.Equal("room_a", engine.World.GetObject("player").Parent);
    }

    [Fact]
    public void UnknownLine_StopsWithDontKnowHow()
    {
        var engine = NewEngine();
        var player = engine.World.GetObject("player");
        var executor = new PlanExecutor(engine, player);

        var steps = executor.Execute(["Open the desk drawer", "Dance a jig", "Take the brass key"]);

        Assert.Equal(2, steps.Count);
        Assert.True(steps[0].Result!.Success);
        Assert.Null(steps[1].Result);
        Assert.Contains("don't know how", steps[1].Note);
        // the key stayed put: the third line never ran
        Assert.Equal("desk", engine.World.GetObject("key").Parent);
    }

    [Fact]
    public void ConditionallyAvailableLine_MatchesOnlyAfterPrerequisite()
    {
        var engine = NewEngine();
        var player = engine.World.GetObject("player");
        var executor = new PlanExecutor(engine, player);

        // "Take the brass key" cannot match while the drawer is closed
        var steps = executor.Execute(["Take the brass key"]);
        Assert.Single(steps);
        Assert.Null(steps[0].Result);
        Assert.Contains("don't know how", steps[0].Note);

        // after opening the drawer the same line matches
        steps = executor.Execute(["Open the desk drawer", "Take the brass key"]);
        Assert.All(steps, s => Assert.True(s.Result!.Success));
        Assert.Equal("player", engine.World.GetObject("key").Parent);
    }

    [Fact]
    public async Task Planner_InventoryLabelSurvives_FromLlmReplyToExecution()
    {
        // regression: the LLM copies the "Check inventory" label verbatim,
        // whose first word is not a verb — it must survive parsing
        var engine = NewEngine();
        var player = engine.World.GetObject("player");
        var fake = new FakeLlmClient().Enqueue("Check inventory");
        var planner = new LlmPlanner(fake, engine);

        var plan = await planner.CreatePlanAsync(player, "inventory", npc: false);
        Assert.Equal(["Check inventory"], plan);

        var steps = new PlanExecutor(engine, player).Execute(plan);
        Assert.Single(steps);
        Assert.Equal(ActionOutcome.Success, steps[0].Result!.Outcome);
        Assert.Contains("carrying nothing", steps[0].Result!.Message);
    }

    [Fact]
    public void RedundantUnlock_IsNoopAndPlanContinues()
    {
        var engine = NewEngine();
        var player = engine.World.GetObject("player");
        var executor = new PlanExecutor(engine, player);

        // unlock the door first, so the plan's "unlock" step is redundant
        executor.Execute(["Open the desk drawer", "Take the brass key", "Unlock the wooden door"]);
        var turnBefore = engine.TurnManager.Turn;

        var steps = executor.Execute(
        [
            "Unlock the wooden door", // noop: already unlocked
            "Open the wooden door",
            "Go north",
        ]);

        Assert.Equal(3, steps.Count);
        Assert.Equal(ActionOutcome.Noop, steps[0].Result!.Outcome);
        Assert.Equal(ActionOutcome.Success, steps[1].Result!.Outcome);
        Assert.Equal(ActionOutcome.Success, steps[2].Result!.Outcome);
        Assert.Equal("room_b", engine.World.GetObject("player").Parent);
        // the noop consumed no turn; the two real actions did
        Assert.Equal(turnBefore + 2, engine.TurnManager.Turn);
    }

    [Fact]
    public void Noop_EmitsNoSignalsAndConsumesNoTurn()
    {
        var engine = NewEngine();
        var player = engine.World.GetObject("player");
        var executor = new PlanExecutor(engine, player);

        // opening an already-open drawer is a noop
        executor.Execute(["Open the desk drawer"]);
        var turnBefore = engine.TurnManager.Turn;

        var steps = executor.Execute(["Open the desk drawer"]);

        Assert.Single(steps);
        Assert.Equal(ActionOutcome.Noop, steps[0].Result!.Outcome);
        Assert.False(steps[0].Result!.Success);
        Assert.Equal(turnBefore, engine.TurnManager.Turn);
        Assert.Empty(engine.SignalBus.Drain("player"));
    }
}
