using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.Scenarios;

namespace AEngine.Tests;

/// <summary>
/// The zork1 scenario loads from its world/ fragments and plays: a
/// walkthrough reaches the kitchen through the window, scores the room
/// bonus, and the attic is properly dark. These exercise the real
/// scenario files end-to-end through the same deterministic walkthrough
/// runner the CLI's --walkthrough flag uses.
/// </summary>
public class Zork1ScenarioTests
{
    private static string FindScenarioDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scenarios", "zork1");
            if (Directory.Exists(Path.Combine(candidate, "world")) ||
                File.Exists(Path.Combine(candidate, "world.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate scenarios/zork1.");
    }

    private static GameEngine NewEngine(int seed = 20260905)
    {
        var engine = GameEngine.CreateWithBuiltinHandlers();
        ScenarioLoader.LoadFrom(engine, FindScenarioDir());
        engine.Random = new Random(seed);
        return engine;
    }

    [Fact]
    public void ScenarioLoads_FromFragments()
    {
        var engine = NewEngine();
        var world = engine.World;
        foreach (var id in new[]
                 {
                     "west_of_house", "north_of_house", "south_of_house", "east_of_house",
                     "kitchen", "attic", "living_room", "player", "lamp", "sword",
                     "trophy_case", "rope", "knife", "game_rules", "passage_state",
                 })
            Assert.True(world.HasObject(id), $"missing object '{id}'");
        Assert.Equal("west_of_house", world.RoomOf("player").Id);
        // the lamp is a finite light source per the original's burn schedule
        Assert.Equal(385, engine.ModuleRegistry.ResolveInt(
            world.GetObject("lamp"), "lightsource", "fuel"));
        // every room fragment loaded, including the dark attic
        Assert.True(engine.ModuleRegistry.ResolveBool(
            world.GetObject("attic"), "room", "dark"));
    }

    [Fact]
    public void Walkthrough_WindowIntoHouse_UpIntoTheDarkAttic()
    {
        var engine = NewEngine();
        var script = new[]
        {
            "# around the house and in through the kitchen window",
            "Go north",
            "Go southeast",
            "Open the kitchen window",
            "Go west",
            "# the kitchen: first scored room",
            "Go up",
            "Look around",
            "Say: hello",
        };
        var result = WalkthroughRunner.Run(engine, script);
        Assert.True(result.Success, result.Error);

        var world = engine.World;
        Assert.Equal("attic", world.RoomOf("player").Id);
        // the kitchen's 10 points landed on the first visit
        Assert.Equal(10, Score.Of(world, engine.ModuleRegistry, world.GetObject("player")));
        // the attic is dark: look reported the scenario's pitch-black line
        Assert.Contains("pitch black", string.Join("\n", result.Transcript));
        // speech in the dark still works (the say entry survives)
        Assert.Contains("hello", string.Join("\n", result.Transcript));
        // darkness hides the attic's rope from the action list
        Assert.DoesNotContain(engine.ActionResolver.Resolve(world.GetObject("player")),
            a => a.TargetId == "rope");
    }

    [Fact]
    public void Walkthrough_FailsLoudlyOnBadCommands()
    {
        var engine = NewEngine();
        var result = WalkthroughRunner.Run(engine, ["Take the sword"]);
        Assert.False(result.Success);
        Assert.Contains("Line 1", result.Error);
    }

    [Fact]
    public void Exits_ShowStateOnlyForClosableDoors()
    {
        var engine = NewEngine();
        var result = WalkthroughRunner.Run(engine, ["Go north", "Go southeast", "Look around"]);
        Assert.True(result.Success, result.Error);
        var look = result.Transcript[^1];

        // bare passages lead on without state; the kitchen window is a
        // real door and reports its (observable, unlocked) open state
        Assert.Contains("north (path around the house)", look);
        Assert.Contains("west (kitchen window, closed)", look);
        Assert.DoesNotContain("path around the house, ", look);
    }

    [Fact]
    public void WestOfHouse_HasTheMailboxAndLeaflet()
    {
        var engine = NewEngine();
        var world = engine.World;

        // the first screen: the mailbox is anchored scenery (no take
        // offered), holding the leaflet until it is opened
        Assert.DoesNotContain(engine.ActionResolver.Resolve(world.GetObject("player")),
            a => a.Verb == "take" && a.TargetId == "mailbox");
        var look = engine.TurnManager.Execute(world.GetObject("player"), "look", "player");
        Assert.Contains("small mailbox", look.Message);
        // the leaflet is inside a closed container: invisible
        Assert.DoesNotContain(look.Message, "leaflet");

        var result = WalkthroughRunner.Run(engine,
        [
            "Open the small mailbox",
            "Take the leaflet",
            "Read the leaflet",
        ]);
        Assert.True(result.Success, result.Error);
        Assert.Equal("player", world.GetObject("leaflet").Parent);
        Assert.Contains("WELCOME TO ZORK", string.Join("\n", result.Transcript));
        Assert.Contains("low cunning", string.Join("\n", result.Transcript));
    }

    /// <summary>
    /// The kitchen table is a surface (no open/close, contents read "on"
    /// it), and the sack on it is a normal container: opening it makes
    /// the garlic reachable through the whole chain.
    /// </summary>
    [Fact]
    public void KitchenTable_IsASurfaceWithReachableSackContents()
    {
        var engine = NewEngine();
        var world = engine.World;
        var player = world.GetObject("player");
        world.MoveObject("player", "kitchen");

        // a surface offers no open/close
        Assert.DoesNotContain(engine.ActionResolver.Resolve(player),
            a => a.Verb is "open" or "close" && a.TargetId == "kitchen_table");

        // the closed sack's contents are not reachable
        Assert.DoesNotContain(engine.ActionResolver.Resolve(player),
            a => a.TargetId == "garlic");
        var result = WalkthroughRunner.Run(engine,
        [
            "Look around",
            "Open the brown sack",
            "Take the clove of garlic",
            "Put the clove of garlic onto the kitchen table",
            "Take the clove of garlic",
        ]);
        Assert.True(result.Success, result.Error);
        Assert.Contains("brown sack (on kitchen table)", result.Transcript[0]);
        Assert.Contains("from the brown sack", result.Transcript[2]);
    }

    /// <summary>
    /// Room descriptions are the original Zork text, verbatim (MIT
    /// source). This pins the opening screen exactly; the rest are
    /// audited against 1dungeon.zil/1actions.zil as they are authored.
    /// Dynamic variants (window ajar/open, rug moved, cyclops hole)
    /// render their initial state here until the state-driven look
    /// work lands.
    /// </summary>
    [Fact]
    public void RoomDescriptions_AreTheOriginalText()
    {
        var engine = NewEngine();
        var world = engine.World;
        // the attic is dark — carry a burning lamp for the tour
        var lamp = world.GetObject("lamp");
        world.MoveObject("lamp", "player");
        world.SetFieldOverride("lamp", "lightsource", "on",
            AEngine.Core.World.World.ToJson(true));
        string Look(string roomId)
        {
            world.MoveObject("player", roomId);
            return engine.TurnManager.Execute(world.GetObject("player"), "look", "player")
                .Message;
        }

        Assert.Contains(
            "You are standing in an open field west of a white house, with a boarded front door.",
            Look("west_of_house"));
        Assert.Contains(
            "You are facing the north side of a white house. There is no door here, and all the windows are boarded up. To the north a narrow path winds through the trees.",
            Look("north_of_house"));
        Assert.Contains(
            "You are facing the south side of a white house. There is no door here, and all the windows are boarded.",
            Look("south_of_house"));
        Assert.Contains(
            "You are behind the white house. A path leads into the forest to the east. In one corner of the house there is a small window which is slightly ajar.",
            Look("east_of_house"));
        Assert.Contains(
            "You are in the kitchen of the white house. A table seems to have been used recently for the preparation of food. A passage leads to the west and a dark staircase can be seen leading upward. A dark chimney leads down and to the east is a small window which is slightly ajar.",
            Look("kitchen"));
        Assert.Contains("This is the attic. The only exit is a stairway leading down.",
            Look("attic"));
        Assert.Contains(
            "You are in the living room. There is a doorway to the east, a wooden door with strange gothic lettering to the west, which appears to be nailed shut, a trophy case, and a large oriental rug in the center of the room.",
            Look("living_room"));
    }
}

/// <summary>Thin alias so scenario tests read as scripts, not machinery.</summary>
internal static class WalkthroughRunner
{
    public static AEngine.Cli.WalkthroughResult Run(
        GameEngine engine, IReadOnlyList<string> lines) =>
        AEngine.Cli.Walkthrough.Run(engine, "player", lines);
}
