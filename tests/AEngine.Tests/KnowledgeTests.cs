using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.World;
using CoreWorld = AEngine.Core.World.World;

namespace AEngine.Tests;

/// <summary>
/// The knowledge module: agents without it know everything (back-compat);
/// agents with it render strangers by their incognito description until
/// an overheard proper name teaches them. Proper names also parse, so a
/// name can be addressed before it can be printed.
/// </summary>
public class KnowledgeTests
{
    private static GameEngine NewEngine()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.ModuleRegistry.LoadJson("""
        [
          { "id": "knowledge", "name": "Knowledge",
            "fields": [ { "name": "knowsNames", "type": "list", "default": [] } ] }
        ]
        """);
        var world = engine.World;
        world.CreateObject("rath", "room_b", "Rath Cinderstorm");
        world.AddModule("rath", "agent");
        world.AddModule("rath", "can_speak");
        world.SetFieldOverride("rath", "agent", "properNames",
            CoreWorld.ToJson(new[] { "Rath", "Cinderstorm" }));
        world.SetFieldOverride("rath", "agent", "incognito",
            CoreWorld.ToJson("a robed figure crackling with static"));
        return engine;
    }

    [Fact]
    public void WithoutKnowledgeModule_AgentKnowsEverything()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice"); // no knowledge module
        var rath = engine.World.GetObject("rath");
        Assert.True(Knowledge.KnowsName(engine.ModuleRegistry, alice, rath));
        Assert.Equal("Rath Cinderstorm",
            Knowledge.NameFor(engine.ModuleRegistry, alice, rath));
    }

    [Fact]
    public void WithKnowledgeModule_StrangerRendersIncognito()
    {
        var engine = NewEngine();
        var world = engine.World;
        var alice = world.GetObject("alice");
        world.AddModule("alice", "knowledge");
        var rath = world.GetObject("rath");

        Assert.False(Knowledge.KnowsName(engine.ModuleRegistry, alice, rath));
        Assert.Equal("a robed figure crackling with static",
            Knowledge.NameFor(engine.ModuleRegistry, alice, rath));
        // self is always nameable
        Assert.True(Knowledge.KnowsName(engine.ModuleRegistry, alice, alice));
    }

    [Fact]
    public void IncognitoUnset_FallsBackToRealName()
    {
        var engine = NewEngine();
        var world = engine.World;
        var alice = world.GetObject("alice");
        world.AddModule("alice", "knowledge");
        var bob = world.GetObject("bob"); // no incognito authored

        Assert.False(Knowledge.KnowsName(engine.ModuleRegistry, alice, bob));
        Assert.Equal("Bob", Knowledge.NameFor(engine.ModuleRegistry, alice, bob));
    }

    [Fact]
    public void LearnFromText_MatchesAnyProperName_WithWordBoundaries()
    {
        var engine = NewEngine();
        var world = engine.World;
        var alice = world.GetObject("alice");
        world.AddModule("alice", "knowledge");
        var rath = world.GetObject("rath");

        Knowledge.LearnFromText(world, engine.ModuleRegistry, alice,
            "You hear someone mention Rath approvingly.");
        Assert.True(Knowledge.KnowsName(engine.ModuleRegistry, alice, rath));

        // word-bounded: "Cinderstorming" is not the name Cinderstorm
        var bob = world.GetObject("bob");
        world.AddModule("bob", "knowledge");
        Knowledge.LearnFromText(world, engine.ModuleRegistry, bob,
            "they were Cinderstorming all night");
        Assert.False(Knowledge.KnowsName(engine.ModuleRegistry, bob, rath));
        Knowledge.LearnFromText(world, engine.ModuleRegistry, bob,
            "\"Cinderstorm,\" she said.");
        Assert.True(Knowledge.KnowsName(engine.ModuleRegistry, bob, rath));
    }

    [Fact]
    public void PrePopulatedKnowledge_RendersRealName()
    {
        var engine = NewEngine();
        var world = engine.World;
        var alice = world.GetObject("alice");
        world.AddModule("alice", "knowledge");
        world.SetFieldOverride("alice", "knowledge", "knowsNames",
            CoreWorld.ToJson(new[] { "rath" }));
        var rath = world.GetObject("rath");

        Assert.Equal("Rath Cinderstorm",
            Knowledge.NameFor(engine.ModuleRegistry, alice, rath));
    }

    [Fact]
    public void SignalsTeachNames_ObserverRelative()
    {
        var engine = NewEngine();
        var world = engine.World;
        world.MoveObject("rath", "room_a");
        var alice = world.GetObject("alice");
        world.AddModule("alice", "knowledge");
        world.SetFieldOverride("alice", "agent", "incognito",
            CoreWorld.ToJson("a soaking-wet traveler"));

        // alice speaks to rath using his name — rath learns nothing new
        // about himself, but the speech is delivered to bob (no knowledge
        // module: renders and stays full-name), and a third tracker
        // (carol, with a knowledge module) would learn from it
        world.CreateObject("carol", "room_a", "Carol");
        world.AddModule("carol", "agent");
        world.AddModule("carol", "knowledge");

        var action = AEngine.Llm.PlanExecutor.MatchAvailableOrPotential(
            engine, alice, "Say to Rath: \"Rath, you look terrible.\"");
        Assert.Equal("rath", action!.TargetId); // proper-name parsing works
        engine.TurnManager.PerformAction(alice, action, action.Text);

        // carol overheard the name in the delivered speech and learned it
        var carol = world.GetObject("carol");
        var rath = world.GetObject("rath");
        Assert.True(Knowledge.KnowsName(engine.ModuleRegistry, carol, rath));
    }

    [Fact]
    public void DescriptionsAreKnowledgeGated_IncognitoDescription()
    {
        var engine = NewEngine();
        var world = engine.World;
        var alice = world.GetObject("alice");
        world.AddModule("alice", "knowledge");
        var rath = world.GetObject("rath");
        world.SetFieldOverride("rath", "agent", "incognitoDescription",
            CoreWorld.ToJson("A robed figure crackling with static."));

        // a stranger sees the incognito description — no names leak
        Assert.Equal("A robed figure crackling with static.",
            Knowledge.DescriptionFor(engine.ModuleRegistry, alice, rath));
        // once known, the full description returns
        Knowledge.LearnName(world, engine.ModuleRegistry, alice, "rath");
        Assert.Equal(rath.Description,
            Knowledge.DescriptionFor(engine.ModuleRegistry, alice, rath));
        // unset incognitoDescription falls back to the real one for strangers
        var bob = world.GetObject("bob");
        Assert.Equal(bob.Description,
            Knowledge.DescriptionFor(engine.ModuleRegistry, alice, bob));
    }
}

