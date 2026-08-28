using System.Text;
using AEngine.Core.World;

namespace AEngine.Core.Actions;

/// <summary>Built-in MVP handlers: look, go, open, close, take, drop, unlock, lock, inventory.</summary>
public static class BuiltinHandlers
{
    public static IEnumerable<IActionHandler> All() =>
    [
        new LookHandler(),
        new GoHandler(),
        new OpenHandler(),
        new CloseHandler(),
        new TakeHandler(),
        new DropHandler(),
        new UnlockHandler(),
        new LockHandler(),
        new InventoryHandler(),
    ];

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

            var here = ctx.World.ChildrenOf(room.Id)
                .Where(c => c.Id != ctx.Agent.Id && !c.HasModule("portal"))
                .ToList();
            if (here.Count > 0)
                sb.AppendLine("You see: " + string.Join(", ", here.Select(c => c.Name)));

            var exits = ctx.World.ChildrenOf(room.Id).Where(c => c.HasModule("portal")).ToList();
            if (exits.Count > 0)
            {
                var parts = exits.Select(p =>
                {
                    var dir = ctx.Modules.ResolveString(p, "portal", "direction") ?? "somewhere";
                    var state = HandlerState.IsOpen(ctx, p) ? "open"
                        : HandlerState.IsLocked(ctx, p) ? "locked" : "closed";
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
                return ActionResult.Fail($"The {target.Name} is already open.");
            HandlerState.SetOpen(ctx, target, true);
            return ActionResult.Ok($"You open the {target.Name}.");
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
                return ActionResult.Fail($"The {target.Name} is already closed.");
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
            if (!target.HasModule("portable"))
                return ActionResult.Fail($"You can't take the {target.Name}.");
            if (HandlerState.IsHeld(ctx, target))
                return ActionResult.Fail($"You already have the {target.Name}.");

            var room = HandlerState.RoomOf(ctx);
            if (target.Parent == room.Id)
            {
                // directly in the room — fine
            }
            else if (target.Parent.Length > 0 && ctx.World.HasObject(target.Parent))
            {
                // inside a container in the room: the container must be open
                var container = ctx.World.GetObject(target.Parent);
                if (container.Parent != room.Id)
                    return ActionResult.Fail($"You don't see the {target.Name} here.");
                if (!HandlerState.IsOpen(ctx, container))
                    return ActionResult.Fail($"The {target.Name} is inside the closed {container.Name}.");
            }
            else
            {
                return ActionResult.Fail($"You don't see the {target.Name} here.");
            }

            ctx.World.MoveObject(target.Id, ctx.Agent.Id);
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
                return ActionResult.Fail($"You're not carrying the {target.Name}.");
            var room = HandlerState.RoomOf(ctx);
            ctx.World.MoveObject(target.Id, room.Id);
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
            var keyRef = ctx.Modules.ResolveString(target, "lockable", "keyRef");
            if (keyRef is not null)
            {
                if (!ctx.World.HasObject(keyRef))
                    return ActionResult.Fail("The key for this lock is missing from the world.");
                if (!HandlerState.IsHeld(ctx, ctx.World.GetObject(keyRef)))
                    return ActionResult.Fail($"You need the {ctx.World.GetObject(keyRef).Name} to {(locked ? "lock" : "unlock")} the {target.Name}.");
            }
            if (HandlerState.IsLocked(ctx, target) == locked)
                return ActionResult.Fail($"The {target.Name} is already {(locked ? "locked" : "unlocked")}.");
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
            var items = ctx.World.ChildrenOf(ctx.Agent.Id).ToList();
            return ActionResult.Ok(items.Count == 0
                ? "You are carrying nothing."
                : "You are carrying: " + string.Join(", ", items.Select(i => i.Name)));
        }
    }
}
