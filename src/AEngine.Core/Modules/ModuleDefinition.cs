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
/// signal specs emitted to observers on success. Duration is how long the
/// action takes, in seconds/turns (default 1) — in real-time mode an
/// agent is busy for that many ticks after performing it. RepeatBackoff
/// marks an idle verb (look, wait): each consecutive repeat doubles the
/// duration up to RepeatBackoffCap, so a bored agent idles instead of
/// thrashing its policy — and the backoff is interruptible by new
/// observed signals, so the agent stays reactive.
/// </summary>
public sealed class AffordanceDefinition
{
    public required string Verb { get; init; }
    public required string Handler { get; init; }
    public string? Requires { get; init; }
    public string? Prompt { get; init; }
    public int Duration { get; init; } = 1;
    public bool RepeatBackoff { get; init; }
    public int RepeatBackoffCap { get; init; } = 30;
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
