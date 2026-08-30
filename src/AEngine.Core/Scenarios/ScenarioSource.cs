namespace AEngine.Core.Scenarios;

/// <summary>One raw scenario JSON document, as supplied by a source.</summary>
public sealed record ScenarioDocument(string Label, string Json);

/// <summary>
/// A scenario packaging format. Sources only know how to turn a path into
/// the raw JSON documents; merging them into an engine is
/// <see cref="ScenarioLoader"/>'s job. New packaging (e.g. image-card
/// metadata) implements this interface and registers in
/// <see cref="ScenarioSources"/>.
/// </summary>
public interface IScenarioSource
{
    /// <summary>Whether this source recognizes the path (directory shape, magic bytes, …).</summary>
    bool CanHandle(string path);

    /// <summary>Extract the scenario documents (modules and world JSON).</summary>
    IReadOnlyList<ScenarioDocument> Load(string path);
}

/// <summary>Registry of scenario sources, consulted in order.</summary>
public static class ScenarioSources
{
    public static IReadOnlyList<IScenarioSource> All { get; } =
        [new DirectoryScenarioSource(), new ZipScenarioSource()];

    /// <summary>Find the source that recognizes the path, or throw.</summary>
    public static IScenarioSource Resolve(string path) =>
        All.FirstOrDefault(s => s.CanHandle(path))
        ?? throw new InvalidDataException(
            $"'{path}' is not a scenario directory or a recognized scenario file.");
}
