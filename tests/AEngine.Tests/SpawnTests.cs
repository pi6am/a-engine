using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.World;
using CoreWorld = AEngine.Core.World.World;

namespace AEngine.Tests;

public class SpawnTests
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
        "id": "surface", "name": "Surface",
        "fields": [ { "name": "capacity", "type": "int", "default": 10 } ],
        "affordances": []
      },
      {
        "id": "spawner", "name": "Spawner",
        "fields": [
          { "name": "prefab", "type": "ref", "default": null },
          { "name": "spawnTo", "type": "ref", "default": null },
          { "name": "maxChildren", "type": "int", "default": 1 }
        ],
        "affordances": []
      },
      {
        "id": "tap", "name": "Tap",
        "fields": [],
        "affordances": [
          {
            "verb": "pull", "handler": "spawn", "label": "Pull a mug of ale from the {target}",
            "signals": [ { "sense": "visual", "priority": 5, "text": "{agent} pulls a fresh mug from the {target}." } ]
          }
        ]
      },
      {
        "id": "beverage", "name": "Beverage",
        "fields": [
          { "name": "alcohol", "type": "number", "default": 0.0 },
          { "name": "empty", "type": "bool", "default": false }
        ],
        "affordances": [
          {
            "verb": "drink", "handler": "consume",
            "when": [ { "module": "beverage", "field": "empty", "equals": false } ]
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

        // the prefab template: top-level, unreachable, with modules and overrides
        world.CreateObject("tpl_ale", CoreWorld.RootId, "mug of Green Gullet ale",
            "A chipped mug of dark, bitter ale.");
        world.AddModule("tpl_ale", "portable");
        world.AddModule("tpl_ale", "beverage");
        world.SetFieldOverride("tpl_ale", "beverage", "alcohol", CoreWorld.ToJson(0.3));

        // the tap spawns ONTO the counter — it is not a container itself,
        // so there is no "put into the tap" or "open the tap"
        world.CreateObject("counter", "room_a", "bar counter");
        world.AddModule("counter", "surface");
        world.CreateObject("ale_tap", "room_a", "ale tap");
        world.AddModule("ale_tap", "spawner");
        world.SetFieldOverride("ale_tap", "spawner", "prefab", CoreWorld.ToJson("tpl_ale"));
        world.SetFieldOverride("ale_tap", "spawner", "spawnTo", CoreWorld.ToJson("counter"));
        world.AddModule("ale_tap", "tap"); // the phrasing affordance (pull)
        return engine;
    }

    private static List<string> Verbs(GameEngine engine, string agentId, string targetId) =>
        engine.ActionResolver.Resolve(engine.World.GetObject(agentId))
            .Where(a => a.TargetId == targetId)
            .Select(a => a.Verb)
            .ToList();

    [Fact]
    public void Spawn_LandsOnTheSpawnTarget_WithFreshIds()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");

        var result = engine.TurnManager.PerformAction(
            alice, TestWorlds.Find(engine, "alice", "pull", "ale_tap"));

        Assert.Equal(ActionOutcome.Success, result.Outcome);
        Assert.Equal("A mug of Green Gullet ale now sits on the bar counter.", result.Message);
        Assert.True(engine.World.HasObject("tpl_ale_1"));
        var clone = engine.World.GetObject("tpl_ale_1");
        Assert.Equal("counter", clone.Parent);
        Assert.Equal("mug of Green Gullet ale", clone.Name);
        Assert.True(clone.HasModule("beverage"));
        Assert.Equal(0.3, engine.ModuleRegistry.ResolveDouble(clone, "beverage", "alcohol"));
        // the template itself stays top-level
        Assert.Equal(CoreWorld.RootId, engine.World.GetObject("tpl_ale").Parent);
    }

    [Fact]
    public void SpawnerHost_IsNotAContainer_NoPutOrOpenAffordances()
    {
        var engine = NewEngine();
        var tapVerbs = Verbs(engine, "alice", "ale_tap");
        Assert.Contains("pull", tapVerbs);
        Assert.DoesNotContain("put", tapVerbs);
        Assert.DoesNotContain("open", tapVerbs);
        Assert.DoesNotContain("close", tapVerbs);
    }

    [Fact]
    public void Spawn_HidesAtCapacity_AndReturnsAfterTheDrinkIsTaken()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");

        engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "pull", "ale_tap"));
        // the single slot on the counter is full — the affordance disappears
        Assert.DoesNotContain(engine.ActionResolver.Resolve(alice),
            a => a.Verb == "pull" && a.TargetId == "ale_tap");

        // take the mug; the slot frees up
        var take = engine.TurnManager.Execute(alice, "take", "tpl_ale_1");
        Assert.True(take.Success, $"take failed: {take.Message} ({take.Outcome})");
        Assert.Contains(engine.ActionResolver.Resolve(alice),
            a => a.Verb == "pull" && a.TargetId == "ale_tap");

        // and the second spawn gets a fresh id
        engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "pull", "ale_tap"));
        Assert.True(engine.World.HasObject("tpl_ale_2"));
    }

    [Fact]
    public void SlotCountsOnlyThisPrefabsClones_OnTheSharedSurface()
    {
        var engine = NewEngine();
        var world = engine.World;
        var alice = world.GetObject("alice");

        // a foreign template's clone already sits on the counter — the ale
        // slot is still free (per-prefab slots, shared surface)
        world.CreateObject("tpl_whiskey", CoreWorld.RootId, "shot of Gut-Rot");
        world.AddModule("tpl_whiskey", "portable");
        world.CreateObject("tpl_whiskey_1", "counter", "shot of Gut-Rot");
        world.AddModule("tpl_whiskey_1", "portable");

        var result = engine.TurnManager.PerformAction(
            alice, TestWorlds.Find(engine, "alice", "pull", "ale_tap"));
        Assert.Equal(ActionOutcome.Success, result.Outcome);
    }

    [Fact]
    public void SpawnedDrink_FlowsThroughConsume()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        engine.World.AddModule("alice", "metabolism");

        engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "pull", "ale_tap"));
        var drink = TestWorlds.Find(engine, "alice", "drink", "tpl_ale_1");
        var result = engine.TurnManager.PerformAction(alice, drink);

        Assert.Equal(ActionOutcome.Success, result.Outcome);
        // 0.3 from the ale, minus the action-tail upkeep burn (1s × 0.002)
        Assert.Equal(0.298, engine.ModuleRegistry.ResolveDouble(
            alice, "metabolism", "alcohol"), 6);
        // the vessel stays behind (on the counter), empty
        Assert.True(engine.ModuleRegistry.ResolveBool(
            engine.World.GetObject("tpl_ale_1"), "beverage", "empty"));
    }

    [Fact]
    public void DefaultSpawnTo_IsTheSpawnerItself()
    {
        var engine = NewEngine();
        engine.World.RemoveModule("ale_tap", "spawner");
        engine.World.AddModule("ale_tap", "spawner");
        engine.World.SetFieldOverride("ale_tap", "spawner", "prefab", CoreWorld.ToJson("tpl_ale"));
        var alice = engine.World.GetObject("alice");

        var result = engine.TurnManager.PerformAction(
            alice, TestWorlds.Find(engine, "alice", "pull", "ale_tap"));

        Assert.Equal(ActionOutcome.Success, result.Outcome);
        Assert.Equal("ale_tap", engine.World.GetObject("tpl_ale_1").Parent);
        Assert.Equal("A mug of Green Gullet ale now sits at the ale tap.", result.Message);
    }
}
