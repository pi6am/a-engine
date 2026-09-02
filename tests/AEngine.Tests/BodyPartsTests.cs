using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Llm;

namespace AEngine.Tests;

/// <summary>
/// RPG stage 6: granular body parts. Parts are child objects of the agent
/// (bodypart + health modules); vital parts and the optional shock
/// threshold drive incapacitation; cripple effects (disarm, prone,
/// no_stand) fire at 0 hp; attacks land on an aimed or random part with
/// region-scoped armor; the rules crunch level switches damage/condition
/// reporting between numeric and descriptive. Agents without parts keep
/// the monolithic health pool.
/// </summary>
public class BodyPartsTests
{
    private const string PartModulesJson = """
    [
      {
        "id": "rules", "name": "Rules",
        "fields": [
          { "name": "diceCount", "type": "int", "default": 0 },
          { "name": "diceSides", "type": "int", "default": 0 },
          { "name": "crunch", "type": "string", "default": "numeric" },
          { "name": "blowBands", "type": "map", "default": {} },
          { "name": "conditionBands", "type": "map", "default": {} },
          { "name": "shockThreshold", "type": "int", "default": 0 }
        ],
        "affordances": []
      },
      {
        "id": "stats", "name": "Stats",
        "fields": [ { "name": "values", "type": "map", "default": {} } ],
        "affordances": []
      },
      {
        "id": "skills", "name": "Skills",
        "fields": [ { "name": "values", "type": "map", "default": {} } ],
        "affordances": []
      },
      {
        "id": "health", "name": "Health",
        "fields": [
          { "name": "maxHp", "type": "int", "default": 10 },
          { "name": "hp", "type": "int", "default": 10 },
          { "name": "incapacitatedAt", "type": "int", "default": 0 }
        ],
        "affordances": []
      },
      {
        "id": "bodypart", "name": "Body Part",
        "fields": [
          { "name": "region", "type": "string", "default": "" },
          { "name": "vital", "type": "bool", "default": false },
          { "name": "crippleEffects", "type": "list", "default": [] },
          { "name": "aimedPenalty", "type": "int", "default": 4 }
        ],
        "affordances": []
      },
      {
        "id": "body", "name": "Body",
        "fields": [ { "name": "regions", "type": "list", "default": ["head", "top", "bottom", "hand", "held"] } ],
        "affordances": []
      },
      {
        "id": "wearable", "name": "Wearable",
        "fields": [
          { "name": "regions", "type": "list", "default": [] },
          { "name": "worn", "type": "bool", "default": false }
        ],
        "affordances": [
          { "verb": "wear", "handler": "wear" },
          { "verb": "remove", "handler": "remove" }
        ]
      },
      {
        "id": "weapon", "name": "Weapon",
        "fields": [
          { "name": "damageBonus", "type": "int", "default": 0 },
          { "name": "damageDice", "type": "int", "default": 1 },
          { "name": "damageSides", "type": "int", "default": 4 },
          { "name": "skill", "type": "string", "default": "" },
          { "name": "stat", "type": "string", "default": "" }
        ],
        "affordances": []
      },
      {
        "id": "armor", "name": "Armor",
        "fields": [ { "name": "protection", "type": "int", "default": 1 } ],
        "affordances": []
      },
      {
        "id": "combatant", "name": "Combatant",
        "fields": [
          { "name": "attackStat", "type": "string", "default": "strength" },
          { "name": "attackSkill", "type": "string", "default": "brawling" },
          { "name": "defenseStat", "type": "string", "default": "agility" },
          { "name": "defenseSkill", "type": "string", "default": "" },
          { "name": "damageBonus", "type": "int", "default": 0 },
          { "name": "damageDice", "type": "int", "default": 1 },
          { "name": "damageSides", "type": "int", "default": 2 }
        ],
        "affordances": []
      },
      {
        "id": "attackable", "name": "Attackable",
        "fields": [],
        "affordances": [
          {
            "verb": "attack", "handler": "attack",
            "signals": [],
            "failSignals": [
              { "sense": "visual", "priority": 10, "text": "{agent} swings at the {target} and misses." }
            ]
          }
        ]
      },
      {
        "id": "chokeable", "name": "Chokeable",
        "fields": [ { "name": "part", "type": "string", "default": "head" } ],
        "affordances": [
          { "verb": "choke", "handler": "choke" }
        ]
      }
    ]
    """;

