using AEngine.Core.Actions;
using AEngine.Core.Modules;
using AEngine.Core.Runtime;
using AEngine.Core.World;
using AEngine.Llm;

namespace AEngine.Tests;

/// <summary>
/// Part-targeted actions and chatter as engine features, tested against
/// a neutral fixture: per-part listing with clothing exposure, owner
/// delivery of onlyTarget signals, state-driven reaction defaults
/// (defaultWhen), chatter objects, give's item naming, exit endings,
/// and touch plan parsing. Handlers here are the built-ins — scenario
/// handlers (a touch simulation) live in their own projects.
/// </summary>
public partial class TouchTests
{
    private const string ModulesJson = """
    [ { "id": "room", "name": "Room", "fields": [], "affordances": [] },
      { "id": "agent", "name": "Agent",
        "fields": [ { "name": "policy", "type": "string", "default": "player" },
                    { "name": "posture", "type": "string", "default": "standing" },
                    { "name": "activity", "type": "string", "default": "" },
                    { "name": "speakingSoon", "type": "bool", "default": false } ],
        "affordances": [ { "verb": "look", "handler": "look", "repeatBackoff": true },
                         { "verb": "wait", "handler": "wait", "repeatBackoff": true },
                         { "verb": "say", "handler": "say", "speech": true, "prompt": "Say what?" } ] },
      { "id": "mood", "name": "Mood",
        "fields": [ { "name": "comfort", "type": "number", "default": 50.0 } ],
        "affordances": [] },
      { "id": "body", "name": "Body",
        "fields": [ { "name": "regions", "type": "list", "default": ["top", "bottom"] } ],
        "affordances": [] },
      { "id": "bodypart", "name": "Body Part",
        "fields": [ { "name": "region", "type": "string", "default": "" },
                    { "name": "intimate", "type": "bool", "default": false },
                    { "name": "sensitivity", "type": "number", "default": 1.0 } ],
        "affordances": [] },
      { "id": "wearable", "name": "Wearable",
        "fields": [ { "name": "regions", "type": "list", "default": [] },
                    { "name": "worn", "type": "bool", "default": false } ],
        "affordances": [] },
      { "id": "portable", "name": "Portable", "fields": [],
        "affordances": [ { "verb": "drop", "handler": "drop", "duration": 1 },
                         { "verb": "give", "handler": "give", "duration": 2,
                           "signals": [ { "sense": "visual", "priority": 5,
                                          "text": "{agent} hands the {item} to {target}." } ] } ] },
      { "id": "chatter", "name": "Chatter",
        "fields": [ { "name": "on", "type": "bool", "default": false },
                    { "name": "channel", "type": "string", "default": "news" },
                    { "name": "channels", "type": "map", "default": {} },
                    { "name": "interval", "type": "number", "default": 40.0 },
                    { "name": "elapsed", "type": "int", "default": 0 },
                    { "name": "nextDue", "type": "int", "default": 0 } ],
        "affordances": [ { "verb": "turnon", "handler": "set", "duration": 2,
                           "label": "Turn on the {target}",
                           "when": [ { "module": "chatter", "field": "on", "equals": false } ],
                           "data": { "module": "chatter", "field": "on", "value": "true",
                                     "self": "{target} starts up." } } ] },
      { "id": "touch", "name": "Touch", "fields": [],
        "affordances": [
          { "verb": "kiss", "handler": "basic", "targetParts": true, "duration": 3,
            "reaction": { "window": 3, "telegraph": "{agent} leans in to kiss {holder}.",
              "options": [
                { "id": "melt", "label": "Melt into it", "noResist": true,
                  "defaultWhen": { "module": "mood", "field": "comfort", "min": 55 },
                  "text": "You melt into it.", "report": "{agent} melts into it." },
                { "id": "stop", "label": "Ease back", "noResist": true, "default": true,
                  "text": "You ease back.", "report": "{agent} eases back." } ] },
            "signals": [ { "sense": "visual", "priority": 8, "audience": "onlyTarget",
                           "text": "{agent} kisses your {target}." },
                         { "sense": "visual", "priority": 6,
                           "text": "{agent} kisses {holder}'s {target}." } ] },
          { "verb": "stroke", "handler": "basic", "targetParts": true, "intimateParts": true,
            "duration": 4,
            "gates": [ { "kind": "exposed", "failText": "Clothes are in the way of that." } ] } ] },
      { "id": "condition", "name": "Condition",
        "fields": [ { "name": "kind", "type": "string", "default": "" },
                    { "name": "label", "type": "string", "default": "" },
                    { "name": "visible", "type": "bool", "default": true },
                    { "name": "selfText", "type": "string", "default": "" },
                    { "name": "clearText", "type": "string", "default": "" },
                    { "name": "traits", "type": "string", "default": "" } ],
        "affordances": [] },
      { "id": "exit", "name": "Exit",
        "fields": [ { "name": "text", "type": "string", "default": "" },
                    { "name": "endings", "type": "map", "default": {} } ],
        "affordances": [ { "verb": "leave", "handler": "leave", "duration": 5,
                           "playerOnly": true, "label": "Go home" } ] }
    ]
    """;

