using System.Text.Json;
using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.World;

namespace AEngine.Tests;

/// <summary>
/// Stage-0 engine substrate for Zork: inventory/weight gates, immobile,
/// concealment, darkness and light sources, the dark hazard, the shared
/// effect vocabulary (effect/answer/read handlers), automations (edge
/// triggers, delays, intervals), and scoring. Fixtures are
/// self-contained like TestWorlds so the tests survive scenario-content
/// churn.
/// </summary>
public class Zork0EngineTests
{
    private const string ModulesJson = """
    [
      {
        "id": "room", "name": "Room",
        "fields": [
          { "name": "dark", "type": "bool", "default": false },
          { "name": "value", "type": "int", "default": 0 },
          { "name": "visited", "type": "bool", "default": false }
        ],
        "affordances": []
      },
      {
        "id": "agent", "name": "Agent",
        "fields": [
          { "name": "policy", "type": "string", "default": "player" },
          { "name": "alwaysLit", "type": "bool", "default": false }
        ],
        "affordances": [
          { "verb": "look", "handler": "look", "repeatBackoff": true },
          { "verb": "inventory", "handler": "inventory" },
          { "verb": "wait", "handler": "wait", "repeatBackoff": true }
        ]
      },
      {
        "id": "portal", "name": "Portal",
        "fields": [
          { "name": "stateRef", "type": "ref", "default": null },
          { "name": "direction", "type": "string", "default": "" },
          { "name": "to", "type": "ref", "default": null }
        ],
        "affordances": [
          {
            "verb": "go", "handler": "go", "postures": ["standing"],
            "when": [ { "module": "immobile", "absent": true, "on": "actor", "field": "value" } ]
          }
        ]
      },
      {
        "id": "doorstate", "name": "Door state",
        "fields": [
          { "name": "open", "type": "bool", "default": false },
          { "name": "locked", "type": "bool", "default": false }
        ],
        "affordances": []
      },
      {
        "id": "portable", "name": "Portable",
        "fields": [ { "name": "weight", "type": "int", "default": 5 } ],
        "affordances": [
          {
            "verb": "take", "handler": "take",
            "gates": [
              { "kind": "loadUnder", "args": { "max": 100, "includeTarget": true },
                "failText": "Your load is too heavy." }
            ]
          },
          { "verb": "drop", "handler": "drop" }
        ]
      },
      {
        "id": "container", "name": "Container",
        "fields": [ { "name": "capacity", "type": "int", "default": 10 } ],
        "affordances": [ { "verb": "put", "handler": "put" } ]
      },
      {
        "id": "openable", "name": "Openable",
        "fields": [ { "name": "open", "type": "bool", "default": false } ],
        "affordances": [
          { "verb": "open", "handler": "open" },
          { "verb": "close", "handler": "close" }
        ]
      },
      {
        "id": "hideable", "name": "Hideable",
        "fields": [ { "name": "concealed", "type": "bool", "default": false } ],
        "affordances": []
      },
      {
        "id": "lightsource", "name": "Light source",
        "fields": [
          { "name": "on", "type": "bool", "default": false },
          { "name": "fuel", "type": "int", "default": -1 },
          { "name": "dead", "type": "bool", "default": false },
          { "name": "outText", "type": "string", "default": "" },
          { "name": "burnStages", "type": "list", "default": [] }
        ],
        "affordances": [
          {
            "verb": "turn on", "handler": "set",
            "when": [ { "module": "lightsource", "field": "on", "equals": false },
                      { "module": "lightsource", "field": "dead", "equals": false } ],
            "data": { "module": "lightsource", "field": "on", "value": "true" }
          },
          {
            "verb": "turn off", "handler": "set",
            "when": [ { "module": "lightsource", "field": "on", "equals": true } ],
            "data": { "module": "lightsource", "field": "on", "value": "false" }
          }
        ]
      },
      {
        "id": "readable", "name": "Readable",
        "fields": [ { "name": "text", "type": "string", "default": "" } ],
        "affordances": [ { "verb": "read", "handler": "read" } ]
      },
      {
        "id": "lever", "name": "Lever (effect verb fixture)",
        "fields": [ { "name": "onEffects", "type": "list", "default": [] } ],
        "affordances": [
          { "verb": "pull", "handler": "effect",
            "data": { "effectsField": "onEffects", "self": "You pull the {target}." } }
        ]
      },
      {
        "id": "oracle", "name": "Oracle (answer verb fixture)",
        "fields": [ { "name": "answers", "type": "list", "default": [] } ],
        "affordances": [
          { "verb": "ask", "handler": "answer", "prompt": "Ask what?",
            "data": { "answersField": "answers", "onWrong": "The oracle stays silent." } }
        ]
      },
      {
        "id": "treasure", "name": "Treasure",
        "fields": [
          { "name": "value", "type": "int", "default": 0 },
          { "name": "tvalue", "type": "int", "default": 0 },
          { "name": "scored", "type": "bool", "default": false }
        ],
        "affordances": []
      },
      {
        "id": "scorecard", "name": "Scorecard",
        "fields": [
          { "name": "score", "type": "int", "default": 0 },
          { "name": "deaths", "type": "int", "default": 0 },
          { "name": "caseRef", "type": "ref", "default": null }
        ],
        "affordances": [ { "verb": "score", "handler": "score" } ]
      },
      {
        "id": "rules", "name": "Rules",
        "fields": [
          { "name": "winScore", "type": "int", "default": 0 },
          { "name": "won", "type": "bool", "default": false },
          { "name": "winText", "type": "string", "default": "" },
          { "name": "ranks", "type": "list", "default": [] },
          { "name": "darkHazardChance", "type": "int", "default": 0 },
          { "name": "darkHazardText", "type": "string", "default": "" },
          { "name": "darkHazardCondition", "type": "ref", "default": null }
        ],
        "affordances": []
      },
      {
        "id": "immobile", "name": "Immobile",
        "fields": [],
        "affordances": []
      },
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
        "id": "automation", "name": "Automation rule",
        "fields": [
          { "name": "when", "type": "list", "default": [] },
          { "name": "effects", "type": "list", "default": [] },
          { "name": "once", "type": "bool", "default": false },
          { "name": "every", "type": "int", "default": 0 },
          { "name": "delay", "type": "int", "default": 0 },
          { "name": "armed", "type": "bool", "default": true },
          { "name": "countdown", "type": "int", "default": 0 }
        ],
        "affordances": []
      }
    ]
    """;

