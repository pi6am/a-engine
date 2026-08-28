using System.Text.Json;

namespace AEngine.Core.World;

/// <summary>
/// A single object in the world tree. Attribute values are scalars or
/// object-id references (by convention, e.g. a "stateRef" attribute holding
/// the id of a shared doorstate object).
/// </summary>
public sealed class WorldObject
{
    public required string Id { get; init; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Parent { get; internal set; } = "";
    public List<string> Children { get; } = [];
    public Dictionary<string, JsonElement> Attributes { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Attached modules with per-object field overrides:
    /// module id -> (field name -> value).
    /// </summary>
    public List<ModuleAttachment> Modules { get; } = [];

    public ModuleAttachment? GetModule(string moduleId) =>
        Modules.FirstOrDefault(m => m.ModuleId == moduleId);

    public bool HasModule(string moduleId) => GetModule(moduleId) is not null;
}

/// <summary>An attached module plus its per-object field overrides.</summary>
public sealed class ModuleAttachment
{
    public required string ModuleId { get; init; }
    public Dictionary<string, JsonElement> Overrides { get; } = new(StringComparer.Ordinal);
}
