using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.Scenarios;
using AEngine.Core.Signals;

namespace AEngine.Tests;

/// <summary>
/// Integration: load scenarios/npc, play scripted player turns with
/// RunNpcTurns after each, and check that the cook acts and the player
/// perceives the cook's actions with sense-appropriate signals.
/// </summary>
public class NpcScenarioTests
{
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

    private static GameEngine NewEngine(int seed)
    {
        var engine = GameEngine.CreateWithBuiltinHandlers();
        engine.Random = new Random(seed);
        var dir = FindScenarioDir();
        ScenarioLoader.LoadInto(
            engine,
            Path.Combine(dir, "modules.json"),
            Path.Combine(dir, "world.json"));
        return engine;
    }

    private bool DoorOpen(GameEngine engine) =>
        engine.ModuleRegistry.ResolveBool(
            engine.World.GetObject("door_state"), "doorstate", "open");

    private List<Signal> PlayRound(GameEngine engine)
    {
        var player = engine.World.GetObject("player");
        var look = engine.ActionResolver.Resolve(player)
            .First(a => a.Verb == "look");
        Assert.True(engine.TurnManager.PerformAction(player, look).Success);
        engine.TurnManager.RunNpcTurns();
        return [.. engine.SignalBus.Drain("player")];
    }

    [Fact]
    public void ScenarioLoads_CookHasRandomPolicy()
    {
        var engine = NewEngine(1);
        var cook = engine.World.GetObject("cook");
        Assert.Equal("dining_hall", cook.Parent);
        Assert.Equal("random",
            engine.ModuleRegistry.ResolveString(cook, "agent", "policy"));
        Assert.Equal("player",
            engine.ModuleRegistry.ResolveString(
                engine.World.GetObject("player"), "agent", "policy"));
        // the cook's menu includes say (with a prompt) and no CLI verbs
        var cookActions = engine.ActionResolver.Resolve(cook);
        Assert.Contains(cookActions, a => a.Verb == "say" && a.Prompt == "Say what?");
        Assert.DoesNotContain(cookActions, a => a.Verb == "quit");
    }

    [Fact]
    public void CookActs_ThroughClosedDoor_PlayerGetsAudioOnly()
    {
        var engine = NewEngine(42);
        var signals = new List<Signal>();
        var playerActions = 0;

        for (var round = 0; round < 60 && signals.Count < 3; round++)
        {
            playerActions++;
            foreach (var signal in PlayRound(engine))
            {
                signals.Add(signal);
                var playerInKitchen =
                    engine.World.GetObject("player").Parent == "kitchen";
                var cookInHall =
                    engine.World.GetObject("cook").Parent == "dining_hall";
                if (!DoorOpen(engine) && playerInKitchen && cookInHall)
                {
                    // closed wooden door between them: audio only — except
                    // actions on the door itself, which manifest on both sides
                    var doorTarget = signal.TargetId is "door_kitchen_side" or "door_hall_side";
                    if (!doorTarget)
                        Assert.Equal(SignalSense.Audible, signal.Sense);
                }
            }
        }

        Assert.NotEmpty(signals); // the cook acted perceptibly
        // NPC actions advanced the turn beyond the player's own actions
        Assert.True(engine.TurnManager.Turn > playerActions);
    }

    [Fact]
    public void CookActs_ThroughOpenDoor_PlayerEventuallySees()
    {
        var engine = NewEngine(42);
        var player = engine.World.GetObject("player");

        // player opens the kitchen door, then keeps looking around
        var openDoor = engine.ActionResolver.Resolve(player)
            .First(a => a.Verb == "open" && a.TargetId == "door_kitchen_side");
        Assert.True(engine.TurnManager.PerformAction(player, openDoor).Success);
        engine.TurnManager.RunNpcTurns();
        engine.SignalBus.Drain("player");

        var sawVisual = false;
        for (var round = 0; round < 80 && !sawVisual; round++)
        {
            sawVisual = PlayRound(engine).Any(s => s.Sense == SignalSense.Visual);
        }
        Assert.True(sawVisual,
            "expected at least one visual signal from the cook within 80 rounds");
    }
}
