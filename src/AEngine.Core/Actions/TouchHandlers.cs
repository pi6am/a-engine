using AEngine.Core.Modules;
using AEngine.Core.Runtime;
using AEngine.Core.World;

namespace AEngine.Core.Actions;

/// <summary>
/// Handlers for touching and embracing — the generic interaction
/// layer, with all vocabulary, tuning, and prose in scenario data:
/// motive names arrive as <c>impulse.&lt;motive&gt;</c> data keys (the
/// consume convention), reaction options declare their own
/// <c>effect</c> (welcome/hesitate/refuse — no word matching), and
/// pair states are embrace objects (<see cref="Embraces"/>). The
/// handlers know nothing about what a touch means — a massage, a hug,
/// and more intimate interactions are the same machinery with
/// different data.
/// <list type="bullet">
/// <item><c>touch</c> — a contact on a body part (or an agent):
/// receiver-side <c>impulse.&lt;motive&gt;</c> deltas scaled by the
/// part's sensitivity for the motives listed in
/// <c>sensitivityScales</c>, a <c>giver.impulse.&lt;motive&gt;</c>
/// set for the actor, <c>melt.impulse.*</c>/<c>stop.impulse.*</c>
/// extras for welcomed/refused moments (<c>hesitantScale</c> scales a
/// hesitant one), <c>pace.&lt;position&gt;</c> multipliers and
/// <c>self.&lt;position&gt;</c> prose while actor and receiver share
/// an embrace (the sub-action grammar of a joined pair — "move in
/// her" is a touch with an embrace requirement), and
/// <c>onEvent</c>/<c>onSelfEvent</c> appended when the impulse fires
/// a motive event on the receiver/the actor.</item>
/// <item><c>embrace</c> — enter a pair state by cloning a template
/// (data <c>template</c>, optional initial <c>position</c>): same
/// room required, an <c>exposeRequired</c> wear region checked on
/// both sides, one-embrace-per-agent, entry impulses to both, an
/// <c>entryPosture</c> taken together on a lyable shared support —
/// and <c>autoEnd</c> templates (a hug) dissolve the moment the
/// action completes.</item>
/// <item><c>reposition</c> — shift a lasting embrace to one of its
/// template's declared <c>positions</c>, with partner impulses.</item>
/// <item><c>disengage</c> — dissolve, with an optional "left
/// unfinished" branch (the partner lacking the
/// <c>unfinishedKind</c> condition while
/// <c>unfinishedModule</c>/<c>unfinishedField</c>/<c>unfinishedMin</c>
/// holds) choosing the prose and applying
/// <c>unfinished.impulse.*</c>.</item>
/// </list>
/// </summary>
public static class TouchHandlers
{
    public static IEnumerable<IActionHandler> All() =>
    [
        new TouchHandler(),
        new EmbraceHandler(),
        new RepositionHandler(),
        new DisengageHandler(),
    ];

    private static string Data(ActionContext ctx, string key, string fallback = "") =>
        ctx.Data is not null && ctx.Data.TryGetValue(key, out var value) && value.Length > 0
            ? value
            : fallback;

    private static double DataDouble(ActionContext ctx, string key, double fallback = 0) =>
        ctx.Data is not null && ctx.Data.TryGetValue(key, out var value) &&
        double.TryParse(value, out var parsed) ? parsed : fallback;

    /// <summary>
    /// All data keys under a dotted prefix as motive deltas —
    /// "impulse.", "giver.impulse.", "melt.impulse.", "stop.impulse.".
    /// </summary>
    private static Dictionary<string, double> Impulses(ActionContext ctx, string prefix)
    {
        var deltas = new Dictionary<string, double>(StringComparer.Ordinal);
        if (ctx.Data is null)
            return deltas;
        foreach (var (key, value) in ctx.Data)
            if (key.StartsWith(prefix, StringComparison.Ordinal) &&
                double.TryParse(value, out var parsed))
                deltas[key[prefix.Length..]] = parsed;
        return deltas;
    }

    /// <summary>
    /// The agent a touch lands on: the owner of the target body part,
    /// the target itself when it's an agent (a cuddle), or null for
    /// plain-object targets (watching TV) — impulses still accrue to
    /// the actor.
    /// </summary>
    private static WorldObject? ReceiverOf(ActionContext ctx)
    {
        if (ctx.Target is null)
            return null;
        if (ctx.Target.HasModule("agent"))
            return ctx.Target;
        return ctx.Target.Parent.Length > 0 && ctx.World.HasObject(ctx.Target.Parent) &&
               ctx.World.GetObject(ctx.Target.Parent).HasModule("agent")
            ? ctx.World.GetObject(ctx.Target.Parent)
            : null;
    }

