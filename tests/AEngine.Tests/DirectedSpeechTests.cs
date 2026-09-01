using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.World;
using AEngine.Llm;
using CoreWorld = AEngine.Core.World.World;

namespace AEngine.Tests;

/// <summary>
/// Directed speech, end to end: the actor is told whom they addressed,
/// the addressee receives an audience-restricted "says to you" signal
/// (and remembers it as such), bystanders keep the ambient rendering.
/// </summary>
public class DirectedSpeechTests
{
    private static GameEngine NewCrowdedEngine()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.World.MoveObject("bob", "room_a");
        engine.World.CreateObject("carol", "room_a", "Carol");
        engine.World.AddModule("carol", "agent");
        engine.World.AddModule("carol", "can_speak");
        return engine;
    }

    private static ActionResult Say(GameEngine engine, string line)
    {
        var alice = engine.World.GetObject("alice");
        var action = PlanExecutor.MatchAvailableOrPotential(engine, alice, line);
        Assert.NotNull(action);
        return engine.TurnManager.PerformAction(alice, action!, action.Text);
    }

    [Fact]
    public void DirectedSay_AddresseeGetsSaysToYou_BystanderGetsAmbient()
    {
        var engine = NewCrowdedEngine();

        var result = Say(engine, "Say to Bob: \"Hey, what's up?\"");

        // the actor sees whom they addressed
        Assert.Equal("You say to Bob: \"Hey, what's up?\"", result.Message);
        // the addressee receives the audience-restricted directed form...
        var toBob = Assert.Single(engine.SignalBus.Drain("bob"));
        Assert.Equal("Alice says to you: \"Hey, what's up?\"", toBob.Text);
        // ...and remembers it as directed at them
        Assert.Contains(engine.Memory.Recall("bob"),
            m => m == "Alice says to you: \"Hey, what's up?\"");
        // the bystander keeps the ambient rendering — full fidelity, no change
        var toCarol = Assert.Single(engine.SignalBus.Drain("carol"));
        Assert.Equal("Alice says: \"Hey, what's up?\"", toCarol.Text);
    }

    [Fact]
    public void UndirectedSay_EveryoneGetsAmbient()
    {
        var engine = NewCrowdedEngine();

        // undirected action: the onlyTarget spec's target is the actor
        // themself, so nobody qualifies for it
        var result = Say(engine, "Say: last call, everyone");
        Assert.Equal("You say: \"last call, everyone\"", result.Message);

        Assert.Equal("Alice says: \"last call, everyone\"",
            Assert.Single(engine.SignalBus.Drain("bob")).Text);
        Assert.Equal("Alice says: \"last call, everyone\"",
            Assert.Single(engine.SignalBus.Drain("carol")).Text);
    }

    [Fact]
    public void DirectedSay_ThroughADoor_AddresseeStillTargeted()
    {
        var engine = NewCrowdedEngine();
        var alice = engine.World.GetObject("alice");

        // resolve the directed entry while carol is present, then she steps
        // into room_b before the words land — the directed spec still finds
        // her (audible passes the closed door), with the portal suffix
        var action = PlanExecutor.MatchAvailableOrPotential(
            engine, alice, "Say to Carol: \"quiet, you\"");
        Assert.Equal("carol", action!.TargetId);
        engine.World.MoveObject("carol", "room_b");

        engine.TurnManager.PerformAction(alice, action, action.Text);

        Assert.Equal("Alice says to you: \"quiet, you\" through the wooden door to the south.",
            Assert.Single(engine.SignalBus.Drain("carol")).Text);
        // bob, same room as the actor, keeps the ambient form
        Assert.Equal("Alice says: \"quiet, you\"",
            Assert.Single(engine.SignalBus.Drain("bob")).Text);
    }

    [Fact]
    public void AudienceSpecs_FilterPerObserver_OnAnyAction()
    {
        // audience filtering is general, not say-specific: an affordance
        // may reserve a spec for its target and another for everyone else
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.ModuleRegistry.LoadJson("""
        [
          { "id": "toastable", "name": "Toastable", "fields": [],
            "affordances": [
              { "verb": "toast", "handler": "basic",
                "signals": [
                  { "sense": "audible", "priority": 5, "audience": "onlyTarget",
                    "text": "{agent} whispers the secret word to you" },
                  { "sense": "audible", "priority": 5, "audience": "exceptTarget",
                    "text": "{agent} makes a toast" }
                ] }
            ] }
        ]
        """);
        var world = engine.World;
        world.MoveObject("bob", "room_a");
        world.CreateObject("carol", "room_a", "Carol");
        world.AddModule("carol", "agent");
        world.AddModule("bob", "toastable");

        var result = engine.TurnManager.PerformAction(
            world.GetObject("alice"), TestWorlds.Find(engine, "alice", "toast", "bob"));

        Assert.Equal(ActionOutcome.Success, result.Outcome);
        Assert.Equal("Alice whispers the secret word to you",
            Assert.Single(engine.SignalBus.Drain("bob")).Text);
        Assert.Equal("Alice makes a toast",
            Assert.Single(engine.SignalBus.Drain("carol")).Text);
    }

    [Fact]
    public void OnlyTargetSpec_OnNonAgentTarget_ReachesNobody()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.ModuleRegistry.LoadJson("""
        [
          { "id": "enchanted", "name": "Enchanted", "fields": [],
            "affordances": [
              { "verb": "touch", "handler": "basic",
                "signals": [
                  { "sense": "audible", "priority": 5, "audience": "onlyTarget",
                    "text": "only the chest may hear this" }
                ] }
            ] }
        ]
        """);
        var world = engine.World;
        world.AddModule("chest", "enchanted");
        world.MoveObject("bob", "room_a");

        engine.TurnManager.PerformAction(
            world.GetObject("alice"), TestWorlds.Find(engine, "alice", "touch", "chest"));

        Assert.Empty(engine.SignalBus.Drain("bob"));
    }

    [Fact]
    public void Tavern_DirectedOrder_ReachesNixAsDirectedMemory()
    {
        var engine = LoadTavern();
        var player = engine.World.GetObject("player");
        engine.World.MoveObject("player", "tavern");

        var action = PlanExecutor.MatchAvailableOrPotential(
            engine, player, "Say to Nix the goblin: \"Hey Nix, what's up?\"");
        Assert.NotNull(action);
        Assert.Equal("nix", action!.TargetId);

        var result = engine.TurnManager.PerformAction(player, action, action.Text);
        Assert.Equal("You say to Nix the goblin: \"Hey Nix, what's up?\"", result.Message);
        Assert.Contains(engine.Memory.Recall("nix"),
            m => m == "the human stranger says to you: \"Hey Nix, what's up?\"");
    }

    private static GameEngine LoadTavern()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scenarios", "tavern");
            if (File.Exists(Path.Combine(candidate, "world.json")))
            {
                var engine = GameEngine.CreateWithBuiltinHandlers();
                Core.Scenarios.ScenarioLoader.LoadInto(
                    engine,
                    Path.Combine(candidate, "modules.json"),
                    Path.Combine(candidate, "world.json"));
                return engine;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate scenarios/tavern.");
    }
}
