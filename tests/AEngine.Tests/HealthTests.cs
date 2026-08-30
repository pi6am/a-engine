using AEngine.Core.Actions;
using AEngine.Core.Runtime;

namespace AEngine.Tests;

/// <summary>
/// RPG stage 3: health pools, damage application/clamping, and
/// incapacitation (only look remains; policies skip unconscious agents).
/// </summary>
public class HealthTests
{
    private const string HealthModuleJson = """
    [
      {
        "id": "health", "name": "Health",
        "fields": [
          { "name": "maxHp", "type": "int", "default": 10 },
          { "name": "hp", "type": "int", "default": 10 },
          { "name": "incapacitatedAt", "type": "int", "default": 0 }
        ],
        "affordances": []
      }
    ]
    """;

    private static GameEngine NewEngine()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.ModuleRegistry.LoadJson(HealthModuleJson);
        engine.World.AddModule("alice", "health");
        engine.World.AddModule("bob", "health");
        engine.World.MoveObject("bob", "room_a");
        return engine;
    }

    [Fact]
    public void Damage_ClampsAtZero_AndReportsIncapacitationOnce()
    {
        var engine = NewEngine();
        var bob = engine.World.GetObject("bob");

        Assert.Null(Damage.Apply(engine.World, engine.ModuleRegistry, bob, 4));
        Assert.Equal(6, engine.ModuleRegistry.ResolveInt(bob, "health", "hp"));
        Assert.False(Health.IsIncapacitated(engine.World, engine.ModuleRegistry, bob));

        var fragment = Damage.Apply(engine.World, engine.ModuleRegistry, bob, 50);
        Assert.Equal("Bob collapses, incapacitated!", fragment);
        Assert.Equal(0, engine.ModuleRegistry.ResolveInt(bob, "health", "hp"));
        Assert.True(Health.IsIncapacitated(engine.World, engine.ModuleRegistry, bob));
        // a standing agent crumples
        Assert.Equal(Postures.Prone, Postures.Of(engine.World, engine.ModuleRegistry, bob));

        // further damage to a downed agent reports nothing new
        Assert.Null(Damage.Apply(engine.World, engine.ModuleRegistry, bob, 3));

        // objects without a health module are unaffected
        Assert.Null(Damage.Apply(engine.World, engine.ModuleRegistry,
            engine.World.GetObject("chest"), 10));
    }

    [Fact]
    public void Incapacitated_Agent_CanOnlyLook()
    {
        var engine = NewEngine();
        var bob = engine.World.GetObject("bob");
        Damage.Apply(engine.World, engine.ModuleRegistry, bob, 99);

        var actions = engine.ActionResolver.Resolve(bob);
        Assert.Single(actions);
        Assert.Equal("look", actions[0].Verb);

        // looking still works — he is unconscious, not blind
        var look = engine.TurnManager.PerformAction(bob, actions[0]);
        Assert.True(look.Success);
    }

    [Fact]
    public void Incapacitated_Npc_GetsNoTurns()
    {
        var engine = NewEngine();
        var bob = engine.World.GetObject("bob");
        Damage.Apply(engine.World, engine.ModuleRegistry, bob, 99);

        engine.TurnManager.RunNpcTurns();
        engine.TurnManager.RunNpcTurns();
        Assert.Empty(engine.Memory.Recall("bob")); // Bob never acted

        // he still shows in the room, marked — and prone, having crumpled
        var look = engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "look"));
        Assert.Contains("Bob (prone, incapacitated)", look.Message);
    }

    [Fact]
    public void Incapacitated_ShowsInExamine()
    {
        var engine = NewEngine();
        var bob = engine.World.GetObject("bob");
        bob.Description = "A shabby-looking fellow.";
        Damage.Apply(engine.World, engine.ModuleRegistry, bob, 99);

        var result = engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "examine", "bob"));
        Assert.Contains("Bob is incapacitated.", result.Message);
    }
}