    /// <summary>A fresh engine with the stage-0 module set: two rooms (lit A, dark B), player in A.</summary>
    private static GameEngine NewEngine()
    {
        var engine = GameEngine.CreateWithBuiltinHandlers();
        engine.ModuleRegistry.LoadJson(ModulesJson);
        var world = engine.World;

        world.CreateObject("room_a", World.RootId, "Room A");
        world.CreateObject("room_b", World.RootId, "Room B");
        world.CreateObject("passage_state", World.RootId, "open passage");
        world.CreateObject("game_rules", World.RootId, "rules");
        world.AddModule("room_a", "room");
        world.AddModule("room_b", "room");
        world.SetFieldOverride("room_b", "room", "dark", World.ToJson(true));
        world.AddModule("passage_state", "doorstate");
        world.SetFieldOverride("passage_state", "doorstate", "open", World.ToJson(true));
        world.AddModule("game_rules", "rules");

        world.CreateObject("player", "room_a", "the adventurer");
        world.AddModule("player", "agent");
        world.AddModule("player", "scorecard");

        world.CreateObject("lamp", "room_a", "brass lantern");
        world.AddModule("lamp", "lightsource");
        world.AddModule("lamp", "portable");

        Portal(world, "a_to_b", "room_a", "north", "room_b");
        Portal(world, "b_to_a", "room_b", "south", "room_a");
        return engine;
    }

