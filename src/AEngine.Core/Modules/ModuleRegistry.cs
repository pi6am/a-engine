using System.Text.Json;
using AEngine.Core.World;

namespace AEngine.Core.Modules;

/// <summary>
/// Registry of module definitions, loadable from JSON and mutable at
/// runtime (Register/Update/Unregister). Also resolves effective field
/// values: object override -> module default.
/// </summary>
public sealed class ModuleRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly Dictionary<string, ModuleDefinition> _modules = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, ModuleDefinition> Modules => _modules;

    public ModuleDefinition Get(string id) =>
        _modules.TryGetValue(id, out var def)
            ? def
            : throw new KeyNotFoundException($"No module with id '{id}'.");

    public bool Has(string id) => _modules.ContainsKey(id);

    public void Register(ModuleDefinition definition)
    {
        if (_modules.ContainsKey(definition.Id))
            throw new InvalidOperationException($"Module '{definition.Id}' is already registered.");
        _modules[definition.Id] = definition;
    }

    /// <summary>Replace an existing module definition at runtime.</summary>
    public void Update(ModuleDefinition definition)
    {
        if (!_modules.ContainsKey(definition.Id))
            throw new KeyNotFoundException($"No module with id '{definition.Id}' to update.");
        _modules[definition.Id] = definition;
    }

    public void Unregister(string id)
    {
        if (!_modules.Remove(id))
            throw new KeyNotFoundException($"No module with id '{id}'.");
    }

    /// <summary>Parse module definitions from a JSON array string.</summary>
    public static List<ModuleDefinition> ParseJson(string json)
    {
        var defs = JsonSerializer.Deserialize<List<ModuleDefinition>>(json, JsonOptions)
            ?? throw new InvalidDataException("Module JSON parsed to null.");
        return defs;
    }

    /// <summary>Load and register module definitions from a JSON array string.</summary>
    public void LoadJson(string json)
    {
        foreach (var def in ParseJson(json))
        {
            _modules[def.Id] = def; // later definitions override by id
        }
    }

    /// <summary>
    /// Resolve the effective value of a module field for an object:
    /// object override -> module default. Returns null when neither exists.
    /// </summary>
    public JsonElement? ResolveField(WorldObject obj, string moduleId, string field)
    {
        var attachment = obj.GetModule(moduleId)
            ?? throw new InvalidOperationException(
                $"Object '{obj.Id}' does not have module '{moduleId}' attached.");
        if (attachment.Overrides.TryGetValue(field, out var overrideValue))
            return overrideValue;
        return Get(moduleId).GetField(field)?.Default;
    }

    public string? ResolveString(WorldObject obj, string moduleId, string field) =>
        ResolveField(obj, moduleId, field) is { } e && e.ValueKind != JsonValueKind.Null && e.ValueKind != JsonValueKind.Undefined
            ? e.GetString()
            : null;

    public bool ResolveBool(WorldObject obj, string moduleId, string field, bool fallback = false) =>
        ResolveField(obj, moduleId, field) is { } e && (e.ValueKind == JsonValueKind.True || e.ValueKind == JsonValueKind.False)
            ? e.GetBoolean()
            : fallback;

    public int ResolveInt(WorldObject obj, string moduleId, string field, int fallback = 0) =>
        ResolveField(obj, moduleId, field) is { } e && e.ValueKind == JsonValueKind.Number
            ? e.GetInt32()
            : fallback;
}
