using System.Text.RegularExpressions;
using AEngine.Core.Modules;
using AEngine.Core.World;

namespace AEngine.Core.Actions;

/// <summary>
/// Who an agent can name. An agent carrying a `knowledge` module tracks
/// learned facts; the only fact type so far is knowing another agent's
/// name (the module's `knowsNames` list of agent ids, pre-populatable in
/// scenario data — the regulars all know each other). Agents WITHOUT the
/// module know everything (back-compat: unchanged behavior for every
/// existing scenario).
///
/// Names are learned from overheard text: when a delivered signal
/// mentions an agent's proper name ("Nix", word-bounded), the observer
/// learns it — so introductions happen through conversation, and a name
/// overheard through a doorway is a name learned. Until then the agent
/// renders by their `incognito` description ("a short green-skinned
/// goblin woman"); the proper `name` prints once known. `properNames`
/// (unique in the scenario) also feeds ACTION PARSING — "Say to Nix:"
/// resolves to her whether or not the speaker could print her name.
/// </summary>
public static class Knowledge
{
    /// <summary>Whether this agent tracks what they know at all.</summary>
    public static bool Tracks(WorldObject observer) => observer.HasModule("knowledge");

    /// <summary>
    /// Whether the observer can name the target: always true when the
    /// observer tracks nothing; otherwise the target's id must be in the
    /// observer's learned/pre-populated knowsNames list.
    /// </summary>
    public static bool KnowsName(ModuleRegistry modules, WorldObject observer, WorldObject target)
    {
        if (!Tracks(observer))
            return true;
        if (observer.Id == target.Id)
            return true; // everyone can name themselves
        return (modules.ResolveStringList(observer, "knowledge", "knowsNames") ?? [])
            .Contains(target.Id, StringComparer.Ordinal);
    }

    /// <summary>Record that the observer has learned the named agent's name.</summary>
    public static void LearnName(World.World world, ModuleRegistry modules, WorldObject observer, string targetId)
    {
        if (!Tracks(observer) || observer.Id == targetId)
            return;
        var known = modules.ResolveStringList(observer, "knowledge", "knowsNames") ?? [];
        if (known.Contains(targetId, StringComparer.Ordinal))
            return;
        known.Add(targetId);
        world.SetFieldOverride(observer.Id, "knowledge", "knowsNames", World.World.ToJson(known));
    }

    /// <summary>
    /// An agent's proper names (the agent module's `properNames` list —
    /// "Nix", or ["Rath", "Cinderstorm"]), unique in the scenario. Used
    /// for parsing and for learning, never for printing.
    /// </summary>
    public static List<string> ProperNames(ModuleRegistry modules, WorldObject agent) =>
        agent.HasModule("agent")
            ? modules.ResolveStringList(agent, "agent", "properNames") ?? []
            : [];

    /// <summary>
    /// Learn every proper name mentioned in a text the observer just
    /// perceived (delivered signals — speech content, mostly): a
    /// word-bounded, case-sensitive match against every agent's proper
    /// names records the fact.
    /// </summary>
    public static void LearnFromText(World.World world, ModuleRegistry modules, WorldObject observer, string text)
    {
        if (!Tracks(observer) || text.Length == 0)
            return;
        foreach (var candidate in world.Objects.Values)
        {
            if (candidate.Id == observer.Id || !candidate.HasModule("agent"))
                continue;
            foreach (var proper in ProperNames(modules, candidate))
            {
                if (proper.Length == 0)
                    continue;
                var pattern = $"\\b{Regex.Escape(proper)}\\b";
                if (Regex.IsMatch(text, pattern))
                {
                    LearnName(world, modules, observer, candidate.Id);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// The name the observer should see for the object: the object's real
    /// name when they know it (or never had to learn it), else the
    /// object's `incognito` description (falling back to the real name
    /// when no incognito is authored).
    /// </summary>
    public static string NameFor(ModuleRegistry modules, WorldObject observer, WorldObject obj) =>
        obj.HasModule("agent") && !KnowsName(modules, observer, obj)
            ? modules.ResolveString(obj, "agent", "incognito") is { Length: > 0 } incognito
                ? incognito
                : obj.Name
            : obj.Name;
}