    static GameEngine NewFixture()
    {
        var engine = TestWorlds.NewEngine();
        engine.ModuleRegistry.LoadJson(ModulesJson);
        var world = engine.World;
        world.CreateObject("room_a", World.RootId, "Room A");
        world.AddModule("room_a", "room");
        world.CreateObject("tv", "room_a", "radio");
        world.AddModule("tv", "chatter");
        world.SetFieldOverride("tv", "chatter", "interval", World.ToJson(40.0));
        world.SetFieldOverride("tv", "chatter", "channels", World.ToJson(
            new Dictionary<string, List<string>>
            {
                ["news"] = ["…the council deferred the vote again…", "…stocks closed mixed…"],
                ["test"] = ["a test jingle plays."],
            }));
        world.CreateObject("exitdoor", "room_a", "front door");
        world.AddModule("exitdoor", "exit");
        world.SetFieldOverride("exitdoor", "exit", "text", World.ToJson("You say your goodbyes."));
        world.SetFieldOverride("exitdoor", "exit", "endings", World.ToJson(
            new Dictionary<string, string> { ["satisfied"] = "They wave until the corner." }));

        foreach (var id in new[] { "player", "sam" })
        {
            world.CreateObject(id, "room_a", id == "player" ? "Riley" : "Sam");
            world.AddModule(id, "agent");
            world.AddModule(id, "touch");
            world.AddModule(id, "body");
            world.AddModule(id, "mood");
            world.SetFieldOverride(id, "mood", "comfort", World.ToJson(id == "player" ? 45.0 : 52.0));
        }
        world.SetFieldOverride("sam", "agent", "policy", World.ToJson("auto"));

        foreach (var (owner, part, region, intimate) in new[]
                 {
                     ("player", "lips", "", false), ("player", "neck", "", false),
                     ("player", "chest", "top", false), ("player", "sex", "bottom", true),
                     ("sam", "lips", "", false), ("sam", "neck", "", false),
                     ("sam", "shoulders", "", false), ("sam", "chest", "top", true),
                     ("sam", "thighs", "bottom", true), ("sam", "sex", "bottom", true),
                 })
        {
            var id = $"{owner}_{part}";
            world.CreateObject(id, owner, part);
            world.AddModule(id, "bodypart");
            if (region.Length > 0)
                world.SetFieldOverride(id, "bodypart", "region", World.ToJson(region));
            world.SetFieldOverride(id, "bodypart", "intimate", World.ToJson(intimate));
        }

        foreach (var (id, owner, name, regions) in new[]
                 {
                     ("g_shirt", "player", "shirt", new[] { "top" }),
                     ("g_trousers", "player", "trousers", new[] { "bottom" }),
                     ("g_sweater", "sam", "sweater", new[] { "top" }),
                     ("g_underwear", "sam", "underwear", new[] { "bottom" }),
                 })
        {
            world.CreateObject(id, owner, name);
            world.AddModule(id, "portable");
            world.AddModule(id, "wearable");
            world.SetFieldOverride(id, "wearable", "regions", World.ToJson(regions));
            world.SetFieldOverride(id, "wearable", "worn", World.ToJson(true));
        }

        foreach (var (id, kind) in new[] { ("cond_satisfied", "satisfied") })
        {
            world.CreateObject(id, World.RootId, id);
            world.AddModule(id, "condition");
            world.SetFieldOverride(id, "condition", "kind", World.ToJson(kind));
        }
        engine.TurnManager.EvaluateUpkeep();
        return engine;
    }

