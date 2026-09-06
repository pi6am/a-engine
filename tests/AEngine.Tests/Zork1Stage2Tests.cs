using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.Scenarios;
using AEngine.Core.World;

namespace AEngine.Tests;

/// <summary>
/// Stage 2 — the maze (with its one-ways), the skeleton's curse, the
/// grating, the cyclops (both defeats), the thief's hideaway, and the
/// death/ghost/resurrection cycle.
/// </summary>
public class Zork1Stage2Tests
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

    private static AEngine.Cli.WalkthroughResult RunScript(
        GameEngine engine, params string[] lines) =>
        AEngine.Cli.Walkthrough.Run(engine, "player", lines);

    /// <summary>Hand the player a burning lamp (dark-room test fixture).</summary>
    private static void LightLamp(GameEngine engine)
    {
        var world = engine.World;
        world.MoveObject("lamp", "player");
        world.SetFieldOverride("lamp", "lightsource", "on", World.ToJson(true));
    }

    private static string FindScenarioFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scenarios", "zork1", name);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(name);
    }

    [Fact]
    public void Stage2Walkthrough_CasesTheMazeLoot()
    {
        var engine = NewEngine();
        var result = AEngine.Cli.Walkthrough.Run(engine, "player",
            File.ReadAllLines(FindScenarioFile("walkthrough-stage2.txt")));
        Assert.True(result.Success, result.Error);

        var world = engine.World;
        Assert.True(Conditions.Has(world, engine.ModuleRegistry,
            world.GetObject("thief"),
            result.Transcript.Any(t => t.Contains("death blow")) ? "dead" : "unconscious"));
        Assert.False(world.HasObject("cyclops")); // fled through the wall
        Assert.True(engine.ModuleRegistry.ResolveBool(
            world.GetObject("magic_state"), "flag", "value"));
        foreach (var id in new[] { "bag_of_coins", "egg", "canary", "chalice" })
            Assert.Equal("trophy_case", world.GetObject(id).Parent);
        Assert.Equal(110, Score.Of(world, engine.ModuleRegistry, world.GetObject("player")));
        Assert.Equal("living_room", world.RoomOf("player").Id);
    }

    [Fact]
    public void MazeDropsAreOneWay()
    {
        var engine = NewEngine();
        var world = engine.World;
        var player = world.GetObject("player");
        world.MoveObject("player", "maze_2");
        LightLamp(engine);

        var down = engine.ActionResolver.Resolve(player)
            .First(a => a.Verb == "go" && a.TargetId == "maze_2_down");
        Assert.True(engine.TurnManager.PerformAction(player, down).Success);
        Assert.Equal("maze_4", world.RoomOf("player").Id);
        // and there is no way back up from maze_4
        Assert.DoesNotContain(engine.ActionResolver.Resolve(player),
            a => a.Verb == "go" && a.Label.Contains("up", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DisturbingTheSkeleton_CursesYourValuables()
    {
        var engine = NewEngine();
        var world = engine.World;
        world.MoveObject("player", "maze_5");
        LightLamp(engine);
        world.MoveObject("bag_of_coins", "player"); // one treasure in hand

        var result = RunScript(engine, ["Move the skeleton"]);
        Assert.True(result.Success, result.Error);
        Assert.Contains(engine.SignalBus.Drain("player").Select(s => s.Text),
            t => t.Contains("casts a curse"));

        // the treasure is gone from your hands — it lies with the dead
        Assert.Equal("land_of_living_dead", world.RoomOf("bag_of_coins").Id);
    }

    [Fact]
    public void Grating_UnlocksFromBelow_WithTheSkeletonKey()
    {
        var engine = NewEngine();
        var world = engine.World;
        var player = world.GetObject("player");

        // above: the leaves conceal the grate — including the exit line
        world.MoveObject("player", "grating_clearing");
        var lookBefore = engine.TurnManager.Execute(player, "look", "player");
        Assert.DoesNotContain("down", lookBefore.Message);
        Assert.DoesNotContain(engine.ActionResolver.Resolve(player),
            a => a.TargetId == "gc_down");

        // moving them: Zork's terse pair, and the exit appears
        var moved = RunScript(engine, ["Move the pile of leaves"]);
        Assert.True(moved.Success, moved.Error);
        Assert.Equal("Done.", moved.Transcript[0]);
        Assert.Contains(engine.SignalBus.Drain("player").Select(s => s.Text),
            t => t == "In disturbing the pile of leaves, a grating is revealed.");
        Assert.Contains(engine.ActionResolver.Resolve(player), a => a.TargetId == "gc_down");
        var lookAfter = engine.TurnManager.Execute(player, "look", "player");
        Assert.Contains("down (steel grating, closed)", lookAfter.Message);

        // below: locked, and only the skeleton key opens it
        world.MoveObject("player", "grating_room");
        LightLamp(engine);
        var actions = engine.ActionResolver.Resolve(player);
        var unlock = actions.First(a => a.Verb == "unlock");
        Assert.Equal("gr_up", unlock.TargetId);
        var noKey = engine.TurnManager.PerformAction(player, unlock);
        Assert.False(noKey.Success); // no key held
        world.MoveObject("keys", "player");
        Assert.True(engine.TurnManager.PerformAction(player, unlock).Success);
        var open = engine.ActionResolver.Resolve(player).First(a => a.Verb == "open" && a.TargetId == "gr_up");
        Assert.True(engine.TurnManager.PerformAction(player, open).Success);

        // sunlight floods in, and the way up opens
        Assert.False(engine.ModuleRegistry.ResolveBool(
            world.GetObject("grating_room"), "room", "dark"));
        var up = engine.ActionResolver.Resolve(player).First(a => a.Verb == "go" && a.TargetId == "gr_up");
        Assert.True(engine.TurnManager.PerformAction(player, up).Success);
        Assert.Equal("grating_clearing", world.RoomOf("player").Id);

        // and the grate above is unlocked now too (shared state)
        Assert.True(RunScript(engine, ["Go down"]).Success);
        Assert.Equal("grating_room", world.RoomOf("player").Id);
    }

    [Fact]
    public void Cyclops_FightIsHopeless_TheNameIsNot()
    {
        var engine = NewEngine();
        var world = engine.World;
        var player = world.GetObject("player");
        world.MoveObject("player", "cyclops_room");
        LightLamp(engine);
        world.MoveObject("sword", "player");

        // swings always miss a strength-10000 defender
        var attack = engine.ActionResolver.Resolve(player)
            .First(a => a.Verb == "attack" && a.TargetId == "cyclops");
        for (var i = 0; i < 5; i++)
        {
            var swing = engine.TurnManager.PerformAction(player, attack);
            Assert.True(swing.Success);
            Assert.Contains("miss", swing.Message);
        }

        // the wrong name does nothing
        var wrong = RunScript(engine, ["Speak to the cyclops :: nobody"]);
        Assert.False(wrong.Success);
        Assert.True(world.HasObject("cyclops"));

        // the right name: he flees through the wall, everything opens
        var right = RunScript(engine, ["Speak to the cyclops :: odysseus"]);
        Assert.True(right.Success, right.Error);
        Assert.False(world.HasObject("cyclops"));
        Assert.True(engine.ModuleRegistry.ResolveBool(
            world.GetObject("magic_state"), "flag", "value"));
        Assert.True(engine.ModuleRegistry.ResolveBool(
            world.GetObject("cyclops_out"), "flag", "value"));
        // and the living room's nailed door opens onto the passage
        Assert.True(RunScript(engine, ["Go up", "Go down", "Go east", "Go east"]).Success);
        Assert.Equal("living_room", world.RoomOf("player").Id);
    }

    [Fact]
    public void Cyclops_CanBeFedToSleep()
    {
        var engine = NewEngine();
        var world = engine.World;
        var player = world.GetObject("player");
        world.MoveObject("player", "kitchen");

        // lunch and a full bottle of water, delivered
        Assert.True(RunScript(engine,
        [
            "Open the brown sack",
            "Take the hot pepper sandwich",
            "Take the glass bottle",
        ]).Success);
        world.MoveObject("player", "cyclops_room");
        LightLamp(engine);

        var give = engine.ActionResolver.Resolve(player)
            .First(a => a.Verb == "give" && a.TargetId == "cyclops");
        // only one giveable held item at a time: hand over the lunch first
        world.MoveObject("bottle", "kitchen_table");
        Assert.True(engine.TurnManager.PerformAction(player,
            give with { AuxTargetId = "lunch" }).Success);
        Assert.True(engine.ModuleRegistry.ResolveBool(
            world.GetObject("cyclops_hungry"), "flag", "value"));

        // then the bottle: he drinks and collapses
        world.MoveObject("bottle", "player");
        Assert.True(engine.TurnManager.PerformAction(player,
            give with { AuxTargetId = "bottle" }).Success);
        Assert.True(Conditions.Has(world, engine.ModuleRegistry,
            world.GetObject("cyclops"), "unconscious"));
        Assert.True(engine.ModuleRegistry.ResolveBool(
            world.GetObject("cyclops_out"), "flag", "value"));
        // the staircase is passable, but the east wall stayed solid
        Assert.True(RunScript(engine, ["Go up"]).Success);
        Assert.Equal("treasure_room", world.RoomOf("player").Id);
        Assert.False(engine.ModuleRegistry.ResolveBool(
            world.GetObject("magic_state"), "flag", "value"));
    }

    [Fact]
    public void Thief_GuardsTheChalice_AndOpensTheEgg()
    {
        var engine = NewEngine();
        var world = engine.World;
        var player = world.GetObject("player");
        world.MoveObject("player", "treasure_room");
        LightLamp(engine);

        // the chalice is guarded while the thief stands
        var take = engine.ActionResolver.Resolve(player)
            .First(a => a.Verb == "take" && a.TargetId == "chalice");
        var blocked = engine.TurnManager.PerformAction(player, take);
        Assert.False(blocked.Success);
        Assert.Equal("You'd be stabbed in the back first.", blocked.Message);

        // the egg, surrendered, is opened with care
        world.MoveObject("egg", "player");
        Assert.True(engine.TurnManager.PerformAction(player,
            engine.ActionResolver.Resolve(player)
                .First(a => a.Verb == "give" && a.TargetId == "thief" && a.AuxTargetId == "egg")).Success);
        Assert.Equal("thief", world.GetObject("egg").Parent);
        Assert.True(engine.ModuleRegistry.ResolveBool(
            world.GetObject("egg"), "openable", "open"));
        Assert.Equal("egg", world.GetObject("canary").Parent);
    }

    [Fact]
    public void Death_MakesAGhost_ResurrectionWalksItBack()
    {
        var engine = NewEngine();
        var world = engine.World;
        var player = world.GetObject("player");
        // the adventurer falls in the maze, clutching the egg
        world.MoveObject("player", "maze_5");
        LightLamp(engine);
        world.MoveObject("egg", "player");
        LightLamp(engine);
        var scoreBefore = Score.Of(world, engine.ModuleRegistry, player);

        Conditions.Attach(world, engine.ModuleRegistry, player, "cond_dead");
        var wait = engine.ActionResolver.Resolve(player).First(a => a.Verb == "wait");
        Assert.True(engine.TurnManager.PerformAction(player, wait).Success);

        // a ghost at the gates of Hell: lit, robbed, and unable to grasp
        Assert.Equal("entrance_to_hades", world.RoomOf("player").Id);
        Assert.True(engine.ModuleRegistry.ResolveBool(player, "agent", "alwaysLit"));
        Assert.Equal(1, engine.ModuleRegistry.ResolveInt(player, "scorecard", "deaths"));
        Assert.Equal(scoreBefore - 10, Score.Of(world, engine.ModuleRegistry, player));
        // scattered into the maze's dark rooms — somewhere the ghost is not
        Assert.NotEqual("player", world.GetObject("egg").Parent);
        Assert.Equal("Maze", world.RoomOf("egg").Name);
        Assert.Equal("living_room", world.RoomOf("lamp").Id); // the lamp comes home
        Assert.DoesNotContain(engine.ActionResolver.Resolve(player),
            a => a.Verb == "take");

        // prayer at the altar (reached by whatever means) restores all
        world.MoveObject("player", "south_temple");
        Assert.True(RunScript(engine, ["Pray at the altar"]).Success);
        Assert.False(Conditions.Has(world, engine.ModuleRegistry, player, "dead"));
        Assert.False(engine.ModuleRegistry.ResolveBool(player, "agent", "alwaysLit"));
        Assert.Equal("forest_1", world.RoomOf("player").Id);
        // flesh again: the world can be picked up once more
        world.MoveObject("egg", "forest_1");
        Assert.Contains(engine.ActionResolver.Resolve(world.GetObject("player")),
            a => a.Verb == "take");
    }
}
