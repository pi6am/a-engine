using AEngine.Core.Actions;
using AEngine.Core.Runtime;

namespace AEngine.Tests;

/// <summary>
/// RPG stage 2: opposed checks (both sides roll), the prone posture
/// (shove knocks down, stand costs an action), and pickpocketing (steal
/// from another agent's inventory; failed checks rattle the victim via
/// failSignals).
/// </summary>
public class OpposedCheckTests
{
    private const string Stage2ModulesJson = """
    [
      {
        "id": "rules", "name": "Rules",
        "fields": [
          { "name": "diceCount", "type": "int", "default": 0 },
          { "name": "diceSides", "type": "int", "default": 0 }
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
        "id": "agent", "name": "Agent",
        "fields": [
          { "name": "policy", "type": "string", "default": "player" },
          { "name": "memoryLength", "type": "int", "default": 25 },
          { "name": "posture", "type": "string", "default": "standing" }
        ],
        "affordances": [
          { "verb": "look", "handler": "look", "repeatBackoff": true },
          { "verb": "inventory", "handler": "inventory" },
          { "verb": "wait", "handler": "wait", "repeatBackoff": true },
          { "verb": "stand", "handler": "stand", "postures": ["prone"] }
        ]
      },
      {
        "id": "shoveable", "name": "Shoveable",
        "fields": [],
        "affordances": [
          {
            "verb": "shove", "handler": "shove",
            "check": {
              "stat": "strength", "opposed": { "stat": "agility" },
              "failSignals": [
                { "sense": "visual", "priority": 10, "text": "{agent} tries to shove the {target}, who holds their ground." }
              ]
            },
            "signals": [
              { "sense": "visual", "priority": 10, "text": "{agent} shoves the {target} to the ground." }
            ]
          }
        ]
      },
      {
        "id": "portable", "name": "Portable",
        "fields": [],
        "affordances": [
          {
            "verb": "take", "handler": "take",
            "signals": [ { "sense": "visual", "priority": 5, "text": "{agent} picks up the {target}." } ]
          },
          { "verb": "drop", "handler": "drop" },
          {
            "verb": "steal", "handler": "steal",
            "check": {
              "stat": "agility", "skill": "pickpocket", "opposed": { "stat": "perception" },
              "failSignals": [
                { "sense": "visual", "priority": 10, "text": "{agent} makes a grab for the {target}!" }
              ]
            },
            "signals": [
              { "sense": "visual", "priority": 2, "text": "{agent} lifts the {target}." }
            ]
          }
        ]
      }
    ]
    """;