    private static void Portal(World world, string id, string roomId, string direction, string to)
    {
        world.CreateObject(id, roomId, "passage");
        world.AddModule(id, "portal");
        world.SetFieldOverride(id, "portal", "stateRef", World.ToJson("passage_state"));
        world.SetFieldOverride(id, "portal", "direction", World.ToJson(direction));
        world.SetFieldOverride(id, "portal", "to", World.ToJson(to));
    }

    private static AvailableAction Find(GameEngine engine, string verb, string? targetId = null) =>
        TestWorlds.Find(engine, "player", verb, targetId);

    private static ActionResult Do(GameEngine engine, string verb, string? targetId = null,
        string? text = null)
    {
        var action = Find(engine, verb, targetId);
        return engine.TurnManager.PerformAction(
            engine.World.GetObject("player"), action, text ?? action.Text);
    }

    // ------------------------------------------------------------------
    // gates: objectField (via `of`), carrying, notCarrying, loadUnder,
    // allowOnly
    // ------------------------------------------------------------------

    [Fact]
    public void FieldGate_OfThirdObject_BlocksAndPasses()
    {
        var engine = NewEngine();
        var world = engine.World;
        // a bridge gate keyed on a third object's field
        world.CreateObject("bridge_state", World.RootId, "bridge state");
        world.AddModule("bridge_state", "doorstate");
        world.SetFieldOverride("bridge_state", "doorstate", "open", World.ToJson(true));
        // re-gate the north portal with an objectField gate: needs a custom
        // affordance — attach a second portal whose go affordance has gates.
        // Simplest: test the gate directly through a take-gated item.
        world.CreateObject("idol", "room_a", "golden idol");
        world.AddModule("idol", "portable");
        // replace portable's take affordance gate list via a module update
        engine.ModuleRegistry.LoadJson("""
        [
          {
            "id": "portable", "name": "Portable",
            "fields": [ { "name": "weight", "type": "int", "default": 5 } ],
            "affordances": [
              {
                "verb": "take", "handler": "take",
                "gates": [
                  { "kind": "field",
                    "args": { "of": "bridge_state", "module": "doorstate", "field": "locked",
                              "equals": false },
                    "failText": "The bridge keeper eyes your pockets." }
                ]
              },
              { "verb": "drop", "handler": "drop" }
            ]
          }
        ]
        """);

        Assert.True(Do(engine, "take", "idol").Success); // bridge_state not locked
        Assert.True(Do(engine, "drop", "idol").Success);
        world.SetFieldOverride("bridge_state", "doorstate", "locked", World.ToJson(true));
        var result = Do(engine, "take", "idol");
        Assert.False(result.Success);
        Assert.Contains("bridge keeper", result.Message);
    }

    [Fact]
    public void LoadGate_BlocksOverweightTake()
    {
        var engine = NewEngine();
        var world = engine.World;
        // rocks of weight 50 and 60: the second take exceeds the 100 cap
        world.CreateObject("rock1", "room_a", "heavy rock");
        world.CreateObject("rock2", "room_a", "heavier rock");
        foreach (var (id, weight) in new[] { ("rock1", 50), ("rock2", 60) })
        {
            world.AddModule(id, "portable");
            world.SetFieldOverride(id, "portable", "weight", World.ToJson(weight));
        }
        Assert.True(Do(engine, "take", "rock1").Success);
        var result = Do(engine, "take", "rock2");
        Assert.False(result.Success);
        Assert.Contains("too heavy", result.Message);
    }

    [Fact]
    public void LoadGate_CapFromActorField()
    {
        var engine = NewEngine();
        var world = engine.World;
        world.CreateObject("pebble", "room_a", "pebble");
        world.AddModule("pebble", "portable");
        world.SetFieldOverride("pebble", "portable", "weight", World.ToJson(5));
        engine.ModuleRegistry.LoadJson("""
        [
          {
            "id": "portable", "name": "Portable",
            "fields": [ { "name": "weight", "type": "int", "default": 5 } ],
            "affordances": [
              {
                "verb": "take", "handler": "take",
                "gates": [
                  { "kind": "loadUnder",
                    "args": { "maxField": { "module": "scorecard", "field": "score" },
                              "includeTarget": true },
                    "failText": "You can't carry any more." }
                ]
              },
              { "verb": "drop", "handler": "drop" }
            ]
          }
        ]
        """);
        // score 0: even a 5-weight pebble exceeds a 0 cap
        Assert.False(Do(engine, "take", "pebble").Success);
        world.SetFieldOverride("player", "scorecard", "score", World.ToJson(10));
        Assert.True(Do(engine, "take", "pebble").Success);
    }

