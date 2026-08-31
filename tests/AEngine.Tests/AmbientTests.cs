using AEngine.Core.Runtime;

namespace AEngine.Tests;

/// <summary>
/// Ambient emissions: an object with the `ambient` module periodically
/// sends one of its `texts` variants (picked at random, after a delay
/// rolled from the `interval` spec — a number or { min, max }) as a
/// private sensation to the agent holding it — a cursed mark burning.
/// Timing follows time passing (the holder's own action durations in
/// turn-based mode), not turn counts, so other agents' activity doesn't
/// accelerate it. Non-agent holders pause the timer and emit nothing.
/// </summary>
public class AmbientTests
{
    private const string AmbientModulesJson = """
    [
      {
        "id": "ambient", "name": "Ambient",
        "fields": [
          { "name": "texts", "type": "list", "default": [] },
          { "name": "interval", "type": "int", "default": 3 }
        ],
        "affordances": []
      }
    ]
    """;

    private static GameEngine NewEngine()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.ModuleRegistry.LoadJson(AmbientModulesJson);
        engine.World.CreateObject("mark", "alice", "itchy mark");
        engine.World.AddModule("mark", "ambient");
        engine.World.SetFieldOverride("mark", "ambient", "texts",
            Core.World.World.ToJson(new[] { "The mark burns.", "The mark itches." }));
        return engine;
    }

    private static void Wait(GameEngine engine, int turns)
    {
        var alice = engine.World.GetObject("alice");
        for (var i = 0; i < turns; i++)
            engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "wait"));
    }

    [Fact]
    public void Ambient_EmitsAVariantToTheHolder_AfterIntervalSeconds()
    {
        var engine = NewEngine();
        engine.Random = new Random(1);
        var alice = engine.World.GetObject("alice");

        // interval 3: one wait (1s) is not enough...
        Wait(engine, 1);
        Assert.Empty(engine.SignalBus.Drain("alice"));

        // ...but the second wait (2s more = 3s of her own time) fires it
        Wait(engine, 1);
        var signal = Assert.Single(engine.SignalBus.Drain("alice"));
        Assert.True(signal.Text is "The mark burns." or "The mark itches.", signal.Text);

        // and it keeps coming, also remembered
        Wait(engine, 4);
        Assert.NotEmpty(engine.SignalBus.Drain("alice"));
        Assert.Contains(engine.Memory.Recall("alice"),
            m => m == "The mark burns." || m == "The mark itches.");
    }

    [Fact]
    public void Ambient_OtherAgentsActions_DontAdvanceTheTimer()
    {
        var engine = NewEngine();
        var bob = engine.World.GetObject("bob");

        // Bob is busy for many turns; Alice (holding the mark) does nothing,
        // so no time passes for the mark and nothing fires
        for (var i = 0; i < 10; i++)
            engine.TurnManager.PerformAction(bob, TestWorlds.Find(engine, "bob", "wait"));
        Assert.Empty(engine.SignalBus.Drain("alice"));
    }

    [Fact]
    public void Ambient_IntervalRange_FiresWithinMinMax()
    {
        var engine = NewEngine();
        engine.World.SetFieldOverride("mark", "ambient", "interval",
            Core.World.World.ToJson(new { min = 2, max = 4 }));

        // 1s of holder time: below the minimum delay, nothing fires
        Wait(engine, 1);
        Assert.Empty(engine.SignalBus.Drain("alice"));

        // a few more seconds: past even the maximum delay, it has fired
        Wait(engine, 3);
        Assert.NotEmpty(engine.SignalBus.Drain("alice"));
    }

    [Fact]
    public void Ambient_NoAgentHolder_NoEmission()
    {
        var engine = NewEngine();
        engine.World.MoveObject("mark", "room_a"); // on the floor
        engine.World.MoveObject("bob", "room_a");

        Wait(engine, 10);
        Assert.Empty(engine.SignalBus.Drain("alice"));
        Assert.Empty(engine.SignalBus.Drain("bob"));
    }
}
