using AEngine.Core.Runtime;

namespace AEngine.Tests;

/// <summary>
/// Ambient emissions: an object with the `ambient` module periodically
/// sends one of its `texts` variants (picked at random, interval plus
/// jitter) as a private sensation to the agent holding it — a cursed mark
/// burning. Non-agent holders and empty variant lists emit nothing.
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
    public void Ambient_EmitsAVariantToTheHolder_EveryInterval()
    {
        var engine = NewEngine();
        engine.Random = new Random(1);
        var alice = engine.World.GetObject("alice");

        // nothing before the first interval elapses (scheduled at load)
        Wait(engine, 2);
        Assert.Empty(engine.SignalBus.Drain("alice"));

        Wait(engine, 2); // turn 4: interval 3 due (first fire at load + interval)
        var signal = Assert.Single(engine.SignalBus.Drain("alice"));
        Assert.True(signal.Text is "The mark burns." or "The mark itches.", signal.Text);

        // and it keeps coming, also remembered
        Wait(engine, 8);
        Assert.NotEmpty(engine.SignalBus.Drain("alice"));
        Assert.Contains(engine.Memory.Recall("alice"),
            m => m == "The mark burns." || m == "The mark itches.");
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
