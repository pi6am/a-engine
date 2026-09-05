using System.Text.Json;
using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.World;
using CoreWorld = AEngine.Core.World.World;

namespace AEngine.Tests;

/// <summary>
/// The motive simulation machinery as a generic engine feature, tested
/// against a neutral fixture: drift modes (linear stop-at-target,
/// exact exponential approach), delta routing, snapshot order
/// independence, sub-step parity, bands with capacity scaling,
/// threshold events (sets/adjusts, silent bands, attach, texts), and
/// the impulse API. The drunkenness/intimacy semantics these replace
/// are pinned by the migrated Metabolism/Intimacy tests.
/// </summary>
public class MotivesTests
{
    private const string ModulesJson = """
    [
      {
        "id": "condition", "name": "Condition",
        "fields": [
          { "name": "kind", "type": "string", "default": "" },
          { "name": "label", "type": "string", "default": "" },
          { "name": "visible", "type": "bool", "default": true },
          { "name": "selfText", "type": "string", "default": "" },
          { "name": "clearText", "type": "string", "default": "" }
        ],
        "affordances": []
      },
      {
        "id": "mood", "name": "Mood",
        "fields": [
          { "name": "value", "type": "number", "default": 0.0 },
          { "name": "other", "type": "number", "default": 0.0 },
          { "name": "bank", "type": "number", "default": 0.0 },
          { "name": "setpoint", "type": "number", "default": 0.0 },
          { "name": "seek", "type": "number", "default": 0.0 },
          { "name": "wide", "type": "number", "default": 0.0 },
          { "name": "capacity", "type": "number", "default": 1.0 },
          { "name": "motives", "type": "list", "default": [
            { "id": "value", "drift": [ { "mode": "linear", "target": 0, "rate": 0.01 } ] },
            { "id": "other", "drift": [ { "mode": "linear", "target": 0.3, "rate": 0.01 } ] },
            { "id": "bank" },
            { "id": "setpoint" },
            { "id": "seek", "drift": [ { "mode": "proportional", "target": "setpoint", "rate": 0.1 } ] },
            { "id": "wide", "min": 0, "max": null,
              "scaleField": "capacity",
              "drift": [ { "mode": "linear", "target": 0, "rate": 0.01 } ],
              "routes": [ { "to": "bank", "factor": 2.0 } ],
              "bands": [ { "min": 0.25, "condition": "cond_warm" },
                         { "min": 0.5, "condition": "cond_hot" } ] }
          ] }
        ],
        "affordances": []
      },
      {
        "id": "swing", "name": "Swing",
        "fields": [
          { "name": "heat", "type": "number", "default": 0.0 },
          { "name": "cool", "type": "number", "default": 0.0 },
          { "name": "drain", "type": "number", "default": 0.0 },
          { "name": "pool", "type": "number", "default": 0.0 },
          { "name": "maxStep", "type": "number", "default": 1.0 },
          { "name": "motives", "type": "list", "default": [
            { "id": "heat", "drift": [
                { "when": [ { "field": "cool", "min": 0.5 } ], "rate": 0.01 },
                { "rate": -0.01 } ] },
            { "id": "cool", "drift": [ { "mode": "proportional", "target": "heat", "rate": 0.1 } ] },
            { "id": "drain", "drift": [ { "mode": "linear", "target": 0, "rate": 0.05 } ],
              "routes": [ { "to": "pool", "factor": 1.0 } ],
              "onEmpty": { "id": "dried", "self": "The drain runs dry." } },
            { "id": "pool" }
          ] }
        ],
        "affordances": []
      },
      {
        "id": "climax", "name": "Climax",
        "fields": [
          { "name": "urge", "type": "number", "default": 0.0 },
          { "name": "after", "type": "number", "default": 0.0 },
          { "name": "base", "type": "number", "default": 0.5 },
          { "name": "motives", "type": "list", "default": [
            { "id": "urge",
              "bands": [ { "min": 0.6, "condition": "cond_flushed" } ],
              "onFull": { "id": "peak", "set": { "urge": 0, "after": 0.25 },
                "adjust": { "base": -0.1 },
                "attach": [ "cond_spent" ],
                "self": "It crests, and breaks.",
                "signal": { "sense": "audible", "priority": 8, "salience": 14,
                            "text": "{agent} shudders." },
                "silentBands": true } },
            { "id": "after" },
            { "id": "base" }
          ] }
        ],
        "affordances": []
      }
    ]
    """;

