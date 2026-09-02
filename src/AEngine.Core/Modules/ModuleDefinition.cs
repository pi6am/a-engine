using System.Text.Json;
using System.Text.Json.Serialization;
using AEngine.Core.Actions;

namespace AEngine.Core.Modules;

/// <summary>How a speech verb's {speech} entries are aimed (see AffordanceDefinition.SpeechTargets).</summary>
public enum SpeechTargeting
{
    /// <summary>One undirected entry aimed at everyone ("Shout: {speech}").</summary>
    Broadcast,
    /// <summary>One entry per other agent present; none when alone ("Whisper to X: {speech}").</summary>
    Directed,
    /// <summary>Undirected broadcast, plus directed entries in a crowd (say).</summary>
    Both,
}

/// <summary>Field types supported by module definitions.</summary>
public enum FieldType
{
    String,
    Int,
    Number, // floating-point (e.g. alcohol level, bladder fill)
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
    /// <summary>
    /// Optional display label overriding the verb-generated one (for
    /// phrasing the menu/LLM can't produce from a verb alone, e.g. "Ask
    /// Rath to remove the dragon-mark"); {target} is substituted.
    /// </summary>
    public string? Label { get; init; }
    /// <summary>
    /// Condition kinds (comma-separated, ANY of which the actor must
    /// currently carry) for the affordance to be listed or executed —
    /// "use the urinal" while needing to pee or bursting. Any-of because
    /// condition kinds are often exclusive tiers (tipsy/drunk). Enforced
    /// by the resolver alongside Excludes and When.
    /// </summary>
    public string? Requires { get; init; }
    /// <summary>
    /// Condition kinds (comma-separated; ANY present suppresses the
    /// affordance) — the verb disappears from the actor's menu entirely.
    /// For attemptable-but-doomed actions (drinking while bursting) use
    /// an execution gate instead, so agents can still try and fail loudly.
    /// </summary>
    public string? Excludes { get; init; }
    /// <summary>
    /// Observable-state gates on a module field of the target (default) or
    /// the actor: the affordance is only listed while every spec matches
    /// (Equals literal, or Min/Max on a number). Hides "Drink the ale"
    /// once the vessel is empty, shows "Clear the mug" only once it is.
    /// </summary>
    public List<WhenSpec>? When { get; init; }
    /// <summary>
    /// Execution-time gates, evaluated in TurnManager.PerformAction
    /// BEFORE reaction parking and the check roll. Unlike
    /// Requires/Excludes/When (which hide the action from menus and
    /// policies), a blocked gate FAILS the attempt with its failText —
    /// the action stays listed so agents can try and be told why not.
    /// Kinds resolve through the GateRegistry ("condition", "field", ...).
    /// </summary>
    public List<GateSpec>? Gates { get; init; }
    /// <summary>Free-text prompt (the verb takes an argument).</summary>
    public string? Prompt { get; init; }
    /// <summary>
    /// Never offered against the module's owner itself — for services an
    /// agent performs for others ("Ask the blacksmith to repair..." makes
    /// no sense in the blacksmith's own action list).
    /// </summary>
    public bool OthersOnly { get; init; }
    /// <summary>
    /// Offered only to the player-controlled agent (policy "player") —
    /// e.g. the game-ending "Go home": an NPC picking it would end the
    /// PLAYER's game.
    /// </summary>
    public bool PlayerOnly { get; init; }
    /// <summary>Offered only to autonomous agents (policy != "player") — e.g. an NPC's own, quieter way home.</summary>
    public bool NpcOnly { get; init; }
    /// <summary>
    /// Emitted from the agent's own modules once per other agent present,
    /// with that agent as the target ("Perform the rite on {target}") —
    /// the performer-facing direction of a service.
    /// </summary>
    public bool TargetOthers { get; init; }
    public int Duration { get; init; } = 1;
    /// <summary>
    /// How salient the ACTOR's own memory of performing this action is
    /// (events of age-resistance); unset = the agent's
    /// memorySalienceBoost. Conversation verbs declare a high value so
    /// exchanges outlive ambient chatter on both sides (the listener's
    /// side comes from signal-spec salience plus the addressed boost).
    /// </summary>
    public int? Salience { get; init; }
    /// <summary>
    /// Speech-track affordance (say, and future vocalizations like shout
    /// or whisper): the action occupies the agent's speech track instead
    /// of the action track, so talking doesn't block movement or attacks.
    /// </summary>
    public bool Speech { get; init; }
    /// <summary>
    /// Directionality of a speech verb: how the resolver parameterizes it
    /// with {speech} entries. Broadcast — one undirected entry ("Shout:
    /// {speech}"); Directed — one entry per other agent present, none
    /// when alone ("Whisper to Nix: {speech}"); Both — say's broadcast
    /// plus directed entries in a crowd (null behaves as Both for verb
    /// "say"; other speech verbs must declare it).
    /// </summary>
    public SpeechTargeting? SpeechTargets { get; init; }
    /// <summary>
    /// Offered from the agent's own modules once per BODY PART of each
    /// other agent present ("Kiss Maya's neck", "Massage her shoulders")
    /// — the touch family. Pairs with <see cref="IntimateParts"/>:
    /// non-intimate parts (neck, shoulders) are always listed, intimate
    /// ones (marked intimate on their bodypart module) only when their
    /// region is uncovered. The action targets the PART; the reaction
    /// system resolves the defending holder from its parent.
    /// </summary>
    public bool TargetParts { get; init; }
    /// <summary>With TargetParts: list intimate parts (exposed ones) instead of non-intimate ones.</summary>
    public bool IntimateParts { get; init; }
    /// <summary>
    /// Free string payload for the affordance's handler (answers for ask
    /// verbs, intensities for stimulation, targets for set-style verbs) —
    /// keeps scenario text and tuning in data rather than handler code.
    /// </summary>
    public Dictionary<string, string>? Data { get; init; }
    /// <summary>
    /// Present-tense description of performing this action ("massaging
    /// {target}"), shown while the actor is busy with it — in room
    /// listings, examine, and LLM contexts. Unset: a naive gerund of the
    /// verb plus target ("kissing Maya's neck").
    /// </summary>
    public string? Activity { get; init; }
    public bool RepeatBackoff { get; init; }
    public int RepeatBackoffCap { get; init; } = 30;
    public List<string>? Postures { get; init; }
    public bool SameSupport { get; init; }
    public List<Signals.SignalSpec> Signals { get; init; } = [];
    /// <summary>
    /// Signals emitted when the action FAILS (a failed check or a failed
    /// handler — e.g. a missed attack or a botched pickpocketing rattling
    /// the victim). Failures are otherwise silent to observers.
    /// </summary>
    public List<Signals.SignalSpec> FailSignals { get; init; } = [];

