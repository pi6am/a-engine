using AEngine.Core.Signals;

namespace AEngine.Tests;

/// <summary>
/// Signal propagation matrix and SignalBus queue semantics, driven
/// through real resolved actions and TurnManager.PerformAction.
/// </summary>
public class SignalTests
{
    [Fact]
    public void SameRoom_ObserverGetsVisualOverAudible()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.World.MoveObject("bob", "room_a"); // same room as alice

        var open = TestWorlds.Find(engine, "alice", "open", "chest");
        Assert.True(engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), open).Success);

        var signal = Assert.Single(engine.SignalBus.Drain("bob"));
        Assert.Equal(SignalSense.Visual, signal.Sense);
        Assert.Equal("Alice opens the chest.", signal.Text);
        Assert.Equal("room_a", signal.OriginRoomId);
    }

    [Fact]
    public void ClosedDoor_AdjacentObserverGetsAudibleOnly()
    {
        var engine = TestWorlds.NewTwoRoomEngine(); // bob in room_b, door closed

        var open = TestWorlds.Find(engine, "alice", "open", "chest");
        engine.TurnManager.PerformAction(engine.World.GetObject("alice"), open);

        // visual (whenOpen, door closed) is blocked; audio (always) passes
        var signal = Assert.Single(engine.SignalBus.Drain("bob"));
        Assert.Equal(SignalSense.Audible, signal.Sense);
        Assert.Equal("You hear something creak open.", signal.Text);
    }

    [Fact]
    public void OpenDoor_AdjacentObserverGetsVisual()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        var alice = engine.World.GetObject("alice");

        // open the door first; drain the "wood sliding" audio it generates
        var openDoor = TestWorlds.Find(engine, "alice", "open", "door_a");
        engine.TurnManager.PerformAction(alice, openDoor);
        engine.SignalBus.Drain("bob");

        var openChest = TestWorlds.Find(engine, "alice", "open", "chest");
        engine.TurnManager.PerformAction(alice, openChest);

        // door open: visual (whenOpen -> open) now passes and wins on priority
        var signal = Assert.Single(engine.SignalBus.Drain("bob"));
        Assert.Equal(SignalSense.Visual, signal.Sense);
        Assert.Equal("Alice opens the chest.", signal.Text);
    }

    [Fact]
    public void GlassDoor_TransmitVisualAlways_LetsVisualThroughClosedDoor()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        // glass door: the room_a side transmits visual even while closed
        engine.World.SetFieldOverride(
            "door_a", "portal", "transmitVisual", Core.World.World.ToJson("always"));

        var open = TestWorlds.Find(engine, "alice", "open", "chest");
        engine.TurnManager.PerformAction(engine.World.GetObject("alice"), open);

        var signal = Assert.Single(engine.SignalBus.Drain("bob"));
        Assert.Equal(SignalSense.Visual, signal.Sense);
    }

    [Fact]
    public void OneWayDoor_VisualTransmitsAToBButNotBToA()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        // one-way mirror: room_a side always transmits visual, room_b side never
        engine.World.SetFieldOverride(
            "door_a", "portal", "transmitVisual", Core.World.World.ToJson("always"));
        engine.World.SetFieldOverride(
            "door_b", "portal", "transmitVisual", Core.World.World.ToJson("never"));

        // A -> B: alice takes the apple (visual-only signal); bob sees it
        var take = TestWorlds.Find(engine, "alice", "take", "apple");
        engine.TurnManager.PerformAction(engine.World.GetObject("alice"), take);
        var seen = Assert.Single(engine.SignalBus.Drain("bob"));
        Assert.Equal(SignalSense.Visual, seen.Sense);
        Assert.Equal("Alice picks up the apple.", seen.Text);

        // B -> A: bob takes the pear; the room_b side transmits nothing visual,
        // and 'take' has no audible spec, so alice gets nothing at all
        var bobTake = TestWorlds.Find(engine, "bob", "take", "pear");
        engine.TurnManager.PerformAction(engine.World.GetObject("bob"), bobTake);
        Assert.Empty(engine.SignalBus.Drain("alice"));
    }

    [Fact]
    public void Say_ObserverHearsWordsNotLips()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.World.MoveObject("bob", "room_a");

        var say = TestWorlds.Find(engine, "alice", "say", "alice");
        Assert.Equal("Say what?", say.Prompt);
        var result = engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), say, "dinner is ready");
        Assert.True(result.Success);
        Assert.Equal("You say: \"dinner is ready\"", result.Message);

        // audible p=10 beats visual p=1 (lips)
        var signal = Assert.Single(engine.SignalBus.Drain("bob"));
        Assert.Equal(SignalSense.Audible, signal.Sense);
        Assert.Equal("Alice says: \"dinner is ready\"", signal.Text);
    }

    [Fact]
    public void FailedAction_EmitsNoSignals()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.World.MoveObject("bob", "room_a");

        // the chest is already closed; closing it fails
        var close = new Core.Actions.AvailableAction(
            "close", "chest", "Close the chest", "close", "openable");
        var result = engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), close);
        Assert.False(result.Success);
        Assert.Empty(engine.SignalBus.Drain("bob"));
    }

    [Fact]
    public void Drain_ClearsQueue()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        var open = TestWorlds.Find(engine, "alice", "open", "chest");
        engine.TurnManager.PerformAction(engine.World.GetObject("alice"), open);

        Assert.Single(engine.SignalBus.Drain("bob"));
        Assert.Empty(engine.SignalBus.Drain("bob"));
        Assert.Empty(engine.SignalBus.Peek("bob"));
    }

    [Fact]
    public void ActorAndNonAgents_GetNoSignals()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        var open = TestWorlds.Find(engine, "alice", "open", "chest");
        engine.TurnManager.PerformAction(engine.World.GetObject("alice"), open);

        Assert.Empty(engine.SignalBus.Drain("alice")); // no self-signal
        Assert.Empty(engine.SignalBus.Drain("chest")); // not an agent
    }

    [Fact]
    public void DistantObserver_GetsNothing()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        // carol is two rooms away (room_c is not connected to room_a)
        engine.World.CreateObject("room_c", Core.World.World.RootId, "Room C");
        engine.World.AddModule("room_c", "room");
        engine.World.CreateObject("carol", "room_c", "Carol");
        engine.World.AddModule("carol", "agent");

        var open = TestWorlds.Find(engine, "alice", "open", "chest");
        engine.TurnManager.PerformAction(engine.World.GetObject("alice"), open);

        Assert.Empty(engine.SignalBus.Drain("carol"));
    }
}
