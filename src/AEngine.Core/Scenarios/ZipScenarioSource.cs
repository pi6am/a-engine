using System.IO.Compression;

namespace AEngine.Core.Scenarios;

/// <summary>
/// A scenario packaged as a zip archive holding modules.json and
/// world.json (matched by file name, at any depth). Recognized by the
/// "PK" magic bytes, so the file extension is irrelevant (.zip, .scen, …).
/// </summary>
public sealed class ZipScenarioSource : IScenarioSource
{
    public bool CanHandle(string path)
    {
        if (!File.Exists(path))
            return false;
        using var stream = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[2];
        return stream.Read(magic) == 2 && magic[0] == (byte)'P' && magic[1] == (byte)'K';
    }

    public IReadOnlyList<ScenarioDocument> Load(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var documents = new List<ScenarioDocument>();
        var worldFound = false;
        // modules before world, matching the directory layout's load order
        foreach (var name in new[] { "modules.json", "world.json" })
        {
            var entry = archive.Entries.FirstOrDefault(
                e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                continue;
            worldFound |= name == "world.json";
            using var reader = new StreamReader(entry.Open());
            documents.Add(new ScenarioDocument($"{path}:{entry.FullName}", reader.ReadToEnd()));
        }
        if (!worldFound)
            throw new InvalidDataException($"Scenario archive '{path}' contains no world.json.");
        return documents;
    }
}
