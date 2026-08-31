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
        new PutHandler(),
        new GiveHandler(),
        new UnlockHandler(),
        new LockHandler(),
        new PickLockHandler(),
        new InventoryHandler(),
        new SayHandler(),
        new WaitHandler(),
        new SitHandler(),
        new LieHandler(),
        new StandHandler(),
        new WearHandler(),
        new RemoveHandler(),
        new ShoveHandler(),
        new StealHandler(),
        new AttackHandler(),
        new GrappleHandler(),
        new ReleaseHandler(),
        new EscapeHandler(),
        new ChokeHandler(),
        new ExamineHandler(),
        new TradeHandler(),
        new RitualHandler(),
    ];

    /// <summary>A module's string field, or null when the module is absent or the field is empty.</summary>
    private static string? Field(ActionContext ctx, WorldObject obj, string module, string field)
    {
        if (!obj.HasModule(module))
            return null;
        var value = ctx.Modules.ResolveString(obj, module, field);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // flavor verbs that don't change the world ("Touch the red flower") —
    // the message interpolates the affordance's verb
    private sealed class BasicHandler : IActionHandler
    {
        public string Id => "basic";

        public ActionResult Execute(ActionContext ctx)
        {
            var target = ctx.Target ?? throw new InvalidOperationException("basic requires a target.");
            var verb = ctx.Verb ?? "touch";
            return ActionResult.Ok($"You {verb} {Perception.WithDefiniteArticle(target.Name)}.");
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
            WorldObject? holder = null;
            if (target.Parent == room.Id)
            {
                // directly in the room — fine
            }
            else if (target.Parent.Length > 0 && ctx.World.HasObject(target.Parent))
            {
                // on furniture the target is reachable; inside a container
                // in the room, the container must be open
                holder = ctx.World.GetObject(target.Parent);
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
            // name the holder the item came out of ("from the cupboard")
            var from = holder is null || holder.HasModule("agent")
                ? ""
                : $" from the {holder.Name}";
            return ActionResult.Ok($"You take the {target.Name}{from}.");
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

    // put: stow a held item in a container (the target), respecting its
    // open state and capacity; the item is the action's aux target
    private sealed class PutHandler : IActionHandler
    {
        public string Id => "put";

        public ActionResult Execute(ActionContext ctx)
        {
            var container = ctx.Target ?? throw new InvalidOperationException("put requires a target container.");
            if (!container.HasModule("container"))
                return ActionResult.Fail($"You can't put anything into the {container.Name}.");
            var item = ctx.AuxTarget ?? throw new InvalidOperationException("put requires an item (aux target).");
            if (item.Id == container.Id)
                return ActionResult.Fail($"You can't put the {item.Name} into itself.");
            if (item.Parent != ctx.Agent.Id)
                return ActionResult.Noop($"You're not carrying the {item.Name}.");
            if (Clothing.IsWorn(ctx.Modules, item))
                return ActionResult.Fail($"Take off the {item.Name} first.");
            if (HandlerState.GetOpenState(ctx, container) is not null && !HandlerState.IsOpen(ctx, container))
                return ActionResult.Fail($"The {container.Name} is closed.");
            var capacity = ctx.Modules.ResolveInt(container, "container", "capacity", 10);
            if (ctx.World.ChildrenOf(container.Id).Count() >= capacity)
                return ActionResult.Fail($"The {container.Name} is full.");
            ctx.World.MoveObject(item.Id, container.Id);
            return ActionResult.Ok(
                $"You put {Perception.WithDefiniteArticle(item.Name)} into {Perception.WithDefiniteArticle(container.Name)}.");
        }
    }

    // give: offer a held item (the aux target) to another agent (the
    // target). The recipient's reaction gates the hand-off — any choice
    // that isn't noResist declines it; an incapacitated recipient can't
    // react, so the hand-off just happens (you set it on them)
    private sealed class GiveHandler : IActionHandler
    {
        public string Id => "give";

        public ActionResult Execute(ActionContext ctx)
        {
            var recipient = ctx.Target ?? throw new InvalidOperationException("give requires a target agent.");
            if (!recipient.HasModule("agent"))
                return ActionResult.Fail($"The {recipient.Name} can't take that.");
            var item = ctx.AuxTarget ?? throw new InvalidOperationException("give requires an item (aux target).");
            if (item.Parent != ctx.Agent.Id)
                return ActionResult.Noop($"You're not carrying the {item.Name}.");
            if (ctx.Reaction is { NoResist: false })
                return ActionResult.Fail(Capitalize(
                    $"{recipient.Name} declines {Perception.WithDefiniteArticle(item.Name)}."));
            ctx.World.MoveObject(item.Id, recipient.Id);
            return ActionResult.Ok(
                $"You give {Perception.WithDefiniteArticle(item.Name)} to {recipient.Name}.");
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

    // lockpicking: unlock without the key. The skill check gates this in
    // PerformAction (affordance check spec); the handler just does the deed.
    private sealed class PickLockHandler : IActionHandler
    {
        public string Id => "pick";

        public ActionResult Execute(ActionContext ctx)
        {
            var target = ctx.Target ?? throw new InvalidOperationException("pick requires a target.");
            if (!target.HasModule("lockable"))
                return ActionResult.Fail($"The {target.Name} has no lock to pick.");
            if (!HandlerState.IsLocked(ctx, target))
                return ActionResult.Noop($"The {target.Name} isn't locked.");
            HandlerState.SetLocked(ctx, target, false);
            return ActionResult.Ok($"You pick the lock on the {target.Name}.");
        }
    }

    private sealed class InventoryHandler : IActionHandler
    {
        public string Id => "inventory";

        public ActionResult Execute(ActionContext ctx)
        {
            var worn = new List<string>();
            var carried = new List<string>();
            var borne = new List<string>();
            foreach (var item in ctx.World.ChildrenOf(ctx.Agent.Id))
            {
                if (item.HasModule("bodypart"))
                    continue; // anatomy, not belongings
                if (Clothing.IsWorn(ctx.Modules, item))
                    worn.Add(item.Name);
                else if (item.HasModule("portable"))
                    carried.Add(item.Name);
                else
                    borne.Add(item.Name); // inalienable: a brand, a curse
            }

            var parts = new List<string>();
            if (worn.Count > 0)
                parts.Add("You are wearing: " + string.Join(", ", worn.Select(Perception.WithArticle)));
            parts.Add(carried.Count == 0
                ? "You are carrying nothing."
                : "You are carrying: " + string.Join(", ", carried.Select(Perception.WithArticle)));
            if (borne.Count > 0)
                parts.Add("You bear: " + string.Join(", ", borne.Select(Perception.WithArticle)));
            parts.AddRange(Condition.SelfLines(ctx.World, ctx.Modules, ctx.Agent));
            return ActionResult.Ok(string.Join("\n", parts));
        }
    }

    private sealed class SayHandler : IActionHandler
    {
        // speaking takes time proportional to the words: a base cost plus a
        // per-character factor, tunable via the scenario's rules module
        // (sayBaseSeconds / sayMillisPerChar; defaults 2s + 100ms/char, so a
        // 60-char sentence takes about 8s — listeners get time to respond)
        public const int DefaultBaseSeconds = 2;
        public const int DefaultMillisPerChar = 100;

        public string Id => "say";

        public ActionResult Execute(ActionContext ctx)
        {
            var text = ctx.Args.TryGetValue("text", out var t) ? t : "";
            var rulesHost = Checks.RulesHost(ctx.World);
            var baseSeconds = rulesHost is null
                ? DefaultBaseSeconds
                : ctx.Modules.ResolveInt(rulesHost, "rules", "sayBaseSeconds", DefaultBaseSeconds);
            var millisPerChar = rulesHost is null
                ? DefaultMillisPerChar
                : ctx.Modules.ResolveInt(rulesHost, "rules", "sayMillisPerChar", DefaultMillisPerChar);
            var duration = baseSeconds + (int)(text.Length * millisPerChar / 1000.0);
            return ActionResult.Ok($"You say: \"{text}\"", duration);
        }
    }

    // waiting just passes the turn; quiet by default (no signal specs)
    private sealed class WaitHandler : IActionHandler
    {
        public string Id => "wait";

        public ActionResult Execute(ActionContext ctx) => ActionResult.Ok("You wait.");
    }

    // attack: the opposed roll lives here (not in the affordance's check
    // spec) because the attacker's bonus depends on the wielded weapon.
    // A wielded weapon is a worn item with the weapon module; without one
    // the attacker's combatant module supplies unarmed defaults. Armor is
    // the sum of armor.protection over the defender's worn garments —
    // region-scoped when the defender has body parts (only garments
    // covering the hit part's region soak). A part-ful defender takes the
    // blow on one part: aimed via the optional free-text argument (an
    // unknown part fails; rules.aimedPenalty applies) or a uniform random
    // part. Damage and blow wording follow the rules crunch level.
    private sealed class AttackHandler : IActionHandler
    {
        public string Id => "attack";

        public ActionResult Execute(ActionContext ctx)
        {
            var target = ctx.Target ?? throw new InvalidOperationException("attack requires a target.");
            if (!target.HasModule("attackable"))
                return ActionResult.Fail($"There's no point attacking {Perception.WithDefiniteArticle(target.Name)}.");
            var random = ctx.Random ?? new Random();
            var targetName = Perception.WithDefiniteArticle(target.Name);

            // the wielded weapon (a worn weapon-module item), if any
            var weapon = Clothing.WornItems(ctx.World, ctx.Modules, ctx.Agent)
                .FirstOrDefault(w => w.HasModule("weapon"));
            var combatant = ctx.Agent.HasModule("combatant");

            var attackStat = (weapon is not null ? Field(ctx, weapon, "weapon", "stat") : null)
                ?? (combatant ? Field(ctx, ctx.Agent, "combatant", "attackStat") : null)
                ?? "strength";
            var attackSkill = (weapon is not null ? Field(ctx, weapon, "weapon", "skill") : null)
                ?? (combatant ? Field(ctx, ctx.Agent, "combatant", "attackSkill") : null)
                ?? "brawling";

            // the defender's guard: their combatant defense stat/skill
            var defStat = Field(ctx, target, "combatant", "defenseStat") ?? "agility";
            var defSkill = Field(ctx, target, "combatant", "defenseSkill");

            var spec = new Modules.CheckSpec
            {
                Stat = attackStat,
                Skill = attackSkill,
                Opposed = new Modules.OpposedSpec { Stat = defStat, Skill = defSkill },
            };
            // non-agent targets (a training dummy) don't defend themselves
            var margin = target.HasModule("agent")
                ? Checks.EvaluateOpposed(ctx.World, ctx.Modules, random, ctx.Agent, spec, target,
                    ctx.Reaction)
                : 1;

            // body parts: the blow lands on one part — aimed (free-text
            // argument, with the data-driven penalty) or random
            var parts = BodyParts.Of(ctx.World, target);
            WorldObject? part = null;
            if (parts.Count > 0)
            {
                if (ctx.Args.TryGetValue("text", out var aimed) && aimed.Length > 0)
                {
                    part = BodyParts.FindByName(ctx.World, target, aimed, random);
                    if (part is null)
                        return ActionResult.Fail($"{Capitalize(targetName)} has no such part.");
                    margin -= BodyParts.AimedPenalty(ctx.Modules, part);
                }
                else
                {
                    part = parts[random.Next(parts.Count)];
                }
                if (margin < 0)
                    return ActionResult.Fail($"You swing at {targetName}'s {part.Name} and miss.");
            }
            else if (margin < 0)
                return ActionResult.Fail($"You swing at {targetName} and miss.");

            var damageBonus = weapon is not null
                ? ctx.Modules.ResolveInt(weapon, "weapon", "damageBonus")
                : combatant ? ctx.Modules.ResolveInt(ctx.Agent, "combatant", "damageBonus") : 0;
            var damageDice = weapon is not null
                ? ctx.Modules.ResolveInt(weapon, "weapon", "damageDice", 1)
                : combatant ? ctx.Modules.ResolveInt(ctx.Agent, "combatant", "damageDice", 1) : 1;
            var damageSides = weapon is not null
                ? ctx.Modules.ResolveInt(weapon, "weapon", "damageSides", 4)
                : combatant ? ctx.Modules.ResolveInt(ctx.Agent, "combatant", "damageSides", 2) : 2;
            var armor = Clothing.WornItems(ctx.World, ctx.Modules, target)
                .Where(w => w.HasModule("armor"))
                .Where(w => part is null || Clothing.GarmentRegions(ctx.Modules, w)
                    .Contains(BodyParts.Region(ctx.Modules, part)))
                .Sum(w => ctx.Modules.ResolveInt(w, "armor", "protection"));

            var damage = Math.Max(
                damageBonus + Checks.RollDice(random, damageDice, damageSides) - armor, 0);
            var weaponSuffix = weapon is not null ? $" with the {weapon.Name}" : "";
            string message;
            string? fragment;
            if (part is not null)
            {
                fragment = Damage.ApplyToPart(ctx.World, ctx.Modules, part, damage);
                message = Condition.Descriptive(ctx.World, ctx.Modules)
                    ? $"You land {Perception.WithArticle(Condition.BlowCategory(ctx.World, ctx.Modules, part, damage))} " +
                      $"blow on {targetName}'s {part.Name}{weaponSuffix}."
                    : $"You hit {targetName} in the {part.Name}{weaponSuffix} for {damage} damage.";
            }
            else
            {
                fragment = Damage.Apply(ctx.World, ctx.Modules, target, damage);
                message = Condition.Descriptive(ctx.World, ctx.Modules) && target.HasModule("health")
                    ? $"You land {Perception.WithArticle(Condition.BlowCategory(ctx.World, ctx.Modules, target, damage))} " +
                      $"blow on {targetName}{weaponSuffix}."
                    : $"You hit {targetName}{weaponSuffix} for {damage} damage.";
            }
            if (fragment is not null)
                message += " " + fragment;
            return ActionResult.Ok(message);
        }
    }

    // examine: per-object detail. Universal (the resolver offers it for
    // every visible object); agents show what they're wearing and carrying,
    // open containers their contents, openables/portal sides their state.
    // Lock state is never observable.
    private sealed class ExamineHandler : IActionHandler
    {
        public string Id => "examine";

        public ActionResult Execute(ActionContext ctx)
        {
            var target = ctx.Target ?? throw new InvalidOperationException("examine requires a target.");
            var sb = new StringBuilder();
            sb.AppendLine(target.Name);
            if (target.Description.Length > 0)
                sb.AppendLine(target.Description);

            if (target.HasModule("agent"))
            {
                if (Health.IsIncapacitated(ctx.World, ctx.Modules, target))
                    sb.AppendLine($"{target.Name} is incapacitated.");
                foreach (var line in Condition.ExamineLines(ctx.World, ctx.Modules, target))
                    sb.AppendLine(line);
                var posture = Postures.Of(ctx.World, ctx.Modules, target);
                if (posture == Postures.Prone)
                    sb.AppendLine($"{target.Name} is prone on the ground.");
                else if (posture == Postures.Carried)
                    sb.AppendLine($"{target.Name} is being carried by {ctx.World.GetObject(target.Parent).Name}.");
                else if (posture != Postures.Standing)
                    sb.AppendLine($"{target.Name} is {posture} on {Perception.WithDefiniteArticle(ctx.World.GetObject(target.Parent).Name)}.");
                var worn = Clothing.WornItems(ctx.World, ctx.Modules, target);
                if (worn.Count > 0)
                    sb.AppendLine($"Wearing: {string.Join(", ", worn.Select(w => Perception.WithArticle(w.Name)))}.");
                var carried = ctx.World.ChildrenOf(target.Id)
                    .Where(c => !c.HasModule("bodypart") && !Clothing.IsWorn(ctx.Modules, c)).ToList();
                if (carried.Count > 0)
                    sb.AppendLine($"Carrying: {string.Join(", ", carried.Select(c => Perception.WithArticle(c.Name)))}.");
            }
            else
            {
                if (HandlerState.GetOpenState(ctx, target) is not null)
                    sb.AppendLine(HandlerState.IsOpen(ctx, target) ? "It is open." : "It is closed.");
                if (target.HasModule("container") && HandlerState.IsOpen(ctx, target))
                    sb.AppendLine(Perception.ContentsSentence(ctx.World, target));
            }
            return ActionResult.Ok(sb.ToString().TrimEnd());
        }
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
            // a crippled no_stand part (a ruined leg) can't bear weight
            var crippled = BodyParts.Of(ctx.World, ctx.Agent).FirstOrDefault(p =>
                BodyParts.CrippleEffects(ctx.Modules, p).Contains("no_stand") &&
                BodyParts.IsCrippled(ctx.Modules, p));
            if (crippled is not null)
                return ActionResult.Fail($"You can't stand — your {crippled.Name} is crippled.");
            if (posture == Postures.Prone)
            {
                // knocked down in the room: no furniture to climb off
                ctx.World.SetFieldOverride(
                    ctx.Agent.Id, "agent", "posture", World.World.ToJson(Postures.Standing));
                return ActionResult.Ok("You get up.");
            }
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
                        $"You're already wearing the {worn.Name} there.");
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

            // pulling a garment off another agent is an opposed check, rolled
            // here (like attack) so self-removal stays check-free; the stats
            // come from the combatant modules (strength/brawling vs agility)
            if (target.Parent != ctx.Agent.Id && ctx.World.HasObject(target.Parent) &&
                ctx.World.GetObject(target.Parent) is { } wearer && wearer.HasModule("agent"))
            {
                var random = ctx.Random ?? new Random();
                var spec = new Modules.CheckSpec
                {
                    Stat = Field(ctx, ctx.Agent, "combatant", "attackStat") ?? "strength",
                    Skill = Field(ctx, ctx.Agent, "combatant", "attackSkill") ?? "brawling",
                    Opposed = new Modules.OpposedSpec
                    {
                        Stat = Field(ctx, wearer, "combatant", "defenseStat") ?? "agility",
                        Skill = Field(ctx, wearer, "combatant", "defenseSkill"),
                    },
                };
                if (Checks.EvaluateOpposed(ctx.World, ctx.Modules, random, ctx.Agent, spec, wearer,
                        ctx.Reaction) < 0)
                    return ActionResult.Fail(
                        $"You grab at the {target.Name}, but {wearer.Name} keeps it on.");
                ctx.World.SetFieldOverride(
                    target.Id, "wearable", "worn", World.World.ToJson(false));
                ctx.World.MoveObject(target.Id, ctx.Agent.Id);
                return ActionResult.Ok($"You pull the {target.Name} off {wearer.Name}.");
            }

            ctx.World.SetFieldOverride(
                target.Id, "wearable", "worn", World.World.ToJson(false));
            return ActionResult.Ok($"You take off the {target.Name}.");
        }
    }

    // shove: the opposed check gates this in PerformAction; the handler
    // knocks the victim prone
    private sealed class ShoveHandler : IActionHandler
    {
        public string Id => "shove";

        public ActionResult Execute(ActionContext ctx)
        {
            var target = ctx.Target ?? throw new InvalidOperationException("shove requires a target.");
            if (!target.HasModule("agent"))
                return ActionResult.Fail($"You can't shove the {target.Name}.");
            if (Postures.Of(ctx.World, ctx.Modules, target) == Postures.Prone)
                return ActionResult.Noop("They're already prone.");
            ctx.World.SetFieldOverride(
                target.Id, "agent", "posture", World.World.ToJson(Postures.Prone));
            return ActionResult.Ok($"You shove {Perception.WithDefiniteArticle(target.Name)} to the ground.");
        }
    }

    // steal: the opposed check (against the item's holder) gates this in
    // PerformAction; the handler moves the item into the thief's inventory
    private sealed class StealHandler : IActionHandler
    {
        public string Id => "steal";

        public ActionResult Execute(ActionContext ctx)
        {
            var target = ctx.Target ?? throw new InvalidOperationException("steal requires a target.");
            if (target.Parent.Length == 0 || !ctx.World.HasObject(target.Parent) ||
                !ctx.World.GetObject(target.Parent).HasModule("agent") ||
                target.Parent == ctx.Agent.Id)
                return ActionResult.Fail($"The {target.Name} isn't in anyone's pockets.");
            if (Clothing.IsWorn(ctx.Modules, target))
                return ActionResult.Fail($"You can't slip off the worn {target.Name}.");
            var holder = ctx.World.GetObject(target.Parent);
            ctx.World.MoveObject(target.Id, ctx.Agent.Id);
            return ActionResult.Ok($"You steal the {target.Name} from {holder.Name}.");
        }
    }

    // grappling (RPG stage 5): grapple hauls the victim into forced
    // carrying — the carried-posture restrictions do the rest. The opposed
    // check gates in PerformAction. release sets a grappled victim down;
    // escape is the victim's opposed break-out, rolled here (like attack)
    // because the check's defender is the carrier, not the self-target;
    // choke is a no-roll unarmed attack on a victim you're holding and
    // ignores armor.
    private sealed class GrappleHandler : IActionHandler
    {
        public string Id => "grapple";

        public ActionResult Execute(ActionContext ctx)
        {
            var target = ctx.Target ?? throw new InvalidOperationException("grapple requires a target.");
            if (!target.HasModule("agent"))
                return ActionResult.Fail($"You can't grapple the {target.Name}.");
            if (target.Parent == ctx.Agent.Id)
                return ActionResult.Noop(
                    $"You're already grappling {Perception.WithDefiniteArticle(target.Name)}.");
            ctx.World.MoveObject(target.Id, ctx.Agent.Id);
            // hauled upright — whatever they were on, they're in your grasp
            ctx.World.SetFieldOverride(
                target.Id, "agent", "posture", World.World.ToJson(Postures.Standing));
            return ActionResult.Ok($"You seize {Perception.WithDefiniteArticle(target.Name)}.");
        }
    }

    private sealed class ReleaseHandler : IActionHandler
    {
        public string Id => "release";

        public ActionResult Execute(ActionContext ctx)
        {
            var target = ctx.Target ?? throw new InvalidOperationException("release requires a target.");
            if (target.Parent != ctx.Agent.Id)
                return ActionResult.Fail($"You aren't holding {Perception.WithDefiniteArticle(target.Name)}.");
            ctx.World.MoveObject(target.Id, HandlerState.RoomOf(ctx).Id);
            return ActionResult.Ok($"You release {Perception.WithDefiniteArticle(target.Name)}.");
        }
    }

    private sealed class EscapeHandler : IActionHandler
    {
        public string Id => "escape";

        public ActionResult Execute(ActionContext ctx)
        {
            if (ctx.Agent.Parent.Length == 0 || !ctx.World.HasObject(ctx.Agent.Parent) ||
                ctx.World.GetObject(ctx.Agent.Parent) is not { } carrier || !carrier.HasModule("agent"))
                return ActionResult.Fail("No one is holding you.");
            var random = ctx.Random ?? new Random();
            var spec = new Modules.CheckSpec
            {
                Stat = Field(ctx, ctx.Agent, "combatant", "attackStat") ?? "strength",
                Skill = Field(ctx, ctx.Agent, "combatant", "attackSkill") ?? "brawling",
                Opposed = new Modules.OpposedSpec
                {
                    Stat = Field(ctx, carrier, "combatant", "defenseStat") ?? "agility",
                    Skill = Field(ctx, carrier, "combatant", "defenseSkill"),
                },
            };
            if (Checks.EvaluateOpposed(ctx.World, ctx.Modules, random, ctx.Agent, spec, carrier) < 0)
                return ActionResult.Fail($"You struggle against {carrier.Name}, but can't break free.");
            ctx.World.MoveObject(ctx.Agent.Id, HandlerState.RoomOf(ctx).Id);
            return ActionResult.Ok($"You break free of {carrier.Name}.");
        }
    }

    private sealed class ChokeHandler : IActionHandler
    {
        public string Id => "choke";

        public ActionResult Execute(ActionContext ctx)
        {
            var target = ctx.Target ?? throw new InvalidOperationException("choke requires a target.");
            if (target.Parent != ctx.Agent.Id)
                return ActionResult.Fail($"You aren't holding {Perception.WithDefiniteArticle(target.Name)}.");
            var random = ctx.Random ?? new Random();
            var combatant = ctx.Agent.HasModule("combatant");
            var damage = (combatant ? ctx.Modules.ResolveInt(ctx.Agent, "combatant", "damageBonus") : 0)
                         + Checks.RollDice(
                             random,
                             combatant ? ctx.Modules.ResolveInt(ctx.Agent, "combatant", "damageDice", 1) : 1,
                             combatant ? ctx.Modules.ResolveInt(ctx.Agent, "combatant", "damageSides", 2) : 2);
            // a no-roll unarmed attack, armor ignored; against a part-ful
            // victim the choke crushes the chokeable module's `part`
            // (fallback: a random part)
            var targetName = Perception.WithDefiniteArticle(target.Name);
            var parts = BodyParts.Of(ctx.World, target);
            if (parts.Count > 0)
            {
                var named = ctx.Modules.ResolveString(target, "chokeable", "part");
                var part = named is { Length: > 0 }
                    ? BodyParts.FindByName(ctx.World, target, named, random) ?? parts[random.Next(parts.Count)]
                    : parts[random.Next(parts.Count)];
                var message = Condition.Descriptive(ctx.World, ctx.Modules)
                    ? $"You choke {targetName}: {Perception.WithArticle(Condition.BlowCategory(ctx.World, ctx.Modules, part, damage))} " +
                      $"blow to their {part.Name}."
                    : $"You choke {targetName} for {damage} damage.";
                if (Damage.ApplyToPart(ctx.World, ctx.Modules, part, damage) is { } partFragment)
                    message += " " + partFragment;
                return ActionResult.Ok(message);
            }
            var monolithic = $"You choke {targetName} for {damage} damage.";
            if (Damage.Apply(ctx.World, ctx.Modules, target, damage) is { } fragment)
                monolithic += " " + fragment;
            return ActionResult.Ok(monolithic);
        }
    }

    // barter: the target is a ware another agent holds; the ware module's
    // `wants` field names the item id the trader wants in exchange — the
    // two items swap inventories. A missing offer is a real outcome, not
    // a silent failure: when the ware declares a `refusal` line the holder
    // speaks it aloud (audible to the room, remembered by the holder).
    private sealed class TradeHandler : IActionHandler
    {
        public string Id => "trade";

        public ActionResult Execute(ActionContext ctx)
        {
            var ware = ctx.Target ?? throw new InvalidOperationException("trade requires a target ware.");
            var holder = ware.Parent.Length > 0 && ctx.World.HasObject(ware.Parent)
                ? ctx.World.GetObject(ware.Parent) : null;
            if (holder is null || !holder.HasModule("agent"))
                return ActionResult.Fail($"There's nobody here to trade the {ware.Name} with.");
            if (holder.Id == ctx.Agent.Id)
                return ActionResult.Noop($"You're already carrying the {ware.Name}.");
            var wantsId = ctx.Modules.ResolveString(ware, "ware", "wants");
            if (wantsId is null || !ctx.World.HasObject(wantsId))
                return ActionResult.Fail($"{Capitalize(holder.Name)} isn't trading the {ware.Name}.");
            var wants = ctx.World.GetObject(wantsId);
            // the wanted item counts whether the actor still holds it or has
            // already handed it over (a gift ahead of the barter)
            if (wants.Parent != ctx.Agent.Id && wants.Parent != holder.Id)
            {
                // a data-driven refusal is real speech, not a narrator
                // aside: the holder says it aloud (the room hears it, the
                // actor included) and remembers saying it
                if (ctx.Modules.ResolveString(ware, "ware", "refusal") is { Length: > 0 } refusal)
                {
                    ctx.Memory.Record(holder, $"You say: \"{refusal}\"");
                    ctx.Signals.Emit(holder, null,
                        [new Signals.SignalSpec
                        {
                            Sense = Signals.SignalSense.Audible, Priority = 10,
                            Text = "{agent} says: \"{arg}\"",
                        }],
                        refusal);
                    return ActionResult.Fail(
                        $"You try to barter for {Perception.WithDefiniteArticle(ware.Name)}.");
                }
                return ActionResult.Fail(
                    $"{Capitalize(holder.Name)} wants {Perception.WithDefiniteArticle(wants.Name)} in exchange.");
            }
            ctx.World.MoveObject(wants.Id, holder.Id);
            ctx.World.MoveObject(ware.Id, ctx.Agent.Id);
            return ActionResult.Ok(
                $"You trade {Perception.WithDefiniteArticle(wants.Name)} for {Perception.WithDefiniteArticle(ware.Name)}.");
        }
    }

    // a requirements-gated rite or service (unbinding a curse, forging an
    // item): the `ritual` module lives on the rite's host (the sorcerer, the
    // altar) and lists required item ids (held by the host or the
    // supplicant), items to consume, modules to remove from the supplicant,
    // and an epilogue; `endsGame` ends the game with that epilogue. Two
    // directions share the handler: the supplicant asks (actor = supplicant,
    // target = host) or the host performs (a targetOthers affordance —
    // actor = host, target = supplicant).
    private sealed class RitualHandler : IActionHandler
    {
        public string Id => "ritual";

        public ActionResult Execute(ActionContext ctx)
        {
            // the host is whichever side carries the ritual module
            var host = ctx.Agent.HasModule("ritual") &&
                       (ctx.Target is null || !ctx.Target.HasModule("ritual"))
                ? ctx.Agent
                : ctx.Target ?? throw new InvalidOperationException("ritual requires a target.");
            var supplicant = ReferenceEquals(host, ctx.Agent) ? ctx.Target : ctx.Agent;
            if (supplicant is null)
                return ActionResult.Fail("There is nobody here to receive the rite.");
            bool IsAtHand(string id) => ctx.World.HasObject(id) &&
                (ctx.World.GetObject(id).Parent == host.Id ||
                 ctx.World.GetObject(id).Parent == supplicant.Id);
            var missing = (ctx.Modules.ResolveStringList(host, "ritual", "requiresItems") ?? [])
                .Where(id => !IsAtHand(id)).ToList();
            if (missing.Count > 0)
            {
                var names = missing.Select(id =>
                    ctx.World.HasObject(id) ? ctx.World.GetObject(id).Name : id);
                return ActionResult.Fail(
                    $"{Capitalize(host.Name)} shakes their head — the rite still needs: {string.Join(", ", names)}.");
            }
            foreach (var id in ctx.Modules.ResolveStringList(host, "ritual", "consumesItems") ?? [])
                if (IsAtHand(id))
                    ctx.World.DestroyObject(id);
            foreach (var module in ctx.Modules.ResolveStringList(host, "ritual", "removesModules") ?? [])
                if (supplicant.HasModule(module))
                    ctx.World.RemoveModule(supplicant.Id, module);
            var epilogue = Field(ctx, host, "ritual", "epilogue") ?? "It is done.";
            var result = ActionResult.Ok(epilogue);
            return ctx.Modules.ResolveBool(host, "ritual", "endsGame")
                ? result with { EndsGame = true }
                : result;
        }
    }
}