    [Fact]
    public void TouchListing_HidesIntimatePartsWhileDressed()
    {
        var engine = NewFixture();
        var labels = engine.ActionResolver.Resolve(engine.World.GetObject("player"))
            .Where(a => a.Verb is "kiss" or "stroke")
            .Select(a => a.Label).ToList();

        Assert.Contains("Kiss Sam's lips", labels);
        Assert.Contains("Kiss Sam's shoulders", labels);
        Assert.DoesNotContain(labels, l => l.Contains("chest"));
        Assert.DoesNotContain(labels, l => l.Contains("thighs"));
        Assert.DoesNotContain(labels, l => l.Contains("sex"));

        // uncovered: the intimate parts list
        engine.World.SetFieldOverride("g_underwear", "wearable", "worn", World.ToJson(false));
        labels = engine.ActionResolver.Resolve(engine.World.GetObject("player"))
            .Where(a => a.Verb is "kiss" or "stroke").Select(a => a.Label).ToList();
        Assert.Contains("Stroke Sam's thighs", labels);
        Assert.Contains("Stroke Sam's sex", labels);
    }

    [Fact]
    public void Kiss_ReachesThePartsOwnerAsYou()
    {
        var engine = NewFixture();
        var player = engine.World.GetObject("player");

        engine.TurnManager.PerformAction(player,
            TestWorlds.Find(engine, "player", "kiss", "sam_lips"));

        // the owner receives the audience-restricted form, possessives
        // rendered for them ("your lips", never "you's"), alongside the
        // telegraph and her defaulted response
        Assert.Contains(engine.SignalBus.Drain("sam"),
            s => s.Text == "Riley kisses your lips.");
    }

    [Fact]
    public void DefaultWhen_TheReactionDefaultFollowsDefenderState()
    {
        var engine = NewFixture();
        var player = engine.World.GetObject("player");
        // an externally-driven defender: the pending survives for
        // inspection (auto policies resolve instantly via the same
        // default machinery)
        engine.World.SetFieldOverride("sam", "agent", "policy", World.ToJson("player"));

        // at low comfort the default deflection applies
        engine.World.SetFieldOverride("sam", "mood", "comfort", World.ToJson(40.0));
        engine.TurnManager.PerformAction(player,
            TestWorlds.Find(engine, "player", "kiss", "sam_lips"));
        var cool = Assert.Single(engine.Reactions.Pending);
        Assert.Equal("stop", engine.Reactions.EffectiveDefault(cool).Id);
        engine.Reactions.ForceDefault(cool.Id);
        Assert.Contains(engine.SignalBus.Drain("sam"), s => s.Text == "You ease back.");

        // warmed up, the same telegraph melts by default — no policy or
        // LLM round-trip
        engine.World.SetFieldOverride("sam", "mood", "comfort", World.ToJson(60.0));
        engine.TurnManager.PerformAction(player,
            TestWorlds.Find(engine, "player", "kiss", "sam_neck"));
        var warm = Assert.Single(engine.Reactions.Pending);
        Assert.Equal("melt", engine.Reactions.EffectiveDefault(warm).Id);
    }

