using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.World;
using CoreWorld = AEngine.Core.World.World;

namespace AEngine.Tests;

public class MetabolismTests
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
        "id": "metabolism", "name": "Metabolism",
        "fields": [
          { "name": "alcohol", "type": "number", "default": 0.0 },
          { "name": "bladder", "type": "number", "default": 0.0 },
          { "name": "capacity", "type": "number", "default": 1.0 },
          { "name": "alcoholDecayPerSec", "type": "number", "default": 0.01 },
          { "name": "bladderFromAlcohol", "type": "number", "default": 1.0 },
          { "name": "stages", "type": "list", "default": [
            { "min": 0.25, "condition": "cond_tipsy" },
            { "min": 0.5, "condition": "cond_drunk" },
            { "min": 0.85, "condition": "cond_hammered" }
          ] },
          { "name": "bladderStages", "type": "list", "default": [
            { "min": 0.7, "condition": "cond_pee" },
            { "min": 0.95, "condition": "cond_bursting" }
          ] }
        ],
        "affordances": []
      },
      {
        "id": "waiter", "name": "Waiter",
        "fields": [],
        "affordances": [ { "verb": "wait", "handler": "wait", "repeatBackoff": true } ]
      }
    ]
    """;

    private static GameEngine NewEngine()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.ModuleRegistry.LoadJson(ModulesJson);
        var world = engine.World;
        void Template(string id, string kind, string selfText, string clearText)
        {
            world.CreateObject(id, CoreWorld.RootId, $"{kind} template");
            world.AddModule(id, "condition");
            world.SetFieldOverride(id, "condition", "kind", CoreWorld.ToJson(kind));
            world.SetFieldOverride(id, "condition", "selfText", CoreWorld.ToJson(selfText));
            world.SetFieldOverride(id, "condition", "clearText", CoreWorld.ToJson(clearText));
        }
        Template("cond_tipsy", "tipsy", "You feel warm and loose-tongued.", "The buzz fades.");
        Template("cond_drunk", "drunk", "The room gently tilts.", "You feel steadier.");
        Template("cond_hammered", "hammered", "The floor keeps bumping into you.", null!);
        Template("cond_pee", "needs_to_pee", "You need to pee.", "The pressure recedes.");
        Template("cond_bursting", "bursting", "Your bladder is bursting!", null!);
        return engine;
    }

    private static void Set(GameEngine engine, string agentId, string field, double value) =>
        engine.World.SetFieldOverride(agentId, "metabolism", field, CoreWorld.ToJson(value));

    private static double Get(GameEngine engine, string agentId, string field) =>
        engine.ModuleRegistry.ResolveDouble(engine.World.GetObject(agentId), "metabolism", field);

    private static bool HasCond(GameEngine engine, string agentId, string kind) =>
        Conditions.Has(engine.World, engine.ModuleRegistry, engine.World.GetObject(agentId), kind);

    [Fact]
    public void Decay_BurnsAlcohol_IntoBladder()
    {
        var engine = NewEngine();
        engine.World.AddModule("alice", "metabolism");
        Set(engine, "alice", "alcohol", 0.5);
        engine.TurnManager.EvaluateUpkeep(); // initial band sync

        Metabolism.Advance(engine, 20); // decay 0.01/s → 0.2 burned

        Assert.Equal(0.3, Get(engine, "alice", "alcohol"), 6);
        Assert.Equal(0.2, Get(engine, "alice", "bladder"), 6);
    }

    [Fact]
    public void Bands_AreExclusive_HighestReachedWins()
    {
        var engine = NewEngine();
        engine.World.AddModule("alice", "metabolism");
        // capacity 1.0: 0.6 alcohol → drunk band (0.5..0.85)
        Set(engine, "alice", "alcohol", 0.6);
        engine.TurnManager.EvaluateUpkeep();

        Assert.False(HasCond(engine, "alice", "tipsy"));
        Assert.True(HasCond(engine, "alice", "drunk"));
        Assert.False(HasCond(engine, "alice", "hammered"));
        // the transition announced itself
        Assert.Contains(engine.SignalBus.Drain("alice"), s => s.Text == "The room gently tilts.");
    }

    [Fact]
    public void Bands_AreFractionsOfCapacity_OrcOutDrinksGoblin()
    {
        var engine = NewEngine();
        var world = engine.World;
        world.AddModule("alice", "metabolism"); // capacity 1.0
        Set(engine, "alice", "capacity", 1.0);
        Set(engine, "alice", "alcohol", 0.3);
        // orc: same absolute alcohol, 2.5x capacity → ratio 0.12, sober
        world.CreateObject("orc", "room_b", "Orc");
        world.AddModule("orc", "agent");
        world.AddModule("orc", "metabolism");
        Set(engine, "orc", "capacity", 2.5);
        Set(engine, "orc", "alcohol", 0.3);

        engine.TurnManager.EvaluateUpkeep();

        Assert.True(HasCond(engine, "alice", "tipsy"));
        Assert.False(HasCond(engine, "orc", "tipsy"));
    }

    [Fact]
    public void SoberingUp_TransitionsDown_WithClearText()
    {
        var engine = NewEngine();
        engine.World.AddModule("alice", "metabolism");
        Set(engine, "alice", "alcohol", 0.6);
        engine.TurnManager.EvaluateUpkeep();
        Assert.True(HasCond(engine, "alice", "drunk"));

        Metabolism.Advance(engine, 20); // burn 0.2 → 0.4, down into the tipsy band

        Assert.False(HasCond(engine, "alice", "drunk"));
        Assert.True(HasCond(engine, "alice", "tipsy"));
        var signals = engine.SignalBus.Drain("alice");
        Assert.Contains(signals, s => s.Text == "You feel steadier.");
        Assert.Contains(signals, s => s.Text == "You feel warm and loose-tongued.");
    }

    [Fact]
    public void BladderBands_AppearAsItFills_FromBurnedAlcohol()
    {
        var engine = NewEngine();
        engine.World.AddModule("alice", "metabolism");
        Set(engine, "alice", "alcohol", 0.75);
        Set(engine, "alice", "bladder", 0.65);
        engine.TurnManager.EvaluateUpkeep();

        Metabolism.Advance(engine, 5); // burn 0.05 → bladder 0.7 crosses the band

        Assert.True(HasCond(engine, "alice", "needs_to_pee"));
        Assert.Contains(engine.SignalBus.Drain("alice"), s => s.Text == "You need to pee.");
    }

    [Fact]
    public void TurnBased_ActionsAdvanceTheWorldClock()
    {
        var engine = NewEngine();
        engine.World.AddModule("alice", "metabolism");
        Set(engine, "alice", "alcohol", 0.6);
        engine.TurnManager.EvaluateUpkeep();
        Assert.True(HasCond(engine, "alice", "drunk"));

        // a long wait: backoff-free handler here — use execute + manual turns?
        // wait is on the agent module with repeatBackoff; perform it directly
        var alice = engine.World.GetObject("alice");
        var wait = engine.ActionResolver.Resolve(alice).First(a => a.Verb == "wait");
        engine.TurnManager.PerformAction(alice, wait); // duration 1 → burns 0.01

        Assert.Equal(0.59, Get(engine, "alice", "alcohol"), 6);
    }

    [Fact]
    public void RealTime_TicksAdvanceEveryone()
    {
        var engine = NewEngine();
        engine.TimeMode = TimeMode.RealTime;
        engine.World.AddModule("alice", "metabolism");
        engine.World.AddModule("bob", "metabolism");
        Set(engine, "alice", "alcohol", 0.3);
        Set(engine, "bob", "alcohol", 0.1);

        engine.TurnManager.Tick();
        engine.TurnManager.Tick();

        Assert.Equal(0.28, Get(engine, "alice", "alcohol"), 6);
        Assert.Equal(0.08, Get(engine, "bob", "alcohol"), 6);
    }

    [Fact]
    public void Alcohol_ClampsAtZero_BladderAtOne()
    {
        var engine = NewEngine();
        engine.World.AddModule("alice", "metabolism");
        Set(engine, "alice", "alcohol", 0.005);
        Set(engine, "alice", "bladder", 0.999);

        Metabolism.Advance(engine, 100);

        Assert.Equal(0.0, Get(engine, "alice", "alcohol"), 6);
        Assert.Equal(1.0, Get(engine, "alice", "bladder"), 6);
    }
}
