using System.Text;
using AEngine.Core.World;

namespace AEngine.Core.Actions;

/// <summary>Built-in handlers: look, go, open, close, take, drop, unlock, lock, inventory, say, wait.</summary>
public static class BuiltinHandlers
{
    public static IEnumerable<IActionHandler> All() =>
    [
        new BasicHandler(),
        new LookHandler(),
        new GoHandler(),
        new OpenHandler(),
        new CloseHandler(),
        new TakeHandler(),
        new DropHandler(),
        new UnlockHandler(),
        new LockHandler(),
        new InventoryHandler(),
        new SayHandler(),
        new WaitHandler(),
        new SitHandler(),
        new LieHandler(),
        new StandHandler(),
        new WearHandler(),
        new RemoveHandler(),
    ];

    // flavor verbs that don't change the world ("Touch the red flower") —
    // the message interpolates the affordance's verb
    private sealed class BasicHandler : IActionHandler
    {
        public string Id => "basic";

        public ActionResult Execute(ActionContext ctx)
        {
            var target = ctx.Target ?? throw new InvalidOperationException("basic requires a target.");
            var verb = ctx.Verb ?? "touch";
            return ActionResult.Ok($"You {verb} the {target.Name}.");
        }
    }

    private sealed class LookHandler : IActionHandler
    {
        public string Id => "look";

        public ActionResult Execute(ActionContext ctx)
        {
            var room = HandlerState.RoomOf(ctx);
            var sb = new StringBuilder();
            sb.AppendLine(room.Name);
            if (room.Description.Length > 0)
                sb.AppendLine(room.Description);
            if (Perception.PostureLine(ctx.World, ctx.Modules, ctx.Agent) is { } posture)
                sb.AppendLine(posture);

            // openables report their state; open containers' contents list
            // as separate entries ("brass key (in desk drawer)")
            var items = Perception.DescribeRoomContents(ctx.World, ctx.Modules, room, ctx.Agent.Id);
            if (items.Count > 0)
                sb.AppendLine("You see: " + string.Join(", ", items));

            // dressed agents get a line each — the listing stays compact
            foreach (var line in Perception.DressedLines(ctx.World, ctx.Modules, room, ctx.Agent.Id))
                sb.AppendLine(line);

            var exits = ctx.World.ChildrenOf(room.Id).Where(c => c.HasModule("portal")).ToList();
            if (exits.Count > 0)
            {
                var parts = exits.Select(p =>
                {
                    var dir = ctx.Modules.ResolveString(p, "portal", "direction") ?? "somewhere";
                    // lock state is not observable: exits show open/closed only
                    var state = HandlerState.IsOpen(ctx, p) ? "open" : "closed";
                    return $"{dir} ({p.Name}, {state})";
                });
                sb.AppendLine("Exits: " + string.Join(", ", parts));
            }
            return ActionResult.Ok(sb.ToString().TrimEnd());
        }
    }

    private sealed class GoHandler : IActionHandler
    {
        public string Id => "go";

        public ActionResult Execute(ActionContext ctx)
        {
            var portal = ctx.Target ?? throw new InvalidOperationException("go requires a target portal.");
            if (!portal.HasModule("portal"))
                return ActionResult.Fail("You can't go that way.");
            if (!HandlerState.IsOpen(ctx, portal))
            {
                // Only a closed door blocks; an open door is passable even if locked.
                return ActionResult.Fail(HandlerState.IsLocked(ctx, portal)
                    ? $"The {portal.Name} is locked."
                    : $"The {portal.Name} is closed.");
            }

            var to = ctx.Modules.ResolveString(portal, "portal", "to");
            if (to is null || !ctx.World.HasObject(to))
                return ActionResult.Fail("That way leads nowhere.");
            ctx.World.MoveObject(ctx.Agent.Id, to);
            var room = ctx.World.GetObject(to);
            return ActionResult.Ok($"You go through the {portal.Name} into {room.Name}.");
        }
    }

    private sealed class OpenHandler : IActionHandler
    {
        public string Id => "open";

