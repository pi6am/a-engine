using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.World;
using CoreWorld = AEngine.Core.World.World;

namespace AEngine.Tests;

/// <summary>
/// clear (bus empty vessels), the data-driven toilet `use` (a set-handler
/// write of the actor's bladder with a per-object relief sensation),
/// leave (end the game via an exit object).
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
          { "name": "capacity", "type": "number", "default": 1.0 },
          { "name": "motives", "type": "list", "default": [
            { "id": "alcohol", "max": null },
            { "id": "bladder" }
          ] }
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
            "when": [ { "module": "beverage", "field": "empty", "equals": false } ],
            "data": { "impulse.alcohol": "alcohol" } },
          { "verb": "clear", "handler": "destroy",
            "when": [ { "module": "beverage", "field": "empty", "equals": true } ],
            "data": { "self": "You clear away {target}." },
            "signals": [ { "sense": "visual", "priority": 5, "text": "{agent} clears away the {target}." } ] }
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
          { "verb": "use", "handler": "set", "requires": "needs_to_pee", "duration": 20,
            "data": { "on": "actor", "module": "metabolism", "field": "bladder", "value": "0",
                      "self": "You use {target}.",
                      "sensationField": "reliefText",
                      "sensationFallback": "You feel enormously better." } }
        ]
      },
      {
        "id": "exit", "name": "Exit",
        "fields": [
          { "name": "text", "type": "string", "default": "" },
          { "name": "departText", "type": "string", "default": "" }
        ],
        "affordances": [
          { "verb": "leave", "handler": "leave", "label": "Go home", "playerOnly": true },
          { "verb": "depart", "handler": "depart", "label": "Go home", "npcOnly": true }
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
        world.SetFieldOverride("bus_stop", "exit", "departText",
            CoreWorld.ToJson("{agent} steps onto the bus and leaves."));
        return engine;
    }

    [Fact]
    public void Destroy_RemovesTheTarget_ItsSignalNamesTheDeparted()
    {
        var engine = NewEngine();
        var world = engine.World;
        var alice = world.GetObject("alice");
        // a same-room observer: the affordance's visual signal crosses no
        // doors — and still names the mug, though it no longer exists
        world.CreateObject("carol", "room_a", "Carol");
        world.AddModule("carol", "agent");

        var result = engine.TurnManager.PerformAction(
            alice, TestWorlds.Find(engine, "alice", "clear", "mug"));

        Assert.Equal(ActionOutcome.Success, result.Outcome);
        Assert.Equal("You clear away the empty mug.", result.Message);
        Assert.False(engine.World.HasObject("mug"));
        Assert.Contains(engine.SignalBus.Drain("carol"),
            s => s.Text == "Alice clears away the empty mug.");
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

    [Fact]
    public void GoHome_SplitByAudience_PlayerSeesLeave_NpcSeesDepart()
    {
        var engine = NewEngine();
        var world = engine.World;
        world.CreateObject("carol", "room_a", "Carol");
        world.AddModule("carol", "agent");
        world.SetFieldOverride("carol", "agent", "policy", CoreWorld.ToJson("auto"));

        var aliceVerbs = engine.ActionResolver.Resolve(world.GetObject("alice"))
            .Where(a => a.TargetId == "bus_stop").Select(a => a.Verb).ToList();
        Assert.Contains("leave", aliceVerbs);
        Assert.DoesNotContain("depart", aliceVerbs);

        var carolVerbs = engine.ActionResolver.Resolve(world.GetObject("carol"))
            .Where(a => a.TargetId == "bus_stop").Select(a => a.Verb).ToList();
        Assert.Contains("depart", carolVerbs);
        Assert.DoesNotContain("leave", carolVerbs);
    }

    [Fact]
    public void NpcDepart_RemovesThemFromTheWorld_WithoutEndingTheGame()
    {
        var engine = NewEngine();
        var world = engine.World;
        world.CreateObject("carol", "room_a", "Carol");
        world.AddModule("carol", "agent");
        world.SetFieldOverride("carol", "agent", "policy", CoreWorld.ToJson("auto"));
        // a same-room observer sees her go
        var alice = world.GetObject("alice");

        var carol = world.GetObject("carol");
        var result = engine.TurnManager.PerformAction(
            carol, TestWorlds.Find(engine, "carol", "depart", "bus_stop"));

        Assert.Equal(ActionOutcome.Success, result.Outcome);
        Assert.Equal("You go home.", result.Message);
        Assert.False(world.HasObject("carol")); // gone from the scenario
        Assert.Null(engine.GameOver); // the player's game continues
        Assert.Contains(engine.SignalBus.Drain("alice"),
            s => s.Text == "Carol steps onto the bus and leaves.");
    }

    [Fact]
    public void NpcDepart_RefusedWhileCarryingAnotherAgent()
    {
        var engine = NewEngine();
        var world = engine.World;
        world.CreateObject("carol", "room_a", "Carol");
        world.AddModule("carol", "agent");
        world.SetFieldOverride("carol", "agent", "policy", CoreWorld.ToJson("auto"));
        var carol = world.GetObject("carol");
        world.MoveObject("alice", "carol"); // carol is carrying alice

        var result = engine.TurnManager.PerformAction(
            carol, TestWorlds.Find(engine, "carol", "depart", "bus_stop"));

        Assert.Equal(ActionOutcome.Failure, result.Outcome);
        Assert.True(world.HasObject("carol"));
        Assert.True(world.HasObject("alice"));
    }
}