    // diceless rules (0d0): opposed checks resolve as attacker bonus vs
    // defender bonus, ties to the attacker
    private static GameEngine NewEngine()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.ModuleRegistry.LoadJson(Stage2ModulesJson);
        engine.World.CreateObject("rules", Core.World.World.RootId, "rules");
        engine.World.AddModule("rules", "rules");
        engine.World.AddModule("alice", "stats");
        engine.World.AddModule("alice", "skills");
        engine.World.AddModule("bob", "stats");
        engine.World.AddModule("bob", "shoveable");
        engine.World.MoveObject("bob", "room_a"); // same room as Alice
        return engine;
    }

    private static void SetStat(GameEngine engine, string id, string stat, int value) =>
        Stats.Set(engine.World, engine.ModuleRegistry, engine.World.GetObject(id), "stats", stat, value);

    private static void SetSkill(GameEngine engine, string id, string skill, int value) =>
        Stats.Set(engine.World, engine.ModuleRegistry, engine.World.GetObject(id), "skills", skill, value);

    [Fact]
    public void Shove_OpposedCheck_FailsAgainstABetterDefender()
    {
        var engine = NewEngine();
        SetStat(engine, "alice", "strength", 10);
        SetStat(engine, "bob", "agility", 12); // 10 vs 12: fails
        var alice = engine.World.GetObject("alice");

        var shove = TestWorlds.Find(engine, "alice", "shove", "bob");
        var result = engine.TurnManager.PerformAction(alice, shove);
        Assert.False(result.Success);
        Assert.Equal(Postures.Standing,
            Postures.Of(engine.World, engine.ModuleRegistry, engine.World.GetObject("bob")));

        // the botched shove is observable by the victim (failSignals)
        Assert.Contains(engine.SignalBus.Drain("bob"),
            s => s.Text == "Alice tries to shove the Bob, who holds their ground.");
    }

    [Fact]
    public void Shove_KnocksProne_AndGettingUpCostsAnAction()
    {
        var engine = NewEngine();
        SetStat(engine, "alice", "strength", 14);
        SetStat(engine, "bob", "agility", 12); // 14 vs 12: succeeds
        var alice = engine.World.GetObject("alice");
        var bob = engine.World.GetObject("bob");

        var result = engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "shove", "bob"));
        Assert.True(result.Success);
        Assert.Equal("You shove the Bob to the ground.", result.Message);
        Assert.Equal(Postures.Prone, Postures.Of(engine.World, engine.ModuleRegistry, bob));
        Assert.Contains(engine.SignalBus.Drain("bob"), s => s.Text.Contains("shoves"));
        // the room listing marks him
        Assert.Contains("Bob (prone)",
            engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "look")).Message);

        // prone: no go, but Stand up is available (self-targeted)
        var prone = engine.ActionResolver.Resolve(bob);
        Assert.DoesNotContain(prone, a => a.Verb == "go");
        var stand = Assert.Single(prone, a => a.Verb == "stand");
        Assert.Equal("Stand up", stand.Label);
        Assert.Contains("You are prone on the ground.",
            engine.TurnManager.PerformAction(bob, TestWorlds.Find(engine, "bob", "look")).Message);

        var up = engine.TurnManager.PerformAction(bob, stand);
        Assert.True(up.Success);
        Assert.Equal("You get up.", up.Message);
        Assert.Equal(Postures.Standing, Postures.Of(engine.World, engine.ModuleRegistry, bob));
        Assert.Contains(engine.ActionResolver.Resolve(bob), a => a.Verb == "go");
    }

    [Fact]
    public void Steal_TakesFromAPocket_WhenTheCheckPasses()
    {
        var engine = NewEngine();
        engine.World.MoveObject("apple", "bob"); // the apple rides in Bob's pocket
        SetStat(engine, "alice", "agility", 14);
        SetSkill(engine, "alice", "pickpocket", 3); // 17 vs Bob's 0: succeeds
        var alice = engine.World.GetObject("alice");

        // the pocket item offers steal, not take
        var actions = engine.ActionResolver.Resolve(alice);
        var steal = Assert.Single(actions, a => a.Verb == "steal" && a.TargetId == "apple");
        Assert.Equal("Steal the apple", steal.Label);
        Assert.DoesNotContain(actions, a => a.Verb == "take" && a.TargetId == "apple");

        var result = engine.TurnManager.PerformAction(alice, steal);
        Assert.True(result.Success);
        Assert.Equal("You steal the apple from Bob.", result.Message);
        Assert.Equal("alice", engine.World.GetObject("apple").Parent);
    }

    [Fact]
    public void Steal_Failure_RattlesTheVictim()
    {
        var engine = NewEngine();
        engine.World.MoveObject("apple", "bob");
        SetStat(engine, "alice", "agility", 8);
        SetStat(engine, "bob", "perception", 20); // 8 vs 20: fails
        var alice = engine.World.GetObject("alice");

        var result = engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "steal", "apple"));
        Assert.False(result.Success);
        Assert.Equal("bob", engine.World.GetObject("apple").Parent); // the apple stays
        Assert.Contains(engine.SignalBus.Drain("bob"),
            s => s.Text == "Alice makes a grab for the apple!");
    }

    [Fact]
    public void Steal_WornItemsAreNotOffered()
    {
        var engine = NewEngine();
        engine.ModuleRegistry.LoadJson("""
        [
          {
            "id": "wearable", "name": "Wearable",
            "fields": [
              { "name": "regions", "type": "list", "default": [] },
              { "name": "worn", "type": "bool", "default": false }
            ],
            "affordances": []
          }
        ]
        """);
        engine.World.MoveObject("apple", "bob");
        engine.World.AddModule("apple", "wearable");
        engine.World.SetFieldOverride("apple", "wearable", "worn", Core.World.World.ToJson(true));

        var actions = engine.ActionResolver.Resolve(engine.World.GetObject("alice"));
        Assert.DoesNotContain(actions, a => a.TargetId == "apple");
    }
}
