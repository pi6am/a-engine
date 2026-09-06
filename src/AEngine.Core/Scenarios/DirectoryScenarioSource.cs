namespace AEngine.Core.Scenarios;

/// <summary>
/// The classic scenario layout: a directory holding modules.json and
/// world.json, and optionally a world/ subdirectory of additional world
/// fragments (world/house.json, world/maze.json, ...) merged in sorted
/// file order after world.json — large scenarios split by area while the
/// loader's later-documents-override-by-id rule keeps composition exact.
/// </summary>
public sealed class DirectoryScenarioSource : IScenarioSource
{
    public bool CanHandle(string path) =>
        Directory.Exists(path) &&
        (File.Exists(Path.Combine(path, "world.json")) || HasWorldFragments(path));

    private static bool HasWorldFragments(string path)
    {
        var dir = Path.Combine(path, "world");
        return Directory.Exists(dir) &&
               Directory.EnumerateFiles(dir, "*.json").Any();
    }

    public IReadOnlyList<ScenarioDocument> Load(string path)
    {
        var documents = new List<ScenarioDocument>();
        var modulesPath = Path.Combine(path, "modules.json");
        if (File.Exists(modulesPath))
            documents.Add(new ScenarioDocument(modulesPath, File.ReadAllText(modulesPath)));
        var worldPath = Path.Combine(path, "world.json");
        if (File.Exists(worldPath))
            documents.Add(new ScenarioDocument(worldPath, File.ReadAllText(worldPath)));
        var worldDir = Path.Combine(path, "world");
        if (Directory.Exists(worldDir))
            foreach (var fragment in Directory.EnumerateFiles(worldDir, "*.json").Order())
                documents.Add(new ScenarioDocument(fragment, File.ReadAllText(fragment)));
        return documents;
    }
}
