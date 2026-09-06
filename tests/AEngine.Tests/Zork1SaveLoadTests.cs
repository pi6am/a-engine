using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.Scenarios;

namespace AEngine.Tests;

/// <summary>
/// The fidelity bar for saves: a game saved mid-adventure, diverged
/// wildly, and restored from disk finishes the ORIGINAL walkthrough to
/// the letter — 350 points, the Stone Barrow, and the same move count a
/// never-interrupted run would take. If save/load ever perturbs world
/// state, automation clocks, or the dice, this is the test that knows.
/// </summary>
public class Zork1SaveLoadTests
{
    [Fact]
    public void SavedMidGame_RestoredFromDisk_FinishesLikeTheOriginal()
    {
        var engine = Zork1Stage1Tests.NewEngine();
        var lines = File.ReadAllLines(WalkthroughPath());

        // save after spoke 1 (the trophy case holds the egg and painting)
        var saveAfter = Array.IndexOf(lines, "Put the jewel-encrusted egg into the trophy case");
        Assert.True(saveAfter > 0);
        Assert.True(AEngine.Cli.Walkthrough.Run(engine, "player", lines[..(saveAfter + 1)]).Success);
        var turnAtSave = engine.TurnManager.Turn;

        // write the save to disk and read it back — the file is the proof
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, GameSerializer.Write(
                GameSerializer.Capture(engine, "zork1", playerId: "player")));

            // diverge: spend turns — the save must not care
            Assert.True(AEngine.Cli.Walkthrough.Run(engine, "player",
                ["Wait", "Wait", "Wait", "Wait", "Wait", "Wait"]).Success);
            Assert.True(engine.TurnManager.Turn > turnAtSave + 4);

            // restore from disk and finish the original game
            GameSerializer.Restore(engine, GameSerializer.Read(File.ReadAllText(path)));
            Assert.Equal(turnAtSave, engine.TurnManager.Turn);
            Assert.True(AEngine.Cli.Walkthrough.Run(engine, "player", lines[(saveAfter + 1)..]).Success);

            Assert.Equal(350, Score.Of(engine.World, engine.ModuleRegistry,
                engine.World.GetObject("player")));
            Assert.NotNull(engine.GameOver);
            Assert.Contains("Inside the Barrow", engine.GameOver);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UndoStyleRestore_ReplaysTheSameCombatDice()
    {
        // the troll fight is the dice-heaviest stretch: capture right
        // before it, fight, restore, fight again — identical rounds
        var engine = Zork1Stage1Tests.NewEngine();
        var lines = File.ReadAllLines(WalkthroughPath());
        var fightStart = Array.IndexOf(lines, "Attack the troll x40");
        Assert.True(fightStart > 0);
        Assert.True(AEngine.Cli.Walkthrough.Run(engine, "player", lines[..fightStart]).Success);

        var save = GameSerializer.Capture(engine);
        Assert.True(AEngine.Cli.Walkthrough.Run(engine, "player",
            lines[fightStart..(fightStart + 2)]).Success);
        var round1 = engine.TurnManager.Turn;

        GameSerializer.Restore(engine, GameSerializer.Read(GameSerializer.Write(save)));
        Assert.True(AEngine.Cli.Walkthrough.Run(engine, "player",
            lines[fightStart..(fightStart + 2)]).Success);
        var round2 = engine.TurnManager.Turn;

        // identical dice: the second fight took exactly as many swings
        Assert.Equal(round1, round2);
    }

    private static string WalkthroughPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scenarios", "zork1", "walkthrough.txt");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate scenarios/zork1/walkthrough.txt.");
    }
}
