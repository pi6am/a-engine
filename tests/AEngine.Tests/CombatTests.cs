using AEngine.Core.Actions;
using AEngine.Core.Runtime;

namespace AEngine.Tests;

/// <summary>
/// RPG stage 4: combat. Attack does its opposed roll in-handler (the
/// attacker's bonus depends on the wielded weapon); damage is
/// N + n d m minus the defender's worn armor; enough damage incapacitates.
/// </summary>
public class CombatTests
{
    private const string CombatModulesJson = """
    [
      {
        "id": "rules", "name": "Rules",
        "fields": [
          { "name": "diceCount", "type": "int", "default": 0 },
          { "name": "diceSides", "type": "int", "default": 0 }
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
        "id": "body", "name": "Body",
        "fields": [ { "name": "regions", "type": "list", "default": ["top", "hand"] } ],
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
      }
    ]
    """;

    private static GameEngine NewEngine()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.ModuleRegistry.LoadJson(CombatModulesJson);
        engine.World.CreateObject("rules", Core.World.World.RootId, "rules");
        engine.World.AddModule("rules", "rules"); // 0d0: deterministic checks
        foreach (var id in new[] { "alice", "bob" })
        {
            engine.World.AddModule(id, "stats");
            engine.World.AddModule(id, "skills");
            engine.World.AddModule(id, "health");
            engine.World.AddModule(id, "body");
            engine.World.AddModule(id, "combatant");
            engine.World.AddModule(id, "attackable");
        }
        engine.World.MoveObject("bob", "room_a");
        return engine;
    }

    private static void SetStat(GameEngine engine, string id, string stat, int value) =>
        Stats.Set(engine.World, engine.ModuleRegistry, engine.World.GetObject(id), "stats", stat, value);

    private static void AddSword(GameEngine engine, string holderId, int damageBonus = 5)
    {
        engine.World.CreateObject("sword", holderId, "sword");
        engine.World.AddModule("sword", "wearable");
        engine.World.AddModule("sword", "weapon");
        engine.World.SetFieldOverride("sword", "wearable", "regions", Core.World.World.ToJson(new[] { "hand" }));
        engine.World.SetFieldOverride("sword", "weapon", "damageBonus", Core.World.World.ToJson(damageBonus));
        engine.World.SetFieldOverride("sword", "weapon", "damageDice", Core.World.World.ToJson(0));
    }

    [Fact]
    public void Attack_Hit_Unarmed_DealsUnarmedDamage()
    {
        var engine = NewEngine();
        SetStat(engine, "alice", "strength", 10);
        SetStat(engine, "bob", "agility", 5); // 10 vs 5: hit
        // 0d0 checks but unarmed damage is 1d2 — seed the roll
        engine.Random = new Random(0);
        var alice = engine.World.GetObject("alice");

        var result = engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "attack", "bob"));
        Assert.True(result.Success);
        Assert.Matches(@"You hit the Bob for [12] damage\.", result.Message);
    }

    [Fact]
    public void Attack_Hit_VictimSeesTheDamage()
    {
        var engine = NewEngine();
        SetStat(engine, "alice", "strength", 10);
        SetStat(engine, "bob", "agility", 5);
        AddSword(engine, "alice");
        Assert.True(engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "wear", "sword")).Success);

        Assert.True(engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "attack", "bob")).Success);

        // the victim hears the damage they took, observer-relative
        Assert.Contains(engine.SignalBus.Drain("bob"),
            s => s.Text == "Alice hits you for 5 damage.");
    }

    [Fact]
    public void Attack_VictimCondition_WarnsOnBandCrossings()
    {
        var engine = NewEngine();
        SetStat(engine, "alice", "strength", 10);
        SetStat(engine, "bob", "agility", 5);
        AddSword(engine, "alice");
        Assert.True(engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "wear", "sword")).Success);
        var attack = TestWorlds.Find(engine, "alice", "attack", "bob");

        // 10 -> 5 hp crosses into "wounded"; 5 -> 0 into "severely
        // wounded", and the knockout is felt too
        Assert.True(engine.TurnManager.PerformAction(engine.World.GetObject("alice"), attack).Success);
        Assert.True(engine.TurnManager.PerformAction(engine.World.GetObject("alice"), attack).Success);

        // the hit report precedes the wound reports it caused
        var felt = engine.SignalBus.Drain("bob").Select(s => s.Text).ToList();
        Assert.Equal(
            [
                "Alice hits you for 5 damage.",
                "You are wounded.",
                "Alice hits you for 5 damage.",
                "You are severely wounded.",
                "You collapse, incapacitated!",
            ],
            felt);
    }

    [Fact]
    public void Attack_OnIncapacitatedTarget_SaysSoInTheReport()
    {
        var engine = NewEngine();
        SetStat(engine, "alice", "strength", 10);
        SetStat(engine, "bob", "agility", 5);
        AddSword(engine, "alice", damageBonus: 50); // one hit downs bob
        var alice = engine.World.GetObject("alice");
        Assert.True(engine.TurnManager.PerformAction(
            alice, TestWorlds.Find(engine, "alice", "wear", "sword")).Success);

        var down = engine.TurnManager.PerformAction(
            alice, TestWorlds.Find(engine, "alice", "attack", "bob"));
        Assert.Contains("Bob collapses, incapacitated!", down.Message);

        // further blows on the helpless target say so — the planner reading
        // its own outcome learns the fight is over
        var again = engine.TurnManager.PerformAction(
            alice, TestWorlds.Find(engine, "alice", "attack", "bob"));
        Assert.Matches(@"You hit the Bob( with the sword)? for \d+ damage\. The Bob is already incapacitated\.",
            again.Message);
        // and the knockout line doesn't repeat
        Assert.DoesNotContain("collapses", again.Message);
    }

    [Fact]
    public void Attack_Miss_Fails_AndIsObservable()
    {
        var engine = NewEngine();
        SetStat(engine, "alice", "strength", 3);
        SetStat(engine, "bob", "agility", 20); // 3 vs 20: miss
        var alice = engine.World.GetObject("alice");

        var result = engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "attack", "bob"));
        Assert.False(result.Success);
        Assert.Equal("You swing at the Bob and miss.", result.Message);
        Assert.Equal(10, engine.ModuleRegistry.ResolveInt(engine.World.GetObject("bob"), "health", "hp"));
        Assert.Contains(engine.SignalBus.Drain("bob"), s => s.Text == "Alice swings at you and misses.");
    }

    [Fact]
    public void Attack_WieldedWeapon_OnlyCountsWhenWorn()
    {
        var engine = NewEngine();
        SetStat(engine, "alice", "strength", 10);
        SetStat(engine, "bob", "agility", 5);
        AddSword(engine, "alice"); // in Alice's inventory, not worn
        var alice = engine.World.GetObject("alice");

        // held but not worn: the attack is unarmed
        var unarmed = engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "attack", "bob"));
        Assert.True(unarmed.Success);
        Assert.DoesNotContain("with the sword", unarmed.Message);
        var hpAfterUnarmed = engine.ModuleRegistry.ResolveInt(engine.World.GetObject("bob"), "health", "hp");

        // wield it: weapon damage (bonus 5, 0 dice) and the flavor text
        Assert.True(engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "wear", "sword")).Success);
        var armed = engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "attack", "bob"));
        Assert.True(armed.Success);
        Assert.Equal("You hit the Bob with the sword for 5 damage.", armed.Message);
        Assert.Equal(hpAfterUnarmed - 5,
            engine.ModuleRegistry.ResolveInt(engine.World.GetObject("bob"), "health", "hp"));
    }

    [Fact]
    public void Attack_Armor_ReducesDamage_ToAMinimumOfZero()
    {
        var engine = NewEngine();
        SetStat(engine, "alice", "strength", 10);
        SetStat(engine, "bob", "agility", 5);
        AddSword(engine, "alice", damageBonus: 2);
        engine.World.SetFieldOverride("sword", "wearable", "worn", Core.World.World.ToJson(true));

        engine.World.CreateObject("mail", "bob", "mail shirt");
        engine.World.AddModule("mail", "wearable");
        engine.World.AddModule("mail", "armor");
        engine.World.SetFieldOverride("mail", "armor", "protection", Core.World.World.ToJson(5));
        engine.World.SetFieldOverride("mail", "wearable", "worn", Core.World.World.ToJson(true));

        var result = engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "attack", "bob"));
        Assert.True(result.Success);
        Assert.Equal("You hit the Bob with the sword for 0 damage.", result.Message);
        Assert.Equal(10, engine.ModuleRegistry.ResolveInt(engine.World.GetObject("bob"), "health", "hp"));
    }

    [Fact]
    public void Attack_DefenderWearingNonArmorItem_DoesNotThrow()
    {
        var engine = NewEngine();
        SetStat(engine, "alice", "strength", 10);
        SetStat(engine, "bob", "agility", 5);
        AddSword(engine, "bob"); // bob's worn sword is not armor
        engine.World.SetFieldOverride("sword", "wearable", "worn", Core.World.World.ToJson(true));

        var result = engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "attack", "bob"));
        Assert.True(result.Success);
        Assert.Matches(@"You hit the Bob for [12] damage\.", result.Message);
    }

    [Fact]
    public void Attack_EnoughDamage_Incapacitates()
    {
        var engine = NewEngine();
        SetStat(engine, "alice", "strength", 10);
        SetStat(engine, "bob", "agility", 5);
        AddSword(engine, "alice", damageBonus: 50);
        engine.World.SetFieldOverride("sword", "wearable", "worn", Core.World.World.ToJson(true));

        var result = engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "attack", "bob"));
        Assert.True(result.Success);
        Assert.EndsWith("Bob collapses, incapacitated!", result.Message);
        Assert.True(Health.IsIncapacitated(engine.World, engine.ModuleRegistry, engine.World.GetObject("bob")));
    }

    [Fact]
    public void Attack_NonAgentTarget_IsAnAutomaticHit()
    {
        var engine = NewEngine();
        engine.World.CreateObject("dummy", "room_a", "training dummy");
        engine.World.AddModule("dummy", "attackable");
        engine.World.AddModule("dummy", "health");
        var alice = engine.World.GetObject("alice");

        // no stats at all: an undefended dummy is always hit
        var result = engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "attack", "dummy"));
        Assert.True(result.Success);
        Assert.Matches(@"You hit the training dummy for [12] damage\.", result.Message);
    }
}
