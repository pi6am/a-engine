using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.Scenarios;
using AEngine.Llm;

namespace AEngine.Tests;

/// <summary>
/// Speech variants: shout (broadcast-only, strength 2 — carries its
/// words through one portal, degrading beyond) and whisper
/// (directed-only, strength 0 — addressee only, a content-free murmur
/// for same-room bystanders, nothing through walls).
/// </summary>
public class SpeechVariantTests
{
    private const string VariantsJson = """
    [ { "id": "speech_variants", "name": "Speech variants", "fields": [],
        "affordances": [
          { "verb": "shout", "handler": "say", "speech": true, "prompt": "Shout what?",
            "speechTargets": "broadcast", "salience": 24,
            "signals": [
              { "sense": "audible", "priority": 12, "strength": 2, "salience": 10,
                "text": "{agent} shouts: \"{arg}\"",
                "degrade": [ { "below": 1, "text": "a {voice} voice shouting something" } ] }
            ] },
          { "verb": "whisper", "handler": "say", "speech": true, "prompt": "Whisper what?",
            "speechTargets": "directed", "salience": 24,
            "signals": [
              { "sense": "audible", "priority": 10, "audience": "onlyTarget", "strength": 0,
                "salience": 16, "text": "{agent} whispers to you: \"{arg}\"" },
              { "sense": "audible", "priority": 4, "audience": "exceptTarget", "strength": 0,
                "text": "{agent} murmurs something to {target}" }
            ] }
        ] } ]
    """;

