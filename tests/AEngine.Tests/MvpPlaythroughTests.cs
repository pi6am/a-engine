using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.Scenarios;
using AEngine.Core.World;

namespace AEngine.Tests;

/// <summary>
/// Scripted playthrough of the MVP scenario through the real scenario
/// files, the ActionResolver menu, and the TurnManager.
/// </summary>
public class MvpPlaythroughTests
{
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

    private static AvailableAction FindAction(GameEngine engine, string verb, string? targetId = null)
    {
        var player = engine.World.GetObject("player");
        var actions = engine.ActionResolver.Resolve(player);
        return actions.FirstOrDefault(a =>
                a.Verb == verb && (targetId is null || a.TargetId == targetId))
            ?? throw new InvalidOperationException(
                $"No '{verb}' action for '{targetId}'. Menu: " +
                string.Join(", ", actions.Select(a => $"{a.Verb}:{a.TargetId}")));
    }

    private static ActionResult Do(GameEngine engine, string verb, string? targetId = null)
    {
        var action = FindAction(engine, verb, targetId);
        return engine.TurnManager.PerformAction(engine.World.GetObject("player"), action);
    }

    [Fact]
    public void FullPlaythrough_EndsInRoomB()
    {
        var engine = NewEngine();
        var world = engine.World;

        Assert.True(Do(engine, "look").Success);

        // open the desk drawer, take the key
        Assert.True(Do(engine, "open", "desk").Success);
        Assert.True(Do(engine, "take", "key").Success);
        Assert.Equal("player", world.GetObject("key").Parent);

        // unlock and open the door, go north
        Assert.True(Do(engine, "unlock", "door_a_side").Success);
        Assert.True(Do(engine, "open", "door_a_side").Success);
        var go = Do(engine, "go", "door_a_side");
        Assert.True(go.Success, go.Message);

        Assert.Equal("room_b", world.GetObject("player").Parent);
        // shared state: the other side of the door is open too
        Assert.True(engine.ModuleRegistry.ResolveBool(
            world.GetObject("door_1_state"), "doorstate", "open"));
    }

    [Fact]
    public void TakeKey_WhileDrawerClosed_Fails()
    {
        var engine = NewEngine();

        // key is inside the closed drawer: not even on the menu
        Assert.Throws<InvalidOperationException>(() => FindAction(engine, "take", "key"));

        // executing it directly must also fail
        var result = engine.TurnManager.Execute(engine.World.GetObject("player"), "take", "key");
        Assert.False(result.Success);
    }

    [Fact]
    public void GoThroughDoor_WhileLocked_Fails()
    {
        var engine = NewEngine();

        var result = engine.TurnManager.Execute(engine.World.GetObject("player"), "go", "door_a_side");
        Assert.False(result.Success);
        Assert.Contains("locked", result.Message);
        Assert.Equal("room_a", engine.World.GetObject("player").Parent);
    }

    [Fact]
    public void GoThroughDoor_ClosedAfterUnlock_Fails()
    {
        var engine = NewEngine();

        Assert.True(Do(engine, "open", "desk").Success);
        Assert.True(Do(engine, "take", "key").Success);
        Assert.True(Do(engine, "unlock", "door_a_side").Success);

        // door is unlocked but still closed
        var result = engine.TurnManager.Execute(engine.World.GetObject("player"), "go", "door_a_side");
        Assert.False(result.Success);
        Assert.Contains("closed", result.Message);
        Assert.Equal("room_a", engine.World.GetObject("player").Parent);
    }

    [Fact]
    public void Unlock_WithoutKey_Fails()
    {
        var engine = NewEngine();

        var result = engine.TurnManager.Execute(engine.World.GetObject("player"), "unlock", "door_a_side");
        Assert.False(result.Success);
        Assert.Contains("key", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GoThroughDoor_OpenButLocked_Succeeds()
    {
        var engine = NewEngine();
        var world = engine.World;

        Assert.True(Do(engine, "open", "desk").Success);
        Assert.True(Do(engine, "take", "key").Success);
        Assert.True(Do(engine, "unlock", "door_a_side").Success);
        Assert.True(Do(engine, "open", "door_a_side").Success);
        Assert.True(Do(engine, "lock", "door_a_side").Success);

        // an open door is passable even when locked
        var look = engine.TurnManager.Execute(world.GetObject("player"), "look", "player");
        Assert.Contains("wooden door, open", look.Message);

        var go = Do(engine, "go", "door_a_side");
        Assert.True(go.Success, go.Message);
        Assert.Equal("room_b", world.GetObject("player").Parent);
    }
}
