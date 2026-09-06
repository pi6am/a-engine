using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.Scenarios;
using AEngine.Core.World;

namespace AEngine.Tests;

/// <summary>
/// Stage 1 — the White House slice, played for points: the egg, the
/// troll (wound-level combat through the gated passages), the gallery
/// painting, the chimney's load rule, and the trap door's slam.
/// </summary>
public class Zork1Stage1Tests
{
    internal const int Seed = 42;

    internal static GameEngine NewEngine(int seed = Seed)
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

    internal static AEngine.Cli.WalkthroughResult RunStage1(GameEngine engine) =>
        AEngine.Cli.Walkthrough.Run(engine, "player",
            File.ReadAllLines(FindScenarioFile("walkthrough-stage1.txt")));

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

    private static AEngine.Cli.WalkthroughResult RunScript(
        GameEngine engine, params string[] lines) =>
        AEngine.Cli.Walkthrough.Run(engine, "player", lines);

    [Fact]
    public void Stage1Walkthrough_WinsTheSliceWithSixtyPoints()
    {
        var engine = NewEngine();
        var result = RunStage1(engine);
        Assert.True(result.Success, result.Error);

        var world = engine.World;
        var player = world.GetObject("player");
        // home, with the slice's treasures cased
        Assert.Equal("living_room", world.RoomOf("player").Id);
        Assert.True(AEngine.Core.Actions.Conditions.Has(
            world, engine.ModuleRegistry, world.GetObject("troll"),
            result.Transcript.Any(t => t.Contains("death blow")) ? "dead" : "unconscious"));
        // treasures cased: the egg and the painting live in the trophy case
        Assert.Equal("trophy_case", world.GetObject("egg").Parent);
        Assert.Equal("trophy_case", world.GetObject("painting").Parent);
        // 10 kitchen + 25 cellar + 5 EW passage + 5 egg + 4 painting,
        // plus 5 + 6 deposit points while they sit in the case
        Assert.Equal(60, Score.Of(world, engine.ModuleRegistry, player));
        // the axe fell with its wielder and lies in the troll room
        Assert.Equal("troll_room", world.RoomOf("axe").Id);
    }

    [Fact]
    public void TrapDoor_SlamsShutBehindTheFirstDescent_AndReopensFromAbove()
    {
        var engine = NewEngine();
        var world = engine.World;
        var result = RunScript(engine,
        [
            "Go north", "Go southeast", "Open the kitchen window", "Go west", "Go west",
            "Take the brass lantern",
            "Move the large oriental rug",
        ]);
        // the rug announces what it revealed, in the original's words
        Assert.True(result.Success, result.Error);
        Assert.Equal(
            "With a great effort, the rug is moved to one side of the room, revealing the dusty cover of a closed trap door.",
            result.Transcript[^1]);

        var down = RunScript(engine, ["Open the trap door", "Go down", "Turn on the brass lantern"]);
        Assert.True(down.Success, down.Error);
        // the slam is a private sensation, delivered with the arrival
        Assert.Contains(down.Transcript, t => t.Contains("crashes shut"));
        // shut and barred: the cellar's up side refuses
        Assert.False(engine.ModuleRegistry.ResolveBool(
            world.GetObject("trapdoor_state"), "doorstate", "open"));
        // from below the door never opens — it is barred from above
        var fromBelow = RunScript(engine, ["Open the trap door"]);
        Assert.False(fromBelow.Success);
        Assert.Contains("locked from above", fromBelow.Error);
        // and going up through the closed door is refused too
        var up = RunScript(engine, ["Go up"]);
        Assert.False(up.Success);
        Assert.Contains("closed", up.Error);
        // reopening from the living room works (climb the chimney, reopen, descend)
        var back = RunScript(engine,
        [
            "Go south", "Go east", "Go north",
            "Go up", "Go west", "Open the trap door",
        ]);
        Assert.True(back.Success, back.Error);
        Assert.True(engine.ModuleRegistry.ResolveBool(
            world.GetObject("trapdoor_state"), "doorstate", "open"));
    }