    /// <summary>Alice in room_a with the variant verbs; bob joins her room, carol stays in room_b beyond the door.</summary>
    private static GameEngine NewEngine()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.ModuleRegistry.LoadJson(VariantsJson);
        engine.World.AddModule("alice", "speech_variants");
        engine.World.MoveObject("bob", "room_a");
        // carol starts in room_b (behind the door); tests move her as needed
        engine.World.CreateObject("carol", "room_b", "Carol");
        engine.World.AddModule("carol", "agent");
        return engine;
    }

    private static ActionResult Speak(GameEngine engine, string line)
    {
        var alice = engine.World.GetObject("alice");
        var action = PlanExecutor.MatchAvailableOrPotential(engine, alice, line);
        Assert.NotNull(action);
        return engine.TurnManager.PerformAction(alice, action!, action.Text);
    }

    [Fact]
    public void Shout_IsListedAsASingleBroadcastEntry_EvenInACrowd()
    {
        var engine = NewEngine();
        // three agents present (alice, bob, carol moved in)
        engine.World.MoveObject("carol", "room_a");

        var shouts = engine.ActionResolver.Resolve(engine.World.GetObject("alice"))
            .Where(a => a.Verb == "shout").ToList();
        var shout = Assert.Single(shouts);
        Assert.Equal("Shout: {speech}", shout.Label);
        Assert.Equal("alice", shout.TargetId); // undirected
    }

    [Fact]
    public void Whisper_IsDirectedOnly_AndVanishesWhenAlone()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");

        // one per other agent present — no undirected "Whisper: {speech}"
        var whispers = engine.ActionResolver.Resolve(alice)
            .Where(a => a.Verb == "whisper").ToList();
        var whisper = Assert.Single(whispers); // bob is present
        Assert.Equal("Whisper to Bob: {speech}", whisper.Label);
        Assert.Equal("bob", whisper.TargetId);

        // alone: no addressee, no whisper
        engine.World.MoveObject("bob", "room_b");
        Assert.DoesNotContain(engine.ActionResolver.Resolve(alice), a => a.Verb == "whisper");
    }

    [Fact]
    public void Shout_CarriesItsWordsThroughTheDoor()
    {
        var engine = NewEngine();

        var result = Speak(engine, "Shout: \"LAST CALL!\"");
        Assert.Equal("You shout: \"LAST CALL!\"", result.Message);

        // strength 2 minus one portal (attenuation 1) leaves 1: above the
        // degrade rung, so carol behind the door hears the words
        var heard = Assert.Single(engine.SignalBus.Drain("carol"));
        Assert.Equal("Alice shouts: \"LAST CALL!\" through the wooden door to the south.", heard.Text);
        Assert.True(heard.ThroughPortal);
    }

    [Fact]
    public void Shout_DegradesToAVoiceTwoRoomsAway()
    {
        var engine = NewEngine();
        // room_b -- room_c chain: shouting from room_a crosses two portals
        engine.World.CreateObject("room_c", AEngine.Core.World.World.RootId, "Room C");
        engine.World.AddModule("room_c", "room");
        engine.World.CreateObject("door_c", "room_b", "iron door");
        engine.World.AddModule("door_c", "portal");
        engine.World.SetFieldOverride("door_c", "portal", "to", AEngine.Core.World.World.ToJson("room_c"));
        engine.World.MoveObject("carol", "room_c");

        Speak(engine, "Shout: \"FIRE!\"");

        // 2 - 2 portals = 0: below the rung — content-free
        var heard = Assert.Single(engine.SignalBus.Drain("carol"));
        Assert.Equal("a muffled voice shouting something through the iron door.", heard.Text);
    }

    [Fact]
    public void Whisper_AddresseeHearsIt_BystandersGetAMurmur_NothingThroughWalls()
    {
        var engine = NewEngine();
        engine.World.MoveObject("carol", "room_a"); // bystander in the room
        var dave = engine.World.CreateObject("dave", "room_b", "Dave");
        engine.World.AddModule("dave", "agent");

        var result = Speak(engine, "Whisper to Bob: \"meet me outside\"");
        Assert.Equal("You whisper to Bob: \"meet me outside\"", result.Message);

        // the addressee gets the words...
        Assert.Equal("Alice whispers to you: \"meet me outside\"",
            Assert.Single(engine.SignalBus.Drain("bob")).Text);
        // ...the bystander learns a whisper happened, not what was said...
        Assert.Equal("Alice murmurs something to Bob",
            Assert.Single(engine.SignalBus.Drain("carol")).Text);
        // ...and the next room hears nothing at all (strength 0)
        Assert.Empty(engine.SignalBus.Drain("dave"));
    }

    [Fact]
    public void PlanParsing_ShoutIgnoresStrayAddressee()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");

        var shout = PlanExecutor.MatchAvailableOrPotential(engine, alice, "Shout to Bob: \"hi\"");
        Assert.NotNull(shout);
        Assert.Equal("shout", shout!.Verb);
        Assert.Equal("alice", shout.TargetId); // still the broadcast entry
    }

    [Fact]
    public void PlanParsing_WhisperNeedsAnUnambiguousAddressee()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");

        // one listener: an addressee-less whisper is unambiguous
        var toBob = PlanExecutor.MatchAvailableOrPotential(engine, alice, "whisper \"psst\"");
        Assert.NotNull(toBob);
        Assert.Equal("bob", toBob!.TargetId);

        // two listeners, no addressee: ambiguous — there is no undirected whisper
        engine.World.MoveObject("carol", "room_a");
        Assert.Null(PlanExecutor.MatchAvailableOrPotential(engine, alice, "whisper \"psst\""));

        // addressed whisper matches by name
        var named = PlanExecutor.MatchAvailableOrPotential(engine, alice, "Whisper to Carol: \"psst\"");
        Assert.NotNull(named);
        Assert.Equal("carol", named!.TargetId);
    }

    [Fact]
    public void SpeechVerbs_AreNeverOfferedFromAnotherAgentsModule()
    {
        var engine = NewEngine();
        // carol can shout/whisper too — her can_speak must not offer the
        // player "Shout the Carol"-style entries; every shout/whisper
        // entry is a parameterized {speech} one
        engine.World.AddModule("carol", "speech_variants");

        var labels = engine.ActionResolver.Resolve(engine.World.GetObject("alice"))
            .Where(a => a.Verb is "shout" or "whisper").Select(a => a.Label).ToList();
        Assert.NotEmpty(labels);
        Assert.All(labels, label => Assert.EndsWith("{speech}", label));
    }

    [Fact]
    public void WhisperByIncognitoRendering_DirectsAtTheStranger()
    {
        var engine = NewEngine();
        // alice tracks knowledge (knows nobody); bob renders incognito
        engine.ModuleRegistry.LoadJson("""
            [ { "id": "knowledge", "name": "Knowledge", "fields": [
                { "name": "knowsNames", "type": "list", "default": [] } ] } ]
            """);
        engine.World.AddModule("alice", "knowledge");
        engine.World.SetFieldOverride("bob", "agent", "incognito",
            AEngine.Core.World.World.ToJson("a burly stranger"));
        var alice = engine.World.GetObject("alice");

        // the advertised entry carries the incognito rendering...
        Assert.Contains(engine.ActionResolver.Resolve(alice),
            a => a.Verb == "whisper" && a.Label == "Whisper to a burly stranger: {speech}");
        // ...and a plan line echoing it finds bob — whisper has no
        // broadcast fallback, so this was a hard failure before the
        // knowledge-rendered match
        var match = PlanExecutor.MatchAvailableOrPotential(
            engine, alice, "Whisper to a burly stranger: \"psst\"");
        Assert.NotNull(match);
        Assert.Equal("bob", match!.TargetId);
    }

    private static string FindTavernDir()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "scenarios", "tavern", "world.json")))
            dir = dir.Parent;
        return dir is null
            ? throw new DirectoryNotFoundException("Could not locate scenarios/tavern.")
            : Path.Combine(dir.FullName, "scenarios", "tavern");
    }

    [Fact]
    public void Tavern_ShoutAndWhisperAreWiredIntoCanSpeak()
    {
        var dir = FindTavernDir();
        var engine = GameEngine.CreateWithBuiltinHandlers();
        ScenarioLoader.LoadFrom(engine, dir);

        // mira tends bar among a crowd
        var actions = engine.ActionResolver.Resolve(engine.World.GetObject("mira")).ToList();
        Assert.Contains(actions, a => a.Verb == "shout" && a.Label == "Shout: {speech}");
        Assert.Contains(actions, a => a.Verb == "whisper" && a.Label.StartsWith("Whisper to "));
        Assert.DoesNotContain(actions, a => a.Verb == "whisper" && a.Label == "Whisper: {speech}");
    }
}
