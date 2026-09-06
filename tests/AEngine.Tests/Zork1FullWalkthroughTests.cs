using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.Scenarios;
using AEngine.Core.World;

namespace AEngine.Tests;

/// <summary>
/// The complete adventure, end to end: every room worth visiting, all
/// nineteen treasures in the trophy case, 350 points, and the walk
/// into the Stone Barrow — deterministically, at seed 42.
/// </summary>
public class Zork1FullWalkthroughTests
{
    [Fact]
    public void TheGreatUndergroundEmpire_IsConquered()
    {
        var engine = GameEngine.CreateWithBuiltinHandlers();
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scenarios", "zork1");
            if (Directory.Exists(Path.Combine(candidate, "world")))
            {
                ScenarioLoader.LoadFrom(engine, candidate);
                break;
            }
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        engine.Random = new Random(Zork1Stage1Tests.Seed);

        var world = engine.World;
        var result = AEngine.Cli.Walkthrough.Run(engine, "player",
            File.ReadAllLines(Path.Combine(
                FindScenarioRoot(), "walkthrough.txt")));
        Assert.True(result.Success, result.Error);

        var player = world.GetObject("player");
        Assert.Equal(350, Score.Of(world, engine.ModuleRegistry, player));
        Assert.Equal(0, engine.ModuleRegistry.ResolveInt(player, "scorecard", "deaths"));

        // all nineteen treasures rest in the case
        var cased = world.ChildrenOf("trophy_case")
            .Where(o => o.HasModule("treasure")).ToList();
        Assert.Equal(19, cased.Count);

        // and the adventure ends inside the Stone Barrow
        Assert.NotNull(engine.GameOver);
        Assert.Contains("Inside the Barrow", engine.GameOver);
        Assert.Contains("You have mastered ZORK", engine.GameOver);
    }

    private static string FindScenarioRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scenarios", "zork1");
            if (Directory.Exists(Path.Combine(candidate, "world")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate scenarios/zork1.");
    }
}
