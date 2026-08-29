using System.Text.Json;

namespace AEngine.Core.Modules;

/// <summary>Field types supported by module definitions.</summary>
public enum FieldType
{
    String,
    Int,
    Bool,
    Ref, // object-id reference
    List, // list of strings (e.g. body regions)
    Map, // string -> int dictionary (e.g. stats, skills)
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
/// observed signals, so the agent stays reactive. Postures is an optional
/// allow-list of postures the affordance is usable from ("standing",
/// "sitting", "lying", "carried"); null = any posture. SameSupport
/// requires the target to share the agent's parent (e.g. cuddle only
/// between occupants of the same bed).
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
    public List<string>? Postures { get; init; }
    public bool SameSupport { get; init; }
    public List<Signals.SignalSpec> Signals { get; init; } = [];

    /// <summary>
    /// Optional stat/skill check gating the affordance: evaluated before
    /// the handler runs (in TurnManager.PerformAction, so player plans,
    /// NPCs, and the debug API all respect it). A failed check consumes the
    /// turn, runs no handler, and emits no signals.
    /// </summary>
    public CheckSpec? Check { get; init; }
}

/// <summary>
/// A stat/skill check declared on an affordance. The actor's bonus is
/// their stat plus skill value; success when dice + bonus >= difficulty.
/// The dice formula (n d m) comes from the scenario's rules module.
/// When Opposed is set, a defender (the target agent, or the agent
/// holding the target item) rolls their own dice + opposed stat/skill and
/// the actor must beat them. FailSignals are emitted on a failed check —
/// e.g. a botched pickpocketing rattles the victim.
/// </summary>
public sealed class CheckSpec
{
    public string? Stat { get; init; }
    public string? Skill { get; init; }
    public int Difficulty { get; init; }
    /// <summary>Defender's stat/skill for opposed checks; null = uncontested.</summary>
    public OpposedSpec? Opposed { get; init; }
    /// <summary>Failure message; defaults to "You try to {verb}..., but fail."</summary>
    public string? FailText { get; init; }
    /// <summary>Signals emitted on a failed check (failed actions are otherwise silent).</summary>
    public List<Signals.SignalSpec> FailSignals { get; init; } = [];
}

/// <summary>The defender's side of an opposed check: their stat/skill.</summary>
public sealed class OpposedSpec
{
    public string? Stat { get; init; }
    public string? Skill { get; init; }
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