        public ActionResult Execute(ActionContext ctx)
        {
            var target = ctx.Target ?? throw new InvalidOperationException("open requires a target.");
            if (HandlerState.GetOpenState(ctx, target) is null)
                return ActionResult.Fail($"You can't open the {target.Name}.");
            if (HandlerState.IsLocked(ctx, target))
                return ActionResult.Fail($"The {target.Name} is locked.");
            if (HandlerState.IsOpen(ctx, target))
                return ActionResult.Noop($"The {target.Name} is already open.");
            HandlerState.SetOpen(ctx, target, true);
            var message = $"You open the {target.Name}.";
            // report what's inside a freshly opened container
            if (target.HasModule("container"))
                message += " " + Perception.ContentsSentence(ctx.World, target);
            return ActionResult.Ok(message);
        }
    }

    private sealed class CloseHandler : IActionHandler
    {
        public string Id => "close";

        public ActionResult Execute(ActionContext ctx)
        {
            var target = ctx.Target ?? throw new InvalidOperationException("close requires a target.");
            if (HandlerState.GetOpenState(ctx, target) is null)
                return ActionResult.Fail($"You can't close the {target.Name}.");
            if (!HandlerState.IsOpen(ctx, target))
                return ActionResult.Noop($"The {target.Name} is already closed.");
            HandlerState.SetOpen(ctx, target, false);
            return ActionResult.Ok($"You close the {target.Name}.");
        }
    }

    private sealed class TakeHandler : IActionHandler
    {
        public string Id => "take";

        public ActionResult Execute(ActionContext ctx)
        {
            var target = ctx.Target ?? throw new InvalidOperationException("take requires a target.");
            if (target.Id == ctx.Agent.Id)
                return ActionResult.Fail("You can't take yourself.");
            if (!target.HasModule("portable"))
                return ActionResult.Fail($"You can't take the {target.Name}.");
            if (HandlerState.IsHeld(ctx, target))
                return ActionResult.Noop($"You already have the {target.Name}.");

            var room = HandlerState.RoomOf(ctx);
            if (target.Parent == room.Id)
            {
                // directly in the room — fine
            }
            else if (target.Parent.Length > 0 && ctx.World.HasObject(target.Parent))
            {
                // on furniture the target is reachable; inside a container
                // in the room, the container must be open
                var holder = ctx.World.GetObject(target.Parent);
                if (holder.Parent != room.Id)
                    return ActionResult.Fail($"You don't see the {target.Name} here.");
                if (!holder.HasModule("sittable") && !holder.HasModule("lyable") &&
                    !HandlerState.IsOpen(ctx, holder))
                    return ActionResult.Fail($"The {target.Name} is inside the closed {holder.Name}.");
            }
            else
            {
                return ActionResult.Fail($"You don't see the {target.Name} here.");
            }

            ctx.World.MoveObject(target.Id, ctx.Agent.Id);
            if (target.HasModule("agent"))
                // a carried agent's posture is derived from containment;
                // clear any stored sit/lie so it can't go stale
                ctx.World.SetFieldOverride(
                    target.Id, "agent", "posture", World.World.ToJson(Postures.Standing));
            return ActionResult.Ok($"You take the {target.Name}.");
        }
    }

    private sealed class DropHandler : IActionHandler
    {
        public string Id => "drop";

        public ActionResult Execute(ActionContext ctx)
        {
            var target = ctx.Target ?? throw new InvalidOperationException("drop requires a target.");
            if (!HandlerState.IsHeld(ctx, target))
                return ActionResult.Noop($"You're not carrying the {target.Name}.");
            if (Clothing.IsWorn(ctx.Modules, target))
                return ActionResult.Fail($"Take off the {target.Name} first.");
            var room = HandlerState.RoomOf(ctx);
            ctx.World.MoveObject(target.Id, room.Id);
            if (target.HasModule("agent"))
                // a dropped agent lands on their feet
                ctx.World.SetFieldOverride(
                    target.Id, "agent", "posture", World.World.ToJson(Postures.Standing));
            return ActionResult.Ok($"You drop the {target.Name}.");
        }
    }

    private sealed class UnlockHandler : IActionHandler
    {
        public string Id => "unlock";