    [Fact]
    public void TrapDoorSlam_OnlyForAnOpenDoor()
    {
        var engine = NewEngine();
        var world = engine.World;
        var down = RunScript(engine,
        [
            "Go north", "Go southeast", "Open the kitchen window", "Go west", "Go west",
            "Take the brass lantern",
            "Move the large oriental rug", "Open the trap door", "Go down",
            "Turn on the brass lantern",
        ]);
        Assert.True(down.Success, down.Error);
        Assert.Contains(down.Transcript, t => t.Contains("crashes shut"));

        // leaving and re-entering the cellar through an already-barred
        // door stays quiet — the slam only happens while it is open
        var revisit = RunScript(engine, ["Go north", "Go south"]);
        Assert.True(revisit.Success, revisit.Error);
        Assert.DoesNotContain(revisit.Transcript, t => t.Contains("crashes shut"));

        // the slam is spent now: reopen from above and descend again
        // WITHOUT re-arming, and the door stays open behind you
        Assert.False(engine.ModuleRegistry.ResolveBool(
            world.GetObject("trapdoor_armed"), "flag", "value"));
        // (set up: chimney out — which re-arms — then disarm by hand to
        // observe the spent state; the chimney test below covers the arm)
        var climbOut = RunScript(engine,
        [
            "Go south", "Go east", "Go north", "Go up",
        ]);
        Assert.True(climbOut.Success, climbOut.Error);
        // the climb passed over the closed door: the slam re-armed
        Assert.True(engine.ModuleRegistry.ResolveBool(
            world.GetObject("trapdoor_armed"), "flag", "value"));
        world.SetFieldOverride("trapdoor_armed", "flag", "value", World.ToJson(false));
        var quiet = RunScript(engine,
        [
            "Go west", "Open the trap door", "Go down",
        ]);
        Assert.True(quiet.Success, quiet.Error);
        Assert.DoesNotContain(quiet.Transcript, t => t.Contains("crashes shut"));
        Assert.True(engine.ModuleRegistry.ResolveBool(
            world.GetObject("trapdoor_state"), "doorstate", "open"));

        // but with the chimney's re-arm in place, the same descent slams
        var engine2 = NewEngine();
        var world2 = engine2.World;
        var reopen = RunScript(engine2,
        [
            "Go north", "Go southeast", "Open the kitchen window", "Go west", "Go west",
            "Take the brass lantern",
            "Move the large oriental rug", "Open the trap door", "Go down",
            "Turn on the brass lantern",
            "Go south", "Go east", "Go north", "Go up", "Go west",
            "Open the trap door", "Go down",
        ]);
        Assert.True(reopen.Success, reopen.Error);
        // first descent slams, the climb re-arms, the second descent slams
        Assert.Equal(2, reopen.Transcript.Count(t => t.Contains("crashes shut")));

        // the re-arm belongs to the CLIMB, not the kitchen: teleporting
        // there (debug console, future spells) leaves the slam spent
        var engine3 = NewEngine();
        var world3 = engine3.World;
        var teleported = RunScript(engine3,
        [
            "Go north", "Go southeast", "Open the kitchen window", "Go west", "Go west",
            "Take the brass lantern",
            "Move the large oriental rug", "Open the trap door", "Go down",
            "Turn on the brass lantern",
        ]);
        Assert.True(teleported.Success, teleported.Error);
        world3.MoveObject("player", "kitchen"); // the debug-teleport shortcut
        var afterTeleport = RunScript(engine3,
        [
            "Go west", "Open the trap door", "Go down",
        ]);
        Assert.True(afterTeleport.Success, afterTeleport.Error);
        Assert.DoesNotContain(afterTeleport.Transcript, t => t.Contains("crashes shut"));
        // the door stays open behind the unslammed descent
        Assert.True(engine3.ModuleRegistry.ResolveBool(
            world3.GetObject("trapdoor_state"), "doorstate", "open"));
    }

    [Fact]
    public void TrollGate_BlocksThePassage_UntilHeIsOut()
    {
        var engine = NewEngine();
        var world = engine.World;
        // stand the player in the troll room with the sword, troll up
        world.MoveObject("player", "troll_room");
        world.MoveObject("sword", "player");

        var east = engine.ActionResolver.Resolve(world.GetObject("player"))
            .First(a => a.Verb == "go" && a.TargetId == "troll_east");
        var blocked = engine.TurnManager.PerformAction(world.GetObject("player"), east);
        Assert.False(blocked.Success);
        Assert.Equal("The troll fends you off with a menacing gesture.", blocked.Message);

        // knocked out: the passage opens
        AEngine.Core.Actions.Conditions.Attach(
            world, engine.ModuleRegistry, world.GetObject("troll"), "cond_unconscious");
        world.SetFieldOverride("troll", "duelist", "out", World.ToJson(true));
        Automations.Advance(engine, 1);
        Assert.True(engine.ModuleRegistry.ResolveBool(
            world.GetObject("troll_state"), "flag", "value"));

        // and he can wake: set his countdown ticking, pass some turns
        world.SetFieldOverride("troll", "duelist", "wakeIn", World.ToJson(2));
        var wait = engine.ActionResolver.Resolve(world.GetObject("player"))
            .First(a => a.Verb == "wait");
        engine.TurnManager.PerformAction(world.GetObject("player"), wait);
        engine.TurnManager.PerformAction(world.GetObject("player"), wait);
        Assert.False(AEngine.Core.Actions.Conditions.Has(
            world, engine.ModuleRegistry, world.GetObject("troll"), "unconscious"));
        Assert.False(engine.ModuleRegistry.ResolveBool(
            world.GetObject("troll_state"), "flag", "value"));
    }

