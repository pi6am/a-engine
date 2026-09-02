using AEngine.Core.Actions;
using AEngine.Core.Runtime;

namespace AEngine.Tests;

/// <summary>
/// Examine is a universal verb offered for every visible object: full
/// description, worn/carried items for agents, open/closed state and
/// contents for openables and containers. Lock state stays hidden.
/// </summary>
public class ExamineTests
{
    [Fact]
    public void Examine_IsOfferedForEveryVisibleThing()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.World.MoveObject("bob", "room_a");
        var actions = engine.ActionResolver.Resolve(engine.World.GetObject("alice"));

        // room items, furniture-less objects, containers, portals, agents —
        // and other agents' pocket contents — are all examinable; you are not
        Assert.Contains(actions, a => a.Verb == "examine" && a.TargetId == "apple");
        Assert.Contains(actions, a => a.Verb == "examine" && a.TargetId == "chest");
        Assert.Contains(actions, a => a.Verb == "examine" && a.TargetId == "door_a");
        Assert.Contains(actions, a => a.Verb == "examine" && a.TargetId == "bob");
        Assert.Contains(actions, a => a.Verb == "examine" && a.Label == "Examine Bob");
        Assert.DoesNotContain(actions, a => a.Verb == "examine" && a.TargetId == "alice");
        // no duplicates
        var examines = actions.Where(a => a.Verb == "examine").ToList();
        Assert.Equal(examines.Count, examines.Select(a => a.TargetId).Distinct().Count());
    }

    [Fact]
    public void Examine_Agent_ShowsDescriptionWearingAndCarrying()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.ModuleRegistry.LoadJson("""
        [
          {
            "id": "body", "name": "Body",
            "fields": [ { "name": "regions", "type": "list", "default": ["top"] } ],
            "affordances": []
          },
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
        var bob = engine.World.GetObject("bob");
        bob.Description = "A shabby-looking fellow.";
        engine.World.MoveObject("bob", "room_a");
        engine.World.MoveObject("pear", "bob"); // Bob's pocket
        engine.World.AddModule("bob", "body");
        engine.World.CreateObject("vest", "bob", "vest");
        engine.World.AddModule("vest", "wearable");
        engine.World.SetFieldOverride("vest", "wearable", "worn", Core.World.World.ToJson(true));

        var alice = engine.World.GetObject("alice");
        var result = engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "examine", "bob"));
        Assert.True(result.Success);
        Assert.Contains("A shabby-looking fellow.", result.Message);
        Assert.Contains("Wearing: a vest.", result.Message);
        Assert.Contains("Carrying: a pear.", result.Message);
    }

    [Fact]
    public void Examine_Container_ShowsStateAndContents_ButNeverLockState()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        var alice = engine.World.GetObject("alice");

        var closed = engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "examine", "chest"));
        Assert.Contains("It is closed.", closed.Message);

        engine.World.MoveObject("apple", "chest");
        engine.World.SetFieldOverride("chest", "openable", "open", Core.World.World.ToJson(true));
        engine.World.SetFieldOverride("chest", "openable", "locked", Core.World.World.ToJson(true));
        var open = engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "examine", "chest"));
        Assert.Contains("It is open.", open.Message);
        Assert.Contains("There is an apple inside.", open.Message);
        Assert.DoesNotContain("lock", open.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Examine_Agent_PostureIsShown()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.World.MoveObject("bob", "room_a");
        engine.World.SetFieldOverride("bob", "agent", "posture", Core.World.World.ToJson("prone"));

        var result = engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "examine", "bob"));
        Assert.Contains("Bob is prone on the ground.", result.Message);
    }

    [Fact]
    public void Examine_CarriedAgentsCannotExamine()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.World.AddModule("bob", "portable");
        engine.World.MoveObject("bob", "room_a");
        var alice = engine.World.GetObject("alice");
        Assert.True(engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "take", "bob")).Success);

        Assert.DoesNotContain(engine.ActionResolver.Resolve(engine.World.GetObject("bob")),
            a => a.Verb == "examine");
    }
}
