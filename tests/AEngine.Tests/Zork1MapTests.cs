using AEngine.Core.Runtime;
using AEngine.Core.Scenarios;
using AEngine.Core.World;

namespace AEngine.Tests;

/// <summary>
/// Map validation for the Zork I scenario: every room of the original
/// exists, ids are unique across all fragments, and every portal leads
/// somewhere (or is a deliberate edge to come).
/// </summary>
public class Zork1MapTests
{
    private static GameEngine NewEngine()
    {
        var engine = GameEngine.CreateWithBuiltinHandlers();
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scenarios", "zork1");
            if (Directory.Exists(Path.Combine(candidate, "world")))
            {
                ScenarioLoader.LoadFrom(engine, candidate);
                return engine;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate scenarios/zork1.");
    }

    /// <summary>The original's 110 rooms (Zork I: The Great Underground Empire).</summary>
    private static readonly string[] ExpectedRooms =
    [
        // surface
        "west_of_house", "north_of_house", "south_of_house", "east_of_house",
        "forest_1", "forest_2", "mountains", "forest_3", "path", "up_a_tree",
        "grating_clearing", "clearing", "stone_barrow",
        // house
        "kitchen", "attic", "living_room",
        // cellar and vicinity
        "cellar", "troll_room", "east_of_chasm", "gallery", "studio", "ew_passage",
        // the maze
        "maze_1", "maze_2", "maze_3", "maze_4", "maze_5", "maze_6", "maze_7",
        "maze_8", "maze_9", "maze_10", "maze_11", "maze_12", "maze_13",
        "maze_14", "maze_15", "dead_end_1", "dead_end_2", "dead_end_3",
        "dead_end_4", "grating_room",
        // cyclops and thief
        "cyclops_room", "strange_passage", "treasure_room",
        // reservoir area
        "reservoir_south", "reservoir", "reservoir_north", "stream_view", "in_stream",
        "dam_room", "dam_lobby", "maintenance_room",
        // round room area
        "round_room", "deep_canyon", "damp_cave", "loud_room", "ns_passage", "chasm_room",
        // hades
        "entrance_to_hades", "land_of_living_dead",
        // mirror rooms and temple
        "mirror_room_1", "mirror_room_2", "small_cave", "tiny_cave", "atlantis_room",
        "cold_passage", "narrow_passage", "winding_passage", "twisting_passage",
        "engavings_cave", "dome_room", "torch_room", "north_temple", "south_temple",
        "egypt_room",
        // coal mines
        "mine_entrance", "squeeky_room", "bat_room", "shaft_room", "smelly_room",
        "gas_room", "mine_1", "mine_2", "mine_3", "mine_4", "ladder_top",
        "ladder_bottom", "dead_end_5", "timber_room", "lower_shaft", "machine_room",
        "slide_room",
        // frigid river and outdoors
        "dam_base", "river_1", "river_2", "river_3", "river_4", "river_5",
        "white_cliffs_north", "white_cliffs_south", "shore", "sandy_beach",
        "sandy_cave", "aragain_falls", "on_rainbow", "end_of_rainbow",
        "canyon_bottom", "cliff_middle", "canyon_view",
    ];

    [Fact]
    public void EveryOriginalRoom_Exists()
    {
        var engine = NewEngine();
        foreach (var id in ExpectedRooms)
            Assert.True(engine.World.HasObject(id), $"missing room '{id}'");
        var rooms = engine.World.Objects.Values
            .Where(o => o.HasModule("room")).Select(o => o.Id).ToHashSet();
        Assert.Equal(ExpectedRooms.Length, rooms.Count);
    }

    [Fact]
    public void EveryPortal_LeadsSomewhere()
    {
        var engine = NewEngine();
        var world = engine.World;
        var broken = new List<string>();
        foreach (var obj in world.Objects.Values.Where(o => o.HasModule("portal")))
        {
            var to = engine.ModuleRegistry.ResolveString(obj, "portal", "to");
            if (string.IsNullOrEmpty(to) || !world.HasObject(to))
                broken.Add($"{obj.Id} -> {to}");
        }
        Assert.True(broken.Count == 0, "dangling portals: " + string.Join(", ", broken));
    }

    /// <summary>
    /// The nineteen treasures of the original, each reachable somewhere
    /// in the initial world.
    /// </summary>
    [Fact]
    public void EveryTreasure_Exists()
    {
        var engine = NewEngine();
        var treasures = engine.World.Objects.Values
            .Where(o => o.HasModule("treasure")).Select(o => o.Id).ToHashSet();
        foreach (var id in new[]
                 {
                     "egg", "canary", "bauble", "skull", "sceptre", "chalice", "trident",
                     "platinum_bar", "torch", "trunk", "diamond", "jade",
                     "bracelet", "bag_of_coins", "pot_of_gold", "scarab",
                     "painting", "coffin", "emerald",
                 })
            Assert.True(treasures.Contains(id), $"missing treasure '{id}'");
        Assert.True(treasures.Count >= 19, $"only {treasures.Count} treasures");
    }

    /// <summary>
    /// The secret path to the Stone Barrow stays secret until the game
    /// is won: hidden from the room's exits and the action list, then
    /// revealed — and walking it ends the adventure with the original's
    /// epilogue.
    /// </summary>
    [Fact]
    public void SecretBarrowPath_HiddenUntilWon()
    {
        var engine = NewEngine();
        var world = engine.World;
        var player = world.GetObject("player");

        var look = engine.TurnManager.Execute(player, "look", "player");
        Assert.DoesNotContain("southwest", look.Message);
        Assert.DoesNotContain(engine.ActionResolver.Resolve(player),
            a => a.TargetId == "woh_southwest");

        // winning: the score flips the rules flag, the pass reveals the path
        world.SetFieldOverride("game_rules", "rules", "won", World.ToJson(true));
        AEngine.Core.Actions.Score.CheckWin(engine);
        var wait = engine.ActionResolver.Resolve(player).First(a => a.Verb == "wait");
        engine.TurnManager.PerformAction(player, wait);
        Assert.True(engine.ModuleRegistry.ResolveBool(
            world.GetObject("won_flag"), "flag", "value"));
        var look2 = engine.TurnManager.Execute(player, "look", "player");
        Assert.Contains("A secret path leads southwest into the forest.", look2.Message);
        Assert.Contains(engine.ActionResolver.Resolve(player),
            a => a.TargetId == "woh_southwest");

        // and walking into the barrow is the end
        var enter = engine.ActionResolver.Resolve(player)
            .First(a => a.TargetId == "woh_southwest");
        var result = engine.TurnManager.PerformAction(player, enter);
        Assert.NotNull(engine.GameOver);
        Assert.Contains("Inside the Barrow", engine.GameOver);
        Assert.Contains("You have mastered ZORK", engine.GameOver);
    }

    /// <summary>
    /// The dam's machinery speaks only to those present: the sluice
    /// announcements are room-granular signals, not world-wide sayings.
    /// </summary>
    [Fact]
    public void DamAnnouncements_ReachOnlyTheNearby()
    {
        var engine = NewEngine();
        var world = engine.World;
        var player = world.GetObject("player");
        LightLamp(engine);

        // someone at the dam hears the gates
        world.MoveObject("player", "dam_room");
        world.SetFieldOverride("gates_open", "flag", "value", World.ToJson(true));
        var wait = engine.ActionResolver.Resolve(player).First(a => a.Verb == "wait");
        engine.TurnManager.PerformAction(player, wait);
        Assert.Contains(engine.SignalBus.Drain("player").Select(s => s.Text),
            t => t.Contains("sluice gates open"));

        // someone far away does not
        var engine2 = NewEngine();
        var world2 = engine2.World;
        LightLamp(engine2);
        world2.SetFieldOverride("gates_open", "flag", "value", World2True());
        var wait2 = engine2.ActionResolver.Resolve(world2.GetObject("player"))
            .First(a => a.Verb == "wait");
        engine2.TurnManager.PerformAction(world2.GetObject("player"), wait2);
        Assert.DoesNotContain(engine2.SignalBus.Drain("player").Select(s => s.Text),
            t => t.Contains("sluice gates"));
    }

    private static System.Text.Json.JsonElement World2True() => World.ToJson(true);

    private static void LightLamp(GameEngine engine)
    {
        var world = engine.World;
        world.MoveObject("lamp", "player");
        world.SetFieldOverride("lamp", "lightsource", "on", World.ToJson(true));
    }
}