    // the swing module's motive list, reversed — the override for the
    // order-independence test (evaluation order must not matter)
    private const string SwingReversed = """
    [
      { "id": "pool" },
      { "id": "drain", "drift": [ { "mode": "linear", "target": 0, "rate": 0.05 } ],
        "routes": [ { "to": "pool", "factor": 1.0 } ],
        "onEmpty": { "id": "dried", "self": "The drain runs dry." } },
      { "id": "cool", "drift": [ { "mode": "proportional", "target": "heat", "rate": 0.1 } ] },
      { "id": "heat", "drift": [
          { "when": [ { "field": "cool", "min": 0.5 } ], "rate": 0.01 },
          { "rate": -0.01 } ] }
    ]
    """;

    private static GameEngine NewEngine()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.ModuleRegistry.LoadJson(ModulesJson);
        var world = engine.World;
        void Template(string id, string kind, string? selfText = null, string? clearText = null)
        {
            world.CreateObject(id, CoreWorld.RootId, $"{kind} template");
            world.AddModule(id, "condition");
            world.SetFieldOverride(id, "condition", "kind", CoreWorld.ToJson(kind));
            if (selfText is not null)
                world.SetFieldOverride(id, "condition", "selfText", CoreWorld.ToJson(selfText));
            if (clearText is not null)
                world.SetFieldOverride(id, "condition", "clearText", CoreWorld.ToJson(clearText));
        }
        Template("cond_warm", "warm", "You feel warm.");
        Template("cond_hot", "hot", "You are hot!", "You cool down.");
        Template("cond_flushed", "flushed", "You are flushed.");
        Template("cond_spent", "spent", "You are spent.");
        return engine;
    }

    private static double Get(GameEngine engine, string agentId, string moduleId, string field) =>
        engine.ModuleRegistry.ResolveDouble(engine.World.GetObject(agentId), moduleId, field);

    private static void Set(GameEngine engine, string agentId, string moduleId, string field, double value) =>
        engine.World.SetFieldOverride(agentId, moduleId, field, CoreWorld.ToJson(value));

    [Fact]
    public void LinearDrift_DecaysToZero_FromAbove()
    {
        var engine = NewEngine();
        engine.World.AddModule("alice", "mood");
        Set(engine, "alice", "mood", "value", 0.5);

        Motives.Advance(engine, 20); // rate 0.01/s → 0.2 burned

        Assert.Equal(0.3, Get(engine, "alice", "mood", "value"), 6);
    }

    [Fact]
    public void LinearDrift_StopsAtTarget_NoOvershootFromEitherSide()
    {
        var engine = NewEngine();
        engine.World.AddModule("alice", "mood");
        // above the 0.3 target, a big step clamps at it exactly
        Set(engine, "alice", "mood", "other", 0.35);
        Motives.Advance(engine, 100);
        Assert.Equal(0.3, Get(engine, "alice", "mood", "other"), 9);

        // below the target, drift rises up to it and no further
        Set(engine, "alice", "mood", "other", 0.28);
        Motives.Advance(engine, 100);
        Assert.Equal(0.3, Get(engine, "alice", "mood", "other"), 9);
    }

    [Fact]
    public void ProportionalDrift_ApproachesMotiveTarget_StableAtHugeSteps()
    {
        var engine = NewEngine();
        engine.World.AddModule("alice", "mood");
        Set(engine, "alice", "mood", "setpoint", 0.7);
        Set(engine, "alice", "mood", "seek", 0.0);

        // one 10000-second advance: the exact exponential update converges
        // without overshooting no matter the step size
        Motives.Advance(engine, 10000);
        Assert.Equal(0.7, Get(engine, "alice", "mood", "seek"), 6);

        // approaching from above: falls to the setpoint, never past it
        Set(engine, "alice", "mood", "seek", 0.95);
        Motives.Advance(engine, 10000);
        Assert.Equal(0.7, Get(engine, "alice", "mood", "seek"), 6);
    }

    [Fact]
    public void RoutedLoss_UsesTheActualLoss_AndClampsTheTarget()
    {
        var engine = NewEngine();
        engine.World.AddModule("alice", "mood");
        Set(engine, "alice", "mood", "wide", 0.005); // less than rate × seconds
        Set(engine, "alice", "mood", "bank", 0.995);

        Motives.Advance(engine, 100);

        // the actual loss (0.005, not the requested 1.0) routes ×2 and
        // clamps at the bank's bound
        Assert.Equal(0.0, Get(engine, "alice", "mood", "wide"), 9);
        Assert.Equal(1.0, Get(engine, "alice", "mood", "bank"), 9);
    }

    [Fact]
    public void RuleConditions_ReadThePreStepSnapshot_OrderIndependent()
    {
        var engine = NewEngine();
        engine.World.AddModule("alice", "swing");
        Set(engine, "alice", "swing", "heat", 0.4);
        Set(engine, "alice", "swing", "cool", 0.1);

        Motives.Advance(engine, 10);

        var heat = Get(engine, "alice", "swing", "heat");
        var cool = Get(engine, "alice", "swing", "cool");
        // cool never reached 0.5, so heat fell at its constant rate the
        // whole time — the coupling cannot change a linear rule's outcome
        Assert.Equal(0.3, heat, 6);
        Assert.True(cool > 0.1 && cool < 0.3, $"cool chased a falling target: {cool}");

        // the same run with the motive list reversed: identical outcome
        var engine2 = NewEngine();
        engine2.World.AddModule("alice", "swing");
        engine2.World.SetFieldOverride("alice", "swing", "motives",
            JsonSerializer.Deserialize<JsonElement>(SwingReversed));
        Set(engine2, "alice", "swing", "heat", 0.4);
        Set(engine2, "alice", "swing", "cool", 0.1);
        Motives.Advance(engine2, 10);

        Assert.Equal(heat, Get(engine2, "alice", "swing", "heat"), 9);
        Assert.Equal(cool, Get(engine2, "alice", "swing", "cool"), 9);
    }

    [Fact]
    public void SubSteps_BigAdvanceMatchesSmallAdvances()
    {
        var engine = NewEngine();
        engine.World.AddModule("alice", "mood");
        Set(engine, "alice", "mood", "value", 0.6);
        Set(engine, "alice", "mood", "wide", 0.4);

        Motives.Advance(engine, 7);
        var atOnce = (Get(engine, "alice", "mood", "value"), Get(engine, "alice", "mood", "wide"));

        var engine2 = NewEngine();
        engine2.World.AddModule("alice", "mood");
        Set(engine2, "alice", "mood", "value", 0.6);
        Set(engine2, "alice", "mood", "wide", 0.4);
        for (var i = 0; i < 7; i++)
            Motives.Advance(engine2, 1);

        Assert.Equal(atOnce.Item1, Get(engine2, "alice", "mood", "value"), 9);
        Assert.Equal(atOnce.Item2, Get(engine2, "alice", "mood", "wide"), 9);
    }

    [Fact]
    public void MaxStep_CapsSubSteps_SplitAdvancesCoincide()
    {
        // a 1-second advance under a 0.5 cap must land exactly where two
        // half-second advances put it (the cap, not the wall clock, is
        // the integration granularity)
        var engine = NewEngine();
        engine.World.AddModule("alice", "swing");
        Set(engine, "alice", "swing", "maxStep", 0.5);
        Set(engine, "alice", "swing", "heat", 0.9);
        Set(engine, "alice", "swing", "cool", 0.0);
        Motives.Advance(engine, 1);
        var capped = Get(engine, "alice", "swing", "cool");

        var engine2 = NewEngine();
        engine2.World.AddModule("alice", "swing");
        Set(engine2, "alice", "swing", "heat", 0.9);
        Set(engine2, "alice", "swing", "cool", 0.0);
        Motives.Advance(engine2, 0.5);
        Motives.Advance(engine2, 0.5);

        Assert.Equal(capped, Get(engine2, "alice", "swing", "cool"), 9);
    }

    [Fact]
    public void Bands_AreExclusive_AndScaleByField()
    {
        var engine = NewEngine();
        var world = engine.World;
        world.AddModule("alice", "mood");
        Set(engine, "alice", "mood", "capacity", 2.0);
        Set(engine, "alice", "mood", "wide", 0.6); // ratio 0.3 → warm, not hot

        Motives.Advance(engine, 0);

        var modules = engine.ModuleRegistry;
        Assert.True(Conditions.Has(world, modules, world.GetObject("alice"), "warm"));
        Assert.False(Conditions.Has(world, modules, world.GetObject("alice"), "hot"));
        Assert.Contains(engine.SignalBus.Drain("alice"), s => s.Text == "You feel warm.");
    }

    [Fact]
    public void ZeroSecondAdvance_SyncsBandsWithoutDrifting()
    {
        var engine = NewEngine();
        engine.World.AddModule("alice", "mood");
        Set(engine, "alice", "mood", "wide", 0.6);

        Motives.Advance(engine, 0);

        Assert.Equal(0.6, Get(engine, "alice", "mood", "wide"), 9);
        Assert.True(Conditions.Has(engine.World, engine.ModuleRegistry,
            engine.World.GetObject("alice"), "hot"));
    }

    [Fact]
    public void OnEmptyEvent_FiresWithSelfText()
    {
        var engine = NewEngine();
        engine.World.AddModule("alice", "swing");
        Set(engine, "alice", "swing", "drain", 0.2);

        Motives.Advance(engine, 10); // drain hits 0 at 4s, loss routed to pool

        Assert.Equal(0.0, Get(engine, "alice", "swing", "drain"), 9);
        Assert.Equal(0.2, Get(engine, "alice", "swing", "pool"), 9);
        Assert.Contains(engine.SignalBus.Drain("alice"), s => s.Text == "The drain runs dry.");
    }

    [Fact]
    public void OnFullEvent_AppliesSetsAndAdjusts_Attaches_DeliversTexts_SilencesBands()
    {
        var engine = NewEngine();
        var world = engine.World;
        world.AddModule("alice", "climax");
        world.CreateObject("carol", "room_a", "Carol"); // same-room observer
        world.AddModule("carol", "agent");
        Set(engine, "alice", "climax", "urge", 0.7);

        // initial sync: the flushed band arrives with its text first
        Motives.Advance(engine, 0);
        var modules = engine.ModuleRegistry;
        var alice = world.GetObject("alice");
        Assert.True(Conditions.Has(world, modules, alice, "flushed"));
        engine.SignalBus.Drain("alice");
        engine.SignalBus.Drain("carol");

        var fired = Motives.Impulse(engine.World, modules, engine.SignalBus,
            alice, new Dictionary<string, double> { ["urge"] = 0.3 });

        Assert.Equal(["peak"], fired);
        Assert.Equal(0.0, modules.ResolveDouble(alice, "climax", "urge"), 9); // set
        Assert.Equal(0.25, modules.ResolveDouble(alice, "climax", "after"), 9); // set
        Assert.Equal(0.4, modules.ResolveDouble(alice, "climax", "base"), 9); // 0.5 − 0.1
        Assert.True(Conditions.Has(world, modules, alice, "spent"));
        // silentBands: flushed fell away without a transition text
        Assert.False(Conditions.Has(world, modules, alice, "flushed"));
        var aliceSignals = engine.SignalBus.Drain("alice");
        Assert.DoesNotContain(aliceSignals,
            s => s.Text.Contains("flushed", StringComparison.Ordinal));
        Assert.Contains(aliceSignals, s => s.Text == "It crests, and breaks.");
        // the ambient signal reached the same-room observer
        Assert.Contains(engine.SignalBus.Drain("carol"), s => s.Text.Contains("shudders"));
    }

    [Fact]
    public void Impulse_ClampsAndSyncsBandsImmediately()
    {
        var engine = NewEngine();
        var world = engine.World;
        world.AddModule("alice", "mood");

        var fired = Motives.Impulse(engine.World, engine.ModuleRegistry, engine.SignalBus,
            world.GetObject("alice"), new Dictionary<string, double>
            {
                ["wide"] = 1.7, // unbounded above; capacity 1 → ratio 1.7 → hot
                ["value"] = -3.0, // clamps at min 0
            });

        Assert.Empty(fired); // neither motive carries events
        Assert.Equal(1.7, Get(engine, "alice", "mood", "wide"), 9);
        Assert.Equal(0.0, Get(engine, "alice", "mood", "value"), 9);
        Assert.True(Conditions.Has(world, engine.ModuleRegistry, world.GetObject("alice"), "hot"));
    }

    [Fact]
    public void FindMotiveModule_ResolvesTheCarryingModule()
    {
        var engine = NewEngine();
        var world = engine.World;
        world.AddModule("alice", "mood");

        Assert.Equal("mood", Motives.FindMotiveModule(
            engine.ModuleRegistry, world.GetObject("alice"), "wide"));
        Assert.Null(Motives.FindMotiveModule(
            engine.ModuleRegistry, world.GetObject("bob"), "wide"));
    }

    [Fact]
    public void Agents_WithoutMotiveModules_AreSkipped()
    {
        var engine = NewEngine();
        Motives.Advance(engine, 100);
        var fired = Motives.Impulse(engine.World, engine.ModuleRegistry, engine.SignalBus,
            engine.World.GetObject("alice"), new Dictionary<string, double> { ["value"] = 1 });
        Assert.Empty(fired);
        Assert.Empty(engine.SignalBus.Drain("alice"));
    }
}
