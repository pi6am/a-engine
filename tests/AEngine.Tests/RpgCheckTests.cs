using AEngine.Core.Actions;
using AEngine.Core.Runtime;

namespace AEngine.Tests;

/// <summary>
/// RPG stage 1: map fields (stats/skills), the data-driven n d m dice
/// formula, and affordance checks gating handler execution (lockpicking).
/// </summary>
public class RpgCheckTests
{
    private const string RpgModulesJson = """
    [
      {
        "id": "rules", "name": "Rules",
        "fields": [
          { "name": "diceCount", "type": "int", "default": 1 },
          { "name": "diceSides", "type": "int", "default": 20 }
        ],
        "affordances": []
      },
      {
        "id": "stats", "name": "Stats",
        "fields": [ { "name": "values", "type": "map", "default": {} } ],
        "affordances": []
      },
      {
        "id": "skills", "name": "Skills",
        "fields": [ { "name": "values", "type": "map", "default": {} } ],
        "affordances": []
      },
      {
        "id": "lockable", "name": "Lockable",
        "fields": [ { "name": "keyRef", "type": "ref", "default": null } ],
        "affordances": [
          {
            "verb": "pick", "handler": "pick", "duration": 5,
            "check": { "skill": "lockpicking", "difficulty": 10 },
            "signals": [ { "sense": "audible", "priority": 5, "text": "a click." } ]
          }
        ]
      }
    ]
    """;

    private static GameEngine NewEngine()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.ModuleRegistry.LoadJson(RpgModulesJson);
        engine.World.AddModule("alice", "skills");
        engine.World.AddModule("chest", "lockable");
        // locked chest (openable has no declared locked field; the override
        // still flows through field resolution)
        engine.World.SetFieldOverride("chest", "openable", "locked", Core.World.World.ToJson(true));
        return engine;
    }

    [Fact]
    public void MapFields_RoundTrip_AndStatsSetPreservesOtherEntries()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");

        Assert.Equal(0, Stats.Get(engine.ModuleRegistry, alice, "skills", "lockpicking"));

        Stats.Set(engine.World, engine.ModuleRegistry, alice, "skills", "lockpicking", 5);
        Stats.Set(engine.World, engine.ModuleRegistry, alice, "skills", "brawling", 2);
        Assert.Equal(5, Stats.Get(engine.ModuleRegistry, alice, "skills", "lockpicking"));
        Assert.Equal(2, Stats.Get(engine.ModuleRegistry, alice, "skills", "brawling"));

        var map = engine.ModuleRegistry.ResolveIntMap(alice, "skills", "values");
        Assert.NotNull(map);
        Assert.Equal(2, map!.Count);
    }

    [Fact]
    public void DiceFormula_DefaultsTo1d20_AndRulesModuleOverrides()
    {
        var engine = NewEngine();
        Assert.Equal((1, 20), Checks.DiceFormula(engine.World, engine.ModuleRegistry));

        // diceless: 0d0 makes checks deterministic
        engine.World.CreateObject("rules", Core.World.World.RootId, "rules");
        engine.World.AddModule("rules", "rules");
        engine.World.SetFieldOverride("rules", "rules", "diceCount", Core.World.World.ToJson(0));
        engine.World.SetFieldOverride("rules", "rules", "diceSides", Core.World.World.ToJson(0));
        Assert.Equal((0, 0), Checks.DiceFormula(engine.World, engine.ModuleRegistry));
        Assert.Equal(0, Checks.RollDice(new Random(0), 0, 0));
    }

    [Fact]
    public void FailedCheck_RunsNoHandler_ConsumesTurn_EmitsNoSignal()
    {
        var engine = NewEngine();
        engine.World.MoveObject("bob", "room_a"); // observer
        Stats.Set(engine.World, engine.ModuleRegistry,
            engine.World.GetObject("alice"), "skills", "lockpicking", 5);
        engine.World.CreateObject("rules", Core.World.World.RootId, "rules");
        engine.World.AddModule("rules", "rules");
        engine.World.SetFieldOverride("rules", "rules", "diceCount", Core.World.World.ToJson(0));

        var alice = engine.World.GetObject("alice");
        var pick = TestWorlds.Find(engine, "alice", "pick", "chest");
        var turn = engine.TurnManager.Turn;
        var result = engine.TurnManager.PerformAction(alice, pick); // 0d0 + 5 vs 10: fails

        Assert.False(result.Success);
        Assert.Equal("You try to pick the chest, but fail.", result.Message);
        Assert.True(engine.TurnManager.Turn > turn); // the attempt took time
        Assert.True(engine.ModuleRegistry.ResolveBool(
            engine.World.GetObject("chest"), "openable", "locked")); // handler never ran
        Assert.Empty(engine.SignalBus.Drain("bob")); // failure is silent to observers
    }

    [Fact]
    public void PassedCheck_RunsHandler_AndUnlocks()
    {
        var engine = NewEngine();
        engine.World.MoveObject("bob", "room_a"); // observer
        Stats.Set(engine.World, engine.ModuleRegistry,
            engine.World.GetObject("alice"), "skills", "lockpicking", 12);
        engine.World.CreateObject("rules", Core.World.World.RootId, "rules");
        engine.World.AddModule("rules", "rules");
        engine.World.SetFieldOverride("rules", "rules", "diceCount", Core.World.World.ToJson(0));

        var alice = engine.World.GetObject("alice");
        var pick = TestWorlds.Find(engine, "alice", "pick", "chest");
        var result = engine.TurnManager.PerformAction(alice, pick); // 0d0 + 12 vs 10: succeeds

        Assert.True(result.Success);
        Assert.Equal("You pick the lock on the chest.", result.Message);
        Assert.False(engine.ModuleRegistry.ResolveBool(
            engine.World.GetObject("chest"), "openable", "locked"));
        Assert.Contains(engine.SignalBus.Drain("bob"), s => s.Text == "a click.");

        // picking an unlocked lock is a noop
        Assert.Equal(Core.Actions.ActionOutcome.Noop,
            engine.TurnManager.Execute(alice, "pick", "chest").Outcome);
    }

    [Fact]
    public void FailText_OverridesTheGenericFailureMessage()
    {
        var engine = NewEngine();
        engine.World.CreateObject("rules", Core.World.World.RootId, "rules");
        engine.World.AddModule("rules", "rules");
        engine.World.SetFieldOverride("rules", "rules", "diceCount", Core.World.World.ToJson(0));

        // override the lockable module with a custom failure message
        engine.ModuleRegistry.LoadJson("""
        [
          {
            "id": "lockable", "name": "Lockable",
            "fields": [ { "name": "keyRef", "type": "ref", "default": null } ],
            "affordances": [
              {
                "verb": "pick", "handler": "pick",
                "check": {
                  "skill": "lockpicking", "difficulty": 10,
                  "failText": "The lock defeats your picks."
                }
              }
            ]
          }
        ]
        """);
        Stats.Set(engine.World, engine.ModuleRegistry,
            engine.World.GetObject("alice"), "skills", "lockpicking", 5);

        var alice = engine.World.GetObject("alice");
        var pick = TestWorlds.Find(engine, "alice", "pick", "chest");
        var result = engine.TurnManager.PerformAction(alice, pick);
        Assert.False(result.Success);
        Assert.Equal("The lock defeats your picks.", result.Message);
    }
}
