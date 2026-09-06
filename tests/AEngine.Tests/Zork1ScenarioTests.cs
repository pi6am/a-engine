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
        engine.ShowExitsInLook = true; // exits are opt-in navigation aid
        var result = WalkthroughRunner.Run(engine, ["Go north", "Go southeast", "Look around"]);
        Assert.True(result.Success, result.Error);
        var look = result.Transcript[^1];

        // bare passages lead on without state; the kitchen window is a
        // real door and reports its (observable, unlocked) open state
        Assert.Contains("north (path around the house)", look);
        Assert.Contains("west (kitchen window, closed)", look);
        Assert.DoesNotContain("path around the house, ", look);
    }

    /// <summary>
    /// Doors open and close in the original's voice: authored
    /// openText/closeText on the shared door state own the whole line.
    /// </summary>
    [Fact]
    public void DoorFlavor_IsTheOriginalText()
    {
        var engine = NewEngine();
        var result = WalkthroughRunner.Run(engine,
        [
            "Go north", "Go southeast",
            "Open the kitchen window", "Close the kitchen window", "Open the kitchen window",
            "Go west", "Go west",
            "Move the carpet", "Open the trap door", "Close the trap door",
        ]);
        Assert.True(result.Success, result.Error);
        Assert.Equal("With great effort, you open the window far enough to allow entry.",
            result.Transcript[2]);
        Assert.Equal("The window closes (more easily than it opened).",
            result.Transcript[3]);
        Assert.Equal(
            "The door reluctantly opens to reveal a rickety staircase descending into darkness.",
            result.Transcript[8]);
        Assert.Equal("The door swings shut and closes.", result.Transcript[9]);
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
        Assert.DoesNotContain("leaflet", look.Message);

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
    /// The bird's nest is a container without a lid: always open (the
    /// egg reads "in" it and is reachable), offering no open/close, and
    /// never annotated with an open/closed state.
    /// </summary>
    [Fact]
    public void Nest_IsALidlessAlwaysOpenContainer()
    {
        var engine = NewEngine();
        var world = engine.World;
        var result = WalkthroughRunner.Run(engine,
        [
            "Go north", "Go north", "Go up", "Look around",
            "Take the jewel-encrusted egg",
            "Put the jewel-encrusted egg into the bird's nest",
        ]);
        Assert.True(result.Success, result.Error);
        var look = result.Transcript[3];
        Assert.Contains("bird's nest, jewel-encrusted egg (in bird's nest)", look);
        Assert.DoesNotContain("nest (open)", look);

        // no lid to work
        Assert.DoesNotContain(engine.ActionResolver.Resolve(world.GetObject("player")),
            a => a.Verb is "open" or "close" && a.TargetId == "nest");
        // and it holds what you hand it
        Assert.Equal("nest", world.GetObject("egg").Parent);
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

    [Fact]
    public void GrueQuestion_ExistsOnlyInDarkness_AndAnswersWithTheLore()
    {
        var engine = NewEngine();
        var world = engine.World;
        var player = world.GetObject("player");
        world.MoveObject("player", "attic"); // a dark room

        // lit: the question is nowhere — light never shows a grue
        world.MoveObject("lamp", "player");
        world.SetFieldOverride("lamp", "lightsource", "on", AEngine.Core.World.World.ToJson(true));
        Assert.DoesNotContain(engine.ActionResolver.Resolve(player), a => a.Verb == "ask");

        // pitch black: it is offered, and it answers with the verbatim lore
        world.SetFieldOverride("lamp", "lightsource", "on", AEngine.Core.World.World.ToJson(false));
        var ask = engine.ActionResolver.Resolve(player)
            .Single(a => a.Verb == "ask" && a.Label == "What is a grue?");
        var result = engine.TurnManager.PerformAction(player, ask);
        Assert.StartsWith("The grue is a sinister, lurking presence", result.Message);
        Assert.EndsWith("few have survived its fearsome jaws to tell the tale.", result.Message);
        Assert.Equal(1, engine.TurnManager.Turn); // asking passes the time
    }

    [Fact]
    public void Cyclops_FleesOnOverheardName_InBroadcastSpeech()
    {
        var engine = NewEngine();
        var world = engine.World;
        var player = world.GetObject("player");
        world.MoveObject("player", "cyclops_room");
        world.MoveObject("lamp", "player");
        world.SetFieldOverride("lamp", "lightsource", "on", AEngine.Core.World.World.ToJson(true));

        // the name buried in an ordinary sentence of broadcast speech —
        // no special command, no directed address: the cyclops listens
        var say = engine.ActionResolver.Resolve(player).First(a => a.Verb == "say");
        var result = engine.TurnManager.PerformAction(player, say,
            "Be afraid, for I am Odysseus, here to kill you.");
        Assert.StartsWith("You say:", result.Message);

        Assert.False(world.HasObject("cyclops")); // fled
        Assert.True(engine.ModuleRegistry.ResolveBool(
            world.GetObject("magic_state"), "flag", "value"));
        Assert.True(engine.ModuleRegistry.ResolveBool(
            world.GetObject("cyclops_out"), "flag", "value"));
        // and the farewell is seen, not inferred
        Assert.Contains(engine.SignalBus.Drain(player.Id), s =>
            s.Text.Contains("father's murderer", StringComparison.Ordinal));
    }

    [Fact]
    public void Cyclops_FleesOnUlyssesToo_AndStaysGone()
    {
        var engine = NewEngine();
        var world = engine.World;
        var player = world.GetObject("player");
        world.MoveObject("player", "cyclops_room");
        world.MoveObject("lamp", "player");
        world.SetFieldOverride("lamp", "lightsource", "on", AEngine.Core.World.World.ToJson(true));

        var say = engine.ActionResolver.Resolve(player).First(a => a.Verb == "say");
        engine.TurnManager.PerformAction(player, say, "Ulysses sends his regards.");
        Assert.False(world.HasObject("cyclops"));

        // the passage he knocked open is one-way magic: gone is gone
        var say2 = engine.ActionResolver.Resolve(player).First(a => a.Verb == "say");
        engine.TurnManager.PerformAction(player, say2, "odysseus! ODYSSEUS!");
        Assert.True(engine.ModuleRegistry.ResolveBool(
            world.GetObject("cyclops_out"), "flag", "value"));
    }

    [Fact]
    public void FirstDescription_ShowsOnce_ThenTheSettledText()
    {
        var engine = NewEngine();
        var world = engine.World;
        var player = world.GetObject("player");

        // the sceptre: FDESC names the coffin and the sharp point, LDESC settles
        world.MoveObject("player", "egypt_room");
        world.MoveObject("sceptre", "egypt_room"); // out of the coffin, in view
        world.MoveObject("lamp", "player");
        world.SetFieldOverride("lamp", "lightsource", "on", AEngine.Core.World.World.ToJson(true));
        var ex = engine.ActionResolver.Resolve(player)
            .Single(a => a.Verb == "examine" && a.TargetId == "sceptre");
        var first = engine.TurnManager.PerformAction(player, ex);
        Assert.Contains("possibly that of ancient Egypt", first.Message);
        Assert.Contains("tapers to a sharp point", first.Message);
        var second = engine.TurnManager.PerformAction(player, ex);
        Assert.Contains("An ornamented sceptre, tapering to a sharp point, is here.",
            second.Message);
        Assert.DoesNotContain("Egypt", second.Message);
    }

    [Fact]
    public void FirstDescription_OnlyItems_FallBare_Afterwards()
    {
        var engine = NewEngine();
        var world = engine.World;
        var player = world.GetObject("player");

        // the sword: FDESC above the trophy case, no LDESC — later
        // examines are just the name (the original shows nothing special)
        world.MoveObject("player", "living_room");
        world.MoveObject("sword", "living_room");
        var ex = engine.ActionResolver.Resolve(player)
            .Single(a => a.Verb == "examine" && a.TargetId == "sword");
        Assert.Contains("Above the trophy case hangs an elvish sword",
            engine.TurnManager.PerformAction(player, ex).Message);
        var later = engine.TurnManager.PerformAction(player, ex).Message;
        Assert.Equal("There's nothing special about the sword.", later);
    }
}

/// <summary>Thin alias so scenario tests read as scripts, not machinery.</summary>
internal static class WalkthroughRunner
{
    public static AEngine.Cli.WalkthroughResult Run(
        GameEngine engine, IReadOnlyList<string> lines) =>
        AEngine.Cli.Walkthrough.Run(engine, "player", lines);
}
