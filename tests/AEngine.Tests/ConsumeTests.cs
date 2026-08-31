using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.World;
using CoreWorld = AEngine.Core.World.World;

namespace AEngine.Tests;

public class ConsumeTests
{
    private const string ModulesJson = """
    [
      {
        "id": "metabolism", "name": "Metabolism",
        "fields": [
          { "name": "alcohol", "type": "number", "default": 0.0 },
          { "name": "bladder", "type": "number", "default": 0.0 },
          { "name": "capacity", "type": "number", "default": 1.0 }
        ],
        "affordances": []
      },
      {
        "id": "beverage", "name": "Beverage",
        "fields": [
          { "name": "alcohol", "type": "number", "default": 0.0 },
          { "name": "volume", "type": "number", "default": 0.0 },
          { "name": "empty", "type": "bool", "default": false },
          { "name": "destroyOnConsume", "type": "bool", "default": false },
          { "name": "taste", "type": "string", "default": "" }
        ],
        "affordances": [
          {
            "verb": "drink", "handler": "consume", "duration": 5,
            "when": [ { "module": "beverage", "field": "empty", "equals": false } ],
            "signals": [ { "sense": "visual", "priority": 5, "text": "{agent} drinks the {target}." } ]
          }
        ]
      },
      {
        "id": "food", "name": "Food",
        "fields": [
          { "name": "sobering", "type": "number", "default": 0.0 },
          { "name": "empty", "type": "bool", "default": false },
          { "name": "destroyOnConsume", "type": "bool", "default": false },
          { "name": "taste", "type": "string", "default": "" }
        ],
        "affordances": [
          {
            "verb": "eat", "handler": "consume", "duration": 15,
            "when": [ { "module": "food", "field": "empty", "equals": false } ],
            "signals": [ { "sense": "visual", "priority": 5, "text": "{agent} eats the {target}." } ]
          }
        ]
      }
    ]
    """;

    private static GameEngine NewEngine()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.ModuleRegistry.LoadJson(ModulesJson);
        var world = engine.World;
        world.AddModule("alice", "metabolism");

        world.CreateObject("ale", "room_a", "mug of ale");
        world.AddModule("ale", "beverage");
        world.SetFieldOverride("ale", "beverage", "alcohol", CoreWorld.ToJson(0.3));
        world.SetFieldOverride("ale", "beverage", "volume", CoreWorld.ToJson(0.25));
        world.SetFieldOverride("ale", "beverage", "taste", CoreWorld.ToJson("The ale is bitter and cold."));

        world.CreateObject("pill", "room_a", "sobering pill");
        world.AddModule("pill", "food");
        world.SetFieldOverride("pill", "food", "sobering", CoreWorld.ToJson(99));
        world.SetFieldOverride("pill", "food", "destroyOnConsume", CoreWorld.ToJson(true));
        return engine;
    }

    private static double Alcohol(GameEngine engine, string agentId) =>
        engine.ModuleRegistry.ResolveDouble(engine.World.GetObject(agentId), "metabolism", "alcohol");

    private static double Bladder(GameEngine engine, string agentId) =>
        engine.ModuleRegistry.ResolveDouble(engine.World.GetObject(agentId), "metabolism", "bladder");

    [Fact]
    public void Drink_AppliesAlcoholAndVolume_AndEmptiesTheVessel()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");

        var result = engine.TurnManager.PerformAction(
            alice, TestWorlds.Find(engine, "alice", "drink", "ale"));

        Assert.Equal(ActionOutcome.Success, result.Outcome);
        Assert.Equal("You drink the mug of ale.", result.Message);
        // PerformAction then runs metabolism upkeep for the action's 5s
        // duration (default decay 0.002/s): 0.3 − 0.01 burned, and the
        // burned 0.01 flows into the bladder
        Assert.Equal(0.29, Alcohol(engine, "alice"), 6);
        Assert.Equal(0.26, Bladder(engine, "alice"), 6);
        // the mug stays behind, empty
        Assert.True(engine.ModuleRegistry.ResolveBool(
            engine.World.GetObject("ale"), "beverage", "empty"));
    }

    [Fact]
    public void DrinkingTwice_SecondIsHidden_ByWhenGate()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "drink", "ale"));

        // the when gate hides the empty vessel's drink affordance...
        Assert.DoesNotContain(engine.ActionResolver.Resolve(alice),
            a => a.Verb == "drink" && a.TargetId == "ale");
        // ...but a direct handler call noops rather than failing
        var again = engine.TurnManager.Execute(alice, "consume", "ale", verb: "drink");
        Assert.Equal(ActionOutcome.Noop, again.Outcome);
        Assert.Equal("The mug of ale is empty.", again.Message);
    }

    [Fact]
    public void Bladder_ClampsAtOne()
    {
        var engine = NewEngine();
        var world = engine.World;
        world.AddModule("bob", "metabolism");
        world.SetFieldOverride("bob", "metabolism", "bladder", CoreWorld.ToJson(0.9));
        world.CreateObject("water", "room_b", "jug of water");
        world.AddModule("water", "beverage");
        world.SetFieldOverride("water", "beverage", "volume", CoreWorld.ToJson(0.5));

        var bob = world.GetObject("bob");
        engine.TurnManager.Execute(bob, "consume", "water", verb: "drink");
        Assert.Equal(1.0, Bladder(engine, "bob"));
    }

    [Fact]
    public void Eat_Sobers_AndMayDestroyInsteadOfEmptying()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        world_alcohol(engine, 0.4);

        var result = engine.TurnManager.PerformAction(
            alice, TestWorlds.Find(engine, "alice", "eat", "pill"));

        Assert.Equal(ActionOutcome.Success, result.Outcome);
        // sobering floors at zero even with sobering=99
        Assert.Equal(0.0, Alcohol(engine, "alice"));
        // destroyOnConsume: no empty plate left behind
        Assert.False(engine.World.HasObject("pill"));
    }

    [Fact]
    public void Taste_IsAPrivateSensation()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        engine.TurnManager.Execute(alice, "consume", "ale", verb: "drink");

        var signals = engine.SignalBus.Drain("alice");
        Assert.Contains(signals, s => s.Text == "The ale is bitter and cold.");
    }

    [Fact]
    public void AgentWithoutMetabolism_CanStillDrink()
    {
        var engine = NewEngine();
        var bob = engine.World.GetObject("bob"); // no metabolism module

        var result = engine.TurnManager.Execute(bob, "consume", "ale", verb: "drink");
        Assert.Equal(ActionOutcome.Success, result.Outcome);
        Assert.Equal("You drink the mug of ale.", result.Message);
    }

    [Fact]
    public void NonConsumableTarget_Fails()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        var result = engine.TurnManager.Execute(alice, "consume", "apple", verb: "eat");
        Assert.Equal(ActionOutcome.Failure, result.Outcome);
    }

    private static void world_alcohol(GameEngine engine, double value) =>
        engine.World.SetFieldOverride("alice", "metabolism", "alcohol",
            CoreWorld.ToJson(value));
}