    private static string Render(string template, ActionContext ctx, WorldObject? instrument = null)
    {
        var receiver = ReceiverOf(ctx);
        var target = ctx.Target is not null && ctx.Target.HasModule("bodypart") && receiver is not null
            ? receiver.Id == ctx.Agent.Id
                ? $"your own {ctx.Target.Name}"
                : $"{Knowledge.NameFor(ctx.Modules, ctx.Agent, receiver)}'s {ctx.Target.Name}"
            : receiver is not null
                ? Perception.WithDefiniteArticle(Knowledge.NameFor(ctx.Modules, ctx.Agent, receiver))
                : ctx.Target is not null
                    ? Perception.WithDefiniteArticle(ctx.Target.Name)
                    : "";
        // receiver pronouns ({receiver.subject} melts…), seen from the
        // actor's chair — always third person here
        var text = Pronouns.ReplaceReferent(template, "receiver", receiver, observer: null, ctx.Modules);
        return text
            .Replace("{verb}", ctx.Verb ?? "touch", StringComparison.Ordinal)
            .Replace("{target}", target, StringComparison.Ordinal)
            .Replace("{instrument}", instrument?.Name ?? "", StringComparison.Ordinal);
    }

    private sealed class TouchHandler : IActionHandler
    {
        public string Id => "touch";

        public ActionResult Execute(ActionContext ctx)
        {
            var receiver = ReceiverOf(ctx);
            var effect = ctx.Reaction?.Effect ?? "welcome";
            if (effect == "refuse")
            {
                // a deflected advance cools things a little — not a
                // punishment, just the moment stalling
                if (receiver is not null)
                    Motives.Impulse(ctx.World, ctx.Modules, ctx.Signals, receiver,
                        Impulses(ctx, "stop.impulse."));
                return ActionResult.Fail(Data(ctx, "onStop",
                    "They draw back, and your hand falls."));
            }
            // penetration: the data names a probe tag, resolved to the
            // actor's part (tongue, penis) or a held toy — the target
            // must be an orifice receiving that tag
            WorldObject? instrument = null;
            if (Data(ctx, "probe") is { Length: > 0 } probe)
            {
                instrument = BodyParts.InstrumentOf(ctx.World, ctx.Modules, ctx.Agent, probe);
                if (instrument is null)
                    return ActionResult.Fail(Data(ctx, "onNoInstrument",
                        "You have nothing for that."));
                if (ctx.Target is null || !ctx.Target.HasModule("bodypart") ||
                    !BodyParts.Receives(ctx.Modules, ctx.Target).Contains(probe))
                    return ActionResult.Fail(Data(ctx, "onIncompatible",
                        "That isn't a way bodies fit."));
                // an instrument that wears a region must be out of it —
                // parts without regions (tongue, fingers) are never
                // blocked here; that presence IS the authoring knob
                if (instrument.HasModule("bodypart") &&
                    BodyParts.Region(ctx.Modules, instrument) is { Length: > 0 } region &&
                    Clothing.CoversRegion(ctx.World, ctx.Modules, ctx.Agent, region))
                    return ActionResult.Fail(Data(ctx, "onCoveredInstrument",
                        "Your own clothes are in the way of that."));
            }
            // the inverted family (targetsProbes): the target must be a
            // part acting as one of those probes — the actor's own
            // orifice is implied by the verb (a mouth, for sucking)
            if (Data(ctx, "targetsProbes") is { Length: > 0 } targets)
            {
                var allowed = targets.Split(',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (ctx.Target is null || !ctx.Target.HasModule("bodypart") ||
                    !allowed.Contains(ctx.Modules.ResolveString(ctx.Target, "bodypart", "probe")))
                    return ActionResult.Fail(Data(ctx, "onIncompatible",
                        "That isn't a way bodies fit."));
            }
            var scale = effect == "hesitate" ? DataDouble(ctx, "hesitantScale", 0.5) : 1.0;
            // the part's sensitivity scales the listed motives (erogenous zones)
            var sensitivity = 1.0;
            if (ctx.Target is not null && ctx.Target.HasModule("bodypart"))
                sensitivity = ctx.Modules.ResolveDouble(ctx.Target, "bodypart", "sensitivity", 1.0);
            var scaled = Data(ctx, "sensitivityScales")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal);
            double SensitivityOf(string motive) => scaled.Contains(motive) ? sensitivity : 1.0;
            Dictionary<string, double> Scaled(string prefix, double factor) =>
                Impulses(ctx, prefix).ToDictionary(
                    kv => kv.Key, kv => kv.Value * factor * SensitivityOf(kv.Key));
            List<string> giverFired = [];
            // an ongoing embrace sets the pace and may rephrase
            var pace = 1.0;
            var position = "";
            if (receiver is not null &&
                Embraces.Find(ctx.World, ctx.Modules, ctx.Agent, receiver) is { } embrace)
            {
                position = Embraces.Position(ctx.Modules, embrace);
                pace = DataDouble(ctx, "pace." + position, 1.0);
            }
            var message = Render(Data(ctx, "self" + (position.Length > 0 ? "." + position : ""),
                "You {verb} {target}."), ctx, instrument);
            // the orifice an in-flight touch engages, for emission
            // placement: a probe action holds the actor's own instrument
            // in the targeted part; an inverted action (sucking) holds
            // the receiver's probe part in the actor's orifice — the
            // usesParts part when it names one, else the actor's first
            // part receiving the probe
            WorldObject? EngagedOrificeFor(WorldObject recipient)
            {
                if (instrument is not null && ReferenceEquals(recipient, ctx.Agent) &&
                    ctx.Target is not null && ctx.Target.HasModule("bodypart"))
                    return ctx.Target;
                if (instrument is null && Data(ctx, "targetsProbes") is { Length: > 0 } targets &&
                    ReferenceEquals(recipient, receiver) &&
                    ctx.Target is not null && ctx.Target.HasModule("bodypart"))
                {
                    if (Data(ctx, "usesParts") is { Length: > 0 } uses)
                        foreach (var name in uses.Split(',',
                                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            foreach (var part in BodyParts.Of(ctx.World, ctx.Agent))
                                if (string.Equals(part.Name, name, StringComparison.OrdinalIgnoreCase))
                                    return part;
                    foreach (var part in BodyParts.Of(ctx.World, ctx.Agent))
                        if (BodyParts.Receives(ctx.Modules, part).Contains(targets.Trim()))
                            return part;
                }
                return null;
            }
            var receiverFired = new List<string>();
            if (receiver is not null)
                receiverFired = Motives.Impulse(ctx.World, ctx.Modules, ctx.Signals, receiver,
                    Scaled("impulse.", scale * pace), EngagedOrificeFor(receiver));
            // the giver feels it a little too
            giverFired = Motives.Impulse(ctx.World, ctx.Modules, ctx.Signals, ctx.Agent,
                Scaled("giver.impulse.", scale * pace), EngagedOrificeFor(ctx.Agent));
            if (effect == "welcome")
                Motives.Impulse(ctx.World, ctx.Modules, ctx.Signals, receiver ?? ctx.Agent,
                    Impulses(ctx, "melt.impulse."));
            if (receiverFired.Count > 0)
                message += " " + Data(ctx, "onEvent",
                    "A tremor runs through them, sudden and complete.");
            if (giverFired.Count > 0 && Data(ctx, "onSelfEvent") is { Length: > 0 } own)
                message += " " + own;
            return ActionResult.Ok(message);
        }
    }

