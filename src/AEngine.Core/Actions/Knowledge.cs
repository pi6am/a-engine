using System.Text.Json;
using System.Text.RegularExpressions;
using AEngine.Core.Modules;
using AEngine.Core.Runtime;
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

    /// <summary>
    /// The description the observer should see: the agent's
    /// `incognitoDescription` while their name is unknown (descriptions
    /// often introduce their subject by name — "Rath Cinderstorm, a
    /// stooped sorcerer…" — and a look shouldn't teach a name), else the
    /// full description. Non-agents and unset fields fall back to the
    /// real description.
    /// </summary>
    public static string DescriptionFor(ModuleRegistry modules, WorldObject observer, WorldObject obj) =>
        obj.HasModule("agent") && !KnowsName(modules, observer, obj) &&
        modules.ResolveString(obj, "agent", "incognitoDescription") is { Length: > 0 } incognito
            ? incognito
            : obj.Description;

    // --- notable items: last-seen memory ---

    /// <summary>
    /// Where an observer last saw a notable item: the container or agent
    /// holding it (null = loose) and the room (null = unknown). Either
    /// side can be unset independently — "held by Mira, somewhere",
    /// "somewhere in the alley" — and a bare entry means seen, whereabouts
    /// forgotten.
    /// </summary>
    public sealed record Sighting(string? Holder, string? Room);

    /// <summary>The observer's last-seen map for notable items (a read-only snapshot).</summary>
    public static IReadOnlyDictionary<string, Sighting> LastSeen(ModuleRegistry modules, WorldObject observer)
    {
        var result = new Dictionary<string, Sighting>(StringComparer.Ordinal);
        if (!Tracks(observer) ||
            modules.ResolveField(observer, "knowledge", "lastSeen") is not { ValueKind: JsonValueKind.Object } e)
            return result;
        foreach (var prop in e.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object)
                continue;
            string? holder = null, room = null;
            if (prop.Value.TryGetProperty("holder", out var h) && h.ValueKind == JsonValueKind.String)
                holder = h.GetString();
            if (prop.Value.TryGetProperty("room", out var r) && r.ValueKind == JsonValueKind.String)
                room = r.GetString();
            result[prop.Name] = new Sighting(holder, room);
        }
        return result;
    }

    /// <summary>
    /// Refresh the observer's last-seen knowledge from their current view
    /// and render it for the LLM context ("Important items: pouch of ember
    /// salt (held by Mira the herbalist in Herbalist's Stall)"). Items
    /// directly observed now (including anything in the observer's own
    /// hands) are not repeated — the context's location report already
    /// says where they are. Three update rules fire on each observation:
    /// notable items in view are recorded (holder + room); a remembered
    /// holder seen WITHOUT the item loses the holder fact; a remembered
    /// room observed without the item loses the room fact — so watching
    /// Mira's stall after she left keeps "held by Mira" but drops the
    /// where. Destroyed items (a consumed reagent) are forgotten.
    /// </summary>
    public static IReadOnlyList<string> ItemReport(GameEngine engine, WorldObject observer)
    {
        if (!Tracks(observer))
            return [];
        var world = engine.World;
        var modules = engine.ModuleRegistry;
        var room = world.RoomOf(observer.Id);
        var visible = VisibleFrom(world, modules, room).Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        var seen = new Dictionary<string, Sighting>(LastSeen(modules, observer));
        var changed = false;

        // record sightings of notable items currently in view
        foreach (var item in world.Objects.Values.Where(o => o.HasModule("notable") && visible.Contains(o.Id)))
        {
            seen[item.Id] = new Sighting(
                Holder: item.Parent != room.Id && world.HasObject(item.Parent) ? item.Parent : null,
                Room: room.Id);
            changed = true;
        }
        // destroyed items are gone — forget them entirely
        foreach (var id in seen.Keys.Where(id => !world.HasObject(id)).ToList())
        {
            seen.Remove(id);
            changed = true;
        }
        // the holder is in view but no longer holds the item: unset holder
        foreach (var id in seen.Keys.ToList())
        {
            var s = seen[id];
            if (s.Holder is not null && world.HasObject(s.Holder) && visible.Contains(s.Holder) &&
                !world.GetObject(s.Holder).Children.Contains(id))
            {
                seen[id] = s with { Holder = null };
                changed = true;
            }
        }
        // the remembered room is here but the item isn't visible: unset room
        foreach (var id in seen.Keys.ToList())
        {
            var s = seen[id];
            if (s.Room == room.Id && !visible.Contains(id))
            {
                seen[id] = s with { Room = null };
                changed = true;
            }
        }

        if (changed)
            WriteLastSeen(world, observer, seen);

        var report = new List<string>();
        foreach (var (id, sighting) in seen)
        {
            if (visible.Contains(id) || !world.HasObject(id))
                continue;
            report.Add(DescribeSighting(engine, observer, world.GetObject(id), sighting));
        }
        return report;
    }

    private static string DescribeSighting(GameEngine engine, WorldObject observer, WorldObject item, Sighting sighting)
    {
        var world = engine.World;
        string where;
        if (sighting.Holder is not null && world.HasObject(sighting.Holder))
        {
            var holder = world.GetObject(sighting.Holder);
            var held = holder.HasModule("agent")
                ? $"held by {NameFor(engine.ModuleRegistry, observer, holder)}"
                : $"in {Perception.WithDefiniteArticle(holder.Name)}";
            where = sighting.Room is not null && world.HasObject(sighting.Room)
                ? $"{held} in {world.GetObject(sighting.Room).Name}"
                : held;
        }
        else if (sighting.Room is not null && world.HasObject(sighting.Room))
            where = $"in {world.GetObject(sighting.Room).Name}";
        else
            where = "somewhere";
        return $"{item.Name} ({where})";
    }

    /// <summary>
    /// Everything an observer in this room can see, recursively: room
    /// contents, open containers' and surfaces' contents, agents' carried
    /// and worn items, furniture occupants. Closed containers, portal
    /// machinery, body parts, and conditions stay invisible.
    /// </summary>
    private static IEnumerable<WorldObject> VisibleFrom(World.World world, ModuleRegistry modules, WorldObject obj)
    {
        foreach (var child in world.ChildrenOf(obj.Id))
        {
            if (child.HasModule("portal") || Conditions.IsInternal(child))
                continue;
            yield return child;
            var reveals =
                child.HasModule("surface") ||
                child.HasModule("container") && Perception.IsOpen(world, modules, child) ||
                child.HasModule("agent") ||
                child.HasModule("sittable") || child.HasModule("lyable");
            if (reveals)
                foreach (var inner in VisibleFrom(world, modules, child))
                    yield return inner;
        }
    }

    private static void WriteLastSeen(World.World world, WorldObject observer, Dictionary<string, Sighting> seen)
    {
        var dict = seen.ToDictionary(kv => kv.Key, kv =>
        {
            var entry = new Dictionary<string, string>();
            if (kv.Value.Holder is not null)
                entry["holder"] = kv.Value.Holder;
            if (kv.Value.Room is not null)
                entry["room"] = kv.Value.Room;
            return (object)entry;
        });
        world.SetFieldOverride(observer.Id, "knowledge", "lastSeen", World.World.ToJson(dict));
    }
}
