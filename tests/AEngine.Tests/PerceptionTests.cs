using AEngine.Core.Runtime;
using AEngine.Core.Scenarios;

namespace AEngine.Tests;

/// <summary>
/// Perception reporting: look annotates open/closed state and open
/// containers reveal their contents; the open action reports contents.
/// </summary>
public class PerceptionTests
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
    public void Look_ReportsClosedState_AndHidesContents()
    {
        var engine = NewEngine();
        var look = engine.TurnManager.Execute(engine.World.GetObject("player"), "look", "player");

        Assert.Contains("desk drawer (closed)", look.Message);
        Assert.DoesNotContain("key", look.Message);
    }

    [Fact]
    public void Open_ReportsContents()
    {
        var engine = NewEngine();
        var result = engine.TurnManager.Execute(engine.World.GetObject("player"), "open", "desk");
        Assert.True(result.Success);
        Assert.Equal("You open the desk drawer. There is a brass key inside.", result.Message);
    }

    [Fact]
    public void Look_AfterOpen_ReportsOpenStateAndContents()
    {
        var engine = NewEngine();
        engine.TurnManager.Execute(engine.World.GetObject("player"), "open", "desk");

        var look = engine.TurnManager.Execute(engine.World.GetObject("player"), "look", "player");
        Assert.Contains("desk drawer (open), brass key (in desk drawer)", look.Message);
    }

    [Fact]
    public void Open_EmptyContainer_SaysEmpty()
    {
        var engine = NewEngine();
        // move the key out first
        engine.World.MoveObject("key", "room_a");
        var result = engine.TurnManager.Execute(engine.World.GetObject("player"), "open", "desk");
        Assert.Equal("You open the desk drawer. It's empty.", result.Message);
    }

    [Fact]
    public void Names_AreObserverRelative()
    {
        var engine = NewEngine();
        var player = engine.World.GetObject("player");
        var desk = engine.World.GetObject("desk");

        // every agent is the protagonist of their own perception: self is
        // "you", others keep their descriptive name
        Assert.Equal("you", Core.Actions.Perception.NameFor(engine.ModuleRegistry, player, player));
        Assert.Equal("desk drawer", Core.Actions.Perception.NameFor(engine.ModuleRegistry, player, desk));
    }
}
