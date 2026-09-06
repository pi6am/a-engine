using AEngine.Core.Modules;
using AEngine.Core.Runtime;
using AEngine.Core.World;

namespace AEngine.Core.Actions;

/// <summary>
/// Adventure scoring, data-driven: treasures carry a `treasure` module
/// (value = points on first acquisition, tvalue = points while deposited
/// in the trophy case), rooms carry a one-time entry value, and the
/// player carries a `scorecard` module whose <c>score</c> field
/// accumulates the base points. The DISPLAYED score is the base plus the
/// live sum of tvalues currently inside the case (<c>caseRef</c>) —
/// deposit points come and go with the loot, exactly like the original.
/// When the total reaches the rules module's <c>winScore</c> the engine
/// sets <c>won</c> (gates and automations key on it) and whispers the
/// rules' <c>winText</c> to the player. Ranks come from the rules'
/// <c>ranks</c> list ("<c>min|Title</c>", highest matching min wins).
/// </summary>
public static class Score
{
    /// <summary>
    /// The agent's current score: base scorecard points plus the tvalue
    /// of every treasure currently inside the referenced case. Agents
    /// without a scorecard read 0.
    /// </summary>
    public static int Of(World.World world, ModuleRegistry modules, WorldObject agent)
    {
        if (!agent.HasModule("scorecard"))
            return 0;
        var total = modules.ResolveInt(agent, "scorecard", "score");
        if (modules.ResolveString(agent, "scorecard", "caseRef") is { } caseId &&
            world.HasObject(caseId))
        {
            // only treasures carry deposit points; the case also holds
            // whatever else an adventurer stuffs into it
            foreach (var child in world.ChildrenOf(caseId))
                if (child.HasModule("treasure"))
                    total += modules.ResolveInt(child, "treasure", "tvalue");
        }
        return total;
    }

    /// <summary>
    /// Award a treasure's one-time acquisition value: called by take on
    /// success (and anything else that hands an object over). Idempotent
    /// per object via the treasure module's scored flag.
    /// </summary>
    public static int AwardItem(World.World world, ModuleRegistry modules, WorldObject item)
    {
        if (!item.HasModule("treasure") ||
            modules.ResolveBool(item, "treasure", "scored"))
            return 0;
        var value = modules.ResolveInt(item, "treasure", "value");
        if (value == 0)
            return 0;
        world.SetFieldOverride(item.Id, "treasure", "scored", World.World.ToJson(true));
        return value;
    }

    /// <summary>
    /// Award a room's one-time entry value (the first visit). Marks the
    /// room visited and returns the points; 0 when none apply.
    /// </summary>
    public static int AwardRoom(World.World world, ModuleRegistry modules, WorldObject room)
    {
        if (!room.HasModule("room") || modules.ResolveBool(room, "room", "visited"))
            return 0;
        var value = modules.ResolveInt(room, "room", "value");
        if (value == 0)
            return 0;
        world.SetFieldOverride(room.Id, "room", "visited", World.World.ToJson(true));
        return value;
    }

    /// <summary>
    /// Add points to the agent's base score (a negative penalty for a
    /// death, a bonus for an achievement). No-op without a scorecard.
    /// </summary>
    public static void Adjust(World.World world, ModuleRegistry modules, WorldObject agent, int delta)
    {
        if (!agent.HasModule("scorecard") || delta == 0)
            return;
        world.SetFieldOverride(agent.Id, "scorecard", "score",
            World.World.ToJson(modules.ResolveInt(agent, "scorecard", "score") + delta));
    }

    /// <summary>
    /// The win check, run on the world-clock pass: any scorecard-carrying
    /// agent reaching the rules' winScore flips <c>won</c> (once) and
    /// receives the whisper. Scenarios key barrow gates and map reveals
    /// on the won flag through gates/automations — the engine knows
    /// nothing about endings themselves.
    /// </summary>
    public static void CheckWin(GameEngine engine)
    {
        var world = engine.World;
        var modules = engine.ModuleRegistry;
        var rules = Checks.RulesHost(world);
        if (rules is null)
            return;
        var winScore = modules.ResolveInt(rules, "rules", "winScore");
        if (winScore <= 0 || modules.ResolveBool(rules, "rules", "won"))
            return;
        foreach (var obj in world.Objects.Values)
        {
            if (!obj.HasModule("agent") || !obj.HasModule("scorecard") ||
                Of(world, modules, obj) < winScore)
                continue;
            world.SetFieldOverride(rules.Id, "rules", "won", World.World.ToJson(true));
            if (modules.ResolveString(rules, "rules", "winText") is { Length: > 0 } text)
                engine.SignalBus.SendTo(obj, text);
            return;
        }
    }

    /// <summary>
    /// The player's rank title for a score: the highest "min|Title" rung
    /// from the rules' ranks list whose min the score reaches (the
    /// empty string when no ranks are authored).
    /// </summary>
    public static string RankOf(World.World world, ModuleRegistry modules, int score)
    {
        var best = "";
        var bestMin = int.MinValue;
        var rules = Checks.RulesHost(world);
        var ranks = rules is null
            ? null
            : modules.ResolveStringList(rules, "rules", "ranks");
        foreach (var rung in ranks ?? [])
        {
            var bar = rung.IndexOf('|');
            if (bar <= 0 || !int.TryParse(rung[..bar], out var min) || score < min || min <= bestMin)
                continue;
            bestMin = min;
            best = rung[(bar + 1)..];
        }
        return best;
    }
}

/// <summary>
/// The "score" verb: reports the player's score, moves, and rank
/// ("Your score is 341 (total of 350 points), in 512 moves. This gives
/// you the rank of Master Adventurer.").
/// </summary>
public sealed class ScoreHandler : IActionHandler
{
    public string Id => "score";

    public ActionResult Execute(ActionContext ctx)
    {
        var score = Score.Of(ctx.World, ctx.Modules, ctx.Agent);
        var rank = Score.RankOf(ctx.World, ctx.Modules, score);
        var rules = Checks.RulesHost(ctx.World);
        var winScore = rules is null
            ? 0
            : ctx.Modules.ResolveInt(rules, "rules", "winScore");
        var total = winScore > 0 ? $" (total of {winScore} points)" : "";
        var rankLine = rank.Length > 0 ? $" This gives you the rank of {rank}." : "";
        return ActionResult.Ok($"Your score is {score}{total}, in {ctx.Turn} moves.{rankLine}");
    }
}
