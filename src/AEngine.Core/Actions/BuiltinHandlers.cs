using System.Text;
using System.Text.Json;
using AEngine.Core.Runtime;
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
        new ConsumeHandler(),
        new SpawnHandler(),
        new DestroyHandler(),
        new CleanHandler(),
        new WashHandler(),
        new LeaveHandler(),
        new DepartHandler(),
    ];

    /// <summary>A module's string field, or null when the module is absent or the field is empty.</summary>
    private static string? Field(ActionContext ctx, WorldObject obj, string module, string field)
    {
        if (!obj.HasModule(module))
            return null;
        var value = ctx.Modules.ResolveString(obj, module, field);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>The affordance Data payload key, or the fallback when unset/empty.</summary>
    private static string Data(ActionContext ctx, string key, string fallback = "") =>
        ctx.Data is not null && ctx.Data.TryGetValue(key, out var value) && value.Length > 0
            ? value
            : fallback;

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
            // an unlit dark room is pitch black: the name, the warning,
            // and nothing else — no contents, no exits, no reading. The
            // scenario overrides the line via the rules module's darkText
            if (!Light.IsLit(ctx.World, ctx.Modules, ctx.Agent))
            {
                var rules = Checks.RulesHost(ctx.World);
                var dark = rules is not null &&
                           ctx.Modules.ResolveString(rules, "rules", "darkText") is { Length: > 0 } text
                    ? text
                    : "It is pitch black.";
                sb.AppendLine(dark);
                return ActionResult.Ok(sb.ToString().TrimEnd());
            }
            if (room.Description.Length > 0)
                sb.AppendLine(room.Description);
            if (Perception.PostureLine(ctx.World, ctx.Modules, ctx.Agent) is { } posture)
                sb.AppendLine(posture);
            // felt status conditions ("You feel tipsy.") right after the
            // posture line — the agent's own state, before the room
            foreach (var line in Conditions.SelfLines(ctx.World, ctx.Modules, ctx.Agent))
                sb.AppendLine(line);

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
                // bare passages show no state — only doors that can be
                // closed announce "open"/"closed"; lock state is never
                // observable either way
                var parts = exits.Select(p => Perception.ExitLabel(ctx.World, ctx.Modules, p));
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
            // stumbling through darkness is a gamble: a scenario opts in
            // via the rules module (darkHazardChance, a percentage per
            // move made while unable to see — a lurking predator, a fatal
            // misstep; the engine knows neither). A hit applies the data
            // hazard: the named condition template attaches to the actor
            // and the hazard text reports it.
            var rules = Checks.RulesHost(ctx.World);
            var hazardChance = rules is null
                ? 0
                : ctx.Modules.ResolveInt(rules, "rules", "darkHazardChance");
            if (!Light.IsLit(ctx.World, ctx.Modules, ctx.Agent) && hazardChance > 0)
            {
                var roll = (ctx.Random ?? new Random()).Next(100);
                if (roll < hazardChance)
                {
                    if (rules is not null &&
                        ctx.Modules.ResolveString(rules, "rules", "darkHazardCondition") is
                            { Length: > 0 } template &&
                        ctx.World.HasObject(template))
                        Conditions.Attach(ctx.World, ctx.Modules, ctx.Agent, template);
                    return ActionResult.Fail(
                        (rules is not null
                            ? ctx.Modules.ResolveString(rules, "rules", "darkHazardText")
                            : null) is { Length: > 0 } hazardText
                            ? hazardText
                            : "Something finds you in the dark. It is the last thing you learn.");
                }
            }
            ctx.World.MoveObject(ctx.Agent.Id, to);
            var room = ctx.World.GetObject(to);
            // traversal effects authored on the exit side fire with the
            // arrival complete (a chimney climb re-arming what it should)
            if (ctx.Modules.ResolveField(portal, "portal", "onExit") is
                    { ValueKind: JsonValueKind.Array } onExit)
                Effects.Apply(ctx.Engine, onExit,
                    new EffectContext(ctx.Agent, portal, null, portal, ctx.Random));
            // the direction rides along ("You go east through the canvas
            // awning into Market Square.") — this message is what memory
            // stores, and direction+destination pairs are how a planner
            // learns the map from its own footsteps
            var direction = ctx.Modules.ResolveString(portal, "portal", "direction") ?? "";
            var via = direction.Length > 0
                ? $"{direction} through the {portal.Name}"
                : $"through the {portal.Name}";
            var message = $"You go {via} into {room.Name}.";
            // first visit to a scored room (the kitchen, the cellar)
            var points = Score.AwardRoom(ctx.World, ctx.Modules, room);
            if (points != 0)
            {
                Score.Adjust(ctx.World, ctx.Modules, ctx.Agent, points);
                message += $" [Your score just went up by {points}.]";
            }
            // arriving where you cannot see earns a warning
            if (!Light.RoomIsLit(ctx.World, ctx.Modules, room, ctx.Agent) &&
                rules is not null &&
                ctx.Modules.ResolveString(rules, "rules", "darkArrivalText") is
                    { Length: > 0 } arrival)
                message += " " + arrival;
            return ActionResult.Ok(message);
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
            if (target.Parent != room.Id)
            {
                // up the containment chain toward the room: every
                // intermediate holder must let contents out — surfaces
                // and furniture always, containers while open (an open
                // sack on a table offers its garlic; a key in a closed
                // drawer stays put). Agents' pockets are steal's domain.
                var node = target.Parent.Length > 0 && ctx.World.HasObject(target.Parent)
                    ? ctx.World.GetObject(target.Parent)
                    : null;
                var reached = false;
                while (node is not null)
                {
                    if (node.Id == room.Id)
                    {
                        reached = true;
                        break;
                    }
                    holder ??= node; // innermost holder names the take
                    node = node.Parent.Length > 0 && ctx.World.HasObject(node.Parent)
                        ? ctx.World.GetObject(node.Parent)
                        : null;
                }
                if (!reached || holder is null)
                    return ActionResult.Fail($"You don't see the {target.Name} here.");
                for (var link = holder; link.Id != room.Id;
                     link = ctx.World.GetObject(link.Parent))
                {
                    if (link.HasModule("surface") ||
                        link.HasModule("sittable") || link.HasModule("lyable"))
                    {
                        // pass through
                    }
                    else if (link.HasModule("agent"))
                    {
                        return ActionResult.Fail($"You don't see the {target.Name} here.");
                    }
                    else if (link.HasModule("container") && HandlerState.IsOpen(ctx, link))
                    {
                        // pass through
                    }
                    else if (link.HasModule("container") || link.HasModule("openable"))
                    {
                        return ActionResult.Fail(
                            $"The {target.Name} is inside the closed {link.Name}.");
                    }
                    else
                    {
                        return ActionResult.Fail($"You don't see the {target.Name} here.");
                    }
                }
                // name the enclosing thing ("from the cupboard"); agents
                // hold their belongings, not contain them
                if (holder.HasModule("agent"))
                    holder = null;
            }

            ctx.World.MoveObject(target.Id, ctx.Agent.Id);
            if (target.HasModule("agent"))
                // a carried agent's posture is derived from containment;
                // clear any stored sit/lie so it can't go stale
                ctx.World.SetFieldOverride(
                    target.Id, "agent", "posture", World.World.ToJson(Postures.Standing));
            // a treasure's first acquisition is worth points
            var points = Score.AwardItem(ctx.World, ctx.Modules, target);
            if (points != 0)
                Score.Adjust(ctx.World, ctx.Modules, ctx.Agent, points);
            // name the holder the item came out of ("from the cupboard")
            var from = holder is null || holder.HasModule("agent")
                ? ""
                : $" from the {holder.Name}";
            return ActionResult.Ok(
                $"You take the {target.Name}{from}." +
                (points != 0 ? $" [Your score just went up by {points}.]" : ""));
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
            var target = ctx.Target ?? throw new InvalidOperationException("put requires a target container.");
            var surface = target.HasModule("surface");
            if (!surface && !target.HasModule("container"))
                return ActionResult.Fail($"You can't put anything onto the {target.Name}.");
            var prep = surface ? "onto" : "into";
            var item = ctx.AuxTarget ?? throw new InvalidOperationException("put requires an item (aux target).");
            if (item.Id == target.Id)
                return ActionResult.Fail($"You can't put the {item.Name} {prep} itself.");
            if (item.Parent != ctx.Agent.Id)
                return ActionResult.Noop($"You're not carrying the {item.Name}.");
            if (Clothing.IsWorn(ctx.Modules, item))
                return ActionResult.Fail($"Take off the {item.Name} first.");
            if (HandlerState.GetOpenState(ctx, target) is not null && !HandlerState.IsOpen(ctx, target))
                return ActionResult.Fail($"The {target.Name} is closed.");
            var capacity = ctx.Modules.ResolveInt(target, surface ? "surface" : "container", "capacity", 10);
            if (ctx.World.ChildrenOf(target.Id).Count() >= capacity)
                return ActionResult.Fail($"The {target.Name} is full.");
            ctx.World.MoveObject(item.Id, target.Id);
            return ActionResult.Ok(
                $"You put {Perception.WithDefiniteArticle(item.Name)} {prep} {Perception.WithDefiniteArticle(target.Name)}.");
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
                return ActionResult.Fail($"{Knowledge.NameFor(ctx.Modules, ctx.Agent, recipient)} can't take that.");
            var item = ctx.AuxTarget ?? throw new InvalidOperationException("give requires an item (aux target).");
            if (item.Parent != ctx.Agent.Id)
                return ActionResult.Noop($"You're not carrying the {item.Name}.");
            if (ctx.Reaction is { NoResist: false })
                return ActionResult.Fail(Capitalize(
                    $"{Knowledge.NameFor(ctx.Modules, ctx.Agent, recipient)} declines {Perception.WithDefiniteArticle(item.Name)}."));
            ctx.World.MoveObject(item.Id, recipient.Id);
            return ActionResult.Ok(
                $"You give {Perception.WithDefiniteArticle(item.Name)} to {Knowledge.NameFor(ctx.Modules, ctx.Agent, recipient)}.");
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
                if (Conditions.IsInternal(item))
                    continue; // anatomy and status conditions, not belongings
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
            parts.AddRange(Conditions.SelfLines(ctx.World, ctx.Modules, ctx.Agent));
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
            // the verb rides along from the affordance (say, shout,
            // whisper) — the speaker hears their own manner of speaking
            var verb = string.IsNullOrEmpty(ctx.Verb) ? "say" : ctx.Verb;
            // a directed say (target = another agent) names the addressee
            // back to the actor — by the name the actor can print
            var addressee = ctx.Target is not null && ctx.Target.Id != ctx.Agent.Id &&
                            ctx.Target.HasModule("agent")
                ? ctx.Target
                : null;
            return ActionResult.Ok(
                addressee is null
                    ? $"You {verb}: \"{text}\""
                    : $"You {verb} to {Knowledge.NameFor(ctx.Modules, ctx.Agent, addressee)}: \"{text}\"",
                duration);
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
            var targetName = Perception.WithDefiniteArticle(
                Knowledge.NameFor(ctx.Modules, ctx.Agent, target));
            // beating someone who is already down reads differently — the
            // report says so, and the planner reading its own outcome knows
            // the fight is over
            var alreadyDown = target.HasModule("agent") &&
                              Health.IsIncapacitated(ctx.World, ctx.Modules, target);

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

            // the hit report goes out BEFORE the damage is applied, so the
            // wound reports it triggers (crippled, condition bands,
            // incapacitation) land in observers' queues after it, in order
            var hitText = part is not null
                ? Condition.Descriptive(ctx.World, ctx.Modules)
                    ? $"{{agent}} lands {Perception.WithArticle(Condition.BlowCategory(ctx.World, ctx.Modules, part, damage))} " +
                      $"blow on {{target}} in the {part.Name}."
                    : $"{{agent}} hits {{target}} in the {part.Name} for {damage} damage."
                : Condition.Descriptive(ctx.World, ctx.Modules) && target.HasModule("health")
                    ? $"{{agent}} lands {Perception.WithArticle(Condition.BlowCategory(ctx.World, ctx.Modules, target, damage))} " +
                      $"blow on {{target}}."
                    : $"{{agent}} hits {{target}} for {damage} damage.";
            ctx.Signals.Emit(ctx.Agent, target,
                [new Signals.SignalSpec { Sense = Signals.SignalSense.Visual, Priority = 10, Text = hitText }]);

            string message;
            string? fragment;
            if (part is not null)
            {
                fragment = Damage.ApplyToPart(ctx.World, ctx.Modules, part, damage, ctx.Signals);
                message = Condition.Descriptive(ctx.World, ctx.Modules)
                    ? $"You land {Perception.WithArticle(Condition.BlowCategory(ctx.World, ctx.Modules, part, damage))} " +
                      $"blow on {targetName}'s {part.Name}{weaponSuffix}."
                    : $"You hit {targetName} in the {part.Name}{weaponSuffix} for {damage} damage.";
            }
            else
            {
                fragment = Damage.Apply(ctx.World, ctx.Modules, target, damage, ctx.Signals);
                message = Condition.Descriptive(ctx.World, ctx.Modules) && target.HasModule("health")
                    ? $"You land {Perception.WithArticle(Condition.BlowCategory(ctx.World, ctx.Modules, target, damage))} " +
                      $"blow on {targetName}{weaponSuffix}."
                    : $"You hit {targetName}{weaponSuffix} for {damage} damage.";
            }
            if (fragment is not null)
                message += " " + fragment;
            if (alreadyDown)
                message += $" {Capitalize(targetName)} is already incapacitated.";
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
            // a body part: its own description (authored per part), plus
            // any deposits held — "There is semen in it."
            if (target.HasModule("bodypart"))
            {
                var owner = target.Parent.Length > 0 && ctx.World.HasObject(target.Parent)
                    ? ctx.World.GetObject(target.Parent)
                    : null;
                var partName = owner is not null && owner.HasModule("agent") && owner.Id != ctx.Agent.Id
                    ? $"{Knowledge.NameFor(ctx.Modules, ctx.Agent, owner)}'s {target.Name}"
                    : $"Your {target.Name}";
                sb.AppendLine(char.ToUpperInvariant(partName[0]) + partName[1..]);
                if (target.Description.Length > 0)
                    sb.AppendLine(target.Description);
                if (ctx.World.ChildrenOf(target.Id).Any())
                    sb.AppendLine(Perception.ContentsSentence(ctx.World, target, "in it"));
                return ActionResult.Ok(sb.ToString().TrimEnd());
            }
            // names are observer-relative: strangers render by their
            // incognito description until the examiner has learned them
            var name = Knowledge.NameFor(ctx.Modules, ctx.Agent, target);
            sb.AppendLine(name);
            // descriptions can introduce their subject by name — strangers
            // get the incognito description, so a look teaches no names
            var description = Knowledge.DescriptionFor(ctx.Modules, ctx.Agent, target);
            if (description.Length > 0)
                sb.AppendLine(description);

            if (target.HasModule("agent"))
            {
                if (Health.IsIncapacitated(ctx.World, ctx.Modules, target))
                    sb.AppendLine($"{name} is incapacitated.");
                // what they're in the middle of, and words being held back
                if (ctx.Modules.ResolveString(target, "agent", "activity") is { Length: > 0 } busy)
                    sb.AppendLine($"{name} is {busy}.");
                if (ctx.Modules.ResolveBool(target, "agent", "speakingSoon"))
                    sb.AppendLine($"{name} looks about to say something.");
                var words = Conditions.VisibleWords(ctx.World, ctx.Modules, target);
                if (words.Count > 0)
                    sb.AppendLine($"{name} looks {string.Join(" and ", words)}.");
                foreach (var line in Condition.ExamineLines(ctx.World, ctx.Modules, target))
                    sb.AppendLine(line);
                var posture = Postures.Of(ctx.World, ctx.Modules, target);
                if (posture == Postures.Prone)
                    sb.AppendLine($"{name} is prone on the ground.");
                else if (posture == Postures.Carried)
                    sb.AppendLine($"{name} is being carried by {Knowledge.NameFor(ctx.Modules, ctx.Agent, ctx.World.GetObject(target.Parent))}.");
                else if (posture != Postures.Standing)
                    sb.AppendLine($"{name} is {posture} on {Perception.WithDefiniteArticle(ctx.World.GetObject(target.Parent).Name)}.");
                var worn = Clothing.WornItems(ctx.World, ctx.Modules, target);
                if (worn.Count > 0)
                    sb.AppendLine($"Wearing: {string.Join(", ", worn.Select(w => Perception.WithArticle(w.Name)))}.");
                var carried = ctx.World.ChildrenOf(target.Id)
                    .Where(c => !Conditions.IsInternal(c) && !Clothing.IsWorn(ctx.Modules, c)).ToList();
                if (carried.Count > 0)
                    sb.AppendLine($"Carrying: {string.Join(", ", carried.Select(c => Perception.WithArticle(c.Name)))}.");
            }
            else
            {
                if (HandlerState.GetOpenState(ctx, target) is not null)
                    sb.AppendLine(HandlerState.IsOpen(ctx, target) ? "It is open." : "It is closed.");
                if (target.HasModule("surface"))
                    sb.AppendLine(Perception.ContentsSentence(ctx.World, target, "on it"));
                else if (target.HasModule("container") && HandlerState.IsOpen(ctx, target))
                    sb.AppendLine(Perception.ContentsSentence(ctx.World, target));
                else if (ctx.World.ChildrenOf(target.Id).Any())
                    // anything else holding children still shows them —
                    // body parts and garments with deposits ("There is
                    // semen in it."), the generic emission rendering
                    sb.AppendLine(Perception.ContentsSentence(ctx.World, target, "in it"));
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
        // already on the SAME support in the other posture: shift
        // directly — lie <-> sit never passes through standing
        if (ctx.Agent.Parent == target.Id)
        {
            ctx.World.SetFieldOverride(ctx.Agent.Id, "agent", "posture",
                World.World.ToJson(posture));
            return ActionResult.Ok(posture == Postures.Lying
                ? $"You stretch out on the {target.Name}."
                : $"You sit up on the {target.Name}.");
        }
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

            // one garment per region per LAYER: undergarments and
            // outerwear stack over the same regions; only same-layer
            // overlaps conflict
            foreach (var worn in Clothing.WornItems(ctx.World, ctx.Modules, ctx.Agent))
            {
                var overlap = Clothing.GarmentRegions(ctx.Modules, worn).Intersect(regions).ToList();
                if (overlap.Count > 0 &&
                    Clothing.Layer(ctx.Modules, worn) == Clothing.Layer(ctx.Modules, target))
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

            // pulling a garment off another agent: a refusing reaction
            // (effect "refuse") keeps it on — consent is deterministic,
            // like the touch family; otherwise an opposed check, rolled
            // here (like attack) so self-removal stays check-free; the
            // stats come from the combatant modules
            // (strength/brawling vs agility)
            if (target.Parent != ctx.Agent.Id && ctx.World.HasObject(target.Parent) &&
                ctx.World.GetObject(target.Parent) is { } wearer && wearer.HasModule("agent"))
            {
                if (ctx.Reaction?.Effect == "refuse")
                    return ActionResult.Fail(
                        $"You grab at the {target.Name}, but {wearer.Name} keeps it on.");
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
            return ActionResult.Ok($"You steal the {target.Name} from {Knowledge.NameFor(ctx.Modules, ctx.Agent, holder)}.");
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
                if (Damage.ApplyToPart(ctx.World, ctx.Modules, part, damage, ctx.Signals) is { } partFragment)
                    message += " " + partFragment;
                return ActionResult.Ok(message);
            }
            var monolithic = $"You choke {targetName} for {damage} damage.";
            if (Damage.Apply(ctx.World, ctx.Modules, target, damage, ctx.Signals) is { } fragment)
                monolithic += " " + fragment;
            return ActionResult.Ok(monolithic);
        }
    }

    // barter — a `ware` module on the item names the seller (`trader`,
    // optional), what it costs (`wants`, an item id the actor must hold
    // or have already handed over), and a spoken `refusal` when the
    // offer isn't enough. The holder consents or declines via the
    // action's reaction; on consent the two items swap parents. All
    // phrasing is data (self / self:gift for the two success forms,
    // onNoHolder / onNotTrading / onDeclined / onWants / onTry for the
    // refusals) with {holder}/{target}/{wants} placeholders.
    private sealed class TradeHandler : IActionHandler
    {
        public string Id => "trade";

        public ActionResult Execute(ActionContext ctx)
        {
            var ware = ctx.Target ?? throw new InvalidOperationException("trade requires a target ware.");
            var holder = ware.Parent.Length > 0 && ctx.World.HasObject(ware.Parent)
                ? ctx.World.GetObject(ware.Parent) : null;
            var holderName = holder is null
                ? ""
                : Knowledge.NameFor(ctx.Modules, ctx.Agent, holder);
            string Render(string template, string? wantsName = null) =>
                Capitalize(template
                    .Replace("{holder}", holderName, StringComparison.Ordinal)
                    .Replace("{target}", Perception.WithDefiniteArticle(ware.Name), StringComparison.Ordinal)
                    .Replace("{wants}",
                        wantsName is null ? "" : Perception.WithDefiniteArticle(wantsName),
                        StringComparison.Ordinal));
            if (holder is null || !holder.HasModule("agent"))
                return ActionResult.Fail(Render(Data(ctx, "onNoHolder",
                    "There's nobody here to trade {target} with.")));
            if (holder.Id == ctx.Agent.Id)
                return ActionResult.Noop($"You're already carrying the {ware.Name}.");
            // a ware with a `trader` sells only through that agent
            if (ctx.Modules.ResolveString(ware, "ware", "trader") is { Length: > 0 } trader &&
                holder.Id != trader)
                return ActionResult.Fail(Render(Data(ctx, "onNotTrading",
                    "{holder} isn't trading {target}.")));
            // the holder consented or declined via the trade's reaction
            if (ctx.Reaction is { NoResist: false })
                return ActionResult.Fail(Render(Data(ctx, "onDeclined",
                    "{holder} declines the offer.")));
            var wantsId = ctx.Modules.ResolveString(ware, "ware", "wants");
            if (wantsId is null || !ctx.World.HasObject(wantsId))
                return ActionResult.Fail(Render(Data(ctx, "onNotTrading",
                    "{holder} isn't trading {target}.")));
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
                    return ActionResult.Fail(Render(Data(ctx, "onTry",
                        "You try to barter for {target}.")));
                }
                return ActionResult.Fail(Render(Data(ctx, "onWants",
                    "{holder} wants {wants} in exchange."), wants.Name));
            }
            // in the gift-ahead case the actor gives nothing now, so say so
            var wantsInHand = wants.Parent == ctx.Agent.Id;
            ctx.World.MoveObject(wants.Id, holder.Id);
            ctx.World.MoveObject(ware.Id, ctx.Agent.Id);
            return ActionResult.Ok(Render(Data(ctx, wantsInHand ? "self" : "self:gift",
                    wantsInHand
                        ? "You trade {wants} for {target}."
                        : "{holder} hands you {target}."),
                wants.Name));
        }
    }

    // a requirements-gated rite or service (unbinding a curse, forging an
    // item): the `ritual` module lives on the rite's host (the sorcerer, the
    // altar) and lists required item ids (held by the host or the
    // supplicant), items to consume, modules to remove from the supplicant,
    // and an epilogue; `endsGame` ends the game with that epilogue. Two
    // directions share the handler: the supplicant asks (actor = supplicant,
    // target = host) or the host performs (a targetOthers affordance —
    // actor = host, target = supplicant). The refusal phrasing is data
    // (onNeeds with {host}/{items}, onAsk for the ask-direction prefix);
    // the epilogue is the module's `epilogue` field.
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
                var lack = Capitalize(Data(ctx, "onNeeds",
                        "{host} shakes their head — the rite still needs: {items}.")
                    .Replace("{host}", host.Name, StringComparison.Ordinal)
                    .Replace("{items}", string.Join(", ", names), StringComparison.Ordinal));
                // the ask direction reads as an answer to the question;
                // otherwise the refusal appears with no visible trigger
                return ActionResult.Fail(ReferenceEquals(host, ctx.Agent)
                    ? lack
                    : Data(ctx, "onAsk", "You ask {host} for the rite.")
                        .Replace("{host}", host.Name, StringComparison.Ordinal) + " " + lack);
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

    // consuming food and drink — one generic handler, everything a
    // scenario tunes lives in the affordance's data. The verb comes
    // from the affordance ("drink" the ale, "eat" the fries); actor
    // effects are `impulse.<motive>` keys naming motive deltas as a
    // field of the consumable (an optional leading '-' negates — food's
    // sobering burns alcohol off) or a literal number, applied through
    // the motive simulation (clamping and band sync included; agents
    // without the matching motives just consume with no effect).
    // Multi-serving items declare a `servings` count (default 1): each
    // consume takes one, and the last empties the vessel — which
    // visibly becomes an empty one (`emptyName`/`emptyDescription`)
    // for the barmaid to clear, unless the data says destroyOnConsume
    // (a swallowed pill, a gum wrapper world).
    private sealed class ConsumeHandler : IActionHandler
    {
        public string Id => "consume";

        public ActionResult Execute(ActionContext ctx)
        {
            var target = ctx.Target ?? throw new InvalidOperationException("consume requires a target.");
            // the affordance's module names the consumable kind; direct
            // handler execution (no affordance) falls back to sniffing
            // the classic modules
            var moduleId = ctx.ModuleId ??
                (target.HasModule("beverage") ? "beverage"
                    : target.HasModule("food") ? "food" : null);
            if (moduleId is null || !target.HasModule(moduleId))
                return ActionResult.Fail($"You can't consume {Perception.WithDefiniteArticle(target.Name)}.");
            if (ctx.Modules.ResolveBool(target, moduleId, "empty"))
                return ActionResult.Noop(
                    $"There's nothing left in {Perception.WithDefiniteArticle(target.Name)}.");
            // the result message names what was consumed, not the empty
            // vessel it becomes
            var consumedName = target.Name;

            var verb = ctx.Verb ?? "consume";
            var taste = Field(ctx, target, moduleId, "taste");
            var deltas = ImpulseDeltas(ctx, target, moduleId);
            if (deltas.Count > 0)
                Motives.Impulse(ctx.World, ctx.Modules, ctx.Signals, ctx.Agent, deltas);
            // multi-serving vessels count down; the last serving empties
            var remaining = ctx.Modules.ResolveInt(target, moduleId, "servings", 1) - 1;
            if (ctx.Modules.ResolveField(target, moduleId, "servings") is not null)
                ctx.World.SetFieldOverride(
                    target.Id, moduleId, "servings", World.World.ToJson(Math.Max(0, remaining)));
            if (remaining > 0)
                return Finish(ctx, target, moduleId, consumedName, verb, taste);
            if (ctx.Modules.ResolveBool(target, moduleId, "destroyOnConsume"))
                ctx.World.DestroyObject(target.Id);
            else
            {
                ctx.World.SetFieldOverride(target.Id, moduleId, "empty", World.World.ToJson(true));
                // a finished vessel visibly becomes an empty one — the state
                // is legible in names, menus, and listings, not hidden in a
                // field ("mug of Green Gullet ale" -> "empty mug")
                if (Field(ctx, target, moduleId, "emptyName") is { } emptyName)
                    target.Name = emptyName;
                if (Field(ctx, target, moduleId, "emptyDescription") is { } emptyDescription)
                    target.Description = emptyDescription;
            }
            return Finish(ctx, target, moduleId, consumedName, verb, taste);
        }

        /// <summary>The shared tail: the taste sensation and result message.</summary>
        private static ActionResult Finish(
            ActionContext ctx, WorldObject target, string moduleId,
            string consumedName, string verb, string? taste)
        {
            if (taste is not null)
                ctx.Signals.SendTo(ctx.Agent, taste);
            var message = Data(ctx, "self") is { Length: > 0 } self
                ? self.Replace("{target}",
                    Perception.WithDefiniteArticle(consumedName), StringComparison.Ordinal)
                : $"You {verb} {Perception.WithDefiniteArticle(consumedName)}.";
            return ActionResult.Ok(message);
        }

        /// <summary>
        /// The affordance data's `impulse.<motive>` keys: each names the
        /// delta as a numeric field of the consumable (leading '-'
        /// negates) or a literal number. Empty when the affordance
        /// carries none (direct handler execution, or a consumable with
        /// no motive effects).
        /// </summary>
        private static Dictionary<string, double> ImpulseDeltas(
            ActionContext ctx, WorldObject target, string moduleId)
        {
            var deltas = new Dictionary<string, double>(StringComparer.Ordinal);
            if (ctx.Data is null)
                return deltas;
            foreach (var (key, spec) in ctx.Data)
            {
                if (!key.StartsWith("impulse.", StringComparison.Ordinal))
                    continue;
                var negate = spec.StartsWith('-');
                var name = negate ? spec[1..] : spec;
                double amount;
                if (ctx.Modules.ResolveField(target, moduleId, name) is
                        { ValueKind: JsonValueKind.Number } field)
                    amount = field.GetDouble();
                else if (double.TryParse(name, out var literal))
                    amount = literal;
                else
                    continue; // an unresolvable reference contributes nothing
                deltas[key["impulse.".Length..]] = negate ? -amount : amount;
            }
            return deltas;
        }
    }

    // spawning a live instance from a prefab template: the `spawner`
    // module on the host (an ale tap, a stove) names a template object
    // (kept outside the room tree, like shared state) and where the clone
    // lands (`spawnTo` — a counter or table, usually a surface; the host
    // itself when unset), plus a slot capacity (default 1 — the
    // anti-flood rule: a random bartender can't pour forever; the drink
    // must be picked up first). The host doesn't need to be a container;
    // the slot counts clones of this spawner's prefab on the target.
    private sealed class SpawnHandler : IActionHandler
    {
        public string Id => "spawn";

        public ActionResult Execute(ActionContext ctx)
        {
            var target = ctx.Target ?? throw new InvalidOperationException("spawn requires a target.");
            if (!target.HasModule("spawner"))
                return ActionResult.Fail($"You can't draw anything from the {target.Name}.");
            var templateId = ctx.Modules.ResolveString(target, "spawner", "prefab");
            if (templateId is null || !ctx.World.HasObject(templateId))
                return ActionResult.Fail($"The {target.Name} has run dry.");
            var parent = Spawning.SpawnTarget(ctx.World, ctx.Modules, target);
            var max = ctx.Modules.ResolveInt(target, "spawner", "maxChildren", 1);
            if (Spawning.CloneCount(ctx.World, parent, templateId) >= max)
                return ActionResult.Noop(
                    $"{Capitalize(Perception.WithDefiniteArticle(parent.Name))} is already occupied.");
            var id = templateId;
            for (var n = 1; ctx.World.HasObject(id); n++)
                id = $"{templateId}_{n}";
            var clone = ctx.World.CloneTree(templateId, parent.Id, id);
            var prep = parent.HasModule("surface") ? "on" : "at";
            return ActionResult.Ok(
                $"{Capitalize(Perception.WithArticle(clone.Name))} now sits {prep} {Perception.WithDefiniteArticle(parent.Name)}.");
        }
    }

    // cleaning deposits off a holder — a body part, a garment, a
    // surface: everything under the target carrying the data-named
    // mess module is destroyed (the emission's template gives its
    // clones that module). Data: `messModule` (which children count as
    // deposits), optional `toolModule` — a held item doing the wiping
    // (a tissue), which spends itself: it gains `spent: true` on that
    // module and renames to its `spentName` when authored, and a
    // spent tool can't clean again. Without a tool the action is bare
    // (swallowing, a finger-swipe) — prose decides what happened.
    private sealed class CleanHandler : IActionHandler
    {
        public string Id => "clean";

        public ActionResult Execute(ActionContext ctx)
        {
            var target = ctx.Target ?? throw new InvalidOperationException("clean requires a target.");
            var messModule = Data(ctx, "messModule");
            if (messModule.Length == 0)
                return ActionResult.Fail("That isn't something you can clean.");
            var deposits = ctx.World.ChildrenOf(target.Id)
                .Where(c => c.HasModule(messModule))
                .ToList();
            if (deposits.Count == 0)
                return ActionResult.Noop($"There is nothing to clean off {CleanableName(ctx, target)}.");

            WorldObject? tool = null;
            if (Data(ctx, "toolModule") is { Length: > 0 } toolModule)
            {
                tool = ctx.World.ChildrenOf(ctx.Agent.Id)
                    .FirstOrDefault(i => i.HasModule(toolModule) &&
                                         !ctx.Modules.ResolveBool(i, toolModule, "spent"));
                if (tool is null)
                    return ActionResult.Fail(Data(ctx, "onNoTool",
                        "You have nothing to clean that up with."));
            }

            foreach (var deposit in deposits)
                ctx.World.DestroyObject(deposit.Id);
            var toolName = tool?.Name;
            if (tool is not null && Data(ctx, "toolModule") is { Length: > 0 } tm)
            {
                ctx.World.SetFieldOverride(tool.Id, tm, "spent", World.World.ToJson(true));
                if (ctx.Modules.ResolveString(tool, tm, "spentName") is { Length: > 0 } spentName)
                    tool.Name = spentName;
            }

            var name = CleanableName(ctx, target);
            return ActionResult.Ok(tool is not null
                ? Data(ctx, "self", $"You clean {name} with the {toolName}.")
                    .Replace("{target}", name, StringComparison.Ordinal)
                    .Replace("{instrument}", toolName, StringComparison.Ordinal)
                : Data(ctx, "self", $"You clean {name}.")
                    .Replace("{target}", name, StringComparison.Ordinal));
        }
    }

    /// <summary>A clean target's name, observer-relative ("Maya's sex", "your cock", "the bed").</summary>
    private static string CleanableName(ActionContext ctx, WorldObject target)
    {
        if (target.HasModule("bodypart") &&
            target.Parent.Length > 0 && ctx.World.HasObject(target.Parent))
        {
            var owner = ctx.World.GetObject(target.Parent);
            if (owner.HasModule("agent"))
                return owner.Id == ctx.Agent.Id
                    ? $"your {target.Name}"
                    : $"{Knowledge.NameFor(ctx.Modules, ctx.Agent, owner)}'s {target.Name}";
        }
        return Perception.WithDefiniteArticle(target.Name);
    }

    // washing the whole body at once — a shower: every deposit the
    // agent's body holds (on any body part, or caught directly on the
    // agent) is rinsed away. Data: `messModule` names the deposit
    // module; `self` is the prose.
    private sealed class WashHandler : IActionHandler
    {
        public string Id => "wash";

        public ActionResult Execute(ActionContext ctx)
        {
            var messModule = Data(ctx, "messModule");
            if (messModule.Length == 0)
                return ActionResult.Fail("That isn't something you can wash.");
            var deposits = ctx.World.ChildrenOf(ctx.Agent.Id)
                .Where(c => c.HasModule(messModule))
                .Concat(ctx.World.ChildrenOf(ctx.Agent.Id)
                    .SelectMany(part => ctx.World.ChildrenOf(part.Id))
                    .Where(c => c.HasModule(messModule)))
                .ToList();
            foreach (var deposit in deposits)
                ctx.World.DestroyObject(deposit.Id);
            return ActionResult.Ok(Data(ctx, "self",
                "You let the hot water carry it away."));
        }
    }

    // destroying a world object outright — the generic removal verb.
    // The affordance's when/gates data decide what may be destroyed (an
    // empty vessel for the barmaid's bus tub, a note after reading);
    // the observable outcome is the affordance's own signals (they
    // render the target's name as captured before the handler ran, so
    // they still read after the object is gone); the message comes from
    // data.self ("You clear away {target}.")
    private sealed class DestroyHandler : IActionHandler
    {
        public string Id => "destroy";

        public ActionResult Execute(ActionContext ctx)
        {
            var target = ctx.Target ?? throw new InvalidOperationException("destroy requires a target.");
            var name = target.Name;
            ctx.World.DestroyObject(target.Id);
            var message = Data(ctx, "self") is { Length: > 0 } self
                ? self.Replace("{target}",
                    Perception.WithDefiniteArticle(name), StringComparison.Ordinal)
                : $"You destroy {Perception.WithDefiniteArticle(name)}.";
            return ActionResult.Ok(message);
        }
    }

    // leaving for good: an `exit` module on a street-side object offers
    // the way out ("Go home") — the handler ends the game with the
    // module's departure text. Player-only by data (an NPC picking it
    // would end the player's game); NPCs get the depart verb instead.
    private sealed class LeaveHandler : IActionHandler
    {
        public string Id => "leave";

        public ActionResult Execute(ActionContext ctx)
        {
            var target = ctx.Target ?? throw new InvalidOperationException("leave requires a target.");
            var text = Field(ctx, target, "exit", "text") ?? "You leave.";
            // the exit's `endings` map keys condition kinds on the actor
            // to epilogue suffixes — a goodbye after a warm evening reads
            // differently than one after a cold one
            if (ctx.Modules.ResolveField(target, "exit", "endings") is
                    { ValueKind: JsonValueKind.Object } endings)
            {
                foreach (var kind in endings.EnumerateObject())
                {
                    if (kind.Value.ValueKind != JsonValueKind.String)
                        continue;
                    if (Conditions.Has(ctx.World, ctx.Modules, ctx.Agent, kind.Name))
                        text = text.TrimEnd() + " " + kind.Value.GetString()!.Trim();
                }
            }
            return ActionResult.Ok(text) with { EndsGame = true };
        }
    }

    // an autonomous agent's way home: the NPC steps out of the scenario —
    // destroyed, gone from the world — without ending anyone's game. The
    // departure is observable (the exit module's `departText`, emitted
    // here BEFORE the actor is destroyed, since affordance-level signals
    // resolve the actor afterward).
    private sealed class DepartHandler : IActionHandler
    {
        public string Id => "depart";

        public ActionResult Execute(ActionContext ctx)
        {
            var target = ctx.Target ?? throw new InvalidOperationException("depart requires a target.");
            if (ctx.Agent.Children.Any(id =>
                    ctx.World.HasObject(id) && ctx.World.GetObject(id).HasModule("agent")))
                return ActionResult.Fail("You can't leave while carrying someone.");
            var text = Field(ctx, target, "exit", "departText") ?? "{agent} leaves.";
            ctx.Signals.Emit(ctx.Agent, target,
                [new Signals.SignalSpec { Sense = Signals.SignalSense.Visual, Priority = 10, Text = text }]);
            ctx.World.DestroyObject(ctx.Agent.Id);
            return ActionResult.Ok("You go home.");
        }
    }
}
