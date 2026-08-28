using AEngine.Core.Runtime;
using AEngine.Core.Scenarios;

namespace AEngine.Tests;

/// <summary>
/// Action availability: listings are filtered by observable state
/// (open/close follow the visible open state; take/drop by held), while
/// unlock/lock are always listed since lock state is not observable. The
/// state-unfiltered potential set (ResolvePotential) keeps redundant
/// open/close resolvable for plan matching. Look exits never reveal lock
/// state.
/// </summary>
public class ActionResolverLiberalizationTests
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
    public void LockedClosedDoor_OffersOpenUnlockAndLock_NotClose()
    {
        var engine = NewEngine(); // the study door starts closed AND locked
        var actions = engine.ActionResolver.Resolve(engine.World.GetObject("player"));

        Assert.Contains(actions, a => a.Verb == "open" && a.TargetId == "door_a_side");
        Assert.Contains(actions, a => a.Verb == "unlock" && a.TargetId == "door_a_side");
        Assert.Contains(actions, a => a.Verb == "lock" && a.TargetId == "door_a_side");
        // close is observable-state-filtered out of the listing…
        Assert.DoesNotContain(actions, a => a.Verb == "close" && a.TargetId == "door_a_side");
        // …but remains in the potential set for plan matching
        var potential = engine.ActionResolver.ResolvePotential(engine.World.GetObject("player"));
        Assert.Contains(potential, a => a.Verb == "close" && a.TargetId == "door_a_side");
    }

    [Fact]
    public void UnlockedOpenDoor_OffersCloseUnlockAndLock_NotOpen()
    {
        var engine = NewEngine();
        var player = engine.World.GetObject("player");
        Assert.True(engine.TurnManager.Execute(player, "open", "desk").Success);
        Assert.True(engine.TurnManager.Execute(player, "take", "key").Success);
        Assert.True(engine.TurnManager.Execute(player, "unlock", "door_a_side").Success);
        Assert.True(engine.TurnManager.Execute(player, "open", "door_a_side").Success);

        var actions = engine.ActionResolver.Resolve(player);
        Assert.DoesNotContain(actions, a => a.Verb == "open" && a.TargetId == "door_a_side");
        Assert.Contains(actions, a => a.Verb == "close" && a.TargetId == "door_a_side");
        Assert.Contains(actions, a => a.Verb == "unlock" && a.TargetId == "door_a_side");
        Assert.Contains(actions, a => a.Verb == "lock" && a.TargetId == "door_a_side");
        // potential set still includes open
        var potential = engine.ActionResolver.ResolvePotential(player);
        Assert.Contains(potential, a => a.Verb == "open" && a.TargetId == "door_a_side");
    }

    [Fact]
    public void Open_WhileLocked_FailsAtRuntime()
    {
        var engine = NewEngine();
        var result = engine.TurnManager.Execute(
            engine.World.GetObject("player"), "open", "door_a_side");
        Assert.False(result.Success);
        Assert.Equal(Core.Actions.ActionOutcome.Failure, result.Outcome);
        Assert.Contains("locked", result.Message);
    }

    [Fact]
    public void Open_WhenAlreadyOpen_IsNoop()
    {
        var engine = NewEngine();
        var player = engine.World.GetObject("player");
        Assert.True(engine.TurnManager.Execute(player, "open", "desk").Success);

        var result = engine.TurnManager.Execute(player, "open", "desk");
        Assert.Equal(Core.Actions.ActionOutcome.Noop, result.Outcome);
        Assert.Contains("already open", result.Message);
    }

    [Fact]
    public void Look_Exits_ShowClosed_NeverLocked()
    {
        var engine = NewEngine(); // the study door starts locked
        var look = engine.TurnManager.Execute(
            engine.World.GetObject("player"), "look", "player");
        Assert.Contains("wooden door, closed", look.Message);
        Assert.DoesNotContain("locked", look.Message);
    }
}
