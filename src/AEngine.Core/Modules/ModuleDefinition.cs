using System.Text.Json;

namespace AEngine.Core.Modules;

/// <summary>Field types supported by module definitions.</summary>
public enum FieldType
{
    String,
    Int,
    Bool,
    Ref, // object-id reference
}

/// <summary>A field declared by a module: name, type, and default value.</summary>
public sealed class FieldDefinition
{
    public required string Name { get; init; }
    public required FieldType Type { get; init; }
    public JsonElement Default { get; init; }
}

/// <summary>
/// An action affordance exposed by a module: verb + handler id, an
/// optional prompt (the verb takes a free-text argument), and optional
/// signal specs emitted to observers on success.
/// </summary>
public sealed class AffordanceDefinition
{
    public required string Verb { get; init; }
    public required string Handler { get; init; }
    public string? Requires { get; init; }
    public string? Prompt { get; init; }
    public List<Signals.SignalSpec> Signals { get; init; } = [];
}

/// <summary>
/// A data-driven module definition. Handlers are resolved by string id
/// against the HandlerRegistry — the seam where custom handlers plug in.
/// </summary>
public sealed class ModuleDefinition
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";
    public List<FieldDefinition> Fields { get; init; } = [];
    public List<AffordanceDefinition> Affordances { get; init; } = [];

    public FieldDefinition? GetField(string name) =>
        Fields.FirstOrDefault(f => f.Name == name);
}
