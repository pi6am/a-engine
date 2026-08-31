using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.World;
using CoreWorld = AEngine.Core.World.World;

namespace AEngine.Tests;

/// <summary>
/// Resolver-level affordance gating: requires/excludes (condition kinds
/// on the actor) and when (observable module-field state of the target or
/// actor). These HIDE actions; execution-time gates (see GateTests) fail
/// them loudly instead.
/// </summary>
public class AffordanceGateTests
{
    private const string ModulesJson = """
    [
      {
        "id": "condition", "name": "Condition",
        "fields": [
          { "name": "kind", "type": "string", "default": "" },
          { "name": "visible", "type": "bool", "default": true }
        ],
        "affordances": []
      },
      {
        "id": "toilet", "name": "Toilet",
        "fields": [],
        "affordances": [
          { "verb": "use", "handler": "basic", "requires": "needs_to_pee" }
        ]
      },
      {
        "id": "stage", "name": "Stage",
        "fields": [],
        "affordances": [
          { "verb": "dance", "handler": "basic", "requires": "tipsy, happy" }
        ]
      },
      {
        "id": "round_buyer", "name": "Round buyer",
        "fields": [],
        "affordances": [
          { "verb": "buy_round", "handler": "basic", "excludes": "banned, broke" }
        ]
      },
      {
        "id": "beverage", "name": "Beverage",
        "fields": [
          { "name": "empty", "type": "bool", "default": false },
          { "name": "alcohol", "type": "number", "default": 0.1 }
        ],
        "affordances": [
          {
            "verb": "drink", "handler": "basic",
            "when": [ { "module": "beverage", "field": "empty", "equals": false } ]
          },
          {
            "verb": "clear", "handler": "basic",
            "when": [ { "module": "beverage", "field": "empty", "equals": true } ]
          },
          {
            "verb": "nurse", "handler": "basic",
            "when": [ { "module": "beverage", "field": "alcohol", "min": 0.05, "max": 0.2 } ]
          }
        ]
      },
      {
        "id": "metabolism", "name": "Metabolism",
        "fields": [ { "name": "bladder", "type": "number", "default": 0.0 } ],
        "affordances": [
          {
            "verb": "squirm", "handler": "basic",
            "when": [ { "on": "actor", "module": "metabolism", "field": "bladder", "min": 0.5 } ]
          }
        ]
      }
    ]
    """;

    private static GameEngine NewEngine()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.ModuleRegistry.LoadJson(ModulesJson);
        var world = engine.World;
        foreach (var (id, kind) in new[] { ("cond_pee", "needs_to_pee"), ("cond_tipsy", "tipsy"),
                                           ("cond_happy", "happy"), ("cond_banned", "banned") })
        {
            world.CreateObject(id, CoreWorld.RootId, $"{kind} template");
            world.AddModule(id, "condition");
            world.SetFieldOverride(id, "condition", "kind", CoreWorld.ToJson(kind));
        }
        world.CreateObject("ale", "room_a", "mug of ale");
        world.AddModule("ale", "beverage");
        world.CreateObject("urinal", "room_a", "urinal");
        world.AddModule("urinal", "toilet");
        world.CreateObject("stage", "room_a", "stage");
        world.AddModule("stage", "stage");
        world.CreateObject("bar_tab", "room_a", "bar tab");
        world.AddModule("bar_tab", "round_buyer");
        return engine;
    }

    private static List<string> Verbs(GameEngine engine, string agentId, string targetId) =>
        engine.ActionResolver.Resolve(engine.World.GetObject(agentId))
            .Where(a => a.TargetId == targetId)
            .Select(a => a.Verb)
            .ToList();

    private static void Attach(GameEngine engine, string agentId, string templateId) =>
        Conditions.Attach(engine.World, engine.ModuleRegistry,
            engine.World.GetObject(agentId), templateId);

    [Fact]
    public void Requires_HidesActionUntilActorHasEveryCondition()
    {
        var engine = NewEngine();
        Assert.DoesNotContain("use", Verbs(engine, "alice", "urinal"));

        Attach(engine, "alice", "cond_pee");
        Assert.Contains("use", Verbs(engine, "alice", "urinal"));
    }

    [Fact]
    public void Requires_ListIsAnyOf_ExclusiveTiers()
    {
        var engine = NewEngine();
        // "tipsy, happy": any one listed kind admits the action —
        // condition kinds are often exclusive tiers (tipsy/drunk), where
        // an all-of list could never pass
        Attach(engine, "alice", "cond_tipsy");
        Assert.Contains("dance", Verbs(engine, "alice", "stage"));
    }

    [Fact]
    public void Excludes_HidesActionWhileAnyConditionActive()
    {
        var engine = NewEngine();
        Assert.Contains("buy_round", Verbs(engine, "alice", "bar_tab"));

        Attach(engine, "alice", "cond_banned");
        Assert.DoesNotContain("buy_round", Verbs(engine, "alice", "bar_tab"));
    }

    [Fact]
    public void When_EqualsTracksObservableTargetState()
    {
        var engine = NewEngine();
        // full mug: drink yes, clear no
        Assert.Contains("drink", Verbs(engine, "alice", "ale"));
        Assert.DoesNotContain("clear", Verbs(engine, "alice", "ale"));

        engine.World.SetFieldOverride("ale", "beverage", "empty", CoreWorld.ToJson(true));
        Assert.DoesNotContain("drink", Verbs(engine, "alice", "ale"));
        Assert.Contains("clear", Verbs(engine, "alice", "ale"));
    }

    [Fact]
    public void When_NumericRangeChecksFieldBounds()
    {
        var engine = NewEngine();
        // default alcohol 0.1 is within [0.05, 0.2]
        Assert.Contains("nurse", Verbs(engine, "alice", "ale"));

        engine.World.SetFieldOverride("ale", "beverage", "alcohol", CoreWorld.ToJson(0.5));
        Assert.DoesNotContain("nurse", Verbs(engine, "alice", "ale"));
    }

    [Fact]
    public void When_OnActorTestsTheActorsOwnField()
    {
        var engine = NewEngine();
        var world = engine.World;
        world.AddModule("alice", "metabolism");
        world.AddModule("bob", "metabolism");
        world.SetFieldOverride("bob", "metabolism", "bladder", CoreWorld.ToJson(0.9));

        // the affordance lives on nothing in particular — it is emitted from
        // the agent's own modules, so attach metabolism's squirm via alice
        Assert.Empty(Verbs(engine, "alice", "alice").Where(v => v == "squirm"));
        world.SetFieldOverride("alice", "metabolism", "bladder", CoreWorld.ToJson(0.6));
        Assert.Contains("squirm", Verbs(engine, "alice", "alice"));
    }

    [Fact]
    public void GatesApplyToPotentialResolutionToo()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        Assert.DoesNotContain(engine.ActionResolver.ResolvePotential(alice),
            a => a.Verb == "use" && a.TargetId == "urinal");

        Attach(engine, "alice", "cond_pee");
        Assert.Contains(engine.ActionResolver.ResolvePotential(alice),
            a => a.Verb == "use" && a.TargetId == "urinal");
    }
}
