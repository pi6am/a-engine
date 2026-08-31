using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.World;
using CoreWorld = AEngine.Core.World.World;

namespace AEngine.Tests;

/// <summary>
/// The `surface` module: an always-open holder that reads "on" instead
/// of "in" — counters and tables. Items on a surface are reachable, can
/// be taken, and can be put onto it (when the surface also carries the
/// `puttable` module — spawn-target counters don't, to keep menus lean);
/// there is no open/close state.
/// </summary>
public class SurfaceTests
{
    private const string ModulesJson = """
    [
      {
        "id": "surface", "name": "Surface",
        "fields": [ { "name": "capacity", "type": "int", "default": 10 } ],
        "affordances": []
      },
      {
        "id": "puttable", "name": "Puttable",
        "fields": [],
        "affordances": [
          {
            "verb": "put", "handler": "put", "duration": 2,
            "signals": [ { "sense": "visual", "priority": 5, "text": "{agent} puts the {item} onto the {target}." } ]
          }
        ]
      }
    ]
    """;

    private static GameEngine NewEngine(bool puttable = true)
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.ModuleRegistry.LoadJson(ModulesJson);
        var world = engine.World;
        world.CreateObject("counter", "room_a", "bar counter");
        world.AddModule("counter", "surface");
        if (puttable)
            world.AddModule("counter", "puttable");
        return engine;
    }

    [Fact]
    public void PutOntoSurface_UsesOntoWording()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        engine.TurnManager.Execute(alice, "take", "apple"); // hold it first

        var put = TestWorlds.Find(engine, "alice", "put", "counter");
        Assert.Equal("apple", put.AuxTargetId);
        Assert.Equal("Put the apple onto the bar counter", put.Label);

        var result = engine.TurnManager.PerformAction(alice, put);
        Assert.Equal(ActionOutcome.Success, result.Outcome);
        Assert.Equal("You put the apple onto the bar counter.", result.Message);
        Assert.Equal("counter", engine.World.GetObject("apple").Parent);
    }

    [Fact]
    public void TakeFromSurface_NamesTheSurface()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        engine.TurnManager.Execute(alice, "take", "apple");
        engine.TurnManager.Execute(alice, "put", "counter", auxTargetId: "apple");

        var result = engine.TurnManager.Execute(alice, "take", "apple");
        Assert.Equal(ActionOutcome.Success, result.Outcome);
        Assert.Equal("You take the apple from the bar counter.", result.Message);
    }

    [Fact]
    public void LookListsSurfaceContents_WithOn()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        engine.TurnManager.Execute(alice, "take", "apple");
        engine.TurnManager.Execute(alice, "put", "counter", auxTargetId: "apple");

        var look = engine.TurnManager.Execute(alice, "look");
        Assert.Contains("apple (on bar counter)", look.Message);
        Assert.DoesNotContain("(open)", look.Message);
    }

    [Fact]
    public void ExamineSurface_ReportsContentsOnIt()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        engine.TurnManager.Execute(alice, "take", "apple");
        engine.TurnManager.Execute(alice, "put", "counter", auxTargetId: "apple");

        var examine = engine.TurnManager.Execute(alice, "examine", "counter");
        Assert.Contains("There is an apple on it.", examine.Message);

        // an empty surface says so
        engine.World.MoveObject("apple", "room_a");
        var bare = engine.TurnManager.Execute(alice, "examine", "counter");
        Assert.Contains("It's empty.", bare.Message);
    }

    [Fact]
    public void SurfaceCapacity_BlocksOverflow()
    {
        var engine = NewEngine();
        var world = engine.World;
        var alice = world.GetObject("alice");
        world.SetFieldOverride("counter", "surface", "capacity", CoreWorld.ToJson(1));
        world.CreateObject("pear_on_counter", "counter", "pear");
        world.AddModule("pear_on_counter", "portable");

        // counter holds one pear already; putting the apple exceeds capacity
        engine.TurnManager.Execute(alice, "take", "apple");
        var result = engine.TurnManager.Execute(alice, "put", "counter", auxTargetId: "apple");
        Assert.Equal(ActionOutcome.Failure, result.Outcome);
        Assert.Equal("The bar counter is full.", result.Message);
    }

    [Fact]
    public void SurfaceHas_NoOpenCloseState_AndPutNeedsPuttable()
    {
        var engine = NewEngine();
        var verbs = engine.ActionResolver.Resolve(engine.World.GetObject("alice"))
            .Where(a => a.TargetId == "counter").Select(a => a.Verb).ToList();
        Assert.DoesNotContain("open", verbs);
        Assert.DoesNotContain("close", verbs);

        // a bare surface (a spawn target like the bar counter) keeps its
        // holding semantics but offers no put — lean menus for the LLM
        var bare = NewEngine(puttable: false);
        var alice = bare.World.GetObject("alice");
        bare.TurnManager.Execute(alice, "take", "apple"); // holding something to put
        var bareVerbs = bare.ActionResolver.Resolve(alice)
            .Where(a => a.TargetId == "counter").Select(a => a.Verb).ToList();
        Assert.DoesNotContain("put", bareVerbs);
        // items on it remain reachable and takeable
        bare.World.MoveObject("apple", "counter");
        Assert.Contains("take", bare.ActionResolver.Resolve(alice)
            .Where(a => a.TargetId == "apple").Select(a => a.Verb));
    }
}