    private sealed class EmbraceHandler : IActionHandler
    {
        public string Id => "embrace";

        public ActionResult Execute(ActionContext ctx)
        {
            var partner = ctx.Target ?? throw new InvalidOperationException("embrace requires a target.");
            if ((ctx.Reaction?.Effect ?? "welcome") == "refuse")
                return ActionResult.Fail(Data(ctx, "onStop",
                    "They catch your face in both hands. \"Not yet,\" they say softly. \"Just — hold me a while?\""));
            if (ctx.World.RoomOf(ctx.Agent.Id).Id != ctx.World.RoomOf(partner.Id).Id)
                return ActionResult.Fail(Data(ctx, "onApart",
                    "You reach for them, but they are not close enough — the space between you is the whole problem."));
            var templateId = Data(ctx, "template");
            if (templateId.Length == 0 || !ctx.World.HasObject(templateId))
                return ActionResult.Fail(Data(ctx, "onImpossible",
                    "That isn't possible here."));
            var template = ctx.World.GetObject(templateId);
            // one embrace per agent — a second pairing must wait
            if (Embraces.Of(ctx.World, ctx.Modules, ctx.Agent) is not null ||
                Embraces.Of(ctx.World, ctx.Modules, partner) is not null)
                return ActionResult.Fail(Data(ctx, "onBusy",
                    "You are already holding someone close."));
            // the template may require a wear region uncovered on both sides
            if (ctx.Modules.ResolveString(template, "embrace", "exposeRequired") is
                    { Length: > 0 } region)
                foreach (var who in new[] { ctx.Agent, partner })
                    if (Clothing.CoversRegion(ctx.World, ctx.Modules, who, region))
                        return ActionResult.Fail(Data(ctx,
                            who.Id == ctx.Agent.Id ? "onClothedSelf" : "onClothedOther",
                            "Your own clothes are in the way."));
            // lie back together if the support allows it
            var entryPosture = ctx.Modules.ResolveString(template, "embrace", "entryPosture");
            if (entryPosture is { Length: > 0 } &&
                ctx.Agent.Parent == partner.Parent && ctx.Agent.Parent.Length > 0 &&
                ctx.World.HasObject(ctx.Agent.Parent) &&
                ctx.World.GetObject(ctx.Agent.Parent).HasModule("lyable"))
                foreach (var who in new[] { ctx.Agent, partner })
                    ctx.World.SetFieldOverride(who.Id, "agent", "posture",
                        World.World.ToJson(entryPosture));
            var embrace = Embraces.Enter(ctx.World, ctx.Modules, templateId,
                ctx.Agent, partner, Data(ctx, "position"));
            // a hesitant welcome halves the warmth, as it does for touch
            var scale = (ctx.Reaction?.Effect ?? "welcome") == "hesitate"
                ? DataDouble(ctx, "hesitantScale", 0.5)
                : 1.0;
            var impulses = Impulses(ctx, "impulse.")
                .ToDictionary(kv => kv.Key, kv => kv.Value * scale);
            foreach (var who in new[] { ctx.Agent, partner })
                Motives.Impulse(ctx.World, ctx.Modules, ctx.Signals, who, impulses);
            // fleeting kinds (a hug) complete with the action itself
            if (Embraces.AutoEnd(ctx.Modules, embrace))
                Embraces.Dissolve(ctx.World, embrace);
            return ActionResult.Ok(Data(ctx, "self",
                "You draw them close, and closer, until there is no space left to close."));
        }
    }

