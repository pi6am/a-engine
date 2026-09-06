using AEngine.Core.Runtime;
using AEngine.Core.Scenarios;
using AEngine.Core.World;

namespace AEngine.Tests;

/// <summary>
/// Save/load, undo, and restart: a captured game is the whole game —
/// world tree, module state, turn clocks, memories, and the exact dice.
/// Restoring into a live engine replaces its state wholesale; the future
/// from a restored save is the future the save was living.
/// </summary>
public class SaveLoadTests
{
    private static GameEngine NewMvpEngine()
    {
        var engine = GameEngine.CreateWithBuiltinHandlers();
        ScenarioLoader.LoadInto(engine,
            Path.Combine(FindMvpRoot(), "modules.json"),
            Path.Combine(FindMvpRoot(), "world.json"));
        engine.Random = new Random(7);
        return engine;
    }

    private static string FindMvpRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scenarios", "mvp");
            if (File.Exists(Path.Combine(candidate, "world.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate scenarios/mvp.");
    }

    /// <summary>Resolve by verb/target and perform; null when unavailable.</summary>
    private static string? Do(GameEngine engine, string verb, string? targetId = null)
    {
        var player = engine.World.GetObject("player");
        var action = engine.ActionResolver.Resolve(player).FirstOrDefault(a =>
            a.Verb == verb && (targetId is null || a.TargetId == targetId));
        return action is null ? null : engine.TurnManager.PerformAction(player, action).Message;
    }

    private static List<string> ActionLabels(GameEngine engine) =>
        engine.ActionResolver.Resolve(engine.World.GetObject("player"))
            .Select(a => a.Label).ToList();

    [Fact]
    public void RoundTrip_PreservesWorldStateAndTheAvailableFuture()
    {
        var engine = NewMvpEngine();
        Do(engine, "open", "desk");
        Do(engine, "take", "key");
        Do(engine, "unlock", "door_a_side");
        Do(engine, "open", "door_a_side");
        Do(engine, "go", "door_a_side");

        var save = GameSerializer.Capture(engine, "mvp");
        var roomAtSave = engine.World.RoomOf("player").Id;
        var actionsAtSave = ActionLabels(engine);
        var turnAtSave = engine.TurnManager.Turn;
        Assert.NotEqual("room_a", roomAtSave); // sanity: the save is somewhere interesting

        // live on and diverge
        Do(engine, "drop", "key");
        Do(engine, "wait");

        GameSerializer.Restore(engine, GameSerializer.Read(GameSerializer.Write(save)));

        Assert.Equal(roomAtSave, engine.World.RoomOf("player").Id);
        Assert.Equal("player", engine.World.GetObject("key").Parent); // the drop is gone
        Assert.Equal(actionsAtSave, ActionLabels(engine));
        Assert.Equal(turnAtSave, engine.TurnManager.Turn);
    }

    [Fact]
    public void RoundTrip_RestoresFieldOverrides_AndDynamicObjectChanges()
    {
        var engine = NewMvpEngine();
        engine.World.SetFieldOverride("player", "agent", "activity",
            AEngine.Core.World.World.ToJson("sulking"));
        var spawned = engine.World.CreateObject("spawned_marker", "room_a", "marker", "");
        var save = GameSerializer.Capture(engine);

        engine.World.DestroyObject(spawned.Id);
        engine.World.SetFieldOverride("player", "agent", "activity",
            AEngine.Core.World.World.ToJson("cheerful"));
        PerformWait(engine);

        GameSerializer.Restore(engine, GameSerializer.Read(GameSerializer.Write(save)));

        Assert.True(engine.World.HasObject("spawned_marker"));
        Assert.Equal("sulking", engine.ModuleRegistry.ResolveString(
            engine.World.GetObject("player"), "agent", "activity"));
    }

    [Fact]
    public void RandomSequence_ContinuesIdenticallyAfterRestore()
    {
        // both runtime PRNG paths: seeded (compat impl) and default (xoshiro)
        foreach (var random in new Random[] { new(42), new() })
        {
            var engine = NewMvpEngine();
            engine.Random = random;
            for (var i = 0; i < 3; i++)
                _ = random.Next(1000) + random.Next(5, 90) + random.NextDouble();
            var json = GameSerializer.Write(GameSerializer.Capture(engine));

            var first = new double[] { random.Next(1000), random.Next(5, 90), random.NextDouble() };
            GameSerializer.Restore(engine, GameSerializer.Read(json));
            var second = new double[] { random.Next(1000), random.Next(5, 90), random.NextDouble() };

            Assert.Equal(first, second);
        }
    }

    [Fact]
    public void TurnState_BusyTimersSurvive()
    {
        var engine = NewMvpEngine();
        engine.TimeMode = TimeMode.RealTime;
        PerformWait(engine);
        var busyBefore = engine.TurnManager.BusyUntilTurn("player");
        Assert.True(busyBefore > 0);

        var save = GameSerializer.Capture(engine);
        PerformWait(engine);
        engine.TurnManager.Tick();
        GameSerializer.Restore(engine, save);

        Assert.Equal(busyBefore, engine.TurnManager.BusyUntilTurn("player"));
        Assert.Equal(save.Turn, engine.TurnManager.Turn);
    }

    [Fact]
    public void Memory_SurvivesAndDivergesCorrectly()
    {
        var engine = NewMvpEngine();
        PerformWait(engine);
        PerformWait(engine);
        var remembered = engine.Memory.Recall("player").ToList();
        Assert.NotEmpty(remembered);

        var save = GameSerializer.Capture(engine);
        PerformWait(engine);
        GameSerializer.Restore(engine, save);
        Assert.Equal(remembered, engine.Memory.Recall("player"));
    }

    [Fact]
    public void EndingText_AndEngineScalars_Survive()
    {
        var engine = NewMvpEngine();
        PerformWait(engine);
        var alive = GameSerializer.Capture(engine); // for the undo-from-ending case
        engine.GameOver = "The curtain falls.";
        engine.DefeatText = "You have died a programmer's death.";
        var save = GameSerializer.Capture(engine);
        GameSerializer.Reset(engine);
        Assert.Null(engine.GameOver);
        GameSerializer.Restore(engine, save);
        Assert.Equal("The curtain falls.", engine.GameOver);
        Assert.Equal("You have died a programmer's death.", engine.DefeatText);

        // undo past an ending: restoring the pre-ending snapshot un-ends
        // the game — the defeat never happened, play continues
        GameSerializer.Restore(engine, alive);
        Assert.Null(engine.GameOver);
        Assert.NotEmpty(engine.ActionResolver.Resolve(engine.World.GetObject("player")));
    }

    [Fact]
    public void Reset_BlanksTheEngineForAScenarioReload()
    {
        var engine = NewMvpEngine();
        PerformWait(engine);
        var objectCount = engine.World.Objects.Count;
        Assert.True(objectCount > 1);

        GameSerializer.Reset(engine);
        Assert.Single(engine.World.Objects);
        Assert.Empty(engine.ModuleRegistry.Modules);
        Assert.Equal(0, engine.TurnManager.Turn);
        Assert.Null(engine.GameOver);

        ScenarioLoader.LoadInto(engine,
            Path.Combine(FindMvpRoot(), "modules.json"),
            Path.Combine(FindMvpRoot(), "world.json"));
        Assert.Equal(objectCount, engine.World.Objects.Count);
        Assert.NotEmpty(engine.ActionResolver.Resolve(engine.World.GetObject("player")));
    }

    [Fact]
    public void BadFormat_IsRefused()
    {
        var engine = NewMvpEngine();
        var json = GameSerializer.Write(GameSerializer.Capture(engine))
            .Replace(GameSerializer.FormatVersion, "aengine-save-0");
        Assert.Throws<InvalidDataException>(
            () => GameSerializer.Restore(engine, GameSerializer.Read(json)));
    }

    private static void PerformWait(GameEngine engine) =>
        Do(engine, "wait");
}