    [Fact]
    public void Chatter_OffByDefault_EmitsOnlyWhenOn_FromItsChannelPool()
    {
        var engine = NewFixture();
        Chatter.Advance(engine, 500);
        Assert.Empty(engine.SignalBus.Drain("player")); // off

        engine.World.SetFieldOverride("tv", "chatter", "on", World.ToJson(true));
        engine.World.SetFieldOverride("tv", "chatter", "nextDue", World.ToJson(1));
        Chatter.Advance(engine, 5);
        Assert.Single(engine.SignalBus.Drain("player")); // one line per due interval

        engine.World.SetFieldOverride("tv", "chatter", "channel", World.ToJson("test"));
        engine.World.SetFieldOverride("tv", "chatter", "elapsed", World.ToJson(0));
        engine.World.SetFieldOverride("tv", "chatter", "nextDue", World.ToJson(1));
        Chatter.Advance(engine, 5);
        Assert.Equal("a test jingle plays.",
            Assert.Single(engine.SignalBus.Drain("player")).Text);
    }

    [Fact]
    public void SetHandler_FlipsFieldsFromData()
    {
        var engine = NewFixture();
        var player = engine.World.GetObject("player");
        Assert.False(engine.ModuleRegistry.ResolveBool(
            engine.World.GetObject("tv"), "chatter", "on"));

        engine.TurnManager.PerformAction(player,
            TestWorlds.Find(engine, "player", "turnon", "tv"));
        Assert.True(engine.ModuleRegistry.ResolveBool(
            engine.World.GetObject("tv"), "chatter", "on"));
    }

    [Fact]
    public void Give_RecipientSeesTheItemNamed()
    {
        var engine = NewFixture();
        engine.World.MoveObject("g_underwear", "sam");
        engine.World.SetFieldOverride("g_underwear", "wearable", "worn", World.ToJson(false));
        engine.World.GetObject("g_underwear").Name = "spare scarf";

        var give = TestWorlds.Find(engine, "sam", "give", "player");
        var result = engine.TurnManager.PerformAction(engine.World.GetObject("sam"), give);
        Assert.Equal(ActionOutcome.Success, result.Outcome);

        Assert.Equal("Sam hands the spare scarf to you.",
            Assert.Single(engine.SignalBus.Drain("player")).Text);
        Assert.Equal("player", engine.World.GetObject("g_underwear").Parent);
    }

    [Fact]
    public void Leaving_AppendsEndingSuffixesForActorConditions()
    {
        var engine = NewFixture();
        var player = engine.World.GetObject("player");

        var plain = engine.TurnManager.PerformAction(
            player, TestWorlds.Find(engine, "player", "leave", "exitdoor"));
        Assert.True(plain.EndsGame);
        Assert.DoesNotContain("wave until the corner", plain.Message);

        var engine2 = NewFixture();
        Conditions.Attach(engine2.World, engine2.ModuleRegistry,
            engine2.World.GetObject("player"), "cond_satisfied");
        var sweet = engine2.TurnManager.PerformAction(
            engine2.World.GetObject("player"), TestWorlds.Find(engine2, "player", "leave", "exitdoor"));
        Assert.Contains("wave until the corner", sweet.Message);
    }

    [Fact]
    public void HealthReporting_SkipsPartsWithoutHealthPools()
    {
        var engine = NewFixture();
        var sam = engine.World.GetObject("sam");

        Assert.Empty(Condition.SelfLines(engine.World, engine.ModuleRegistry, sam));
        Assert.Empty(Condition.ExamineLines(engine.World, engine.ModuleRegistry, sam));
        Assert.Null(Condition.Pool(engine.World, engine.ModuleRegistry, sam));
        Assert.False(Health.IsIncapacitated(engine.World, engine.ModuleRegistry, sam));

        var context = new AgentContextBuilder(engine).BuildContext(sam, npc: false);
        Assert.Contains("Room A", context);
    }

    [Fact]
    public void PlanParsing_FindsPartTouches()
    {
        var engine = NewFixture();
        var player = engine.World.GetObject("player");

        var kiss = PlanExecutor.MatchAvailableOrPotential(engine, player, "Kiss Sam's neck");
        Assert.NotNull(kiss);
        Assert.Equal("sam_neck", kiss!.TargetId);

        var bare = PlanExecutor.MatchAvailableOrPotential(engine, player, "kiss her neck");
        Assert.NotNull(bare);
        Assert.Equal("sam_neck", bare!.TargetId);
    }
}
