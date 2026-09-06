using AEngine.Cli;
using AEngine.Core.Runtime;
using AEngine.Core.Scenarios;

namespace AEngine.Tests;

/// <summary>
/// The /undo history: a bounded ring you can walk backward through —
/// undoing must not destroy the steps behind it (the bug this suite was
/// written for: the first /undo wiped the ring, so undo only ever worked
/// once). Only crossing a game boundary (/load, /restart) clears it.
/// </summary>
public class UndoHistoryTests
{
    private static GameSerializer.SaveData SaveAt(GameEngine engine, int turn) =>
        new() { Turn = turn };

    [Fact]
    public void PopsNewestFirst_AndPoppingKeepsTheRest()
    {
        var ring = new UndoHistory();
        foreach (var turn in new[] { 1, 2, 3 })
            ring.Push(SaveAt(null!, turn));

        Assert.Equal(3, ring.Count);
        Assert.Equal(3, ring.Pop()!.Turn);
        Assert.Equal(2, ring.Count); // the ring survives the pop
        Assert.Equal(2, ring.Pop()!.Turn);
        Assert.Equal(1, ring.Pop()!.Turn);
        Assert.Null(ring.Pop());
    }

    [Fact]
    public void CapacityTen_EvictsOldest()
    {
        var ring = new UndoHistory();
        for (var turn = 1; turn <= 12; turn++)
            ring.Push(SaveAt(null!, turn));

        Assert.Equal(10, ring.Count);
        // drain: newest first, oldest surviving is 3 (1 and 2 were evicted)
        var last = 0;
        while (ring.Pop() is { } snapshot)
            last = snapshot.Turn;
        Assert.Equal(3, last);
        Assert.Equal(0, ring.Count);
    }

    [Fact]
    public void Clear_DropsEverything()
    {
        var ring = new UndoHistory();
        ring.Push(SaveAt(null!, 1));
        ring.Push(SaveAt(null!, 2));
        ring.Clear();
        Assert.Equal(0, ring.Count);
        Assert.Null(ring.Pop());
    }

    /// <summary>
    /// End to end at the engine level: three moves, then /undo three
    /// times (restoring each snapshot) — every step must land on the
    /// turn it was taken before, in reverse order.
    /// </summary>
    [Fact]
    public void ThreeMoves_ThreeUndos_EveryStepLands()
    {
        var engine = NewMvpEngine();
        var ring = new UndoHistory();
        var player = engine.World.GetObject("player");
        var wait = engine.ActionResolver.Resolve(player).First(a => a.Verb == "wait");

        var turns = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            ring.Push(GameSerializer.Capture(engine));
            turns.Add(engine.TurnManager.Turn);
            engine.TurnManager.PerformAction(player, wait);
        }
        Assert.Equal(3, ring.Count);

        // undo all three: reverse order, each restore is a real step back
        for (var i = 2; i >= 0; i--)
        {
            GameSerializer.Restore(engine, ring.Pop()!); // no ring clearing, like /undo
            Assert.Equal(turns[i], engine.TurnManager.Turn);
        }
        Assert.Equal(0, ring.Count);
        Assert.Null(ring.Pop());
    }

    private static GameEngine NewMvpEngine()
    {
        var engine = GameEngine.CreateWithBuiltinHandlers();
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scenarios", "mvp");
            if (File.Exists(Path.Combine(candidate, "world.json")))
            {
                ScenarioLoader.LoadInto(engine,
                    Path.Combine(candidate, "modules.json"),
                    Path.Combine(candidate, "world.json"));
                return engine;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate scenarios/mvp.");
    }
}