    private sealed class RepositionHandler : IActionHandler
    {
        public string Id => "reposition";

        public ActionResult Execute(ActionContext ctx)
        {
            var partner = ctx.Target ?? throw new InvalidOperationException("reposition requires a target.");
            var embrace = Embraces.Find(ctx.World, ctx.Modules, ctx.Agent, partner);
            if (embrace is null)
                return ActionResult.Fail(Data(ctx, "onApart",
                    "You reach for them, but there is nothing between you to rearrange."));
            var position = Data(ctx, "position");
            var valid = Embraces.Positions(ctx.Modules, embrace);
            if (position.Length == 0 || (valid.Count > 0 && !valid.Contains(position)))
                return ActionResult.Fail(Data(ctx, "onImpossible",
                    "That isn't a comfortable way to be."));
            if (Embraces.Position(ctx.Modules, embrace) == position)
                return ActionResult.Noop(Data(ctx, "onSame",
                    "You are already arranged so."));
            Embraces.SetPosition(ctx.World, embrace, position);
            Motives.Impulse(ctx.World, ctx.Modules, ctx.Signals, partner,
                Impulses(ctx, "impulse."));
            return ActionResult.Ok(Data(ctx, "self",
                "You find each other again, in a new arrangement."));
        }
    }

    private sealed class DisengageHandler : IActionHandler
    {
        public string Id => "disengage";

        public ActionResult Execute(ActionContext ctx)
        {
            var partner = ctx.Target ?? throw new InvalidOperationException("disengage requires a target.");
            var embrace = Embraces.Find(ctx.World, ctx.Modules, ctx.Agent, partner);
            if (embrace is null)
                return ActionResult.Noop(Data(ctx, "self",
                    "You draw apart."));
            Embraces.Dissolve(ctx.World, embrace);
            Motives.Impulse(ctx.World, ctx.Modules, ctx.Signals, partner,
                Impulses(ctx, "impulse."));
            // the "left wanting" branch: the partner lacks the marker
            // condition while the named field still runs hot
            var unfinishedKind = Data(ctx, "unfinishedKind");
            var satisfied = unfinishedKind.Length > 0 &&
                Conditions.Has(ctx.World, ctx.Modules, partner, unfinishedKind);
            var restless = Data(ctx, "unfinishedField") is { Length: > 0 } field &&
                ctx.Modules.ResolveDouble(partner, Data(ctx, "unfinishedModule"), field) >=
                DataDouble(ctx, "unfinishedMin", 0.5);
            if (!satisfied && restless)
            {
                Motives.Impulse(ctx.World, ctx.Modules, ctx.Signals, partner,
                    Impulses(ctx, "unfinished.impulse."));
                return ActionResult.Ok(Data(ctx, "self:unfinished",
                    "You ease apart; they keep you close a moment longer, trembling a little, not finished."));
            }
            return ActionResult.Ok(Data(ctx, "self:after", Data(ctx, "self",
                "You ease apart and rest, breathing slowing, nobody in any hurry to move.")));
        }
    }
}