    // ------------------------------------------------------------------
    // immobile
    // ------------------------------------------------------------------

    [Fact]
    public void ImmobileAgent_HasNoGoActions()
    {
        var engine = NewEngine();
        var world = engine.World;
        world.CreateObject("statue", "room_a", "guardian statue");
        world.AddModule("statue", "agent");
        world.AddModule("statue", "immobile");

        var actions = engine.ActionResolver.Resolve(world.GetObject("statue"));
        Assert.DoesNotContain(actions, a => a.Verb == "go");

        // the mobile player still sees theirs
        Assert.Contains(engine.ActionResolver.Resolve(world.GetObject("player")),
            a => a.Verb == "go");
    }

    // ------------------------------------------------------------------
    // concealment
    // ------------------------------------------------------------------

    [Fact]
    public void ConcealedObjects_AreInvisibleUntilRevealed()
    {
        var engine = NewEngine();
        engine.ShowItemsInLook = true;
        var world = engine.World;
        world.CreateObject("trapdoor", "room_a", "trap door");
        world.AddModule("trapdoor", "portable");
        world.AddModule("trapdoor", "hideable");
        world.SetFieldOverride("trapdoor", "hideable", "concealed", World.ToJson(true));

        // hidden: not on the menu, not in the room listing
        Assert.DoesNotContain(engine.ActionResolver.Resolve(world.GetObject("player")),
            a => a.TargetId == "trapdoor");
        var look = Do(engine, "look");
        Assert.DoesNotContain("trap door", look.Message);

        // revealed by an effect
        Effects.Apply(engine, Json("[{ \"reveal\": { \"object\": \"trapdoor\" } }]"),
            new EffectContext(world.GetObject("player")));
        Assert.Contains(engine.ActionResolver.Resolve(world.GetObject("player")),
            a => a.TargetId == "trapdoor");
        look = Do(engine, "look");
        Assert.Contains("trap door", look.Message);
    }

