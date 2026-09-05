using System.Text.RegularExpressions;
using AEngine.Core.Modules;
using AEngine.Core.World;

namespace AEngine.Core.Actions;

/// <summary>
/// Per-agent pronouns, all five forms, resolved in layers: the agent's
/// own <c>pronouns</c> module field override → the bundle module's
/// field default (the agent's <c>pronouns.bundle</c> names a module
/// DEFINITION in modules.json whose field defaults carry the table —
/// <c>female_pronouns</c>, <c>male_pronouns</c>, any set the scenario
/// invents) → the generic they/them/their/theirs/themself fallback,
/// so agents without pronouns still render. Rendering is
/// observer-relative like names: when the observer IS the referent,
/// every form collapses to the second person ("you", "your",
/// "yourself"), and a subject tag at sentence start de-conjugates the
/// verb that follows ("{agent.subject} draws back" → "you draw back")
/// — the same rule <see cref="Signals.SignalBus"/> applies to
/// <c>{target}</c>. Number agreement is the author's to mind: a
/// they-bundled agent takes plural verbs ("they draw"), so generic
/// templates should phrase around names or plural-safe verbs.
/// </summary>
public static class Pronouns
{
    public static readonly string[] Forms = ["subject", "object", "possessive", "possessivePronoun", "reflexive"];

    public static string Subject(ModuleRegistry modules, WorldObject agent) =>
        Form(modules, agent, "subject", "they");

    public static string Object(ModuleRegistry modules, WorldObject agent) =>
        Form(modules, agent, "object", "them");

    /// <summary>The possessive adjective: her mouth, their hand.</summary>
    public static string Possessive(ModuleRegistry modules, WorldObject agent) =>
        Form(modules, agent, "possessive", "their");

    /// <summary>The possessive pronoun: the choice was hers.</summary>
    public static string PossessivePronoun(ModuleRegistry modules, WorldObject agent) =>
        Form(modules, agent, "possessivePronoun", "theirs");

    public static string Reflexive(ModuleRegistry modules, WorldObject agent) =>
        Form(modules, agent, "reflexive", "themself");

    /// <summary>Any form, observer-relative: the second person when the observer is the referent.</summary>
    public static string Form(ModuleRegistry modules, WorldObject agent, string form, WorldObject? observer) =>
        observer is not null && observer.Id == agent.Id
            ? form switch
            {
                "subject" => "you",
                "object" => "you",
                "possessive" => "your",
                "possessivePronoun" => "yours",
                "reflexive" => "yourself",
                _ => "",
            }
            : Form(modules, agent, form, GenericOf(form));

    private static string Form(ModuleRegistry modules, WorldObject agent, string form, string generic)
    {
        if (agent.HasModule("pronouns"))
        {
            if (modules.ResolveString(agent, "pronouns", form) is { Length: > 0 } own)
                return own;
            if (modules.ResolveString(agent, "pronouns", "bundle") is { Length: > 0 } bundle &&
                modules.Has(bundle) &&
                modules.Get(bundle).GetField(form)?.Default is
                    { ValueKind: System.Text.Json.JsonValueKind.String } element &&
                element.GetString() is { Length: > 0 } fromBundle)
                return fromBundle;
        }
        return generic;
    }

    private static string GenericOf(string form) => form switch
    {
        "subject" => "they",
        "object" => "them",
        "possessive" => "their",
        "possessivePronoun" => "theirs",
        "reflexive" => "themself",
        _ => "",
    };

    /// <summary>
    /// Replace one referent's pronoun tags in a template —
    /// <c>{agent.possessive}</c>, <c>{holder.subject}</c>, … — for the
    /// given observer. A null referent's tags render empty (matching
    /// how {holder} collapses without a holder). Subject tags in the
    /// second person de-conjugate a following verb at sentence start.
    /// </summary>
    public static string ReplaceReferent(
        string text, string tag, WorldObject? referent, WorldObject? observer, ModuleRegistry modules)
    {
        if (referent is null)
        {
            foreach (var form in Forms)
                text = text.Replace($"{{{tag}.{form}}}", "", StringComparison.Ordinal);
            return text;
        }
        if (observer is not null && observer.Id == referent.Id)
        {
            text = new Regex($@"\{{{Regex.Escape(tag)}\.subject\}}(?: (\w+))?",
                RegexOptions.Compiled).Replace(text, match =>
            {
                var before = text[..match.Index].TrimEnd();
                var sentenceStart = before.Length == 0 ||
                    before.EndsWith('.') || before.EndsWith('!') || before.EndsWith('?');
                var verb = match.Groups[1].Value;
                return sentenceStart && verb.Length > 0
                    ? "you " + ToSecondPerson(verb)
                    : "you" + (verb.Length > 0 ? " " + verb : "");
            });
            foreach (var form in Forms)
                text = text.Replace($"{{{tag}.{form}}}",
                    Form(modules, referent, form, observer), StringComparison.Ordinal);
            return text;
        }
        foreach (var form in Forms)
            text = text.Replace($"{{{tag}.{form}}}",
                Form(modules, referent, form, GenericOf(form)), StringComparison.Ordinal);
        return text;
    }

    /// <summary>Third-person singular verb → second person: declines → decline, tries → try, watches → watch.</summary>
    public static string ToSecondPerson(string verb)
    {
        if (verb.EndsWith("ies", StringComparison.Ordinal) && verb.Length > 3)
            return verb[..^3] + "y";
        if (verb.EndsWith("shes", StringComparison.Ordinal) ||
            verb.EndsWith("ches", StringComparison.Ordinal) ||
            verb.EndsWith("sses", StringComparison.Ordinal) ||
            verb.EndsWith("xes", StringComparison.Ordinal) ||
            verb.EndsWith("zes", StringComparison.Ordinal) ||
            verb.EndsWith("oes", StringComparison.Ordinal))
            return verb[..^2];
        if (verb.EndsWith('s') && !verb.EndsWith("ss", StringComparison.Ordinal))
            return verb[..^1];
        return verb;
    }
}
