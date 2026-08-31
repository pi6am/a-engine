using System.Text.Json;
using AEngine.Core.Modules;
using AEngine.Core.Runtime;

namespace AEngine.Core.Scenarios;

/// <summary>
/// Loads scenario JSON files into an engine. Scenario files are
/// composable: load multiple files in order and later definitions
/// override earlier ones by object id (an object's definition and parent
/// come from the latest file that defines it; objects not redefined
/// persist).
///
/// File shape: { name, defeatText?, modules?: [module definitions], world?: object tree }.
/// Object tree node: { id, name?, description?, attributes?,
/// modules?: [{ module, overrides? }] (or plain strings), children?: [...] }.
/// </summary>
public static class ScenarioLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed class NodeDto
    {
        public required string Id { get; init; }
        public string? Name { get; init; }
        public string? Description { get; init; }
        public Dictionary<string, JsonElement>? Attributes { get; init; }
        public List<JsonElement>? Modules { get; init; }
        public List<NodeDto>? Children { get; init; }
    }

    private sealed class ScenarioDto
    {
        public string? Name { get; init; }
        /// <summary>Ending text when the player is incapacitated (→ GameEngine.DefeatText).</summary>
        public string? DefeatText { get; init; }
        public List<ModuleDefinition>? Modules { get; init; }
        /// <summary>Top-level objects (each becomes a child of the world root).</summary>
        public List<NodeDto>? World { get; init; }
    }

    private sealed record FlatNode(NodeDto Node, string ParentId);

    /// <summary>Load a scenario from a path, dispatching on packaging:
    /// a scenario directory, a zip archive (any extension), or whatever
    /// sources are registered in <see cref="ScenarioSources"/>.</summary>
    public static string LoadFrom(GameEngine engine, string path) =>
        LoadDocumentsInto(engine, ScenarioSources.Resolve(path).Load(path));

    /// <summary>Load one or more scenario files into the engine, in order.</summary>
    public static string LoadInto(GameEngine engine, params string[] paths)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (paths.Length == 0)
            throw new ArgumentException("At least one scenario file is required.", nameof(paths));
        return LoadDocumentsInto(engine,
            paths.Select(p => new ScenarioDocument(p, File.ReadAllText(p))).ToList());
    }

    /// <summary>Merge raw scenario JSON documents into the engine, in order.</summary>
    public static string LoadDocumentsInto(GameEngine engine, IReadOnlyList<ScenarioDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (documents.Count == 0)
            throw new ArgumentException("At least one scenario document is required.", nameof(documents));

        var name = "";
        var flat = new Dictionary<string, FlatNode>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var document in documents)
        {
            var dto = JsonSerializer.Deserialize<ScenarioDto>(document.Json, JsonOptions)
                ?? throw new InvalidDataException($"Scenario document '{document.Label}' parsed to null.");
            if (dto.Name is not null)
                name = dto.Name;
            if (dto.DefeatText is not null)
                engine.DefeatText = dto.DefeatText;
            if (dto.Modules is not null)
            {
                foreach (var module in dto.Modules)
                {
                    if (engine.ModuleRegistry.Has(module.Id))
                        engine.ModuleRegistry.Update(module);
                    else
                        engine.ModuleRegistry.Register(module);
                }
            }
            if (dto.World is not null)
            {
                foreach (var node in dto.World)
                    Flatten(node, World.World.RootId, flat, order);
            }
        }

        BuildTree(engine.World, flat, order);
        return name;
    }

    private static void Flatten(
        NodeDto node, string parentId,
        Dictionary<string, FlatNode> flat, List<string> order)
    {
        if (!flat.ContainsKey(node.Id))
            order.Add(node.Id);
        flat[node.Id] = new FlatNode(node, parentId); // later files override by id
        foreach (var child in node.Children ?? [])
            Flatten(child, node.Id, flat, order);
    }

    private static void BuildTree(World.World world, Dictionary<string, FlatNode> flat, List<string> order)
    {
        var built = new HashSet<string>(StringComparer.Ordinal) { World.World.RootId };
        var pending = new Queue<string>(order);

        while (pending.Count > 0)
        {
            var id = pending.Dequeue();
            var (node, parentId) = flat[id];
            if (!built.Contains(parentId))
            {
                if (!flat.ContainsKey(parentId))
                    throw new InvalidDataException(
                        $"Object '{id}' has parent '{parentId}' which is never defined.");
                pending.Enqueue(id); // wait for the parent
                continue;
            }

            World.WorldObject obj;
            if (world.HasObject(id))
            {
                obj = world.GetObject(id);
                if (obj.Parent != parentId)
                    world.MoveObject(id, parentId);
            }
            else
            {
                obj = world.CreateObject(id, parentId);
            }

            if (node.Name is not null) obj.Name = node.Name;
            if (node.Description is not null) obj.Description = node.Description;
            foreach (var (key, value) in node.Attributes ?? new())
                obj.Attributes[key] = value;
            foreach (var moduleElement in node.Modules ?? [])
                AttachModule(world, obj, moduleElement);

            built.Add(id);
        }
    }

    private static void AttachModule(World.World world, World.WorldObject obj, JsonElement element)
    {
        // module attachments are either "moduleId" or { module, overrides }
        if (element.ValueKind == JsonValueKind.String)
        {
            world.AddModule(obj.Id, element.GetString()!);
            return;
        }
        var moduleId = element.GetProperty("module").GetString()!;
        world.AddModule(obj.Id, moduleId);
        if (element.TryGetProperty("overrides", out var overrides))
        {
            foreach (var prop in overrides.EnumerateObject())
                world.SetFieldOverride(obj.Id, moduleId, prop.Name, prop.Value.Clone());
        }
    }
}
