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
        Assert.Equal(2, engine.TurnManager.SpeechBusyUntilTurn("bob")); // speech track: base 2 turns
        Assert.Equal(0, engine.TurnManager.BusyUntilTurn("bob")); // action track stays free

        // 100 chars: 2 + (int)(100 * 100ms) = 12 turns busy
        var turn = engine.TurnManager.Turn;
        Assert.True(engine.TurnManager.PerformAction(bob, say, new string('x', 100)).Success);
        Assert.Equal(turn + 12, engine.TurnManager.SpeechBusyUntilTurn("bob"));
    }

    [Fact]
    public void Say_SpeechTrack_DoesNotBlockActions()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        var bob = engine.World.GetObject("bob");
        // open the door so Bob can move
        var door = TestWorlds.Find(engine, "bob", "open", "door_b");
        Assert.True(engine.TurnManager.PerformAction(bob, door).Success);

        Assert.True(engine.TurnManager.PerformAction(
            bob, TestWorlds.Find(engine, "bob", "say"), new string('x', 100)).Success);
        Assert.True(engine.TurnManager.Turn < engine.TurnManager.SpeechBusyUntilTurn("bob"));

        // mid-monologue, Bob can still act: he walks through the door
        var go = TestWorlds.Find(engine, "bob", "go", "door_b");
        Assert.True(engine.TurnManager.PerformAction(bob, go).Success);
        Assert.Equal("room_a", engine.World.GetObject("bob").Parent);
    }

    [Fact]
    public void RepeatBackoff_DoublesConsecutiveIdleDuration_AndResetsOnOtherVerb()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        var bob = engine.World.GetObject("bob");
        var look = TestWorlds.Find(engine, "bob", "look");
        var inventory = TestWorlds.Find(engine, "bob", "inventory");

        // consecutive looks back off: 1x, 2x, 4x (busy until 1, 3, 6)
        engine.TurnManager.PerformAction(bob, look); // turn 0
        Assert.Equal(1, engine.TurnManager.BusyUntilTurn("bob"));
        engine.TurnManager.PerformAction(bob, look); // turn 1
        Assert.Equal(3, engine.TurnManager.BusyUntilTurn("bob"));
        engine.TurnManager.PerformAction(bob, look); // turn 2
        Assert.Equal(6, engine.TurnManager.BusyUntilTurn("bob"));

        // a different verb resets the streak (inventory has no backoff: 1 turn)
        engine.TurnManager.PerformAction(bob, inventory); // turn 3
        Assert.Equal(4, engine.TurnManager.BusyUntilTurn("bob"));
        engine.TurnManager.PerformAction(bob, look); // turn 4 — back to 1x
        Assert.Equal(5, engine.TurnManager.BusyUntilTurn("bob"));
    }

    [Fact]
    public void RepeatBackoff_CapsAtConfiguredMaximum()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.ModuleRegistry.LoadJson("""
        [
          {
            "id": "agent", "name": "Agent",
            "fields": [
              { "name": "policy", "type": "string", "default": "player" },
              { "name": "memoryLength", "type": "int", "default": 25 }
            ],
            "affordances": [
              { "verb": "look", "handler": "look", "repeatBackoff": true, "repeatBackoffCap": 4 },
              { "verb": "inventory", "handler": "inventory" },
              { "verb": "wait", "handler": "wait", "repeatBackoff": true }
            ]
          }
        ]
        """);
        var bob = engine.World.GetObject("bob");
        var look = TestWorlds.Find(engine, "bob", "look");

        engine.TurnManager.PerformAction(bob, look); // turn 0 -> busy 1 (1x)
        engine.TurnManager.PerformAction(bob, look); // turn 1 -> busy 3 (2x)
        engine.TurnManager.PerformAction(bob, look); // turn 2 -> busy 6 (4x)
        Assert.Equal(6, engine.TurnManager.BusyUntilTurn("bob"));
        engine.TurnManager.PerformAction(bob, look); // turn 3 -> 8x capped to 4: busy 7
        Assert.Equal(7, engine.TurnManager.BusyUntilTurn("bob"));
    }

    [Fact]
    public void IdleBusyAgent_WakesOnNewSignal()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.Random = new Random(0);
        var bob = engine.World.GetObject("bob");
        var alice = engine.World.GetObject("alice");

        // Bob idles: three consecutive looks back off to 4 turns busy
        var look = TestWorlds.Find(engine, "bob", "look");
        engine.TurnManager.PerformAction(bob, look);
        engine.TurnManager.PerformAction(bob, look);
        engine.TurnManager.PerformAction(bob, look); // busy until turn 6 (turn now 3)
        Assert.Equal(6, engine.TurnManager.BusyUntilTurn("bob"));

        // Alice speaks; the audio crosses the closed door to Bob
        var say = TestWorlds.Find(engine, "alice", "say");
        Assert.True(engine.TurnManager.PerformAction(alice, say, "wake up!").Success);

        // idle backoff is interruptible: Bob's selection starts despite being busy
        var turnBefore = engine.TurnManager.Turn;
        engine.TurnManager.RunNpcTurns(); // wakes: starts Bob's selection
        engine.TurnManager.RunNpcTurns(); // executes it
        Assert.True(engine.TurnManager.Turn > turnBefore);
    }

    [Fact]
    public void NonIdleBusyAgent_DoesNotWakeOnSignal()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.Random = new Random(0);
        // make "take" a long-running action (5 turns, no backoff)
        engine.ModuleRegistry.LoadJson("""
        [
          {
            "id": "portable", "name": "Portable", "fields": [],
            "affordances": [
              {
                "verb": "take", "handler": "take", "duration": 5,
                "signals": [ { "sense": "visual", "priority": 5, "text": "{agent} picks up the {target}." } ]
              },
              { "verb": "drop", "handler": "drop" }
            ]
          }
        ]
        """);
        var bob = engine.World.GetObject("bob");
        var alice = engine.World.GetObject("alice");

        var take = TestWorlds.Find(engine, "bob", "take", "pear");
        Assert.True(engine.TurnManager.PerformAction(bob, take).Success); // busy until turn 5

        // Alice speaks; Bob observes it but is doing real work, not idling
        var say = TestWorlds.Find(engine, "alice", "say");
        Assert.True(engine.TurnManager.PerformAction(alice, say, "hurry up!").Success);
        var turn = engine.TurnManager.Turn;

        engine.TurnManager.RunNpcTurns();
        engine.TurnManager.RunNpcTurns();
        Assert.Equal(turn, engine.TurnManager.Turn); // Bob stayed busy
    }

    [Fact]
    public void WokenIdleAgent_ExecutesCompletedSelection_WithoutWaitingForBackoff()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.Random = new Random(0);
        var bob = engine.World.GetObject("bob");
        var alice = engine.World.GetObject("alice");

        // Bob idles: three consecutive looks back off to 4 turns busy
        var look = TestWorlds.Find(engine, "bob", "look");
        engine.TurnManager.PerformAction(bob, look);
        engine.TurnManager.PerformAction(bob, look);
        engine.TurnManager.PerformAction(bob, look); // busy until turn 6 (turn now 3)

        // Alice speaks; Bob observes it and wakes: his selection starts
        var say = TestWorlds.Find(engine, "alice", "say");
        Assert.True(engine.TurnManager.PerformAction(alice, say, "fire!").Success);
        engine.TurnManager.RunNpcTurns();

        // the planning context build drains the pending signal queue (as
        // AgentContextBuilder does), so CanWake is false again — but the
        // completed selection must still execute, or the chosen action
        // would stall until the backoff expired
        engine.SignalBus.Drain("bob");
        var turnBefore = engine.TurnManager.Turn;
        engine.TurnManager.RunNpcTurns();
        Assert.True(engine.TurnManager.Turn > turnBefore);
    }
}

