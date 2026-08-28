using AEngine.Core.Runtime;
using AEngine.Core.Scenarios;
using AEngine.Core.World;
using AEngine.Llm;

namespace AEngine.Tests;

/// <summary>
/// AgentContextBuilder shows only public information: closed-container
/// contents are hidden, exits never reveal lock state, and NPC contexts
/// carry character/goals plus the agent's memory of recent events.
/// </summary>
public class AgentContextBuilderTests
{
    private static string FindScenarioDir(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scenarios", name);
            if (File.Exists(Path.Combine(candidate, "world.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException($"Could not locate scenarios/{name}.");
    }

    private static GameEngine NewEngine(string scenario)
    {
        var engine = GameEngine.CreateWithBuiltinHandlers();
        var dir = FindScenarioDir(scenario);
        ScenarioLoader.LoadInto(
            engine,
            Path.Combine(dir, "modules.json"),
            Path.Combine(dir, "world.json"));
        return engine;
    }

    [Fact]
    public void ClosedDrawer_HidesKey_OpenDrawer_RevealsIt()
    {
        var engine = NewEngine("mvp");
        var player = engine.World.GetObject("player");
        var builder = new AgentContextBuilder(engine);

        var closed = builder.BuildContext(player, npc: false);
        Assert.DoesNotContain("brass key", closed);
        Assert.Contains("desk drawer", closed); // the container itself is visible

        var open = engine.ActionResolver.Resolve(player)
            .First(a => a.Verb == "open" && a.TargetId == "desk");
        Assert.True(engine.TurnManager.PerformAction(player, open).Success);

        var revealed = builder.BuildContext(player, npc: false);
        Assert.Contains("brass key", revealed);
    }

    [Fact]
    public void Exits_ShowOpenClosed_NeverLocked()
    {
        var engine = NewEngine("mvp"); // the study door starts locked
        var player = engine.World.GetObject("player");
        var builder = new AgentContextBuilder(engine);

        var context = builder.BuildContext(player, npc: false);
        Assert.Contains("wooden door, closed", context);
        Assert.DoesNotContain("locked", context);
    }

    [Fact]
    public void Context_ListsActionMenuLabels()
    {
        var engine = NewEngine("mvp");
        var player = engine.World.GetObject("player");
        var context = new AgentContextBuilder(engine).BuildContext(player, npc: false);

        Assert.Contains("Open the desk drawer", context);
        Assert.Contains("Unlock the wooden door", context);
        Assert.Contains("Go north", context);
    }

    [Fact]
    public void NpcContext_IncludesCharacterGoals_AndDrainsSignals()
    {
        var engine = NewEngine("npc");
        var cook = engine.World.GetObject("cook");
        var player = engine.World.GetObject("player");
        var builder = new AgentContextBuilder(engine);

        var context = builder.BuildContext(cook, npc: true);
        Assert.Contains("grizzled old cook", context);
        Assert.Contains("Goals:", context);

        // player talks; the audio crosses the closed door to the cook
        var say = engine.ActionResolver.Resolve(player).First(a => a.Verb == "say");
        Assert.True(engine.TurnManager.PerformAction(player, say, "Hello, cook!").Success);

        var withSignal = builder.BuildContext(cook, npc: true);
        Assert.Contains("Recent observations and actions", withSignal);
        Assert.Contains("Hello, cook!", withSignal);

        // the pending queue is drained, but the memory persists — later
        // contexts keep the observation for continuity
        Assert.Empty(engine.SignalBus.Peek(cook.Id));
        var recalled = builder.BuildContext(cook, npc: true);
        Assert.Contains("Hello, cook!", recalled);
    }

    [Fact]
    public void PlayerContext_OmitsNpcExtras()
    {
        var engine = NewEngine("npc");
        var player = engine.World.GetObject("player");
        var context = new AgentContextBuilder(engine).BuildContext(player, npc: false);
        Assert.DoesNotContain("Goals:", context);
    }
}
