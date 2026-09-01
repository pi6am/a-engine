using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.World;
using CoreWorld = AEngine.Core.World.World;

namespace AEngine.Tests;

/// <summary>
/// Last-seen memory for notable items: sightings record (holder, room);
/// a holder seen without the item loses the holder fact; a room seen
/// without the item loses the room fact; directly observed items (own
/// inventory included) are never repeated in the report.
/// </summary>
public class KnowledgeItemTests
{
    private static GameEngine NewEngine()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.ModuleRegistry.LoadJson("""
        [
          { "id": "knowledge", "name": "Knowledge",
            "fields": [
              { "name": "knowsNames", "type": "list", "default": [] },
              { "name": "lastSeen", "type": "map", "default": {} }
            ] }
        ]
        """);
        var world = engine.World;
        world.MoveObject("bob", "room_a");
        world.CreateObject("stone", "room_a", "quest stone");
        world.AddModule("stone", "portable");
        world.AddModule("stone", "notable");
        return engine;
    }

    private static Knowledge.Sighting? SightingOf(GameEngine engine, string observerId, string itemId)
    {
        return Knowledge.LastSeen(engine.ModuleRegistry, engine.World.GetObject(observerId))
            .GetValueOrDefault(itemId);
    }

    [Fact]
    public void VisibleItem_IsRecorded_ButNotReported()
    {
        var engine = NewEngine();
        var world = engine.World;
        var alice = world.GetObject("alice");
        world.AddModule("alice", "knowledge");

        // bob visibly holds the notable stone
        world.MoveObject("stone", "bob");
        var report = Knowledge.ItemReport(engine, alice);

        Assert.Empty(report); // directly observed — the context says where it is
        var sighting = SightingOf(engine, "alice", "stone");
        Assert.NotNull(sighting);
        Assert.Equal("bob", sighting!.Holder);
        Assert.Equal("room_a", sighting.Room);
    }

    [Fact]
    public void HolderKept_LocationDropped_WhenTheyLeaveWithIt()
    {
        var engine = NewEngine();
        var world = engine.World;
        var alice = world.GetObject("alice");
        world.AddModule("alice", "knowledge");
        world.MoveObject("stone", "bob");
        Knowledge.ItemReport(engine, alice); // record the sighting

        // bob walks to room_b with the stone; alice watches the empty room
        world.MoveObject("bob", "room_b");
        var report = Knowledge.ItemReport(engine, alice);

        Assert.Equal(["quest stone (held by Bob)"], report);
        var sighting = SightingOf(engine, "alice", "stone");
        Assert.Equal("bob", sighting!.Holder);
        Assert.Null(sighting.Room);
    }

    [Fact]
    public void ReSightedElsewhere_UpdatesBothFacts()
    {
        var engine = NewEngine();
        var world = engine.World;
        var alice = world.GetObject("alice");
        world.AddModule("alice", "knowledge");
        world.MoveObject("stone", "bob");
        Knowledge.ItemReport(engine, alice);
        world.MoveObject("bob", "room_b");
        Knowledge.ItemReport(engine, alice);

        // alice follows bob to room_b: fresh sighting, hidden from report
        world.MoveObject("alice", "room_b");
        Assert.Empty(Knowledge.ItemReport(engine, alice));
        var sighting = SightingOf(engine, "alice", "stone");
        Assert.Equal("bob", sighting!.Holder);
        Assert.Equal("room_b", sighting.Room);

        // back in room_a she can't see room_b: holder and last-known room
        // both survive in the report
        world.MoveObject("alice", "room_a");
        Assert.Equal(["quest stone (held by Bob in Room B)"], Knowledge.ItemReport(engine, alice));
    }

    [Fact]
    public void LooseItemTakenAway_FallsToSomewhere()
    {
        var engine = NewEngine();
        var world = engine.World;
        var alice = world.GetObject("alice");
        world.AddModule("alice", "knowledge");

        // stone lies loose in room_a; then someone carries it off
        Knowledge.ItemReport(engine, alice);
        Assert.Equal("room_a", SightingOf(engine, "alice", "stone")!.Room);

        world.MoveObject("stone", "room_b");
        Assert.Equal(["quest stone (somewhere)"], Knowledge.ItemReport(engine, alice));
        var sighting = SightingOf(engine, "alice", "stone");
        Assert.Null(sighting!.Holder);
        Assert.Null(sighting.Room);
    }

    [Fact]
    public void OwnInventory_IsNeverReported()
    {
        var engine = NewEngine();
        var world = engine.World;
        var alice = world.GetObject("alice");
        world.AddModule("alice", "knowledge");
        world.MoveObject("stone", "alice");

        Assert.Empty(Knowledge.ItemReport(engine, alice));
        Assert.Equal("alice", SightingOf(engine, "alice", "stone")!.Holder);
    }

    [Fact]
    public void ClosedContainer_HidesContents_RoomFactDrops()
    {
        var engine = NewEngine();
        var world = engine.World;
        var alice = world.GetObject("alice");
        world.AddModule("alice", "knowledge");

        // seen inside the open chest, then the chest is closed
        world.SetFieldOverride("chest", "openable", "open", CoreWorld.ToJson(true));
        world.MoveObject("stone", "chest");
        Knowledge.ItemReport(engine, alice);
        Assert.Equal("chest", SightingOf(engine, "alice", "stone")!.Holder);

        world.SetFieldOverride("chest", "openable", "open", CoreWorld.ToJson(false));
        var report = Knowledge.ItemReport(engine, alice);

        // the chest is still there (holder survives — the stone may well be
        // inside), but the sighting's room fact drops since the item is no
        // longer visible here
        Assert.Equal(["quest stone (in the chest)"], report);
        var sighting = SightingOf(engine, "alice", "stone");
        Assert.Equal("chest", sighting!.Holder);
        Assert.Null(sighting.Room);
    }

    [Fact]
    public void HolderName_IsObserverRelative()
    {
        var engine = NewEngine();
        var world = engine.World;
        var alice = world.GetObject("alice");
        world.AddModule("alice", "knowledge");
        world.SetFieldOverride("bob", "agent", "incognito", CoreWorld.ToJson("a shifty stranger"));
        world.MoveObject("stone", "bob");
        Knowledge.ItemReport(engine, alice);
        world.MoveObject("bob", "room_b");

        Assert.Equal(["quest stone (held by a shifty stranger)"], Knowledge.ItemReport(engine, alice));
    }

    [Fact]
    public void NoKnowledgeModule_NoReport()
    {
        var engine = NewEngine(); // alice has no knowledge module
        var alice = engine.World.GetObject("alice");
        Assert.Empty(Knowledge.ItemReport(engine, alice));
        Assert.Empty(Knowledge.LastSeen(engine.ModuleRegistry, alice));
    }

    [Fact]
    public void Nail_EmberSalt_IsTrackedAcrossMirasMovements()
    {
        var engine = LoadNail();
        var world = engine.World;
        var player = world.GetObject("player");

        // the player visits the stall and sees Mira with the ember salt
        world.MoveObject("player", "stall");
        Assert.Empty(Knowledge.ItemReport(engine, player));
        Assert.Equal("mira", SightingOf(engine, "player", "ember_salt")!.Holder);
        Assert.Equal("stall", SightingOf(engine, "player", "ember_salt")!.Room);

        // Mira steps out with the salt; the player remembers WHO has it —
        // by the name they can print — but not where she is
        world.MoveObject("mira", "market");
        var report = Knowledge.ItemReport(engine, player);
        Assert.Contains("pouch of ember salt (held by the herbalist)", report);
        Assert.Null(SightingOf(engine, "player", "ember_salt")!.Room);
    }

    [Fact]
    public void ContextBuilder_ReportsImportantItems_NotInView()
    {
        var engine = NewEngine();
        var world = engine.World;
        var alice = world.GetObject("alice");
        world.AddModule("alice", "knowledge");
        world.MoveObject("stone", "bob");
        Knowledge.ItemReport(engine, alice);
        world.MoveObject("bob", "room_b"); // alice can no longer see it

        var context = new AEngine.Llm.AgentContextBuilder(engine).BuildContext(alice, npc: false);
        Assert.Contains("Important items: quest stone (held by Bob)", context);
    }

    private static GameEngine LoadNail()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scenarios", "nail");
            if (File.Exists(Path.Combine(candidate, "world.json")))
            {
                var engine = GameEngine.CreateWithBuiltinHandlers();
                AEngine.Core.Scenarios.ScenarioLoader.LoadInto(
                    engine,
                    Path.Combine(candidate, "modules.json"),
                    Path.Combine(candidate, "world.json"));
                return engine;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate scenarios/nail.");
    }
}
