namespace AEngine.Core.Scenarios;

/// <summary>The classic scenario layout: a directory holding modules.json and world.json.</summary>
public sealed class DirectoryScenarioSource : IScenarioSource
{
    public bool CanHandle(string path) =>
        Directory.Exists(path) && File.Exists(Path.Combine(path, "world.json"));

    public IReadOnlyList<ScenarioDocument> Load(string path)
    {
        var documents = new List<ScenarioDocument>();
        var modulesPath = Path.Combine(path, "modules.json");
        if (File.Exists(modulesPath))
            documents.Add(new ScenarioDocument(modulesPath, File.ReadAllText(modulesPath)));
        var worldPath = Path.Combine(path, "world.json");
        documents.Add(new ScenarioDocument(worldPath, File.ReadAllText(worldPath)));
        return documents;
    }
}
