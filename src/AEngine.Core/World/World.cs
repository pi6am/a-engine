using System.Text.Json;

namespace AEngine.Core.World;

/// <summary>
/// Holds the flat id -> object index and enforces tree invariants:
/// a single root ("world") and no cycles. Each object exists exactly
/// once in the tree.
/// </summary>
public sealed class World
{
    public const string RootId = "world";

    private readonly Dictionary<string, WorldObject> _objects = new(StringComparer.Ordinal);

    public World()
    {
        _objects[RootId] = new WorldObject
        {
            Id = RootId,
            Name = "World",
            Description = "The root of everything.",
        };
    }

    public IReadOnlyDictionary<string, WorldObject> Objects => _objects;

    public WorldObject GetObject(string id) =>
        _objects.TryGetValue(id, out var obj)
            ? obj
            : throw new KeyNotFoundException($"No object with id '{id}'.");

    public bool HasObject(string id) => _objects.ContainsKey(id);

    public IEnumerable<WorldObject> ChildrenOf(string id) =>
        GetObject(id).Children.Select(GetObject);

    /// <summary>Create an object under <paramref name="parentId"/>.</summary>
    public WorldObject CreateObject(string id, string parentId, string name = "", string description = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        if (_objects.ContainsKey(id))
            throw new InvalidOperationException($"Object '{id}' already exists.");
        var parent = GetObject(parentId);

        var obj = new WorldObject
        {
            Id = id,
            Parent = parentId,
            Name = name,
            Description = description,
        };
        _objects[id] = obj;
        parent.Children.Add(id);
        return obj;
    }

    /// <summary>Destroy an object and all its descendants recursively.</summary>
    public void DestroyObject(string id)
    {
        var obj = GetObject(id);
        if (id == RootId)
            throw new InvalidOperationException("Cannot destroy the world root.");
        // copy, because DestroyObject recurses into the list
        foreach (var child in obj.Children.ToArray())
            DestroyObject(child);
        GetObject(obj.Parent).Children.Remove(id);
        _objects.Remove(id);
    }

    /// <summary>Move an object to a new parent, rejecting cycles.</summary>
    public void MoveObject(string id, string newParentId)
    {
        var obj = GetObject(id);
        var newParent = GetObject(newParentId);
        if (id == RootId)
            throw new InvalidOperationException("Cannot move the world root.");
        if (id == newParentId || IsDescendantOf(newParentId, id))
            throw new InvalidOperationException(
                $"Moving '{id}' under '{newParentId}' would create a cycle.");
        if (obj.Parent == newParentId)
            return;

        GetObject(obj.Parent).Children.Remove(id);
        newParent.Children.Add(id);
        obj.Parent = newParentId;
    }

    /// <summary>True if <paramref name="candidate"/> is a descendant of <paramref name="ancestorId"/>.</summary>
    public bool IsDescendantOf(string candidate, string ancestorId)
    {
        var current = GetObject(candidate).Parent;
        while (current.Length > 0)
        {
            if (current == ancestorId)
                return true;
            current = GetObject(current).Parent;
        }
        return false;
    }

    /// <summary>Attach a module to an object (no-op if already attached).</summary>
    public ModuleAttachment AddModule(string id, string moduleId)
    {
        var obj = GetObject(id);
        var existing = obj.GetModule(moduleId);
        if (existing is not null)
            return existing;
        var attachment = new ModuleAttachment { ModuleId = moduleId };
        obj.Modules.Add(attachment);
        return attachment;
    }

    public void RemoveModule(string id, string moduleId)
    {
        var obj = GetObject(id);
        obj.Modules.RemoveAll(m => m.ModuleId == moduleId);
    }

    /// <summary>Set a plain attribute on an object.</summary>
    public void SetAttribute(string id, string name, JsonElement value)
    {
        GetObject(id).Attributes[name] = value;
    }

    /// <summary>Remove a plain attribute from an object. Returns false if absent.</summary>
    public bool RemoveAttribute(string id, string name) =>
        GetObject(id).Attributes.Remove(name);

    /// <summary>Set a per-object module field override.</summary>
    public void SetFieldOverride(string id, string moduleId, string field, JsonElement value)
    {
        var obj = GetObject(id);
        var attachment = obj.GetModule(moduleId)
            ?? throw new InvalidOperationException(
                $"Object '{id}' does not have module '{moduleId}' attached.");
        attachment.Overrides[field] = value;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static JsonElement ToJson(object value) =>
        JsonSerializer.SerializeToElement(value, JsonOptions);
}
