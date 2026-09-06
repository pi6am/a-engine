using System.Text.Json;
using AEngine.Core.Actions;
using AEngine.Core.Modules;
using AEngine.Core.World;

namespace AEngine.Core.Signals;

/// <summary>
/// Keyword reactions to <b>perceived</b> signals — an `eavesdrop` module
/// on an object makes it listen to what is delivered to it: any signal
/// (by default audible — speech) whose text contains one of a trigger's
/// keywords fires that trigger's effects, gated by its `when`
/// conditions (selectors see the listener as "self", the same rule
/// vocabulary automations use). The cyclops doesn't know you spoke to
/// HIM — he hears the name of his father's murderer in anything said
/// aloud in his room, a shouted rumor, a bard's line of TV chatter.
/// <para>
/// Reactions are collected at delivery (see
/// <see cref="SignalBus.Enqueue"/>) and applied when the outermost
/// Emit/SendTo completes — a fleeing listener's own farewell signal
/// can't re-enter a delivery loop mid-iteration, and world mutations
/// (the flee teleports the listener away) never race the observer walk.
/// Attenuated renderings protect the words: a murmur through a door
/// carries no {arg}, so no keywords — overheard means heard clearly.
/// </para>
/// </summary>
public static class Eavesdrop
{
    /// <summary>
    /// Match an observer's triggers against a delivered signal and
    /// collect the reactions (applied later, at flush time).
    /// </summary>
    public static IEnumerable<(WorldObject Listener, JsonElement Effects)> Match(
        World.World world, ModuleRegistry modules, WorldObject observer, Signal signal)
    {
        var triggers = modules.ResolveField(observer, "eavesdrop", "triggers");
        if (triggers is not { ValueKind: JsonValueKind.Array } list)
            yield break;
        foreach (var trigger in list.EnumerateArray())
        {
            // sense gate: speech is audible by default; "any" hears all
            if (trigger.TryGetProperty("sense", out var sense) &&
                sense.GetString() is { } senseName &&
                !signal.Sense.ToString().Equals(senseName, StringComparison.OrdinalIgnoreCase) &&
                !senseName.Equals("any", StringComparison.OrdinalIgnoreCase))
                continue;
            if (trigger.TryGetProperty("keywords", out var keywords) &&
                keywords.ValueKind == JsonValueKind.Array &&
                !keywords.EnumerateArray().Any(k =>
                    k.ValueKind == JsonValueKind.String &&
                    signal.Text.Contains(k.GetString()!, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (trigger.TryGetProperty("when", out var when) &&
                !RuleConditions.Evaluate(world, modules, when,
                    new EffectContext(Self: observer)))
                continue;
            if (trigger.TryGetProperty("effects", out var effects) &&
                effects.ValueKind == JsonValueKind.Array)
                yield return (observer, effects);
        }
    }
}