    /// <summary>
    /// Optional stat/skill check gating the affordance: evaluated before
    /// the handler runs (in TurnManager.PerformAction, so player plans,
    /// NPCs, and the debug API all respect it). A failed check consumes the
    /// turn, runs no handler, and emits no signals.
    /// </summary>
    public CheckSpec? Check { get; init; }

    /// <summary>
    /// Optional quick-time reaction: when the action targets another agent,
    /// the attempt telegraphs (observers see it, the actor is committed)
    /// and the target gets a window to choose a response before the check
    /// and handler resolve. Null = no reaction possible.
    /// </summary>
    public ReactionSpec? Reaction { get; init; }
}

/// <summary>
/// A quick-time reaction declared on an affordance. When the action
/// targets a (non-incapacitated) agent and at least two options survive
/// availability filtering, the defender gets Window game seconds to pick
/// an option; the Default option applies at the deadline. Options with a
/// RequiresWornModule are only available when the defender wears an item
/// with that module (block needs a shield, parry a weapon). Window 0 =
/// too fast to react; the action resolves immediately.
/// </summary>
public sealed class ReactionSpec
{
    /// <summary>Game seconds the defender has to decide; 0 = no reaction.</summary>
    public int Window { get; init; }
    /// <summary>Signal text telegraphing the attempt ("{agent} swings at the {target}!").</summary>
    public string? Telegraph { get; init; }
    /// <summary>Message for the actor on committing; defaults to "You {verb} {target}."</summary>
    public string? ActorText { get; init; }
    public List<ReactionOptionSpec> Options { get; init; } = [];
}

/// <summary>
/// One reaction choice. Stat/Skill/Bonus replace the defender's side of
/// the opposed check (Bonus is flat, e.g. a shield); NoResist accepts the
/// action (defender contributes 0 — the default for positive actions).
/// Text is the defender-side memory line ("You dodge the blow.").
/// Report is the actor-facing line for the choice ("{agent} attempts to
/// dodge." — {agent} is the reacting defender, {target} the actor, "you");
/// it's written because the choice itself is invisible to the actor
/// otherwise (signals never reach the actor). Unset = quiet.
/// </summary>
public sealed class ReactionOptionSpec
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string? Stat { get; init; }
    public string? Skill { get; init; }
    public int Bonus { get; init; }
    public string? RequiresWornModule { get; init; }
    public bool NoResist { get; init; }
    public bool Default { get; init; }
    /// <summary>
    /// Dynamic default: while this field condition holds on the
    /// defender, this option is the effective default (checked before
    /// the static Default flag) — a date melts into a touch when her
    /// relationship is high and gently stops it otherwise, without a
    /// policy or LLM round-trip.
    /// </summary>
    public WhenSpec? DefaultWhen { get; init; }
    public string? Text { get; init; }
    public string? Report { get; init; }
}

/// <summary>
/// A stat/skill check declared on an affordance. The actor's bonus is
/// their stat plus skill value; success when dice + bonus >= difficulty.
/// The dice formula (n d m) comes from the scenario's rules module.
/// When Opposed is set, a defender (the target agent, or the agent
/// holding the target item) rolls their own dice + opposed stat/skill and
/// the actor must beat them.
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
}

/// <summary>The defender's side of an opposed check: their stat/skill.</summary>
public sealed class OpposedSpec
{
    public string? Stat { get; init; }
    public string? Skill { get; init; }
}

/// <summary>
/// One observable-state gate on a module field: which object to test (On
/// "target", the default, or "actor"), and the comparison the field must
/// pass — a literal bool/number/string compared verbatim (JSON "equals"),
/// or numeric Min/Max bounds. All specs on an affordance must match.
/// </summary>
public sealed class WhenSpec
{
    public required string Module { get; init; }
    public required string Field { get; init; }
    /// <summary>"target" (default) or "actor".</summary>
    public string? On { get; init; }
    [JsonPropertyName("equals")]
    public JsonElement? EqualsValue { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
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
