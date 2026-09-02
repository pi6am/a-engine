using AEngine.Core.Modules;
using AEngine.Core.Signals;
using AEngine.Core.World;

namespace AEngine.Core.Runtime;

/// <summary>
/// World-clock upkeep for objects with a `chatter` module that are on:
/// periodically emits one of the current channel's lines as a low,
/// easily-forgotten audible signal in the object's room (a television
/// murmuring in the corner). Lines come from the `channels` map field
/// (channel name → line pool); `interval` is fixed seconds or
/// {"min","max"} re-rolled per emission (mirroring the ambient module's
/// timing). Elapsed/next-due ride as fields (`elapsed`, `nextDue`) so
/// the state is inspectable and survives save/restore. A chattering
/// object needs no policy, no reactions, and no room-granular ears —
/// it is a voice, not an agent.
/// </summary>
public static class Chatter
{
    /// <summary>Advance every chattering object by seconds of world time.</summary>
    public static void Advance(GameEngine engine, int seconds)
    {
        var chatty = engine.World.Objects.Values.Where(o => o.HasModule("chatter")).ToList();
        foreach (var obj in chatty)
        {
            var modules = engine.ModuleRegistry;
            if (!modules.ResolveBool(obj, "chatter", "on"))
                continue;
            var elapsed = modules.ResolveInt(obj, "chatter", "elapsed") + seconds;
            var due = modules.ResolveInt(obj, "chatter", "nextDue");
            if (due <= 0)
                due = RollDelay(modules, obj, engine.Random); // first line after one interval
            if (elapsed < due)
            {
                engine.World.SetFieldOverride(obj.Id, "chatter", "elapsed",
                    World.World.ToJson(elapsed));
                continue;
            }
            engine.World.SetFieldOverride(obj.Id, "chatter", "elapsed", World.World.ToJson(0));
            engine.World.SetFieldOverride(obj.Id, "chatter", "nextDue",
                World.World.ToJson(RollDelay(modules, obj, engine.Random)));
            var line = PickLine(modules, obj, engine.Random);
            if (line is null)
                continue;
            engine.SignalBus.Emit(obj, null,
            [
                new SignalSpec
                {
                    Sense = SignalSense.Audible,
                    Priority = 1,
                    // background noise: TV chatter ages out of memory fast
                    Salience = -4,
                    Strength = 1,
                    Text = line,
                },
            ], null);
        }
    }

    private static string? PickLine(ModuleRegistry modules, WorldObject obj, Random random)
    {
        if (modules.ResolveField(obj, "chatter", "channels") is not
                { ValueKind: System.Text.Json.JsonValueKind.Object } map)
            return null;
        var channel = modules.ResolveString(obj, "chatter", "channel") ?? "";
        if (!map.TryGetProperty(channel, out var pool) ||
            pool.ValueKind != System.Text.Json.JsonValueKind.Array)
            return null;
        var lines = pool.EnumerateArray()
            .Where(l => l.ValueKind == System.Text.Json.JsonValueKind.String)
            .Select(l => l.GetString()!)
            .ToList();
        return lines.Count == 0 ? null : lines[random.Next(lines.Count)];
    }

    private static int RollDelay(ModuleRegistry modules, WorldObject obj, Random random)
    {
        var min = 25;
        var max = 45;
        switch (modules.ResolveField(obj, "chatter", "interval"))
        {
            case { ValueKind: System.Text.Json.JsonValueKind.Number } e:
                min = max = e.GetInt32();
                break;
            case { ValueKind: System.Text.Json.JsonValueKind.Object } e:
                if (e.TryGetProperty("min", out var mn) && mn.ValueKind == System.Text.Json.JsonValueKind.Number)
                    min = mn.GetInt32();
                if (e.TryGetProperty("max", out var mx) && mx.ValueKind == System.Text.Json.JsonValueKind.Number)
                    max = mx.GetInt32();
                break;
        }
        min = Math.Max(1, min);
        max = Math.Max(min, max);
        return min + random.Next(max - min + 1);
    }
}
