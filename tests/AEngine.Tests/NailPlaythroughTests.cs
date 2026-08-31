using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.Scenarios;
using AEngine.Core.World;

namespace AEngine.Tests;

/// <summary>
/// Scripted playthrough of the Nail scenario through the real scenario
/// files: barter for the ember salt, pickpocket the cultist's key, loot
/// the reliquary, then persuade the sorcerer to unbind the dragon-mark —
/// ending the game with the epilogue. Dice are zeroed (rules override) and
/// NPC policies are stilled so the route is deterministic; Nail's
/// persuasion is bumped so the unbinding check passes with no dice.
/// </summary>
public class NailPlaythroughTests
{
    private static string FindScenarioDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scenarios", "nail");
            if (File.Exists(Path.Combine(candidate, "world.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate scenarios/nail.");
    }

    private static GameEngine NewEngine()
    {
        var engine = GameEngine.CreateWithBuiltinHandlers();
        var dir = FindScenarioDir();
        ScenarioLoader.LoadInto(
            engine,
            Path.Combine(dir, "modules.json"),
            Path.Combine(dir, "world.json"));
        // deterministic checks: 0d0 dice
        engine.ModuleRegistry.LoadJson("""
        [
          {
            "id": "rules", "name": "Rules",
            "fields": [
              { "name": "diceCount", "type": "int", "default": 0 },
              { "name": "diceSides", "type": "int", "default": 0 }
            ],
            "affordances": []
          }
        ]
        """);
        // still the NPCs so nobody wanders or picks a fight mid-route
        foreach (var id in new[] { "ferret", "mira", "krell", "rath" })
            engine.World.SetFieldOverride(id, "agent", "policy", World.ToJson("player"));
        // with 0d0 the persuasion check needs Nail at +20: 12 charisma + 8
        engine.World.SetFieldOverride("player", "skills", "values",
            World.ToJson(new Dictionary<string, int>
                { ["persuasion"] = 8, ["lockpicking"] = 3, ["pickpocket"] = 4, ["blades"] = 4, ["brawling"] = 1 }));
        return engine;
    }

    private static void Do(GameEngine engine, string verb, string? targetId = null)
    {
        var player = engine.World.GetObject("player");
        var action = engine.ActionResolver.Resolve(player)
            .FirstOrDefault(a => a.Verb == verb && (targetId is null || a.TargetId == targetId));
        Assert.NotNull(action); // the action must be listed at this point in the route
        var result = engine.TurnManager.PerformAction(player, action);
        Assert.True(result.Success, $"{verb} {targetId}: {result.Message}");
    }

    private static void AssertRoom(GameEngine engine, string roomId) =>
        Assert.Equal(roomId, engine.World.RoomOf("player").Id);

    [Fact]
    public void Nail_FindsRath_AndIsUnbound()
    {
        var engine = NewEngine();

        // the brand starts suppressed but present — borne, not carried
        Assert.Equal("player", engine.World.GetObject("dragonmark").Parent);
        var inventory = engine.TurnManager.Execute(
            engine.World.GetObject("player"), "inventory", "player", verb: "inventory");
        Assert.Contains("You bear: a dragon-mark", inventory.Message);

        // docks → market → alley: pick the moonpetal
        AssertRoom(engine, "docks");
        Do(engine, "go", "gate_docks_side");
        AssertRoom(engine, "market");
        Do(engine, "go", "passage_market_side");
        AssertRoom(engine, "alley");
        Do(engine, "take", "moonpetal");

        // market → stall: barter the bloom for the ember salt
        Do(engine, "go", "passage_alley_side");
        Do(engine, "go", "awning_market_side");
        AssertRoom(engine, "stall");
        var trade = engine.ActionResolver.Resolve(engine.World.GetObject("player"))
            .First(a => a.Verb == "trade" && a.TargetId == "ember_salt");
        Assert.Equal("Barter for the pouch of ember salt", trade.Label);
        Do(engine, "trade", "ember_salt");
        Assert.Equal("player", engine.World.GetObject("ember_salt").Parent);
        Assert.Equal("mira", engine.World.GetObject("moonpetal").Parent);

        // back to the alley, down the trapdoor: the cultist guards the crystal
        Do(engine, "go", "awning_stall_side");
        Do(engine, "go", "passage_market_side");
        Do(engine, "open", "hatch_alley_side");
        Do(engine, "go", "hatch_alley_side");
        AssertRoom(engine, "cellar");

        // pickpocket the key (18 vs Krell's 10, no dice), then loot the reliquary
        Do(engine, "steal", "cellar_key");
        Do(engine, "unlock", "reliquary");
        Do(engine, "open", "reliquary");
        Do(engine, "take", "focus_crystal");

        // up and out, to the tower
        Do(engine, "go", "hatch_cellar_side");
        Do(engine, "go", "passage_alley_side");
        Do(engine, "open", "tower_door_market_side");
        Do(engine, "go", "tower_door_market_side");
        AssertRoom(engine, "tower_foyer");
        Do(engine, "go", "beads_foyer_side");
        AssertRoom(engine, "tower_study");

        // ask the sorcerer: persuasion 22 vs 20, components in hand
        var ask = engine.ActionResolver.Resolve(engine.World.GetObject("player"))
            .First(a => a.Verb == "unbrand" && a.TargetId == "rath");
        Assert.Equal("Ask the sorcerer to remove the dragon-mark", ask.Label);
        var result = engine.TurnManager.PerformAction(engine.World.GetObject("player"), ask);
        Assert.True(result.Success, result.Message);

        // the mark is gone, the components consumed, the proof kept, game over
        Assert.False(engine.World.HasObject("dragonmark"));
        Assert.False(engine.World.HasObject("ember_salt"));
        Assert.False(engine.World.HasObject("focus_crystal"));
        Assert.Equal("player", engine.World.GetObject("talon_blade").Parent);
        Assert.NotNull(engine.GameOver);
        Assert.Contains("unbinding sigils", engine.GameOver);
    }

    [Fact]
    public void Ritual_RefusesWithoutTheComponents()
    {
        var engine = NewEngine();
        // walk straight to the study with nothing but the talon
        Do(engine, "go", "gate_docks_side");
        Do(engine, "open", "tower_door_market_side");
        Do(engine, "go", "tower_door_market_side");
        Do(engine, "go", "beads_foyer_side");

        var result = engine.TurnManager.PerformAction(engine.World.GetObject("player"),
            engine.ActionResolver.Resolve(engine.World.GetObject("player"))
                .First(a => a.Verb == "unbrand" && a.TargetId == "rath"));
        Assert.False(result.Success);
        Assert.Contains("ember salt", result.Message);
        Assert.Contains("focus crystal", result.Message);
        Assert.Equal("player", engine.World.GetObject("dragonmark").Parent);
        Assert.Null(engine.GameOver);
    }
}
