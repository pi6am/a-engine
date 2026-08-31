using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.World;
using CoreWorld = AEngine.Core.World.World;

namespace AEngine.Tests;

/// <summary>
/// Execution-time gates (GateRegistry): unlike resolver gates these keep
/// the action LISTED but fail the attempt with a message — "drink while
/// bursting" is attemptable and refused loudly.
/// </summary>
public class GateTests
{
    private const string ModulesJson = """
    [
      {
        "id": "condition", "name": "Condition",
        "fields": [ { "name": "kind", "type": "string", "default": "" } ],
        "affordances": []
      },
      {
        "id": "beverage", "name": "Beverage",
        "fields": [ { "name": "empty", "type": "bool", "default": false } ],
        "affordances": [
          {
            "verb": "drink", "handler": "basic", "duration": 3,
            "gates": [
              {
                "kind": "condition",
                "args": { "excludes": ["bursting"] },
                "failText": "Your bladder is bursting — you can't stomach another drop."
              },
              {
                "kind": "condition",
                "args": { "requires": ["sober_enough"] },
                "failText": "You are too far gone to lift the glass."
              }
            ],
            "signals": [ { "sense": "visual", "priority": 5, "text": "{agent} drinks the {target}." } ],
            "failSignals": [ { "sense": "audible", "priority": 5, "text": "{agent} groans and sets the {target} down." } ]
          },
          {
            "verb": "sip", "handler": "basic",
            "gates": [
              {
                "kind": "field",
                "args": { "on": "target", "module": "beverage", "field": "empty", "equals": false },
                "failText": "The {target} is empty — the dregs won't help."
              }
            ]
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
        foreach (var (id, kind) in new[] { ("cond_bursting", "bursting"), ("cond_sober", "sober_enough") })
        {
            world.CreateObject(id, CoreWorld.RootId, $"{kind} template");
            world.AddModule(id, "condition");
            world.SetFieldOverride(id, "condition", "kind", CoreWorld.ToJson(kind));
        }
        world.CreateObject("ale", "room_a", "mug of ale");
        world.AddModule("ale", "beverage");
        return engine;
    }

    private static void Attach(GameEngine engine, string agentId, string templateId) =>
        Conditions.Attach(engine.World, engine.ModuleRegistry,
            engine.World.GetObject(agentId), templateId);

    [Fact]
    public void BlockedGate_FailsWithMessage_AndConsumesTheTurn()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        Attach(engine, "alice", "cond_bursting");

        // still listed — gates do not hide the action
        Assert.Contains(engine.ActionResolver.Resolve(alice),
            a => a.Verb == "drink" && a.TargetId == "ale");

        var action = TestWorlds.Find(engine, "alice", "drink", "ale");
        var result = engine.TurnManager.PerformAction(alice, action);

        Assert.Equal(ActionOutcome.Failure, result.Outcome);
        Assert.Equal("Your bladder is bursting — you can't stomach another drop.", result.Message);
        // a failed attempt still takes time
        Assert.Equal(1, engine.TurnManager.Turn);
    }

    [Fact]
    public void GatesEvaluateInOrder_FirstBlockerWins()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        // no bursting, but also not sober_enough → the second gate blocks
        var result = engine.TurnManager.PerformAction(
            alice, TestWorlds.Find(engine, "alice", "drink", "ale"));
        Assert.Equal("You are too far gone to lift the glass.", result.Message);
    }

    [Fact]
    public void PassingGates_RunTheHandler()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        Attach(engine, "alice", "cond_sober");

        var result = engine.TurnManager.PerformAction(
            alice, TestWorlds.Find(engine, "alice", "drink", "ale"));
        Assert.Equal(ActionOutcome.Success, result.Outcome);
    }

    [Fact]
    public void BlockedGate_EmitsFailSignals_AndRecordsMemory()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        Attach(engine, "alice", "cond_bursting");

        engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "drink", "ale"));

        // the affordance's failSignals reached bob through the always-open door
        var bobSignals = engine.SignalBus.Drain("bob");
        Assert.Contains(bobSignals, s => s.Text.Contains("groans"));

        // the actor remembers the refusal
        Assert.Contains(engine.Memory.Recall("alice"),
            m => m.Contains("bladder is bursting"));
    }

    [Fact]
    public void FieldGate_BlocksOnObservableTargetState()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        engine.World.SetFieldOverride("ale", "beverage", "empty", CoreWorld.ToJson(true));

        var result = engine.TurnManager.PerformAction(
            alice, TestWorlds.Find(engine, "alice", "sip", "ale"));
        Assert.Equal(ActionOutcome.Failure, result.Outcome);
        Assert.Equal("The {target} is empty — the dregs won't help.", result.Message);
    }

    [Fact]
    public void UnknownGateKind_Throws()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.ModuleRegistry.LoadJson("""
        [
          { "id": "mystery", "name": "Mystery",
            "fields": [],
            "affordances": [
              { "verb": "poke", "handler": "basic",
                "gates": [ { "kind": "nonexistent", "failText": "nope" } ] }
            ] }
        ]
        """);
        engine.World.CreateObject("rock", "room_a", "rock");
        engine.World.AddModule("rock", "mystery");
        var alice = engine.World.GetObject("alice");

        Assert.Throws<KeyNotFoundException>(() =>
            engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "poke", "rock")));
    }
}
