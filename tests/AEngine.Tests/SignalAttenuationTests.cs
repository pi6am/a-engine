using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.Signals;
using AEngine.Core.World;
using CoreWorld = AEngine.Core.World.World;

namespace AEngine.Tests;

/// <summary>
/// Signal strength and attenuation: strength is a perceptual log scale
/// (attenuation adds), a signal is imperceptible at negative remaining
/// strength, crossing a portal costs its attenuation plus the average of
/// the two rooms', and room attenuation never applies to same-room
/// listeners. Defaults (strength 1, portal 1, room 0) reproduce the
/// classic one-room-away propagation.
/// </summary>
public class SignalAttenuationTests
{
    private static GameEngine NewThreeRoomEngine()
    {
        var engine = TestWorlds.NewTwoRoomEngine(); // room_a —closed door— room_b
        var world = engine.World;
        world.CreateObject("room_c", CoreWorld.RootId, "Room C");
        world.AddModule("room_c", "room");
        world.CreateObject("door2_state", CoreWorld.RootId, "door 2 state");
        world.AddModule("door2_state", "doorstate");
        AddSide(world, "door_bc_side", "room_b", "east", "room_c", "door2_state");
        AddSide(world, "door_cb_side", "room_c", "west", "room_b", "door2_state");
        world.CreateObject("carol", "room_c", "Carol");
        world.AddModule("carol", "agent");
        world.CreateObject("dave", "room_a", "Dave");
        world.AddModule("dave", "agent");
        return engine;
    }

    private static void AddSide(
        CoreWorld world, string id, string roomId, string direction, string to, string stateRef)
    {
        world.CreateObject(id, roomId, "stone door");
        world.AddModule(id, "portal");
        world.SetFieldOverride(id, "portal", "stateRef", CoreWorld.ToJson(stateRef));
        world.SetFieldOverride(id, "portal", "direction", CoreWorld.ToJson(direction));
        world.SetFieldOverride(id, "portal", "to", CoreWorld.ToJson(to));
    }

    /// <summary>A flavor verb whose signal carries the given strength and sense.</summary>
    private static void LoadEmitter(GameEngine engine, int strength, string sense = "audible")
    {
        engine.ModuleRegistry.LoadJson($$"""
        [
          { "id": "emitter", "name": "Emitter", "fields": [],
            "affordances": [
              { "verb": "boom", "handler": "basic",
                "signals": [ { "sense": "{{sense}}", "priority": 9, "strength": {{strength}},
                               "text": "{agent} booms" } ] }
            ] }
        ]
        """);
        var world = engine.World;
        if (!world.HasObject("drum"))
        {
            world.CreateObject("drum", "room_a", "drum");
            world.AddModule("drum", "emitter");
        }
    }

    private static void Boom(GameEngine engine)
    {
        var result = engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "boom", "drum"));
        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public void Defaults_SignalCarriesExactlyOneRoom()
    {
        var engine = NewThreeRoomEngine();
        LoadEmitter(engine, strength: 1); // the default
        Boom(engine);

        Assert.NotEmpty(engine.SignalBus.Drain("bob")); // one hop: remaining 0
        Assert.Empty(engine.SignalBus.Drain("carol")); // two hops: -1
    }

    [Fact]
    public void LoudSignal_CarriesMultipleRooms_WithRemainingStrength()
    {
        var engine = NewThreeRoomEngine();
        LoadEmitter(engine, strength: 3);
        Boom(engine);

        var toBob = Assert.Single(engine.SignalBus.Drain("bob"));
        Assert.Equal(2, toBob.Strength);
        Assert.True(toBob.ThroughPortal);
        Assert.Contains("through the wooden door", toBob.Text);

        var toCarol = Assert.Single(engine.SignalBus.Drain("carol"));
        Assert.Equal(1, toCarol.Strength);
        // the suffix names the door as seen from carol's room
        Assert.Contains("through the stone door to the west.", toCarol.Text);

        // same-room listeners get full strength, no portal framing
        var toDave = Assert.Single(engine.SignalBus.Drain("dave"));
        Assert.Equal(3, toDave.Strength);
        Assert.False(toDave.ThroughPortal);
    }

    [Fact]
    public void PortalAttenuation_BlocksWeakSignals()
    {
        var engine = NewThreeRoomEngine();
        // an unusually heavy door: crossing costs 2 instead of 1
        engine.World.SetFieldOverride("door_b", "portal", "attenuateAudio",
            CoreWorld.ToJson(2));
        engine.World.SetFieldOverride("door_a", "portal", "attenuateAudio",
            CoreWorld.ToJson(2));
        LoadEmitter(engine, strength: 1);
        Boom(engine);
        Assert.Empty(engine.SignalBus.Drain("bob")); // 1 - 2 < 0

        LoadEmitter(engine, strength: 2);
        Boom(engine);
        var toBob = Assert.Single(engine.SignalBus.Drain("bob")); // exactly 0
        Assert.Equal(0, toBob.Strength);
    }

    [Fact]
    public void RoomAttenuation_AveragesAcrossTheCrossing()
    {
        var engine = NewThreeRoomEngine();
        // a thick room between the two doors: each crossing into or out of
        // it costs 1 + (0 + 2)/2 = 2
        engine.World.SetFieldOverride("room_b", "room", "attenuateAudio",
            CoreWorld.ToJson(2));
        LoadEmitter(engine, strength: 1);
        Boom(engine);
        Assert.Empty(engine.SignalBus.Drain("bob")); // 1 - 2 < 0

        LoadEmitter(engine, strength: 2);
        Boom(engine);
        Assert.Equal(0, Assert.Single(engine.SignalBus.Drain("bob")).Strength);
        Assert.Empty(engine.SignalBus.Drain("carol")); // another 2 to go
    }

    [Fact]
    public void SameRoomListeners_NeverPayRoomAttenuation()
    {
        var engine = NewThreeRoomEngine();
        engine.World.SetFieldOverride("room_a", "room", "attenuateAudio",
            CoreWorld.ToJson(5));
        LoadEmitter(engine, strength: 1);
        Boom(engine);

        Assert.NotEmpty(engine.SignalBus.Drain("dave")); // same room: full strength
        Assert.Empty(engine.SignalBus.Drain("bob")); // 1 - (1 + 5/2) < 0
    }

    [Fact]
    public void TransmitGates_BlockRegardlessOfStrength()
    {
        var engine = NewThreeRoomEngine();
        // a sealed door: no light passes, however bright
        engine.World.SetFieldOverride("door_a", "portal", "transmitVisual",
            CoreWorld.ToJson("never"));
        engine.World.SetFieldOverride("door_b", "portal", "transmitVisual",
            CoreWorld.ToJson("never"));
        LoadEmitter(engine, strength: 9, sense: "visual");
        Boom(engine);

        Assert.NotEmpty(engine.SignalBus.Drain("dave")); // same room
        Assert.Empty(engine.SignalBus.Drain("bob")); // hard gate
    }

    [Fact]
    public void PortalActions_ManifestBothSides_AtFullStrength()
    {
        var engine = NewThreeRoomEngine();
        // even a sound-deadening door doesn't muffle events ON the door:
        // the action manifests in both rooms at cost 0
        engine.World.SetFieldOverride("door_a", "portal", "attenuateAudio",
            CoreWorld.ToJson(5));
        engine.World.SetFieldOverride("door_b", "portal", "attenuateAudio",
            CoreWorld.ToJson(5));

        engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"),
            TestWorlds.Find(engine, "alice", "open", "door_a"));

        Assert.Contains(engine.SignalBus.Drain("bob"),
            s => s.Text.Contains("opens the wooden door"));
    }
}
