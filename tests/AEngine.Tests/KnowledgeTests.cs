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
}
