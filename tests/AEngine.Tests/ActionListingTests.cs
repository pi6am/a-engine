using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.World;
using CoreWorld = AEngine.Core.World.World;

namespace AEngine.Tests;

/// <summary>
/// Action-list hygiene: a seated agent's own verbs appear exactly once
/// (the furniture-occupant scan must not re-scan the actor), and
/// interchangeable objects sharing a name collapse to a single entry
/// per (verb, label) — the LLM and the menus can't tell them apart.
/// </summary>
public class ActionListingTests
{
    private static GameEngine NewEngine()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.ModuleRegistry.LoadJson("""
        [
          {
            "id": "sittable", "name": "Sittable",
            "fields": [ { "name": "capacity", "type": "int", "default": 1 } ],
            "affordances": [
              { "verb": "sit", "handler": "sit", "duration": 2, "postures": ["standing"] },
              { "verb": "stand", "handler": "stand", "duration": 2, "postures": ["sitting"] }
            ]
          },
          {
            "id": "portable", "name": "Portable",
            "fields": [],
            "affordances": [
              { "verb": "take", "handler": "take", "duration": 2 },
              { "verb": "drop", "handler": "drop", "duration": 2 },
              { "verb": "give", "handler": "give", "duration": 2,
                "reaction": {
                  "window": 3,
                  "telegraph": "{agent} offers the {item} to the {target}.",
                  "options": [
                    { "id": "accept", "label": "Accept", "noResist": true, "default": true },
                    { "id": "decline", "label": "Decline" }
                  ]
                } }
            ]
          }
        ]
        """);
        engine.World.MoveObject("bob", "room_a"); // a second agent present
        engine.World.CreateObject("stool", "room_a", "bar stool");
        engine.World.AddModule("stool", "sittable");
        return engine;
    }

    private static List<(string Verb, string Label)> Actions(GameEngine engine, string agentId) =>
        engine.ActionResolver.Resolve(engine.World.GetObject(agentId))
            .Select(a => (a.Verb, a.Label))
            .ToList();

    [Fact]
    public void SeatedAgent_SelfVerbsListedExactlyOnce()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        engine.TurnManager.Execute(alice, "sit", "stool");

        var actions = Actions(engine, "alice");
        Assert.Single(actions, a => a.Verb == "look");
        Assert.Single(actions, a => a.Verb == "inventory");
        Assert.Single(actions, a => a.Verb == "wait");
        // broadcast say once (with one other agent there are no directed
        // entries)
        Assert.Single(actions, a => a.Verb == "say");
    }

    [Fact]
    public void SeatedAgent_HeldItemsListedExactlyOnce()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        engine.TurnManager.Execute(alice, "take", "apple");
        engine.TurnManager.Execute(alice, "sit", "stool");

        var actions = Actions(engine, "alice");
        Assert.Single(actions, a => a.Verb == "drop" && a.Label == "Drop the apple");
        Assert.Single(actions, a => a.Verb == "give" && a.Label == "Give the apple to Bob");
        Assert.Single(actions, a => a.Verb == "examine" && a.Label == "Examine the apple");
    }

    [Fact]
    public void SameNamedObjects_CollapseToOneEntryPerVerbAndLabel()
    {
        var engine = NewEngine();
        var world = engine.World;
        world.CreateObject("mug_one", "room_a", "empty mug");
        world.AddModule("mug_one", "portable");
        world.CreateObject("mug_two", "room_a", "empty mug");
        world.AddModule("mug_two", "portable");
        world.CreateObject("mug_three", "room_a", "empty mug");
        world.AddModule("mug_three", "portable");

        var actions = Actions(engine, "alice");
        // one take, one examine — not three
        Assert.Single(actions, a => a.Verb == "take" && a.Label == "Take the empty mug");
        Assert.Single(actions, a => a.Verb == "examine" && a.Label == "Examine the empty mug");

        // hold all three: held-item verbs collapse too — one drop, one
        // give per recipient, one examine — and the surviving entry
        // references a real object that executes
        var alice = engine.World.GetObject("alice");
        foreach (var id in new[] { "mug_one", "mug_two", "mug_three" })
            Assert.True(engine.TurnManager.Execute(alice, "take", id).Success);

        actions = Actions(engine, "alice");
        Assert.Single(actions, a => a.Verb == "drop" && a.Label == "Drop the empty mug");
        Assert.Single(actions, a => a.Verb == "give" && a.Label == "Give the empty mug to Bob");
        Assert.Single(actions, a => a.Verb == "examine" && a.Label == "Examine the empty mug");

        var drop = engine.ActionResolver.Resolve(alice)
            .First(a => a.Verb == "drop" && a.Label == "Drop the empty mug");
        var result = engine.TurnManager.Execute(alice, "drop", drop.TargetId);
        Assert.Equal(AEngine.Core.Actions.ActionOutcome.Success, result.Outcome);
    }

    [Fact]
    public void DistinctNames_KeepDistinctEntries()
    {
        var engine = NewEngine();
        var actions = Actions(engine, "alice");
        // the apple and the chest are distinct objects with distinct names
        Assert.Single(actions, a => a.Label == "Take the apple");
        Assert.Single(actions, a => a.Label == "Open the chest");
        Assert.Single(actions, a => a.Label == "Examine the apple");
        Assert.Single(actions, a => a.Label == "Examine the chest");
    }
}