        public ActionResult Execute(ActionContext ctx) => SetLock(ctx, false);

        internal static ActionResult SetLock(ActionContext ctx, bool locked)
        {
            var target = ctx.Target ?? throw new InvalidOperationException("lock/unlock requires a target.");
            if (!target.HasModule("lockable"))
                return ActionResult.Fail($"The {target.Name} has no lock.");
            // end state already holds: a noop, no key needed
            if (HandlerState.IsLocked(ctx, target) == locked)
                return ActionResult.Noop($"The {target.Name} is already {(locked ? "locked" : "unlocked")}.");
            var keyRef = ctx.Modules.ResolveString(target, "lockable", "keyRef");
            if (keyRef is not null)
            {
                if (!ctx.World.HasObject(keyRef))
                    return ActionResult.Fail("The key for this lock is missing from the world.");
                if (!HandlerState.IsHeld(ctx, ctx.World.GetObject(keyRef)))
                    return ActionResult.Fail($"You need the {ctx.World.GetObject(keyRef).Name} to {(locked ? "lock" : "unlock")} the {target.Name}.");
            }
            HandlerState.SetLocked(ctx, target, locked);
            return ActionResult.Ok($"You {(locked ? "lock" : "unlock")} the {target.Name}.");
        }
    }

    private sealed class LockHandler : IActionHandler
    {
        public string Id => "lock";

        public ActionResult Execute(ActionContext ctx) => UnlockHandler.SetLock(ctx, true);
    }

    private sealed class InventoryHandler : IActionHandler
    {
        public string Id => "inventory";

        public ActionResult Execute(ActionContext ctx)
        {
            var worn = new List<string>();
            var carried = new List<string>();
            foreach (var item in ctx.World.ChildrenOf(ctx.Agent.Id))
                (Clothing.IsWorn(ctx.Modules, item) ? worn : carried).Add(item.Name);

            var parts = new List<string>();
            if (worn.Count > 0)
                parts.Add("You are wearing: " + string.Join(", ", worn.Select(Perception.WithArticle)));
            parts.Add(carried.Count == 0
                ? "You are carrying nothing."
                : "You are carrying: " + string.Join(", ", carried.Select(Perception.WithArticle)));
            return ActionResult.Ok(string.Join("\n", parts));
        }
    }

    private sealed class SayHandler : IActionHandler
    {
        // speaking takes time proportional to the words: base 1s plus a
        // per-character factor (a 60-char sentence takes about 4s)
        public const double SecondsPerChar = 0.05;

        public string Id => "say";

        public ActionResult Execute(ActionContext ctx)
        {
            var text = ctx.Args.TryGetValue("text", out var t) ? t : "";
            var duration = 1 + (int)(text.Length * SecondsPerChar);
            return ActionResult.Ok($"You say: \"{text}\"", duration);
        }
    }

    // waiting just passes the turn; quiet by default (no signal specs)
    private sealed class WaitHandler : IActionHandler
    {
        public string Id => "wait";

        public ActionResult Execute(ActionContext ctx) => ActionResult.Ok("You wait.");
    }

    // getting onto furniture: sit (sittable) and lie (lyable) share Enter;
    // the agent becomes a child of the support and its posture is recorded
    // on the agent module so a bed can offer both postures
    private sealed class SitHandler : IActionHandler
    {
        public string Id => "sit";

        public ActionResult Execute(ActionContext ctx) =>
            Enter(ctx, "sittable", "sit", Postures.Sitting);
    }

    private sealed class LieHandler : IActionHandler
    {
        public string Id => "lie";

        public ActionResult Execute(ActionContext ctx) =>
            Enter(ctx, "lyable", "lie", Postures.Lying);
    }

