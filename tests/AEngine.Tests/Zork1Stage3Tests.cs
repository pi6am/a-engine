using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.Scenarios;
using AEngine.Core.World;

namespace AEngine.Tests;

/// <summary>
/// Stage 3 — the underworld systems: the dam and its reservoir cycle,
/// the loud room, the mirror rooms, dome rope and torch, the temple
/// and Egypt, the exorcism, the coal mine and its machine, and the
/// Frigid River by boat.
/// </summary>
public class Zork1Stage3Tests
{
    private static GameEngine NewEngine(int seed = Zork1Stage1Tests.Seed)
    {
        var engine = GameEngine.CreateWithBuiltinHandlers();
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scenarios", "zork1");
            if (Directory.Exists(Path.Combine(candidate, "world")))
            {
                ScenarioLoader.LoadFrom(engine, candidate);
                engine.Random = new Random(seed);
                return engine;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate scenarios/zork1.");
    }

    private static AEngine.Cli.WalkthroughResult RunScript(GameEngine engine,
        params string[] lines) =>
        AEngine.Cli.Walkthrough.Run(engine, "player", lines);

    private static void LightLamp(GameEngine engine)
    {
        var world = engine.World;
        world.MoveObject("lamp", "player");
        world.SetFieldOverride("lamp", "lightsource", "on", World.ToJson(true));
    }

    private static bool Flag(GameEngine engine, string id) =>
        engine.ModuleRegistry.ResolveBool(engine.World.GetObject(id), "flag", "value");

    private static void Wait(GameEngine engine, int turns)
    {
        var player = engine.World.GetObject("player");
        var wait = engine.ActionResolver.Resolve(player).First(a => a.Verb == "wait");
        for (var i = 0; i < turns; i++)
            engine.TurnManager.PerformAction(player, wait);
    }

    [Fact]
    public void Dam_DrainsOnBoltTurn_AndRefills()
    {
        var engine = NewEngine();
        var world = engine.World;
        var player = world.GetObject("player");
        world.MoveObject("player", "dam_room");
        LightLamp(engine);
        world.MoveObject("wrench", "player");

        // the bolt refuses without the glow, then turns with the wrench
        var turn = engine.ActionResolver.Resolve(player).First(a => a.Verb == "turn");
        Assert.Equal("bolt", turn.TargetId);
        var noGlow = engine.TurnManager.PerformAction(player, turn);
        Assert.False(noGlow.Success);
        Assert.Contains("best effort", noGlow.Message);
        world.MoveObject("player", "maintenance_room");
        Assert.True(RunScript(engine, ["Press the yellow button"]).Success);
        world.MoveObject("player", "dam_room");
        var withGlow = engine.TurnManager.PerformAction(player, turn);
        Assert.True(withGlow.Success, withGlow.Message);
        Assert.True(Flag(engine, "gates_open"));

        // eight turns later the reservoir has drained
        Wait(engine, 8);
        Assert.True(Flag(engine, "low_tide"));
        Assert.False(engine.ModuleRegistry.ResolveBool(
            world.GetObject("trunk"), "hideable", "concealed"));

        // and the drained reservoir may be crossed on foot
        world.MoveObject("player", "reservoir_south");
        var north = engine.ActionResolver.Resolve(player)
            .First(a => a.Verb == "go" && a.TargetId == "rs_north");
        Assert.True(engine.TurnManager.PerformAction(player, north).Success);

        // closing the gate refills it
        world.MoveObject("player", "dam_room");
        var again = engine.ActionResolver.Resolve(player).First(a => a.Verb == "turn");
        engine.TurnManager.PerformAction(player, again);
        Assert.False(Flag(engine, "gates_open"));
        Wait(engine, 8);
        Assert.False(Flag(engine, "low_tide"));
        Assert.True(engine.ModuleRegistry.ResolveBool(
            world.GetObject("trunk"), "hideable", "concealed"));
    }

    [Fact]
    public void LoudRoom_QuietsByEcho_OrByDrain()
    {
        var engine = NewEngine();
        var world = engine.World;
        var player = world.GetObject("player");
        world.MoveObject("player", "loud_room");
        LightLamp(engine);

        // the bar is beyond reach while the room roars
        var take = engine.ActionResolver.Resolve(player)
            .First(a => a.Verb == "take" && a.TargetId == "platinum_bar");
        var blocked = engine.TurnManager.PerformAction(player, take);
        Assert.False(blocked.Success);
        Assert.Contains("cannot even hear", blocked.Message);

        // the wrong word is lost in the noise; the right one quiets it
        var wrong = RunScript(engine, ["Say: hello"]);
        Assert.True(wrong.Success, wrong.Error);
        Assert.False(Flag(engine, "loud_quiet"));
        var right = RunScript(engine, ["Say: echo"]);
        Assert.True(right.Success, right.Error);
        Assert.Contains(right.Transcript, t => t.Contains("acoustics"));
        Assert.True(Flag(engine, "loud_quiet"));
        Assert.True(engine.TurnManager.PerformAction(player, take).Success);
    }

    [Fact]
    public void Leak_Floods_UnlessPlugged()
    {
        var engine = NewEngine();
        var world = engine.World;
        var player = world.GetObject("player");
        world.MoveObject("player", "maintenance_room");
        LightLamp(engine);

        Assert.True(RunScript(engine, ["Press the blue button"]).Success);
        Wait(engine, 2);
        Assert.True(3 <= engine.ModuleRegistry.ResolveInt(
            world.GetObject("leak_level"), "flag", "count"));

        // putty plugs it
        world.MoveObject("putty", "player");
        Assert.True(RunScript(engine, ["Open the tube", "Patch the leak"]).Success);
        Assert.Equal(0, engine.ModuleRegistry.ResolveInt(
            world.GetObject("leak_level"), "flag", "count"));
    }

    [Fact]
    public void MirrorRub_SwapsTravelers()
    {
        var engine = NewEngine();
        var world = engine.World;
        var player = world.GetObject("player");
        world.MoveObject("player", "mirror_room_1");
        LightLamp(engine);

        Assert.True(RunScript(engine, ["Rub the mirror"]).Success);
        Assert.Equal("mirror_room_2", world.RoomOf("player").Id);
        Assert.True(RunScript(engine, ["Rub the mirror"]).Success);
        Assert.Equal("mirror_room_1", world.RoomOf("player").Id);
    }

    [Fact]
    public void DomeRope_OpensTheDescent()
    {
        var engine = NewEngine();
        var world = engine.World;
        var player = world.GetObject("player");
        world.MoveObject("player", "dome_room");
        LightLamp(engine);

        var down = engine.ActionResolver.Resolve(player)
            .First(a => a.Verb == "go" && a.TargetId == "dome_down");
        var leap = engine.TurnManager.PerformAction(player, down);
        Assert.False(leap.Success);
        Assert.Contains("fracturing many bones", leap.Message);

        world.MoveObject("rope", "player");
        Assert.True(RunScript(engine, ["Tie the rope to the wooden railing"]).Success);
        Assert.True(Flag(engine, "dome_tied"));
        Assert.True(engine.TurnManager.PerformAction(player, down).Success);
        Assert.Equal("torch_room", world.RoomOf("player").Id);

        // the torch is a treasure that never goes out
        Assert.True(RunScript(engine, ["Take the torch"]).Success);
        Assert.True(engine.ModuleRegistry.ResolveBool(
            world.GetObject("torch"), "lightsource", "on"));
    }

    [Fact]
    public void Coffin_TooBigForTheTempleHole()
    {
        var engine = NewEngine();
        var world = engine.World;
        var player = world.GetObject("player");
        world.MoveObject("player", "egypt_room");
        LightLamp(engine);
        world.MoveObject("coffin", "player");

        world.MoveObject("player", "south_temple");
        var down = engine.ActionResolver.Resolve(player)
            .First(a => a.Verb == "go" && a.TargetId == "st_down");
        var squeeze = engine.TurnManager.PerformAction(player, down);
        Assert.False(squeeze.Success);
        Assert.Contains("coffin", squeeze.Message);

        // without it, the hole is passable (and pray still works nearby)
        world.MoveObject("coffin", "egypt_room");
        Assert.True(engine.TurnManager.PerformAction(player, down).Success);
        Assert.Equal("tiny_cave", world.RoomOf("player").Id);
    }

    [Fact]
    public void Exorcism_BellCandlesBook_OpenTheGate()
    {
        var engine = NewEngine();
        var world = engine.World;
        var player = world.GetObject("player");
        LightLamp(engine);
        foreach (var id in new[] { "bell", "candles", "black_book", "match" })
            world.MoveObject(id, "player");
        world.MoveObject("player", "entrance_to_hades");

        // the gate bars the way
        var south = engine.ActionResolver.Resolve(player)
            .First(a => a.Verb == "go" && a.TargetId == "hades_gate_south");
        Assert.False(engine.TurnManager.PerformAction(player, south).Success);

        // the ceremony: bell, relit candles, the prayer
        Assert.True(RunScript(engine,
        [
            "Ring the brass bell",
            "Take the pair of candles",
            "Light the matchbook",
            "Light the pair of candles",
            "Read the black book",
        ]).Success);
        Assert.True(Flag(engine, "lld_state"));
        Assert.True(engine.TurnManager.PerformAction(player, south).Success);
        Assert.Equal("land_of_living_dead", world.RoomOf("player").Id);
        Assert.True(RunScript(engine, ["Take the crystal skull"]).Success);
    }

    [Fact]
    public void Bat_AbductsWithoutGarlic_RespectsItWith()
    {
        var engine = NewEngine();
        var world = engine.World;
        var player = world.GetObject("player");
        world.MoveObject("player", "squeeky_room");
        LightLamp(engine);

        // garlicless: swept away somewhere in the mine
        var north = engine.ActionResolver.Resolve(player)
            .First(a => a.Verb == "go" && a.TargetId == "sq_north");
        Assert.True(engine.TurnManager.PerformAction(player, north).Success);
        Assert.NotEqual("bat_room", world.RoomOf("player").Id);

        // with garlic: the jade is there for the taking
        world.MoveObject("player", "squeeky_room");
        world.MoveObject("garlic", "player");
        Assert.True(engine.TurnManager.PerformAction(player, north).Success);
        Assert.Equal("bat_room", world.RoomOf("player").Id);
        Assert.True(RunScript(engine, ["Take the jade figurine"]).Success);
    }

    [Fact]
    public void Basket_FerriesAcrossTheShaft()
    {
        var engine = NewEngine();
        var world = engine.World;
        var player = world.GetObject("player");
        world.MoveObject("player", "shaft_room");
        LightLamp(engine);
        world.MoveObject("coal", "raised_basket");

        // send the coal down
        Assert.True(RunScript(engine, ["Raise the basket"]).Success);
        Assert.Equal("lower_shaft", world.RoomOf("raised_basket").Id);
        Assert.Equal("shaft_room", world.RoomOf("lowered_basket").Id);

        // and raising from the lower shaft returns the pair
        world.MoveObject("player", "lower_shaft");
        var raiseBack = engine.ActionResolver.Resolve(player)
            .First(a => a.Verb == "raise");
        Assert.True(engine.TurnManager.PerformAction(player, raiseBack).Success);
        Assert.Equal("shaft_room", world.RoomOf("raised_basket").Id);
    }

    [Fact]
    public void TimberCrawl_RequiresEmptyHands()
    {
        var engine = NewEngine();
        var world = engine.World;
        var player = world.GetObject("player");
        world.MoveObject("player", "timber_room");
        LightLamp(engine);

        var west = engine.ActionResolver.Resolve(player)
            .First(a => a.Verb == "go" && a.TargetId == "tm_west");
        var loaded = engine.TurnManager.PerformAction(player, west);
        Assert.False(loaded.Success);
        Assert.Contains("that load", loaded.Message);

        // the lamp itself is too much (weight 5 over a budget of 4)
        world.MoveObject("lamp", "timber_room");
        Assert.True(engine.TurnManager.PerformAction(player, west).Success);
        Assert.Equal("lower_shaft", world.RoomOf("player").Id);
    }

    [Fact]
    public void Machine_TurnsCoalToDiamond()
    {
        var engine = NewEngine();
        var world = engine.World;
        var player = world.GetObject("player");
        world.MoveObject("player", "machine_room");
        LightLamp(engine);
        world.MoveObject("coal", "machine");
        world.MoveObject("screwdriver", "player");

        var turn = engine.ActionResolver.Resolve(player)
            .First(a => a.Verb == "turn" && a.TargetId == "machine_switch");
        var run = engine.TurnManager.PerformAction(player, turn);
        Assert.True(run.Success, run.Message);
        Assert.False(world.HasObject("coal"));
        Assert.Equal("machine", world.GetObject("diamond").Parent);
    }

    [Fact]
    public void GasRoom_KillsOpenFlames_ButNotTheElectricLamp()
    {
        var engine = NewEngine();
        var world = engine.World;
        var player = world.GetObject("player");
        world.MoveObject("player", "gas_room");
        LightLamp(engine);

        // the battery lantern is not a flame: it walks the gas room safely
        var take = engine.ActionResolver.Resolve(player)
            .First(a => a.Verb == "take" && a.TargetId == "bracelet");
        Assert.True(engine.TurnManager.PerformAction(player, take).Success);
        Assert.False(Conditions.Has(world, engine.ModuleRegistry, player, "dead"));

        // the torch is: carrying it in is the end
        var engine2 = NewEngine();
        var world2 = engine2.World;
        var player2 = world2.GetObject("player");
        world2.MoveObject("player", "gas_room");
        world2.MoveObject("torch", "player");
        var take2 = engine2.ActionResolver.Resolve(player2)
            .First(a => a.Verb == "take" && a.TargetId == "bracelet");
        engine2.TurnManager.PerformAction(player2, take2);
        Assert.True(Conditions.Has(world2, engine2.ModuleRegistry, player2, "dead"));
    }

    [Fact]
    public void Boat_InflatesLaunchesLands()
    {
        var engine = NewEngine();
        var world = engine.World;
        var player = world.GetObject("player");
        world.MoveObject("player", "dam_base");
        LightLamp(engine);
        world.MoveObject("pump", "player");
        world.MoveObject("pile_of_plastic", "dam_base");

        Assert.True(RunScript(engine,
        [
            "Inflate the pile of plastic",
            "Board the magic boat",
            "Launch the magic boat",
        ]).Success);
        Assert.Equal("river_1", world.RoomOf("pile_of_plastic").Id);

        // drift carries the boat downstream
        Wait(engine, 5);
        Assert.Equal("river_2", world.RoomOf("pile_of_plastic").Id);

        // land at the white cliffs beach
        world.MoveObject("pile_of_plastic", "river_3");
        world.MoveObject("player", "river_3");
        Assert.True(RunScript(engine, ["Land the magic boat"]).Success);
        Assert.Equal("white_cliffs_north", world.RoomOf("pile_of_plastic").Id);
    }

    [Fact]
    public void Rainbow_SolidifiesUnderTheSceptre()
    {
        var engine = NewEngine();
        var world = engine.World;
        var player = world.GetObject("player");
        world.MoveObject("player", "aragain_falls");
        LightLamp(engine);
        world.MoveObject("sceptre", "player");

        var west = engine.ActionResolver.Resolve(player)
            .First(a => a.Verb == "go" && a.TargetId == "af_west");
        Assert.False(engine.TurnManager.PerformAction(player, west).Success);

        Assert.True(RunScript(engine, ["Wave Ramses\x27 sceptre"]).Success);
        Assert.True(Flag(engine, "rainbow_solid"));
        Assert.True(engine.TurnManager.PerformAction(player, west).Success);
        Assert.Equal("on_rainbow", world.RoomOf("player").Id);

        Assert.True(RunScript(engine, ["Go west"]).Success);
        Assert.False(engine.ModuleRegistry.ResolveBool(
            world.GetObject("pot_of_gold"), "hideable", "concealed"));
        Assert.True(RunScript(engine, ["Take the pot of gold"]).Success);
    }

    [Fact]
    public void Scarab_UnearthsOnTheThirdDig()
    {
        var engine = NewEngine();
        var world = engine.World;
        var player = world.GetObject("player");
        world.MoveObject("player", "sandy_cave");
        LightLamp(engine);

        Assert.True(RunScript(engine,
        [
            "Dig the sand", "Dig the sand", "Dig the sand",
            "Take the beautiful jeweled scarab",
        ]).Success);
        Assert.Equal("player", world.GetObject("scarab").Parent);
    }

    [Fact]
    public void Canary_SingsTheSongbirdsAnswer()
    {
        var engine = NewEngine();
        var world = engine.World;
        var player = world.GetObject("player");
        world.MoveObject("player", "forest_1");
        world.MoveObject("canary", "player");

        Assert.True(RunScript(engine,
        [
            "Wind the golden clockwork canary",
            "Take the beautiful brass bauble",
        ]).Success);
        Assert.Equal("player", world.GetObject("bauble").Parent);
    }
}
