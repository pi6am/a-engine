using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.World;
using AEngine.Llm;
using CoreWorld = AEngine.Core.World.World;

namespace AEngine.Tests;

/// <summary>
/// Salience-ranked memory: entries age out by score (salience − age);
/// being addressed, private sensations, and own actions buy
/// age-resistance (memorySalienceBoost, default 8); per-signal data
/// overrides adjust it (bomb high, jukebox low). The newest entry is
/// never evicted by its own arrival.
/// </summary>
public class MemorySalienceTests
{
    private static GameEngine NewEngine()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.World.MoveObject("bob", "room_a");
        engine.World.CreateObject("carol", "room_a", "Carol");
        engine.World.AddModule("carol", "agent");
        engine.World.AddModule("carol", "can_speak");
        return engine;
    }

    private static void Chatter(GameEngine engine, WorldObject agent, int count) =>
        Chatter(engine, agent, count, 0);

    private static void Chatter(GameEngine engine, WorldObject agent, int count, int from)
    {
        for (var i = from; i < from + count; i++)
            engine.Memory.Record(agent, $"ambient event {i}");
    }

    private static List<string> Recall(GameEngine engine, string agentId) =>
        [.. engine.Memory.Recall(agentId)];

    [Fact]
    public void UniformSalience_EvictsOldest_FirstInFirstOut()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice"); // memoryLength 25

        Chatter(engine, alice, 30);
        var memory = Recall(engine, "alice");
        Assert.Equal(25, memory.Count);
        Assert.DoesNotContain("ambient event 0", memory); // oldest evicted
        Assert.Contains("ambient event 29", memory);
    }

    [Fact]
    public void AddressedMessage_SurvivesAmbientChatter()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        var action = PlanExecutor.MatchAvailableOrPotential(
            engine, alice, "Say to Bob: \"bring me an ale\"");
        engine.TurnManager.PerformAction(alice, action!, action.Text);

        // a wall of ambient events washes over bob (capacity 25)
        var bob = engine.World.GetObject("bob");
        Chatter(engine, bob, 30);

        var memory = Recall(engine, "bob");
        Assert.Contains("Alice says to you: \"bring me an ale\"", memory);
        // while the oldest chatter fell away
        Assert.DoesNotContain("ambient event 0", memory);
    }

    [Fact]
    public void AllHighBuffer_LowArrival_IsKept_AndEvictsStalestHigh()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");

        // fill the whole buffer with high-salience entries
        Chatter(engine, alice, 25);
        for (var i = 0; i < 25; i++)
            engine.Memory.Record(alice, $"important {i}", salience: 8);
        // ...all ambient chatter was already evicted by the highs
        Assert.All(Recall(engine, "alice"), m => m.StartsWith("important"));

        // the buffer is entirely high, and a low arrives: it is still
        // delivered — the stalest high goes instead
        engine.Memory.Record(alice, "humble event");
        var memory = Recall(engine, "alice");
        Assert.Contains("humble event", memory);
        Assert.DoesNotContain("important 0", memory); // oldest high evicted
        Assert.Contains("important 24", memory);
    }

    [Fact]
    public void NewestIsNeverEvictedByItsOwnArrival_EvenWhenOutranked()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        // a one-slot memory makes the hand-off explicit
        engine.World.SetFieldOverride("alice", "agent", "memoryLength", CoreWorld.ToJson(1));

        engine.Memory.Record(alice, "important", salience: 20);
        // the newcomer is always delivered — the incumbent goes instead,
        // however salient
        engine.Memory.Record(alice, "whisper in the din");
        Assert.Equal(["whisper in the din"], Recall(engine, "alice"));

        // and the next arrival may legitimately take it back out
        engine.Memory.Record(alice, "another arrival");
        Assert.Equal(["another arrival"], Recall(engine, "alice"));
    }

    [Fact]
    public void StaleHigh_LosesToFreshAmbient_SalienceBuysAgeNotImmunity()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");

        engine.Memory.Record(alice, "addressed long ago", salience: 8); // seq 1
        // 25 ambients: one eviction, and it's the oldest ambient (score
        // −25), not the addressed entry (8 − 25 = −17)
        for (var i = 0; i < 25; i++)
            engine.Memory.Record(alice, $"recent ambient {i}");
        Assert.Contains("addressed long ago", Recall(engine, "alice"));
        Assert.DoesNotContain("recent ambient 0", Recall(engine, "alice"));

        // keep the flood coming: 15 more and the addressed entry (score
        // 8 − 40 = −32) is among the weakest 16 — age beats stale salience
        for (var i = 25; i < 40; i++)
            engine.Memory.Record(alice, $"recent ambient {i}");
        Assert.DoesNotContain("addressed long ago", Recall(engine, "alice"));
        Assert.Contains("recent ambient 39", Recall(engine, "alice"));
    }

    [Fact]
    public void PrivateSensations_AndOwnActions_AreHighSalience()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");

        // own action outcome (recorded by TurnManager with the boost)
        engine.TurnManager.PerformAction(
            alice, TestWorlds.Find(engine, "alice", "look"));
        // private sensation
        engine.SignalBus.SendTo(alice, "You feel tipsy.");
        Chatter(engine, alice, 25);

        var memory = Recall(engine, "alice");
        Assert.Contains("You feel tipsy.", memory);
        Assert.Contains("You look around.", memory);
    }

    [Fact]
    public void Waiting_IsIdleFiller_AndAgesOutBeforeChatter()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");

        // a wait and a real action, then chatter to overflowing
        engine.TurnManager.PerformAction(
            alice, TestWorlds.Find(engine, "alice", "wait"));
        engine.TurnManager.PerformAction(
            alice, TestWorlds.Find(engine, "alice", "look"));
        Chatter(engine, alice, 24); // 26 entries: one eviction

        // the penalized wait is the weakest entry and goes first
        Assert.DoesNotContain("You wait.", Recall(engine, "alice"));
        // the look survives (boosted, and snapshot-superseded anyway)
        Assert.Contains("You look around.", Recall(engine, "alice"));
    }

    [Fact]
    public void SpecSalience_OverridesInBothDirections()
    {
        var engine = NewEngine();
        engine.ModuleRegistry.LoadJson("""
        [
          { "id": "events", "name": "Events", "fields": [],
            "affordances": [
              { "verb": "detonate", "handler": "basic",
                "signals": [ { "sense": "audible", "priority": 9, "salience": 12,
                               "text": "BOOM" } ] },
              { "verb": "play", "handler": "basic",
                "signals": [ { "sense": "audible", "priority": 9, "salience": -5,
                               "text": "the jukebox mutters" } ] }
            ] }
        ]
        """);
        var world = engine.World;
        world.CreateObject("bomb", "room_a", "bomb");
        world.AddModule("bomb", "events");
        world.CreateObject("jukebox", "room_a", "jukebox");
        world.AddModule("jukebox", "events");
        var alice = world.GetObject("alice");
        var bob = world.GetObject("bob"); // same room: receives the signals

        engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "detonate", "bomb"));
        engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "play", "jukebox"));
        // salience rides the delivered signals into the OBSERVER's memory
        var detailed = engine.Memory.RecallDetailed("bob");
        Assert.Contains(detailed, e => e.Text == "BOOM" && e.Salience == 12);
        Assert.Contains(detailed, e => e.Text == "the jukebox mutters" && e.Salience == -5);

        // under pressure the blast outlives plain chatter and the jukebox
        // doesn't: jukebox (−5) evicts before ambient (0)
        for (var i = 0; i < 25; i++)
            engine.Memory.Record(bob, $"chatter {i}");
        var memory = Recall(engine, "bob");
        Assert.Contains("BOOM", memory);
        Assert.DoesNotContain("the jukebox mutters", memory);
    }

    [Fact]
    public void BoostIsDataDriven_PerAgent()
    {
        var engine = NewEngine();
        var world = engine.World;
        var alice = world.GetObject("alice");
        world.SetFieldOverride("alice", "agent", "memorySalienceBoost", CoreWorld.ToJson(2));

        Assert.Equal(2, engine.Memory.SalienceBoostOf(alice));
        Assert.Equal(AgentMemory.DefaultSalienceBoost,
            engine.Memory.SalienceBoostOf(world.GetObject("bob")));

        engine.Memory.Record(alice, "addressed", salience: 2); // seq 1
        for (var i = 0; i < 25; i++)
            engine.Memory.Record(alice, $"later ambient {i}");
        engine.Memory.Record(alice, "one more"); // seq 27 → two evictions
        // addressed: 2 − 26 = −24; the oldest ambient scores −25, the
        // second-oldest −24 — the tie breaks to the older seq, so the
        // weakly-boosted addressed entry goes while "one more" (newest,
        // never self-evicted) stays
        Assert.DoesNotContain("addressed", Recall(engine, "alice"));
        Assert.Contains("one more", Recall(engine, "alice"));
    }

    [Fact]
    public void SnapshotSupersedeAndDuplicates_UnaffectedBySalience()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");

        engine.Memory.Record(alice, "old look", snapshotKey: "look", salience: 0);
        engine.Memory.Record(alice, "fresh look", snapshotKey: "look", salience: 0);
        Assert.Single(Recall(engine, "alice"));

        engine.Memory.Record(alice, "You wait.", salience: 8);
        engine.Memory.Record(alice, "You wait.", salience: 8); // duplicate dropped
        Assert.Equal(["fresh look", "You wait."], Recall(engine, "alice"));
    }

    [Fact]
    public void RecallDetailed_ReportsLiveScores()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");

        engine.Memory.Record(alice, "addressed", salience: 8);
        engine.Memory.Record(alice, "ambient");

        var detailed = engine.Memory.RecallDetailed("alice");
        Assert.Equal(2, detailed.Count);
        // scores are salience minus age, computed at recall time
        Assert.Equal(8 - 1, detailed[0].Score);
        Assert.Equal(0, detailed[1].Score);
    }

    [Fact]
    public void AffordanceSalience_OverridesTheOwnActionBoost()
    {
        var engine = NewEngine();
        engine.ModuleRegistry.LoadJson("""
        [
          { "id": "conversational", "name": "Conversational", "fields": [],
            "affordances": [
              { "verb": "flatter", "handler": "basic", "salience": 24,
                "signals": [ { "sense": "audible", "priority": 5, "text": "{agent} flatters {target}." } ] }
            ] }
        ]
        """);
        var world = engine.World;
        world.CreateObject("mirror", "room_a", "mirror");
        world.AddModule("mirror", "conversational");
        var alice = world.GetObject("alice");

        var result = engine.TurnManager.PerformAction(
            alice, TestWorlds.Find(engine, "alice", "flatter", "mirror"));
        Assert.Equal(ActionOutcome.Success, result.Outcome);

        // the actor's own entry recorded at the affordance's declared
        // salience, not the generic boost
        Assert.Contains(engine.Memory.RecallDetailed("alice"),
            e => e.Text == "You flatter the mirror." && e.Salience == 24);

        // and it outlives a chatter flood that would drown a boost-8 entry
        Chatter(engine, alice, 30);
        Assert.Contains("You flatter the mirror.", Recall(engine, "alice"));
    }

    [Fact]
    public void Tavern_ConversationSalience_BothSides_OutliveBarChatter()
    {
        var engine = LoadTavern();
        var world = engine.World;
        var player = world.GetObject("player");
        world.MoveObject("player", "tavern");

        var action = PlanExecutor.MatchAvailableOrPotential(
            engine, player, "Say to Nix the goblin: \"Hey Nix, what's up?\"");
        Assert.NotNull(action);
        engine.TurnManager.PerformAction(player, action, action.Text);

        // speaker's own line: affordance-level salience 24
        Assert.Contains(engine.Memory.RecallDetailed("player"),
            e => e.Text == "You say to Nix the goblin: \"Hey Nix, what's up?\"" && e.Salience == 24);
        // addressee: addressed boost 8 + directed-spec salience 16 = 24
        Assert.Contains(engine.Memory.RecallDetailed("nix"),
            e => e.Text == "the human stranger says to you: \"Hey Nix, what's up?\"" && e.Salience == 24);
        // an over-hearing bystander keeps the cheap ambient form
        Assert.Contains(engine.Memory.RecallDetailed("mira"),
            e => e.Text == "the human stranger says: \"Hey Nix, what's up?\"" && e.Salience == 0);
    }

    private static GameEngine LoadTavern()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scenarios", "tavern");
            if (File.Exists(Path.Combine(candidate, "world.json")))
            {
                var engine = GameEngine.CreateWithBuiltinHandlers();
                Core.Scenarios.ScenarioLoader.LoadInto(
                    engine,
                    Path.Combine(candidate, "modules.json"),
                    Path.Combine(candidate, "world.json"));
                return engine;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate scenarios/tavern.");
    }
}
