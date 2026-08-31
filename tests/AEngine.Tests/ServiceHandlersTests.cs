using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.World;
using CoreWorld = AEngine.Core.World.World;

namespace AEngine.Tests;

/// <summary>
/// clear (bus empty vessels), relieve (use the toilet), leave (end the
/// game via an exit object).
/// </summary>
public class ServiceHandlersTests
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
          { "name": "empty", "type": "bool", "default": false }
        ],
        "affordances": [
          { "verb": "drink", "handler": "consume",
            "when": [ { "module": "beverage", "field": "empty", "equals": false } ] },
          { "verb": "clear", "handler": "clear",
            "when": [ { "module": "beverage", "field": "empty", "equals": true } ],
            "signals": [ { "sense": "visual", "priority": 5, "text": "{agent} buses the {target}." } ] }
        ]
      },
      {
        "id": "condition", "name": "Condition",
        "fields": [ { "name": "kind", "type": "string", "default": "" } ],
        "affordances": []
      },
      {
        "id": "toilet", "name": "Toilet",
        "fields": [ { "name": "reliefText", "type": "string", "default": "" } ],
        "affordances": [
          { "verb": "use", "handler": "relieve", "requires": "needs_to_pee", "duration": 20 }
        ]
      },
      {
        "id": "exit", "name": "Exit",
        "fields": [ { "name": "text", "type": "string", "default": "" } ],
        "affordances": [
          { "verb": "leave", "handler": "leave", "label": "Go home" }
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
        world.SetFieldOverride("alice", "metabolism", "bladder", CoreWorld.ToJson(0.8));

        world.CreateObject("cond_pee", CoreWorld.RootId, "pee template");
        world.AddModule("cond_pee", "condition");
        world.SetFieldOverride("cond_pee", "condition", "kind", CoreWorld.ToJson("needs_to_pee"));

        world.CreateObject("mug", "room_a", "empty mug");
        world.AddModule("mug", "beverage");
        world.SetFieldOverride("mug", "beverage", "empty", CoreWorld.ToJson(true));

        world.CreateObject("full_mug", "room_a", "full mug");
        world.AddModule("full_mug", "beverage");

        world.CreateObject("urinal", "room_a", "urinal");
        world.AddModule("urinal", "toilet");

        world.CreateObject("bus_stop", "room_a", "bus stop");
        world.AddModule("bus_stop", "exit");
        world.SetFieldOverride("bus_stop", "exit", "text",
            CoreWorld.ToJson("You hail a night bus and ride home. The tavern's noise fades behind you."));
        return engine;
    }

    [Fact]
    public void Clear_DestroysEmptyVessels_EmitsSignal()
    {
        var engine = NewEngine();
        var world = engine.World;
        var alice = world.GetObject("alice");
        // a same-room observer: the handler's visual signal crosses no doors
        world.CreateObject("carol", "room_a", "Carol");
        world.AddModule("carol", "agent");

        var result = engine.TurnManager.PerformAction(
            alice, TestWorlds.Find(engine, "alice", "clear", "mug"));

        Assert.Equal(ActionOutcome.Success, result.Outcome);
        Assert.Equal("You clear away the empty mug.", result.Message);
        Assert.False(engine.World.HasObject("mug"));
        Assert.Contains(engine.SignalBus.Drain("carol"), s => s.Text.Contains("clears away"));
    }

    [Fact]
    public void Clear_RefusesUnfinishedVessels()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");

        var result = engine.TurnManager.Execute(alice, "clear", "full_mug");
        Assert.Equal(ActionOutcome.Noop, result.Outcome);
        Assert.True(engine.World.HasObject("full_mug"));
    }

    [Fact]
    public void Clear_IsHiddenForFullVessels_ByWhenGate()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        var verbs = engine.ActionResolver.Resolve(alice)
            .Where(a => a.TargetId == "full_mug").Select(a => a.Verb).ToList();
        Assert.DoesNotContain("clear", verbs);
        Assert.Contains("drink", verbs);
    }

    [Fact]
    public void Relieve_ResetsBladder_WithSensation()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        Conditions.Attach(engine.World, engine.ModuleRegistry, alice, "cond_pee");

        var result = engine.TurnManager.PerformAction(
            alice, TestWorlds.Find(engine, "alice", "use", "urinal"));

        Assert.Equal(ActionOutcome.Success, result.Outcome);
        Assert.Equal("You use the urinal.", result.Message);
        Assert.Equal(0.0, engine.ModuleRegistry.ResolveDouble(alice, "metabolism", "bladder"));
        Assert.Contains(engine.SignalBus.Drain("alice"), s => s.Text == "You feel enormously better.");
    }

    [Fact]
    public void Relieve_HiddenWithoutTheBladderCondition()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        // bladder is 0.8 but the condition isn't attached (no metabolism
        // bands in this module set) — requires hides the affordance
        Assert.DoesNotContain(engine.ActionResolver.Resolve(alice),
            a => a.Verb == "use" && a.TargetId == "urinal");
    }

    [Fact]
    public void Relieve_WithoutNeed_Noops()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        engine.World.SetFieldOverride("alice", "metabolism", "bladder", CoreWorld.ToJson(0.0));
        Conditions.Attach(engine.World, engine.ModuleRegistry, alice, "cond_pee");

        var result = engine.TurnManager.Execute(alice, "relieve", "urinal");
        Assert.Equal(ActionOutcome.Noop, result.Outcome);
        Assert.Equal("You don't need to go.", result.Message);
    }

    [Fact]
    public void Leave_EndsTheGame_WithDepartureText()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");

        var result = engine.TurnManager.PerformAction(
            alice, TestWorlds.Find(engine, "alice", "leave", "bus_stop"));

        Assert.Equal(ActionOutcome.Success, result.Outcome);
        Assert.Equal(
            "You hail a night bus and ride home. The tavern's noise fades behind you.",
            result.Message);
        Assert.Equal(result.Message, engine.GameOver);
    }
}