    [Fact]
    public void Chimney_AllowsTheLampAndOneMoreThing()
    {
        var engine = NewEngine();
        var world = engine.World;
        world.MoveObject("player", "studio");
        world.MoveObject("lamp", "player");
        world.SetFieldOverride("lamp", "lightsource", "on", World.ToJson(true));
        world.MoveObject("sword", "player");
        world.MoveObject("painting", "player");

        // sword AND painting besides the lamp: too much (lamp plus one only)
        var climb = engine.ActionResolver.Resolve(world.GetObject("player"))
            .First(a => a.Verb == "go" && a.TargetId == "chimney_up");
        var tooMuch = engine.TurnManager.PerformAction(world.GetObject("player"), climb);
        Assert.False(tooMuch.Success);
        Assert.Contains("chimney", tooMuch.Message);

        // the lamp plus exactly one thing climbs
        world.MoveObject("painting", "studio");
        var climbs = engine.TurnManager.PerformAction(world.GetObject("player"), climb);
        Assert.True(climbs.Success, climbs.Message);
        Assert.Equal("kitchen", world.RoomOf("player").Id);

        // and the lamp alone, of course
        world.MoveObject("player", "studio");
        world.MoveObject("sword", "studio");
        var bare = engine.TurnManager.PerformAction(world.GetObject("player"), climb);
        Assert.True(bare.Success, bare.Message);
    }

    [Fact]
    public void SwordGlows_NearTheTroll()
    {
        var engine = NewEngine();
        var world = engine.World;
        world.MoveObject("sword", "player");
        world.MoveObject("player", "cellar");

        // adjacent: faint
        var look = engine.ActionResolver.Resolve(world.GetObject("player"))
            .First(a => a.Verb == "look");
        engine.TurnManager.PerformAction(world.GetObject("player"), look);
        var signals = engine.SignalBus.Drain("player").Select(s => s.Text).ToList();
        Assert.Contains(signals, t => t.Contains("faint blue glow"));

        // same room: bright
        world.MoveObject("player", "troll_room");
        engine.TurnManager.PerformAction(world.GetObject("player"), look);
        signals = engine.SignalBus.Drain("player").Select(s => s.Text).ToList();
        Assert.Contains(signals, t => t.Contains("very brightly"));

        // away: dims
        world.MoveObject("player", "living_room");
        engine.TurnManager.PerformAction(world.GetObject("player"), look);
        signals = engine.SignalBus.Drain("player").Select(s => s.Text).ToList();
        Assert.Contains(signals, t => t.Contains("no longer glowing"));
    }

    [Fact]
    public void SwordDoesNotGlow_OverTheFallen()
    {
        var engine = NewEngine();
        var world = engine.World;
        // the troll is dead and gone from the fight
        Conditions.Attach(world, engine.ModuleRegistry, world.GetObject("troll"),
            "cond_dead");
        world.SetFieldOverride("troll", "duelist", "out", World.ToJson(true));

        world.MoveObject("sword", "player");
        world.MoveObject("player", "troll_room");
        var look = engine.ActionResolver.Resolve(world.GetObject("player"))
            .First(a => a.Verb == "look");
        engine.TurnManager.PerformAction(world.GetObject("player"), look);

        // his room, the sword in hand — and nothing to warn about
        Assert.DoesNotContain(engine.SignalBus.Drain("player").Select(s => s.Text),
            t => t.Contains("glow"));
        Assert.Equal(0, engine.ModuleRegistry.ResolveInt(
            world.GetObject("sword"), "sense", "glow"));

        // and a mid-fight kill dims it on the spot: glowing, then dead
        var engine2 = NewEngine();
        var world2 = engine2.World;
        world2.MoveObject("sword", "player");
        world2.MoveObject("player", "troll_room");
        var look2 = engine2.ActionResolver.Resolve(world2.GetObject("player"))
            .First(a => a.Verb == "look");
        engine2.TurnManager.PerformAction(world2.GetObject("player"), look2);
        engine2.SignalBus.Drain("player");
        Conditions.Attach(world2, engine2.ModuleRegistry, world2.GetObject("troll"),
            "cond_dead");
        world2.SetFieldOverride("troll", "duelist", "out", World.ToJson(true));
        engine2.TurnManager.PerformAction(world2.GetObject("player"), look2);
        Assert.Contains(engine2.SignalBus.Drain("player").Select(s => s.Text),
            t => t.Contains("no longer glowing"));
    }

    [Fact]
    public void Bottle_HoldsDrinkableWater()
    {
        var engine = NewEngine();
        var world = engine.World;
        world.MoveObject("player", "kitchen");

        var result = RunScript(engine,
        [
            "Take the glass bottle",
            "Open the glass bottle",
            "Drink the quantity of water",
        ]);
        Assert.True(result.Success, result.Error);
        Assert.False(world.HasObject("water")); // consumed outright
    }

    [Fact]
    public void AttackingBareHanded_IsSuicidal()
    {
        var engine = NewEngine();
        var world = engine.World;
        world.MoveObject("player", "troll_room");
        // dark room: bring a light so the fight is even visible
        world.MoveObject("lamp", "player");
        world.SetFieldOverride("lamp", "lightsource", "on", World.ToJson(true));
        var attack = engine.ActionResolver.Resolve(world.GetObject("player"))
            .First(a => a.Verb == "attack" && a.TargetId == "troll");
        var result = engine.TurnManager.PerformAction(world.GetObject("player"), attack);
        Assert.False(result.Success);
        Assert.Contains("suicidal", result.Message);
    }
}
