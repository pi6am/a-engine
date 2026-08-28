using AEngine.Core.Modules;
using AEngine.Core.Runtime;

namespace AEngine.Tests;

/// <summary>
/// Real-time mode: TurnManager.Tick advances time on a wall-clock driver
/// (actions do not), and turn-consuming actions leave the acting agent
/// busy for the affordance's data-driven duration (seconds/turns).
/// </summary>
public class RealTimeTests
{
    [Fact]
    public void Duration_ParsesFromModuleJson_AndDefaultsToOne()
    {
        var registry = new ModuleRegistry();
        registry.LoadJson("""
        [
          {
            "id": "m", "name": "M", "fields": [],
            "affordances": [
              { "verb": "push", "handler": "push", "duration": 3 },
              { "verb": "pull", "handler": "pull" }
            ]
          }
        ]
        """);
        var affordances = registry.Get("m").Affordances;
        Assert.Equal(3, affordances[0].Duration);
        Assert.Equal(1, affordances[1].Duration);
    }

    [Fact]
    public void RealTime_TickAdvancesTurn_ActionsDoNot()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.TimeMode = TimeMode.RealTime;
        var alice = engine.World.GetObject("alice");

        var wait = TestWorlds.Find(engine, "alice", "wait");
        Assert.True(engine.TurnManager.PerformAction(alice, wait).Success);
        Assert.Equal(0, engine.TurnManager.Turn); // actions don't advance time

        engine.TurnManager.Tick();
        engine.TurnManager.Tick();
        Assert.Equal(2, engine.TurnManager.Turn);
    }

    [Fact]
    public void BusyAgent_SkipsNpcTurns_UntilDurationElapses()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.Random = new Random(0);
        // make "take" a long-running action (2 turns)
        engine.ModuleRegistry.LoadJson("""
        [
          {
            "id": "portable", "name": "Portable", "fields": [],
            "affordances": [
              {
                "verb": "take", "handler": "take", "duration": 2,
                "signals": [ { "sense": "visual", "priority": 5, "text": "{agent} picks up the {target}." } ]
              },
              { "verb": "drop", "handler": "drop" }
            ]
          }
        ]
        """);

        var bob = engine.World.GetObject("bob");
        var take = TestWorlds.Find(engine, "bob", "take", "pear");
        Assert.True(engine.TurnManager.PerformAction(bob, take).Success);
        var turnAfterAction = engine.TurnManager.Turn;

        // busy for one more turn: NPC turns are skipped entirely
        engine.TurnManager.RunNpcTurns();
        engine.TurnManager.RunNpcTurns();
        Assert.Equal(turnAfterAction, engine.TurnManager.Turn);

        // a turn passes (Alice waits), then Bob acts again via his policy
        var alice = engine.World.GetObject("alice");
        engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "wait"));
        engine.TurnManager.RunNpcTurns(); // starts Bob's selection
        engine.TurnManager.RunNpcTurns(); // executes it
        Assert.True(engine.TurnManager.Turn > turnAfterAction + 1);
    }

    [Fact]
    public void Say_DurationScalesWithTextLength()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        var bob = engine.World.GetObject("bob");
        var say = TestWorlds.Find(engine, "bob", "say");

        Assert.True(engine.TurnManager.PerformAction(bob, say, "hi").Success);
        Assert.Equal(1, engine.TurnManager.BusyUntilTurn("bob")); // base: 1 turn

        // 100 chars: 1 + (int)(100 * 0.05) = 6 turns busy
        var turn = engine.TurnManager.Turn;
        Assert.True(engine.TurnManager.PerformAction(bob, say, new string('x', 100)).Success);
        Assert.Equal(turn + 6, engine.TurnManager.BusyUntilTurn("bob"));
    }
}