    private static ActionResult Enter(ActionContext ctx, string supportModule, string verb, string posture)
    {
        var target = ctx.Target ?? throw new InvalidOperationException($"{verb} requires a target.");
        if (!target.HasModule(supportModule))
            return ActionResult.Fail($"You can't {verb} on the {target.Name}.");
        if (ctx.Agent.Parent == target.Id &&
            Postures.Of(ctx.World, ctx.Modules, ctx.Agent) == posture)
            return ActionResult.Noop(posture == Postures.Lying
                ? $"You're already lying on the {target.Name}."
                : $"You're already sitting on the {target.Name}.");
        if (Postures.Of(ctx.World, ctx.Modules, ctx.Agent) != Postures.Standing)
            return ActionResult.Fail("You need to stand up first.");
        var capacity = ctx.Modules.ResolveInt(target, supportModule, "capacity", 1);
        var occupants = target.Children.Count(id => ctx.World.GetObject(id).HasModule("agent"));
        if (occupants >= capacity)
            return ActionResult.Fail($"There's no room on the {target.Name}.");
        ctx.World.MoveObject(ctx.Agent.Id, target.Id);
        ctx.World.SetFieldOverride(ctx.Agent.Id, "agent", "posture", World.World.ToJson(posture));
        return ActionResult.Ok(posture == Postures.Lying
            ? $"You lie down on the {target.Name}."
            : $"You sit down on the {target.Name}.");
    }

    private sealed class StandHandler : IActionHandler
    {
        public string Id => "stand";

        public ActionResult Execute(ActionContext ctx)
        {
            var posture = Postures.Of(ctx.World, ctx.Modules, ctx.Agent);
            if (posture == Postures.Standing)
                return ActionResult.Noop("You're already standing.");
            if (posture == Postures.Carried)
                return ActionResult.Fail("You can't get up while being carried.");
            var support = ctx.World.GetObject(ctx.Agent.Parent);
            var room = HandlerState.RoomOf(ctx);
            ctx.World.MoveObject(ctx.Agent.Id, room.Id);
            ctx.World.SetFieldOverride(
                ctx.Agent.Id, "agent", "posture", World.World.ToJson(Postures.Standing));
            return ActionResult.Ok($"You get up from the {support.Name}.");
        }
    }

    // clothing: wearing is containment (the garment is a child of the
    // agent) plus a "worn" flag; conflicts and fit are region-set data —
    // see Clothing
    private sealed class WearHandler : IActionHandler
    {
        public string Id => "wear";

        public ActionResult Execute(ActionContext ctx)
        {
            var target = ctx.Target ?? throw new InvalidOperationException("wear requires a target.");
            if (!target.HasModule("wearable"))
                return ActionResult.Fail($"You can't wear the {target.Name}.");
            if (Clothing.IsWorn(ctx.Modules, target))
                return ActionResult.Noop($"You're already wearing the {target.Name}.");
            if (Clothing.BodyRegions(ctx.Modules, ctx.Agent) is not { } bodyRegions)
                return ActionResult.Fail("You have nothing to wear that on.");
            var regions = Clothing.GarmentRegions(ctx.Modules, target);
            if (regions.Any(r => !bodyRegions.Contains(r)))
                return ActionResult.Fail($"The {target.Name} doesn't fit you.");
            if (!HandlerState.IsHeld(ctx, target))
                return ActionResult.Fail($"You need to pick up the {target.Name} first.");

            // one garment per region: conflict on any shared region
            foreach (var worn in Clothing.WornItems(ctx.World, ctx.Modules, ctx.Agent))
            {
                var overlap = Clothing.GarmentRegions(ctx.Modules, worn).Intersect(regions).ToList();
                if (overlap.Count > 0)
                    return ActionResult.Fail(
                        $"You're already wearing the {worn.Name} on your {overlap[0]}.");
            }

            ctx.World.SetFieldOverride(
                target.Id, "wearable", "worn", World.World.ToJson(true));
            return ActionResult.Ok($"You put on the {target.Name}.");
        }
    }

    private sealed class RemoveHandler : IActionHandler
    {
        public string Id => "remove";

        public ActionResult Execute(ActionContext ctx)
        {
            var target = ctx.Target ?? throw new InvalidOperationException("remove requires a target.");
            if (!Clothing.IsWorn(ctx.Modules, target))
                return ActionResult.Noop($"You're not wearing the {target.Name}.");
            ctx.World.SetFieldOverride(
                target.Id, "wearable", "worn", World.World.ToJson(false));
            return ActionResult.Ok($"You take off the {target.Name}.");
        }
    }
}
