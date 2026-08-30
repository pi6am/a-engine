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
    public void SignalText_CollapsesDoubledArticles()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.World.MoveObject("bob", "room_a"); // same room as alice
        // a name that already carries its article must not double the
        // template's own "the" ("opens the the strongbox")
        engine.World.GetObject("chest").Name = "the strongbox";

        var open = TestWorlds.Find(engine, "alice", "open", "chest");
        Assert.True(engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), open).Success);

        var signal = Assert.Single(engine.SignalBus.Drain("bob"));
        Assert.Equal("Alice opens the strongbox.", signal.Text);
    }

    [Fact]
    public void ClosedDoor_AdjacentObserverGetsAudibleOnly()
    {
        var engine = TestWorlds.NewTwoRoomEngine(); // bob in room_b, door closed

        var open = TestWorlds.Find(engine, "alice", "open", "chest");
        engine.TurnManager.PerformAction(engine.World.GetObject("alice"), open);

        // visual (whenOpen, door closed) is blocked; audio (always) passes,
        // with a directional suffix naming the portal side in bob's room
        var signal = Assert.Single(engine.SignalBus.Drain("bob"));
        Assert.Equal(SignalSense.Audible, signal.Sense);
        Assert.Equal(
            "You hear something creak open through the wooden door to the south.",
            signal.Text);
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
        Assert.Equal("Alice opens the chest through the wooden door to the south.", signal.Text);
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
        Assert.Equal("Alice picks up the apple through the wooden door to the south.", seen.Text);

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
    public void NonSuccessAction_EmitsNoSignals()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.World.MoveObject("bob", "room_a");

        // the chest is already closed; closing it is a noop
        var close = new Core.Actions.AvailableAction(
            "close", "chest", "Close the chest", "close", "openable");
        var result = engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), close);
        Assert.False(result.Success);
        Assert.Equal(Core.Actions.ActionOutcome.Noop, result.Outcome);
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

    [Fact]
    public void OpenDoor_ObservedThroughItself_NoSuffix()
    {
        var engine = TestWorlds.NewTwoRoomEngine(); // bob in room_b, door closed

        // alice opens the door; bob perceives it through that very door —
        // the directional suffix would be redundant
        var openDoor = TestWorlds.Find(engine, "alice", "open", "door_a");
        engine.TurnManager.PerformAction(engine.World.GetObject("alice"), openDoor);

        var signal = Assert.Single(engine.SignalBus.Drain("bob"));
        Assert.Equal(SignalSense.Visual, signal.Sense);
        Assert.Equal("Alice opens the wooden door.", signal.Text);
    }

    [Fact]
    public void CloseDoor_ObservedFromOtherSide_SeesVisual()
    {
        var engine = TestWorlds.NewTwoRoomEngine(); // alice in room_a, bob in room_b
        var bob = engine.World.GetObject("bob");

        engine.TurnManager.PerformAction(bob, TestWorlds.Find(engine, "bob", "open", "door_b"));
        engine.SignalBus.Drain("alice");
        var close = TestWorlds.Find(engine, "bob", "close", "door_b");
        Assert.True(engine.TurnManager.PerformAction(bob, close).Success);

        // the door manifests in both rooms: alice sees her side close even
        // though the door (now closed) would not transmit visual
        var signal = Assert.Single(engine.SignalBus.Drain("alice"));
        Assert.Equal(SignalSense.Visual, signal.Sense);
        Assert.Equal("Bob closes the wooden door.", signal.Text);
        Assert.Equal("door_b", signal.TargetId);
    }

    [Fact]
    public void Say_ThroughPortal_HasDirectionSuffix()
    {
        var engine = TestWorlds.NewTwoRoomEngine(); // alice in room_a, bob in room_b, door closed

        var say = TestWorlds.Find(engine, "alice", "say", "alice");
        engine.TurnManager.PerformAction(engine.World.GetObject("alice"), say, "one moment");

        // audible passes through the closed door, naming bob's side of it
        var signal = Assert.Single(engine.SignalBus.Drain("bob"));
        Assert.Equal(SignalSense.Audible, signal.Sense);
        Assert.Equal(
            "Alice says: \"one moment\" through the wooden door to the south.",
            signal.Text);
    }

    [Fact]
    public void Go_ObserverInArrivalRoom_SeesEntry()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        var bob = engine.World.GetObject("bob");

        // bob opens the door and walks from room_b into room_a (alice's room)
        engine.TurnManager.PerformAction(bob, TestWorlds.Find(engine, "bob", "open", "door_b"));
        engine.SignalBus.Drain("alice");
        var go = TestWorlds.Find(engine, "bob", "go", "door_b");
        Assert.True(engine.TurnManager.PerformAction(bob, go).Success);

        // alice sees the entry, named from the room_a side of the door (to the north)
        var signal = Assert.Single(engine.SignalBus.Drain("alice"));
        Assert.Equal(SignalSense.Visual, signal.Sense);
        Assert.Equal("Bob enters from the wooden door to the north.", signal.Text);
        Assert.Equal("room_a", signal.OriginRoomId);
    }

    [Fact]
    public void Go_ObserverInDepartureRoom_SeesExit()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        // carol watches from room_b, bob's departure room
        engine.World.CreateObject("carol", "room_b", "Carol");
        engine.World.AddModule("carol", "agent");
        var bob = engine.World.GetObject("bob");

        engine.TurnManager.PerformAction(bob, TestWorlds.Find(engine, "bob", "open", "door_b"));
        engine.SignalBus.Drain("carol");
        engine.SignalBus.Drain("alice"); // the door-open audio; not what we're asserting
        var go = TestWorlds.Find(engine, "bob", "go", "door_b");
        Assert.True(engine.TurnManager.PerformAction(bob, go).Success);

        // carol sees the exit through the room_b side of the door (to the south)
        var signal = Assert.Single(engine.SignalBus.Drain("carol"));
        Assert.Equal(SignalSense.Visual, signal.Sense);
        Assert.Equal("Bob exits through the wooden door to the south.", signal.Text);
        Assert.Equal("room_b", signal.OriginRoomId);

        // alice (arrival room) sees the entry in the same traversal
        var entry = Assert.Single(engine.SignalBus.Drain("alice"));
        Assert.Equal("Bob enters from the wooden door to the north.", entry.Text);
    }
}