    private static JsonElement Json(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<JsonElement>(json);

    // ------------------------------------------------------------------
    // darkness and light
    // ------------------------------------------------------------------

    [Fact]
    public void DarkRoom_StripsActionsUntilALightIsCarriedLit()
    {
        var engine = NewEngine();
        var world = engine.World;
        var player = world.GetObject("player");
        world.CreateObject("coin", "room_b", "gold coin");
        world.AddModule("coin", "portable");

        // walk into the dark room without a light
        Assert.True(Do(engine, "go", "a_to_b").Success);
        var actions = engine.ActionResolver.Resolve(player);
        Assert.DoesNotContain(actions, a => a.TargetId == "coin"); // can't see it
        Assert.Contains(actions, a => a.Verb == "go"); // but can still walk
        var look = Do(engine, "look");
        Assert.Contains("pitch black", look.Message);

        // holding an UNLIT lamp in the dark changes nothing
        Assert.True(Do(engine, "go", "b_to_a").Success);
        Assert.True(Do(engine, "take", "lamp").Success);
        Assert.True(Do(engine, "go", "a_to_b").Success);
        Assert.DoesNotContain(engine.ActionResolver.Resolve(player),
            a => a.TargetId == "coin");
        Assert.Contains("pitch black", Do(engine, "look").Message);

        // turning it on is reachable in the dark (held item) and lights the room
        Assert.True(Do(engine, "turn on", "lamp").Success);
        Assert.Contains(engine.ActionResolver.Resolve(player), a => a.TargetId == "coin");
        Assert.DoesNotContain("pitch black", Do(engine, "look").Message);
        Assert.True(Do(engine, "take", "coin").Success);
    }

    [Fact]
    public void LightFuel_BurnsDownAndDies()
    {
        var engine = NewEngine();
        var world = engine.World;
        world.SetFieldOverride("lamp", "lightsource", "fuel", World.ToJson(3));
        world.SetFieldOverride("lamp", "lightsource", "burnStages",
            World.ToJson(new List<string> { "2|The lantern dims." }));

        Do(engine, "take", "lamp");
        Do(engine, "turn on", "lamp"); // a player turn: fuel 3 -> 2
        Assert.Equal(2, engine.ModuleRegistry.ResolveInt(world.GetObject("lamp"),
            "lightsource", "fuel"));
        Do(engine, "wait"); // fuel 2 -> 1: the "2|" stage fires (2 >= 2 && 1 < 2)
        var messages = engine.SignalBus.Drain("player")
            .Select(s => s.Text).ToList();
        Assert.Contains(messages, t => t.Contains("dims"));
        // turn 3: fuel 0 — out
        Do(engine, "wait");
        Assert.True(engine.ModuleRegistry.ResolveBool(world.GetObject("lamp"),
            "lightsource", "dead"));
        Assert.False(engine.ModuleRegistry.ResolveBool(world.GetObject("lamp"),
            "lightsource", "on"));
        // and the room goes dark again: "turn on" is no longer offered
        Assert.DoesNotContain(engine.ActionResolver.Resolve(world.GetObject("player")),
            a => a.Verb == "turn on" && a.TargetId == "lamp");
    }

    [Fact]
    public void DarkHazard_StrikesWhileMovingUnlitInTheDark()
    {
        var engine = NewEngine();
        var world = engine.World;
        // the hazard's condition template (a scenario-defined "death")
        world.CreateObject("cond_dead", World.RootId, "dead");
        world.AddModule("cond_dead", "condition");
        world.SetFieldOverride("cond_dead", "condition", "kind", World.ToJson("dead"));
        world.SetFieldOverride("game_rules", "rules", "darkHazardChance", World.ToJson(100));
        world.SetFieldOverride("game_rules", "rules", "darkHazardText",
            World.ToJson("A lurking predator devours you!"));
        world.SetFieldOverride("game_rules", "rules", "darkHazardCondition",
            World.ToJson("cond_dead"));

        // walk into the dark, then move again while blind: 100% hazard
        Assert.True(Do(engine, "go", "a_to_b").Success);
        var result = Do(engine, "go", "b_to_a");
        Assert.False(result.Success);
        Assert.Contains("devours", result.Message);
        Assert.True(AEngine.Core.Actions.Conditions.Has(
            world, engine.ModuleRegistry, world.GetObject("player"), "dead"));

        // with a burning lamp, the same move is safe
        var engine2 = NewEngine();
        var world2 = engine2.World;
        world2.CreateObject("cond_dead", World.RootId, "dead");
        world2.AddModule("cond_dead", "condition");
        world2.SetFieldOverride("cond_dead", "condition", "kind", World.ToJson("dead"));
        world2.SetFieldOverride("game_rules", "rules", "darkHazardChance", World.ToJson(100));
        world2.SetFieldOverride("game_rules", "rules", "darkHazardCondition",
            World.ToJson("cond_dead"));
        Do(engine2, "take", "lamp");
        Do(engine2, "turn on", "lamp");
        Assert.True(Do(engine2, "go", "a_to_b").Success);
        Assert.True(Do(engine2, "go", "b_to_a").Success);
        Assert.False(AEngine.Core.Actions.Conditions.Has(
            world2, engine2.ModuleRegistry, world2.GetObject("player"), "dead"));
    }

    // ------------------------------------------------------------------
    // effect / answer / read handlers
    // ------------------------------------------------------------------

    [Fact]
    public void EffectHandler_AppliesFieldEffectsFromData()
    {
        var engine = NewEngine();
        var world = engine.World;
        world.CreateObject("lever", "room_a", "iron lever");
        world.AddModule("lever", "lever");
        world.SetFieldOverride("lever", "lever", "onEffects", Json(
            """
            [ { "set": { "of": "passage_state", "module": "doorstate", "field": "open", "value": false } } ]
            """));

        var result = Do(engine, "pull", "lever");
        Assert.True(result.Success);
        Assert.Contains("You pull", result.Message);
        Assert.False(engine.ModuleRegistry.ResolveBool(
            world.GetObject("passage_state"), "doorstate", "open"));
    }

    [Fact]
    public void AnswerHandler_MatchesWordsAndAppliesEffects()
    {
        var engine = NewEngine();
        var world = engine.World;
        world.CreateObject("oracle", "room_a", "stone oracle");
        world.AddModule("oracle", "oracle");
        world.SetFieldOverride("oracle", "oracle", "answers", Json(
            """
            { "echo": [ { "set": { "of": "passage_state", "module": "doorstate", "field": "open", "value": false } } ] }
            """));

        // wrong word: failure, nothing happens
        var wrong = Do(engine, "ask", "oracle", "hello?");
        Assert.False(wrong.Success);
        Assert.Contains("silent", wrong.Message);
        Assert.True(engine.ModuleRegistry.ResolveBool(
            world.GetObject("passage_state"), "doorstate", "open"));

        // right word: effects apply (case/punctuation-tolerant)
        var right = Do(engine, "ask", "oracle", "Echo.");
        Assert.True(right.Success);
        Assert.False(engine.ModuleRegistry.ResolveBool(
            world.GetObject("passage_state"), "doorstate", "open"));
    }

    [Fact]
    public void ReadHandler_PrintsTheText()
    {
        var engine = NewEngine();
        var world = engine.World;
        world.CreateObject("leaflet", "room_a", "leaflet");
        world.AddModule("leaflet", "readable");
        world.AddModule("leaflet", "portable");
        world.SetFieldOverride("leaflet", "readable", "text",
            World.ToJson("Welcome to the Great Underground Empire!"));

        var result = Do(engine, "read", "leaflet");
        Assert.True(result.Success);
        Assert.Contains("Great Underground Empire", result.Message);
    }

    // ------------------------------------------------------------------
    // automations: conditions, edge triggers, delay, interval
    // ------------------------------------------------------------------

    private static WorldObject AddAutomation(
        GameEngine engine, string id, string whenJson, string effectsJson,
        bool once = false, int delay = 0, int every = 0)
    {
        var world = engine.World;
        world.CreateObject(id, World.RootId, id);
        world.AddModule(id, "automation");
        if (whenJson.Length > 0)
            world.SetFieldOverride(id, "automation", "when", Json(whenJson));
        world.SetFieldOverride(id, "automation", "effects", Json(effectsJson));
        if (once)
            world.SetFieldOverride(id, "automation", "once", World.ToJson(true));
        if (delay > 0)
            world.SetFieldOverride(id, "automation", "delay", World.ToJson(delay));
        if (every > 0)
            world.SetFieldOverride(id, "automation", "every", World.ToJson(every));
        return world.GetObject(id);
    }

    private static int Ticks(GameEngine engine) =>
        engine.ModuleRegistry.ResolveInt(engine.World.GetObject("player"),
            "scorecard", "score");

    [Fact]
    public void Automation_FiresWhileConditionsHold()
    {
        var engine = NewEngine();
        var world = engine.World;
        world.CreateObject("counter", World.RootId, "counter");
        world.AddModule("counter", "doorstate"); // any module with fields
        world.SetFieldOverride("counter", "doorstate", "locked", World.ToJson(false));

        AddAutomation(engine, "tick_rule",
            """[ { "of": "counter", "module": "doorstate", "field": "locked", "equals": true } ]""",
            """[ { "adjust": { "of": "player", "module": "scorecard", "field": "score", "by": 5 } } ]""");

        Do(engine, "wait");
        Assert.Equal(0, Ticks(engine));
        world.SetFieldOverride("counter", "doorstate", "locked", World.ToJson(true));
        Do(engine, "wait");
        Assert.Equal(5, Ticks(engine));
        Do(engine, "wait");
        Assert.Equal(10, Ticks(engine)); // continuous rule: every pass
    }

    [Fact]
    public void Automation_OnceIsEdgeTriggeredAndRearms()
    {
        var engine = NewEngine();
        var world = engine.World;
        world.CreateObject("counter", World.RootId, "counter");
        world.AddModule("counter", "doorstate");
        world.SetFieldOverride("counter", "doorstate", "locked", World.ToJson(false));

        AddAutomation(engine, "on_death",
            """[ { "of": "counter", "module": "doorstate", "field": "locked", "equals": true } ]""",
            """[ { "adjust": { "of": "player", "module": "scorecard", "field": "score", "by": -10 } } ]""",
            once: true);

        world.SetFieldOverride("counter", "doorstate", "locked", World.ToJson(true));
        Do(engine, "wait");
        Assert.Equal(-10, Ticks(engine));
        Do(engine, "wait");
        Do(engine, "wait");
        Assert.Equal(-10, Ticks(engine)); // fired once for this stretch

        // conditions drop: re-arm
        world.SetFieldOverride("counter", "doorstate", "locked", World.ToJson(false));
        Do(engine, "wait");
        world.SetFieldOverride("counter", "doorstate", "locked", World.ToJson(true));
        Do(engine, "wait");
        Assert.Equal(-20, Ticks(engine)); // and fires again
    }

    [Fact]
    public void Automation_DelayFiresAfterNPlayerTurns()
    {
        var engine = NewEngine();
        var world = engine.World;
        world.CreateObject("counter", World.RootId, "counter");
        world.AddModule("counter", "doorstate");
        world.SetFieldOverride("counter", "doorstate", "locked", World.ToJson(true));

        AddAutomation(engine, "reservoir_drains",
            """[ { "of": "counter", "module": "doorstate", "field": "locked", "equals": true } ]""",
            """[ { "adjust": { "of": "player", "module": "scorecard", "field": "score", "by": 1 } } ]""",
            delay: 2);

        Do(engine, "wait"); // turn 1 of truth: countdown starts (2), now 1
        Assert.Equal(0, Ticks(engine));
        Do(engine, "wait"); // countdown hits 0: fire
        Assert.Equal(1, Ticks(engine));
        Do(engine, "wait"); // one-shot delay (no once): countdown restarted? no —
        Assert.Equal(1, Ticks(engine)); // a delay rule without every fires once per stretch
    }

    [Fact]
    public void Automation_EveryRepeatsAtInterval()
    {
        var engine = NewEngine();
        var world = engine.World;
        AddAutomation(engine, "drip",
            "",
            """[ { "adjust": { "of": "player", "module": "scorecard", "field": "score", "by": 1 } } ]""",
            every: 2);

        Do(engine, "wait"); // pass 1: arms the interval (no fire yet)
        Assert.Equal(0, Ticks(engine));
        Do(engine, "wait"); // pass 2: the interval elapses, fires
        Assert.Equal(1, Ticks(engine));
        Do(engine, "wait"); // re-armed
        Assert.Equal(1, Ticks(engine));
        Do(engine, "wait"); // fires again
        Assert.Equal(2, Ticks(engine));
    }

    [Fact]
    public void Automation_HasConditionAndHoldsConditions()
    {
        var engine = NewEngine();
        var world = engine.World;
        world.CreateObject("cond_dead", World.RootId, "dead");
        world.AddModule("cond_dead", "condition");
        world.SetFieldOverride("cond_dead", "condition", "kind", World.ToJson("dead"));

        // fires when the player is dead AND holds the lamp
        AddAutomation(engine, "death_flow",
            """
            [ { "of": "player", "hasCondition": "dead" },
              { "holder": "player", "holds": "lamp" } ]
            """,
            """[ { "adjust": { "of": "player", "module": "scorecard", "field": "score", "by": -10 } } ]""",
            once: true);

        Do(engine, "wait");
        Assert.Equal(0, Ticks(engine));

        // dead without the lamp: no fire
        var player = world.GetObject("player");
        world.CloneTree("cond_dead", "player", "dead_player");
        Do(engine, "wait");
        Assert.Equal(0, Ticks(engine));

        // dead and holding the lamp: fire
        Do(engine, "take", "lamp");
        Do(engine, "wait");
        Assert.Equal(-10, Ticks(engine));
    }

    // ------------------------------------------------------------------
    // scoring
    // ------------------------------------------------------------------

    [Fact]
    public void Score_AwardsTakeValueAndLiveTrophyPoints()
    {
        var engine = NewEngine();
        var world = engine.World;
        // a case + a treasure worth 10 now, 5 deposited
        world.CreateObject("case", "room_a", "trophy case");
        world.AddModule("case", "container");
        world.AddModule("case", "openable");
        world.SetFieldOverride("case", "openable", "open", World.ToJson(true));
        world.CreateObject("idol", "room_a", "golden idol");
        world.AddModule("idol", "portable");
        world.AddModule("idol", "treasure");
        world.SetFieldOverride("idol", "treasure", "value", World.ToJson(10));
        world.SetFieldOverride("idol", "treasure", "tvalue", World.ToJson(5));
        world.SetFieldOverride("player", "scorecard", "caseRef", World.ToJson("case"));

        var player = world.GetObject("player");
        Assert.Equal(0, Score.Of(world, engine.ModuleRegistry, player));

        var take = Do(engine, "take", "idol");
        Assert.True(take.Success);
        Assert.Contains("went up by 10", take.Message);
        Assert.Equal(10, Score.Of(world, engine.ModuleRegistry, player));

        // deposit: +tvalue while it sits in the case
        Assert.True(Do(engine, "put", "case").Success);
        Assert.Equal(15, Score.Of(world, engine.ModuleRegistry, player));

        // withdraw: deposit points go with it
        world.MoveObject("idol", "player");
        Assert.Equal(10, Score.Of(world, engine.ModuleRegistry, player));
    }

    [Fact]
    public void Score_RoomEntryAwardsOnce()
    {
        var engine = NewEngine();
        var world = engine.World;
        world.SetFieldOverride("room_b", "room", "value", World.ToJson(25));

        var go = Do(engine, "go", "a_to_b");
        Assert.True(go.Success);
        Assert.Contains("went up by 25", go.Message);
        Assert.Equal(25, Score.Of(world, engine.ModuleRegistry, world.GetObject("player")));

        Do(engine, "go", "b_to_a");
        var again = Do(engine, "go", "a_to_b");
        Assert.DoesNotContain("went up", again.Message);
        Assert.Equal(25, Score.Of(world, engine.ModuleRegistry, world.GetObject("player")));
    }

    [Fact]
    public void Score_WinTriggersOnceAndWhispers()
    {
        var engine = NewEngine();
        var world = engine.World;
        world.SetFieldOverride("game_rules", "rules", "winScore", World.ToJson(30));
        world.SetFieldOverride("game_rules", "rules", "winText", World.ToJson(
            "A voice whispers: look to your treasures."));
        world.SetFieldOverride("game_rules", "rules", "ranks", World.ToJson(
            new List<string> { "30|Winner", "0|Beginner" }));

        world.SetFieldOverride("player", "scorecard", "score", World.ToJson(29));
        Do(engine, "wait"); // below the threshold: nothing
        Assert.False(engine.ModuleRegistry.ResolveBool(world.GetObject("game_rules"),
            "rules", "won"));

        world.SetFieldOverride("player", "scorecard", "score", World.ToJson(30));
        Do(engine, "wait"); // reach it: won flips, whisper delivered
        Assert.True(engine.ModuleRegistry.ResolveBool(world.GetObject("game_rules"),
            "rules", "won"));
        Assert.Contains(engine.SignalBus.Drain("player").Select(s => s.Text),
            t => t.Contains("whispers"));

        var score = Do(engine, "score");
        Assert.Contains("rank of Winner", score.Message);
    }
}