/// <summary>
/// Scenario wiring for name knowledge in scenarios/nail: the old dockhand,
/// the herbalist, and the sorcerer all know each other; the cultist and
/// the player (Nail — Nannan in her native tongue) know nobody; a look
/// never teaches a name.
/// </summary>
public class NailKnowledgeTests
{
    [Fact]
    public void About_CreditsTheSource()
    {
        var engine = LoadNail();
        Assert.Contains("Concedo", engine.ScenarioAbout);
        Assert.Contains("Nail", engine.ScenarioAbout);
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

    private static bool PlayerKnows(GameEngine engine, string id) =>
        Knowledge.KnowsName(engine.ModuleRegistry,
            engine.World.GetObject("player"), engine.World.GetObject(id));

    [Fact]
    public void AcquaintanceMatrix_MatchesTheScenarioBrief()
    {
        var engine = LoadNail();
        // Ferret, Mira, and Rath all know each other
        foreach (var (a, b) in new[] { ("ferret", "mira"), ("ferret", "rath"),
                                       ("mira", "rath"), ("mira", "ferret"),
                                       ("rath", "ferret"), ("rath", "mira") })
            Assert.True(Knowledge.KnowsName(engine.ModuleRegistry,
                engine.World.GetObject(a), engine.World.GetObject(b)), $"{a} -> {b}");
        // the cultist knows nobody; Nail knows nobody
        foreach (var other in new[] { "ferret", "mira", "rath", "player" })
            Assert.False(Knowledge.KnowsName(engine.ModuleRegistry,
                engine.World.GetObject("krell"), engine.World.GetObject(other)), $"krell -> {other}");
        foreach (var other in new[] { "ferret", "mira", "rath", "krell" })
            Assert.False(PlayerKnows(engine, other), $"player -> {other}");
    }

    [Fact]
    public void LookingAtStrangers_TeachesNoNames()
    {
        var engine = LoadNail();
        var player = engine.World.GetObject("player");

        // examining the dockhand renders his incognito description — the
        // proper name "Ferret" appears nowhere
        var examine = engine.TurnManager.Execute(player, "examine", "ferret");
        Assert.Contains("leathery, sun-beaten dockhand", examine.Message);
        Assert.DoesNotContain("Ferret", examine.Message);
        Assert.False(PlayerKnows(engine, "ferret"));

        // the player's own description still names her (self-knowledge)
        var self = engine.TurnManager.Execute(player, "examine", "player");
        Assert.Contains("Nail — Nannan in her native tongue", self.Message);
    }

    [Fact]
    public void BothOfNailsNames_AreProperNames_ForOthersToLearn()
    {
        var engine = LoadNail();
        var world = engine.World;
        var ferret = world.GetObject("ferret");
        var player = world.GetObject("player");

        // ferret hears "Nannan" in the player's speech and learns HER
        var action = AEngine.Llm.PlanExecutor.MatchAvailableOrPotential(
            engine, player, "Say: \"Nannan is tired.\"");
        Assert.NotNull(action);
        engine.TurnManager.PerformAction(player, action, action.Text);
        Assert.True(Knowledge.KnowsName(engine.ModuleRegistry, ferret, player));

        // and can now render her by her real name (which carries it)
        Assert.Equal("Nail the pink kobold",
            Knowledge.NameFor(engine.ModuleRegistry, ferret, player));
    }

    [Fact]
    public void SelfIntroduction_VisiblyTeachesTheName()
    {
        var engine = LoadNail();
        var world = engine.World;
        var player = world.GetObject("player");
        var ferret = world.GetObject("ferret");

        // "Name's Ferret." — the introduction rides the speech signal
        var action = AEngine.Llm.PlanExecutor.MatchAvailableOrPotential(
            engine, ferret, "Say: \"New in port, are ye? Name's Ferret.\"");
        Assert.NotNull(action);
        engine.TurnManager.PerformAction(ferret, action, action.Text);

        // the player learned it, and every rendering flips to the real
        // name — the room listing, and his next line
        Assert.True(Knowledge.KnowsName(engine.ModuleRegistry, player, ferret));
        Assert.Equal("Ferret the old dockhand",
            Knowledge.NameFor(engine.ModuleRegistry, player, ferret));

        var again = AEngine.Llm.PlanExecutor.MatchAvailableOrPotential(
            engine, ferret, "Say: \"Ye look like ye've come a long way, little one.\"");
        Assert.NotNull(again);
        engine.TurnManager.PerformAction(ferret, again, again.Text);
        Assert.Contains(engine.SignalBus.Drain("player"),
            s => s.Text.Contains("Ferret the old dockhand says:"));
    }
}
