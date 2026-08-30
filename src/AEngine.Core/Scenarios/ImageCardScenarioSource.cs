namespace AEngine.Core.Scenarios;

/// <summary>A scenario shared as an image card: PNG or JPEG metadata
/// carrying modules.json and world.json (see <see cref="ImageCard"/>).
/// Recognized by magic bytes, so any file extension works.</summary>
public sealed class ImageCardScenarioSource : IScenarioSource
{
    public bool CanHandle(string path) => ImageCard.DetectFile(path) is not null;

    public IReadOnlyList<ScenarioDocument> Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var format = ImageCard.Detect(bytes)
            ?? throw new InvalidDataException($"'{path}' is not a PNG or JPEG image.");
        var documents = ImageCard.Extract(bytes, format)
            ?? throw new InvalidDataException($"Image '{path}' carries no embedded scenario data.");
        if (!documents.ContainsKey("world.json"))
            throw new InvalidDataException($"Image card '{path}' contains no world.json.");
        // modules before world, matching the directory layout's load order
        return documents
            .OrderBy(d => d.Key == "modules.json" ? 0 : d.Key == "world.json" ? 1 : 2)
            .ThenBy(d => d.Key, StringComparer.Ordinal)
            .Select(d => new ScenarioDocument($"{path}:{d.Key}", d.Value))
            .ToList();
    }
}