    // diceless rules (0d0): checks resolve as bonus vs bonus
    private static GameEngine NewEngine()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.ModuleRegistry.LoadJson(PartModulesJson);
        engine.World.CreateObject("rules", Core.World.World.RootId, "rules");
        engine.World.AddModule("rules", "rules");
        foreach (var id in new[] { "alice", "bob" })
        {
            engine.World.AddModule(id, "stats");
            engine.World.AddModule(id, "skills");
            engine.World.AddModule(id, "body");
            engine.World.AddModule(id, "combatant");
            engine.World.AddModule(id, "attackable");
        }
        engine.World.MoveObject("bob", "room_a"); // same room as Alice
        return engine;
    }

    private static void SetStat(GameEngine engine, string id, string stat, int value) =>
        Stats.Set(engine.World, engine.ModuleRegistry, engine.World.GetObject(id), "stats", stat, value);

    private static void SetDamage(GameEngine engine, string id, int bonus)
    {
        engine.World.SetFieldOverride(id, "combatant", "damageBonus", Core.World.World.ToJson(bonus));
        engine.World.SetFieldOverride(id, "combatant", "damageDice", Core.World.World.ToJson(0));
    }

    private static void AddPart(
        GameEngine engine, string ownerId, string suffix, string name, string region, int hp,
        bool vital = false, params string[] effects)
    {
        var id = $"{ownerId}_{suffix}";
        engine.World.CreateObject(id, ownerId, name);
        engine.World.AddModule(id, "bodypart");
        engine.World.AddModule(id, "health");
        engine.World.SetFieldOverride(id, "bodypart", "region", Core.World.World.ToJson(region));
        if (vital)
            engine.World.SetFieldOverride(id, "bodypart", "vital", Core.World.World.ToJson(true));
        if (effects.Length > 0)
            engine.World.SetFieldOverride(
                id, "bodypart", "crippleEffects", Core.World.World.ToJson(effects.ToList()));
        engine.World.SetFieldOverride(id, "health", "maxHp", Core.World.World.ToJson(hp));
        engine.World.SetFieldOverride(id, "health", "hp", Core.World.World.ToJson(hp));
    }

    private static void AddWornArmor(GameEngine engine, string ownerId, int protection, params string[] regions)
    {
        engine.World.CreateObject("armor_piece", ownerId, "padded armor");
        engine.World.AddModule("armor_piece", "wearable");
        engine.World.AddModule("armor_piece", "armor");
        engine.World.SetFieldOverride(
            "armor_piece", "wearable", "regions", Core.World.World.ToJson(regions.ToList()));
        engine.World.SetFieldOverride("armor_piece", "wearable", "worn", Core.World.World.ToJson(true));
        engine.World.SetFieldOverride(
            "armor_piece", "armor", "protection", Core.World.World.ToJson(protection));
    }

    private static int PartHp(GameEngine engine, string partId) =>
        engine.ModuleRegistry.ResolveInt(engine.World.GetObject(partId), "health", "hp");

    private static ActionResult Attack(GameEngine engine, string? text = null) =>
        engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "attack", "bob"), text);

    [Fact]
    public void Parts_AreAnatomyNotItems()
    {
        var engine = NewEngine();
        AddPart(engine, "bob", "head", "head", "head", 8);
        AddPart(engine, "bob", "torso", "torso", "top", 12);

        // no actions target a part: not takeable, stealable, or examinable
        var actions = engine.ActionResolver.Resolve(engine.World.GetObject("alice"));
        Assert.DoesNotContain(actions, a => a.TargetId is "bob_head" or "bob_torso");

        // inventory and examine don't list parts as belongings (the
        // inventory's Health line is the self status report, not an item)
        var inventory = engine.TurnManager.PerformAction(
            engine.World.GetObject("bob"), TestWorlds.Find(engine, "bob", "inventory"));
        Assert.Contains("You are carrying nothing.", inventory.Message);
        Assert.DoesNotContain("carrying: ", inventory.Message);
        Assert.Contains("Health: head 8/8, torso 12/12.", inventory.Message);
        var examine = engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "examine", "bob"));
        Assert.DoesNotContain("Carrying:", examine.Message);
    }

    [Fact]
    public void Attack_ArmorSoaksOnlyWhenItCoversTheHitPart()
    {
        // torso covered by padded armor: 6 - 3 = 3 damage
        var covered = NewEngine();
        AddPart(covered, "bob", "torso", "torso", "top", 12);
        AddWornArmor(covered, "bob", 3, "top");
        SetStat(covered, "alice", "strength", 10);
        SetDamage(covered, "alice", 6);
        var hit = Attack(covered);
        Assert.Equal("You hit Bob in the torso for 3 damage.", hit.Message);
        Assert.Equal(9, PartHp(covered, "bob_torso"));

        // the same armor does nothing for the head
        var exposed = NewEngine();
        AddPart(exposed, "bob", "head", "head", "head", 8);
        AddWornArmor(exposed, "bob", 3, "top");
        SetStat(exposed, "alice", "strength", 10);
        SetDamage(exposed, "alice", 6);
        hit = Attack(exposed);
        Assert.Equal("You hit Bob in the head for 6 damage.", hit.Message);
        Assert.Equal(2, PartHp(exposed, "bob_head"));
    }

    [Fact]
    public void Attack_Aimed_HitsTheNamedPart_UnknownPartFails()
    {
        var engine = NewEngine();
        AddPart(engine, "bob", "head", "head", "head", 8);
        AddPart(engine, "bob", "torso", "torso", "top", 12);
        SetStat(engine, "alice", "strength", 10);
        SetDamage(engine, "alice", 5);

        // the aimed syntax is advertised on the label
        Assert.Equal("Attack Bob [in the {part}]",
            TestWorlds.Find(engine, "alice", "attack", "bob").Label);

        var hit = Attack(engine, "head");
        Assert.Equal("You hit Bob in the head for 5 damage.", hit.Message);
        Assert.Equal(3, PartHp(engine, "bob_head"));
        Assert.Equal(12, PartHp(engine, "bob_torso"));

        // an unknown part (typo, hallucination) fails without damage
        var miss = Attack(engine, "tail");
        Assert.Equal(ActionOutcome.Failure, miss.Outcome);
        Assert.Equal("Bob has no such part.", miss.Message);
        Assert.Equal(3, PartHp(engine, "bob_head"));
    }

    [Fact]
    public void Attack_Aimed_TakesThePartsPenalty()
    {
        var engine = NewEngine();
        AddPart(engine, "bob", "torso", "torso", "top", 12);
        // the penalty is per-part data on the bodypart module
        engine.World.SetFieldOverride("bob_torso", "bodypart", "aimedPenalty", Core.World.World.ToJson(6));
        SetStat(engine, "alice", "strength", 10);
        SetStat(engine, "bob", "agility", 7); // unaimed margin 3; aimed 3-6 = -3
        SetDamage(engine, "alice", 5);

        Assert.Equal(ActionOutcome.Success, Attack(engine).Outcome); // unaimed hits
        Assert.Equal(7, PartHp(engine, "bob_torso"));
        var aimed = Attack(engine, "torso");
        Assert.Equal(ActionOutcome.Failure, aimed.Outcome);
        Assert.Equal("You swing at Bob's torso and miss.", aimed.Message);
        Assert.Equal(7, PartHp(engine, "bob_torso"));
    }

    [Fact]
    public void Attack_Aimed_AmbiguousSideName_PicksRandomly()
    {
        var engine = NewEngine();
        AddPart(engine, "bob", "left_arm", "left arm", "top", 100);
        AddPart(engine, "bob", "right_arm", "right arm", "top", 100);
        SetStat(engine, "alice", "strength", 10);
        SetDamage(engine, "alice", 1);

        // "arm" matches both arms; over enough swings both sides get hit
        var hitLeft = false;
        var hitRight = false;
        for (var i = 0; i < 40 && (!hitLeft || !hitRight); i++)
        {
            var message = Attack(engine, "arm").Message;
            hitLeft |= message.Contains("left arm");
            hitRight |= message.Contains("right arm");
        }
        Assert.True(hitLeft && hitRight, "aimed 'arm' should hit both sides over time");
    }

    [Fact]
    public void Cripple_Disarm_DropsTheHeldWeapon()
    {
        var engine = NewEngine();
        AddPart(engine, "bob", "right_arm", "right arm", "top", 4, effects: ["disarm"]);
        engine.World.CreateObject("sword", "bob", "arming sword");
        engine.World.AddModule("sword", "wearable");
        engine.World.AddModule("sword", "weapon");
        engine.World.SetFieldOverride(
            "sword", "wearable", "regions", Core.World.World.ToJson(new List<string> { "held" }));
        engine.World.SetFieldOverride("sword", "wearable", "worn", Core.World.World.ToJson(true));
        SetStat(engine, "alice", "strength", 10);
        SetDamage(engine, "alice", 4);

        var hit = Attack(engine, "right arm");
        Assert.Contains("Bob's right arm is crippled!", hit.Message);
        Assert.Contains("Arming sword clatters to the ground.", hit.Message);
        Assert.Equal("room_a", engine.World.GetObject("sword").Parent);
        Assert.False(Clothing.IsWorn(engine.ModuleRegistry, engine.World.GetObject("sword")));
    }

    [Fact]
    public void Cripple_Leg_KnocksProne_AndBlocksStanding()
    {
        var engine = NewEngine();
        AddPart(engine, "bob", "left_leg", "left leg", "bottom", 4, effects: ["prone", "no_stand"]);
        SetStat(engine, "alice", "strength", 10);
        SetDamage(engine, "alice", 4);

        var hit = Attack(engine, "left leg");
        Assert.Contains("topples to the ground", hit.Message);
        var bob = engine.World.GetObject("bob");
        Assert.Equal(Postures.Prone, Postures.Of(engine.World, engine.ModuleRegistry, bob));

        var stand = engine.TurnManager.Execute(bob, "stand");
        Assert.Equal(ActionOutcome.Failure, stand.Outcome);
        Assert.Equal("You can't stand — your left leg is crippled.", stand.Message);
        Assert.Equal(Postures.Prone, Postures.Of(engine.World, engine.ModuleRegistry, bob));
    }

    [Fact]
    public void Cripple_VictimFeelsIt()
    {
        var engine = NewEngine();
        AddPart(engine, "bob", "head", "head", "head", 5);
        SetStat(engine, "alice", "strength", 10);
        SetDamage(engine, "alice", 5);

        Assert.True(Attack(engine, "head").Success);

        var felt = engine.SignalBus.Drain("bob").Select(s => s.Text).ToList();
        Assert.Contains("Alice hits you in the head for 5 damage.", felt);
        Assert.Contains("Your head is crippled!", felt);
    }

    [Fact]
    public void VitalPart_AtZero_Incapacitates_ReportedOnce()
    {
        var engine = NewEngine();
        AddPart(engine, "bob", "head", "head", "head", 5, vital: true);
        SetStat(engine, "alice", "strength", 10);
        SetDamage(engine, "alice", 5);

        var hit = Attack(engine, "head");
        Assert.Contains("Bob collapses, incapacitated!", hit.Message);
        Assert.True(Health.IsIncapacitated(
            engine.World, engine.ModuleRegistry, engine.World.GetObject("bob")));
        // an incapacitated agent can only look
        Assert.All(engine.ActionResolver.Resolve(engine.World.GetObject("bob")),
            a => Assert.Equal("look", a.Verb));

        // further blows don't repeat the collapse announcement — but they
        // do say the target is already down
        var later = Attack(engine, "head");
        Assert.DoesNotContain("collapses", later.Message);
        Assert.Contains("is already incapacitated", later.Message);
    }

    [Fact]
    public void ShockThreshold_IncapacitatesOnCumulativeNonVitalDamage()
    {
        var engine = NewEngine();
        engine.World.SetFieldOverride("rules", "rules", "shockThreshold", Core.World.World.ToJson(50));
        AddPart(engine, "bob", "left_arm", "left arm", "top", 10);
        AddPart(engine, "bob", "right_arm", "right arm", "top", 10);
        SetStat(engine, "alice", "strength", 10);
        SetDamage(engine, "alice", 5);
        var bob = engine.World.GetObject("bob");

        Attack(engine, "left arm"); // 5/20 = 25%: conscious
        Assert.False(Health.IsIncapacitated(engine.World, engine.ModuleRegistry, bob));
        Attack(engine, "right arm"); // 10/20 = 50%: shock
        Assert.True(Health.IsIncapacitated(engine.World, engine.ModuleRegistry, bob));
    }

    [Fact]
    public void Crunch_Numeric_ReportsNumbersAndFractions()
    {
        var engine = NewEngine();
        AddPart(engine, "bob", "head", "head", "head", 5);
        SetStat(engine, "alice", "strength", 10);
        SetDamage(engine, "alice", 2);

        Assert.Equal("You hit Bob in the head for 2 damage.", Attack(engine, "head").Message);
        var examine = engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "examine", "bob"));
        Assert.Contains("Health: head 3/5.", examine.Message);
        var inventory = engine.TurnManager.PerformAction(
            engine.World.GetObject("bob"), TestWorlds.Find(engine, "bob", "inventory"));
        Assert.Contains("Health: head 3/5.", inventory.Message); // self gets the per-part detail too
        // numeric mode keeps the room listing clean
        var look = engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "look"));
        Assert.DoesNotContain("wounded", look.Message);
    }

    [Fact]
    public void Crunch_Descriptive_ReportsBandsAndConditions()
    {
        var engine = NewEngine();
        AddPart(engine, "bob", "head", "head", "head", 5);
        AddPart(engine, "bob", "torso", "torso", "top", 10);
        SetStat(engine, "alice", "strength", 10);
        SetDamage(engine, "alice", 5);
        engine.World.SetFieldOverride(
            "rules", "rules", "crunch", Core.World.World.ToJson("descriptive"));

        // 5/5 = 100% of the part: a severe blow
        Assert.Contains("You land a severe blow on Bob's head.", Attack(engine, "head").Message);
        // custom band tables override the defaults (5/10 = 50% → light)
        engine.World.SetFieldOverride("rules", "rules", "blowBands",
            Core.World.World.ToJson(new Dictionary<string, int> { ["light"] = 50, ["heavy"] = 100 }));
        Assert.Equal("You land a light blow on Bob's torso.", Attack(engine, "torso").Message);

        // 5/15 total remaining (33%): wounded
        var look = engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "look"));
        Assert.Contains("Bob (wounded)", look.Message);

        // finish the torso: 0/15 — severely wounded, both parts crippled
        Assert.Contains("light blow", Attack(engine, "torso").Message);
        look = engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "look"));
        Assert.Contains("Bob (severely wounded)", look.Message);
        var examine = engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "examine", "bob"));
        Assert.Contains("Bob is severely wounded.", examine.Message);
        Assert.Contains("Bob's head is crippled.", examine.Message);
        Assert.Contains("Bob's torso is crippled.", examine.Message);
        var inventory = engine.TurnManager.PerformAction(
            engine.World.GetObject("bob"), TestWorlds.Find(engine, "bob", "inventory"));
        Assert.Contains("You are severely wounded.", inventory.Message);
        Assert.Contains("Your head is crippled.", inventory.Message);
    }

    [Fact]
    public void Choke_CrushesTheChokeablePart()
    {
        var engine = NewEngine();
        AddPart(engine, "bob", "head", "head", "head", 6);
        AddPart(engine, "bob", "torso", "torso", "top", 10);
        engine.World.AddModule("bob", "chokeable"); // part: "head"
        engine.World.MoveObject("bob", "alice");    // held in the grapple
        SetDamage(engine, "alice", 3);

        var result = engine.TurnManager.Execute(engine.World.GetObject("alice"), "choke", "bob");
        Assert.Equal("You choke Bob for 3 damage.", result.Message);
        Assert.Equal(3, PartHp(engine, "bob_head"));
        Assert.Equal(10, PartHp(engine, "bob_torso"));
    }

    [Fact]
    public void Monolithic_TargetsWithoutParts_BehaveAsBefore()
    {
        var engine = NewEngine();
        SetStat(engine, "alice", "strength", 10);
        SetDamage(engine, "alice", 6);

        // a training dummy: attackable + monolithic health, no parts
        engine.World.CreateObject("dummy", "room_a", "training dummy");
        engine.World.AddModule("dummy", "attackable");
        engine.World.AddModule("dummy", "health");
        engine.World.SetFieldOverride("dummy", "health", "maxHp", Core.World.World.ToJson(15));
        engine.World.SetFieldOverride("dummy", "health", "hp", Core.World.World.ToJson(15));
        var dummyHit = engine.TurnManager.PerformAction(engine.World.GetObject("alice"),
            TestWorlds.Find(engine, "alice", "attack", "dummy"));
        Assert.Equal("You hit the training dummy for 6 damage.", dummyHit.Message);
        Assert.Equal(9, engine.ModuleRegistry.ResolveInt(
            engine.World.GetObject("dummy"), "health", "hp"));

        // a part-less agent: the flat armor sum applies, the global pool
        // drives incapacitation
        engine.World.AddModule("bob", "health");
        AddWornArmor(engine, "bob", 2, "top");
        var hit = Attack(engine);
        Assert.Equal("You hit Bob for 4 damage.", hit.Message);
        SetDamage(engine, "alice", 10);
        hit = Attack(engine);
        Assert.Contains("Bob collapses, incapacitated!", hit.Message);
        Assert.True(Health.IsIncapacitated(
            engine.World, engine.ModuleRegistry, engine.World.GetObject("bob")));

        // choke against a part-less victim uses the global pool too
        var engine2 = NewEngine();
        engine2.World.AddModule("bob", "health");
        engine2.World.AddModule("bob", "chokeable");
        engine2.World.MoveObject("bob", "alice");
        SetDamage(engine2, "alice", 3);
        var choke = engine2.TurnManager.Execute(engine2.World.GetObject("alice"), "choke", "bob");
        Assert.Equal("You choke Bob for 3 damage.", choke.Message);
        Assert.Equal(7, engine2.ModuleRegistry.ResolveInt(
            engine2.World.GetObject("bob"), "health", "hp"));
    }

    [Fact]
    public void PlanExecutor_ParsesAimedAttackLines()
    {
        var engine = NewEngine();
        AddPart(engine, "bob", "head", "head", "head", 8);
        var alice = engine.World.GetObject("alice");

        var aimed = PlanExecutor.MatchAvailableOrPotential(engine, alice, "Attack Bob in the head");
        Assert.NotNull(aimed);
        Assert.Equal("attack", aimed.Verb);
        Assert.Equal("head", aimed.Text);

        // the advertised label verbatim means unaimed (raw {part} placeholder)
        var verbatim = PlanExecutor.MatchAvailableOrPotential(
            engine, alice, "Attack Bob [in the {part}]");
        Assert.NotNull(verbatim);
        Assert.Null(verbatim.Text);

        // bracketed aimed form, and plain unaimed
        Assert.Equal("head", PlanExecutor.MatchAvailableOrPotential(
            engine, alice, "Attack Bob [in the head]")!.Text);
        Assert.Null(PlanExecutor.MatchAvailableOrPotential(engine, alice, "Attack Bob")!.Text);
    }
}
