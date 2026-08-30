using AEngine.Core.Actions;
using AEngine.Core.Runtime;

namespace AEngine.Tests;

/// <summary>
/// Quick-time reactions: reaction-eligible actions telegraph and park
/// until the defender picks an option (UI/policy) or the deadline applies
/// the data-driven default. The chosen reaction replaces the defender's
/// side of the opposed check; NoResist accepts the action.
/// </summary>
public class ReactionsTests
{
    private const string ReactionModulesJson = """
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
        "id": "wearable", "name": "Wearable",
        "fields": [
          { "name": "regions", "type": "list", "default": [] },
          { "name": "worn", "type": "bool", "default": false }
        ],
        "affordances": []
      },
      {
        "id": "weapon", "name": "Weapon",
        "fields": [
          { "name": "damageBonus", "type": "int", "default": 0 },
          { "name": "damageDice", "type": "int", "default": 0 },
          { "name": "damageSides", "type": "int", "default": 4 }
        ],
        "affordances": []
      },
      {
        "id": "shield", "name": "Shield",
        "fields": [],
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
            "verb": "attack", "handler": "attack", "duration": 3,
            "signals": [
              { "sense": "visual", "priority": 10, "text": "{agent} hits the {target}!" }
            ],
            "failSignals": [
              { "sense": "visual", "priority": 10, "text": "{agent} swings at the {target} and misses." }
            ],
            "reaction": {
              "window": 3,
              "telegraph": "{agent} swings at the {target}!",
              "options": [
                { "id": "dodge", "label": "Dodge", "stat": "agility", "default": true, "text": "You dodge the blow.", "report": "{agent} attempts to dodge." },
                { "id": "block", "label": "Block", "stat": "strength", "bonus": 2, "requiresWornModule": "shield", "text": "You block with your shield.", "report": "{agent} attempts to block with their shield." },
                { "id": "parry", "label": "Parry", "stat": "agility", "requiresWornModule": "weapon", "text": "You parry the blow.", "report": "{agent} attempts to parry." },
                { "id": "accept", "label": "Take the hit", "noResist": true, "text": "You take the hit.", "report": "{agent} takes the hit." }
              ]
            }
          }
        ]
      },
      {
        "id": "grappleable", "name": "Grappleable",
        "fields": [],
        "affordances": [
          {
            "verb": "grapple", "handler": "grapple",
            "check": { "stat": "strength", "opposed": { "stat": "agility" } },
            "signals": [
              { "sense": "visual", "priority": 10, "text": "{agent} seizes the {target}!" }
            ],
            "reaction": {
              "window": 3,
              "telegraph": "{agent} lunges at the {target}!",
              "options": [
                { "id": "resist", "label": "Resist", "stat": "strength", "default": true, "text": "You resist the grapple." },
                { "id": "accept", "label": "Let them grab you", "noResist": true, "text": "You let them grab you." }
              ]
            }
          }
        ]
      },
      {
        "id": "huggable", "name": "Huggable",
        "fields": [],
        "affordances": [
          {
            "verb": "hug", "handler": "basic",
            "check": { "opposed": { "stat": "agility" } },
            "signals": [
              { "sense": "visual", "priority": 5, "text": "{agent} hugs the {target}." }
            ],
            "failSignals": [
              { "sense": "visual", "priority": 5, "text": "{target} pushes {agent} away." }
            ],
            "reaction": {
              "window": 3,
              "telegraph": "{agent} moves to hug the {target}!",
              "options": [
                { "id": "push_away", "label": "Push them away", "stat": "agility", "text": "You push them away." },
                { "id": "accept", "label": "Hug them back", "noResist": true, "default": true, "text": "You hug them back." }
              ]
            }
          }
        ]
      }
    ]
    """;

    // diceless rules (0d0): checks resolve as bonus vs bonus
    private static GameEngine NewEngine()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.ModuleRegistry.LoadJson(ReactionModulesJson);
        engine.World.CreateObject("rules", Core.World.World.RootId, "rules");
        engine.World.AddModule("rules", "rules");
        foreach (var id in new[] { "alice", "bob" })
        {
            engine.World.AddModule(id, "stats");
            engine.World.AddModule(id, "skills");
            engine.World.AddModule(id, "health");
            engine.World.AddModule(id, "combatant");
            engine.World.AddModule(id, "attackable");
            engine.World.AddModule(id, "grappleable");
            engine.World.AddModule(id, "huggable");
        }
        engine.World.MoveObject("bob", "room_a"); // same room as Alice
        // Bob reacts deliberately (TestWorlds gives him the random policy,
        // which would answer reactions synchronously at park time)
        engine.World.SetFieldOverride("bob", "agent", "policy", Core.World.World.ToJson("player"));
        return engine;
    }

    private static void SetStat(GameEngine engine, string id, string stat, int value) =>
        Stats.Set(engine.World, engine.ModuleRegistry, engine.World.GetObject(id), "stats", stat, value);

    private static void AddWornItem(GameEngine engine, string id, string module, string holderId)
    {
        engine.World.CreateObject(id, holderId, id);
        engine.World.AddModule(id, "wearable");
        engine.World.AddModule(id, module);
        engine.World.SetFieldOverride(id, "wearable", "worn", Core.World.World.ToJson(true));
    }

    private static void ExpireReactions(GameEngine engine)
    {
        while (engine.Reactions.Pending.Count > 0)
            engine.TurnManager.Tick();
    }

    [Fact]
    public void Attack_Telegraphs_Parks_AndTheDeadlineAppliesTheDefault()
    {
        var engine = NewEngine();
        SetStat(engine, "alice", "strength", 10);
        SetStat(engine, "bob", "agility", 20); // dodge (the default) beats 10

        var result = engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "attack", "bob"));
        Assert.Equal("You attack the Bob.", result.Message);

        // parked: no damage yet, the defender has a pending reaction, the
        // actor is already committed (busy), the telegraph is observable
        var pending = Assert.Single(engine.Reactions.Pending);
        Assert.Equal("bob", pending.DefenderId);
        Assert.Equal("Alice swings at you!", pending.Announcement);
        Assert.True(engine.TurnManager.BusyUntilTurn("alice") > engine.TurnManager.Turn);
        Assert.Equal(10, engine.ModuleRegistry.ResolveInt(engine.World.GetObject("bob"), "health", "hp"));
        Assert.Contains(engine.SignalBus.Drain("bob"), s => s.Text == "Alice swings at the Bob!");
        // the shield/weapon-gated options are filtered out for Bob
        Assert.Equal(new[] { "dodge", "accept" }, pending.Options.Select(o => o.Id).ToArray());

        // the deadline applies the default: Bob dodges, the attack misses
        ExpireReactions(engine);
        Assert.Empty(engine.Reactions.Pending);
        Assert.Equal(10, engine.ModuleRegistry.ResolveInt(engine.World.GetObject("bob"), "health", "hp"));
        Assert.Contains(engine.SignalBus.Drain("bob"),
            s => s.Text == "Alice swings at the Bob and misses.");
        Assert.Contains(engine.Memory.Recall("bob"), m => m.Contains("You dodge the blow."));
        // the actor isn't an observer of their own signals — the outcome
        // is reported separately so the UI can show it to them
        Assert.Contains(engine.Reactions.DrainResolved(),
            r => r.ActorId == "alice" && r.Message == "You swing at the Bob and miss.");
    }

    [Fact]
    public void Reaction_Report_IsShownToTheActor_AheadOfTheOutcome()
    {
        var engine = NewEngine();
        SetStat(engine, "alice", "strength", 10);
        SetStat(engine, "bob", "agility", 20); // dodge beats 10

        engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "attack", "bob"));
        var pending = Assert.Single(engine.Reactions.Pending);
        engine.Reactions.Choose(pending.Id, "dodge");

        // the actor sees the choice first, then how it landed
        var messages = engine.Reactions.DrainResolved()
            .Where(r => r.ActorId == "alice").Select(r => r.Message).ToArray();
        Assert.Equal(
            new[] { "The Bob attempts to dodge.", "You swing at the Bob and miss." },
            messages);
        // and the actor's own memory keeps the choice for later context
        var memory = engine.Memory.Recall("alice");
        Assert.Contains(memory, m => m == "The Bob attempts to dodge.");
    }

    [Fact]
    public void Reaction_WithoutReport_StaysQuiet()
    {
        var engine = NewEngine();
        SetStat(engine, "alice", "strength", 10);
        SetStat(engine, "bob", "agility", 20); // resisting would hold — but Bob accepts
        // the grapple options in the fixture carry no report text

        engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "grapple", "bob"));
        var pending = Assert.Single(engine.Reactions.Pending);
        engine.Reactions.Choose(pending.Id, "accept");
        // only the outcome itself is reported — no extra reaction line
        var messages = engine.Reactions.DrainResolved()
            .Where(r => r.ActorId == "alice").ToArray();
        Assert.Single(messages);
    }

    [Fact]
    public void Reaction_ActorIncapacitatedMidWindow_Fizzles()
    {
        var engine = NewEngine();
        SetStat(engine, "alice", "strength", 10);
        SetStat(engine, "bob", "agility", 20);

        engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "attack", "bob"));
        var pending = Assert.Single(engine.Reactions.Pending);

        // Alice is knocked out before Bob's reaction lands; even though Bob
        // takes the hit, Alice is in no condition to follow through
        Damage.Apply(engine.World, engine.ModuleRegistry, engine.World.GetObject("alice"), 50);
        engine.Reactions.Choose(pending.Id, "accept");

        Assert.Equal(10, engine.ModuleRegistry.ResolveInt(
            engine.World.GetObject("bob"), "health", "hp")); // untouched
        Assert.Contains(engine.Reactions.DrainResolved(),
            r => r.ActorId == "alice" && r.Message == "The moment passes.");
        Assert.Contains(engine.Memory.Recall("alice"), m => m == "The moment passes.");
    }

    [Fact]
    public void Attack_ChooseAccept_AutoHits()
    {
        var engine = NewEngine();
        SetStat(engine, "alice", "strength", 10);
        SetStat(engine, "bob", "agility", 20); // would dodge — but Bob accepts
        engine.World.SetFieldOverride("alice", "combatant", "damageDice", Core.World.World.ToJson(0));
        engine.World.SetFieldOverride("alice", "combatant", "damageBonus", Core.World.World.ToJson(4));

        engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "attack", "bob"));
        var pending = Assert.Single(engine.Reactions.Pending);
        Assert.True(engine.Reactions.Choose(pending.Id, "accept"));
        Assert.Empty(engine.Reactions.Pending);
        Assert.Equal(6, engine.ModuleRegistry.ResolveInt(
            engine.World.GetObject("bob"), "health", "hp")); // 0d2 + 4, unopposed
        Assert.Contains(engine.Memory.Recall("bob"), m => m.Contains("You take the hit."));
    }

    [Fact]
    public void Attack_Block_IsOnlyOfferedWithAShield_AndItsBonusApplies()
    {
        var engine = NewEngine();
        SetStat(engine, "alice", "strength", 10);
        SetStat(engine, "bob", "strength", 7); // 7+2 shield bonus vs 10: fails without... 9 < 10

        engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "attack", "bob"));
        var pending = Assert.Single(engine.Reactions.Pending);
        Assert.DoesNotContain(pending.Options, o => o.Id == "block");

        // give Bob a worn shield mid-window? No — options are filtered at
        // park time; a fresh attack offers block
        engine.Reactions.Choose(pending.Id, "dodge");
        AddWornItem(engine, "roundshield", "shield", "bob");

        engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "attack", "bob"));
        pending = Assert.Single(engine.Reactions.Pending);
        Assert.Contains(pending.Options, o => o.Id == "block");
        engine.Reactions.Choose(pending.Id, "block"); // 7+2=9 vs 10: the hit lands
        Assert.Contains(engine.Memory.Recall("bob"), m => m.Contains("You block with your shield."));
    }

    [Fact]
    public void Attack_WindowZero_OrNoChoice_ResolvesImmediately()
    {
        var engine = NewEngine();
        SetStat(engine, "alice", "strength", 10);
        SetStat(engine, "bob", "agility", 20);
        engine.ModuleRegistry.LoadJson("""
        [
          {
            "id": "attackable", "name": "Attackable",
            "fields": [],
            "affordances": [
              {
                "verb": "attack", "handler": "attack",
                "reaction": { "window": 0, "options": [
                  { "id": "dodge", "label": "Dodge", "default": true },
                  { "id": "accept", "label": "Accept", "noResist": true }
                ] }
              }
            ]
          }
        ]
        """);
        // window 0: too fast to react — a plain opposed attack
        var result = engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "attack", "bob"));
        Assert.Equal("You swing at the Bob and miss.", result.Message);
        Assert.Empty(engine.Reactions.Pending);
    }

    [Fact]
    public void Attack_IncapacitatedDefender_GetsNoReaction()
    {
        var engine = NewEngine();
        SetStat(engine, "alice", "strength", 10);
        Damage.Apply(engine.World, engine.ModuleRegistry, engine.World.GetObject("bob"), 50);
        engine.World.SetFieldOverride("alice", "combatant", "damageDice", Core.World.World.ToJson(0));

        var result = engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "attack", "bob"));
        Assert.StartsWith("You hit the Bob", result.Message);
        Assert.Empty(engine.Reactions.Pending);
    }

    [Fact]
    public void Grapple_ChooseAccept_BeatsTheGateCheck()
    {
        var engine = NewEngine();
        SetStat(engine, "alice", "strength", 3);
        SetStat(engine, "bob", "agility", 20); // resisting would hold — but Bob accepts

        engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "grapple", "bob"));
        var pending = Assert.Single(engine.Reactions.Pending);
        engine.Reactions.Choose(pending.Id, "accept");
        Assert.Equal("alice", engine.World.GetObject("bob").Parent); // seized
    }

    [Fact]
    public void Hug_PositiveAction_DefaultsToAccept()
    {
        var engine = NewEngine();
        SetStat(engine, "alice", "strength", 10);
        SetStat(engine, "bob", "agility", 20);

        engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "hug", "bob"));
        Assert.Single(engine.Reactions.Pending);
        ExpireReactions(engine);
        Assert.Contains(engine.SignalBus.Drain("bob"), s => s.Text == "Alice hugs the Bob.");
        Assert.Contains(engine.Memory.Recall("bob"), m => m.Contains("You hug them back."));
    }

    [Fact]
    public void Reaction_RemembersTheLastChoice_AsTheEffectiveDefault()
    {
        var engine = NewEngine();
        SetStat(engine, "alice", "strength", 10);
        SetStat(engine, "bob", "agility", 20); // the configured default (dodge) beats 10
        engine.World.SetFieldOverride("alice", "combatant", "damageDice", Core.World.World.ToJson(0));
        engine.World.SetFieldOverride("alice", "combatant", "damageBonus", Core.World.World.ToJson(4));
        var bob = engine.World.GetObject("bob");

        // Bob explicitly takes the hit once...
        engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "attack", "bob"));
        var pending = Assert.Single(engine.Reactions.Pending);
        Assert.Equal("dodge", engine.Reactions.EffectiveDefault(pending).Id); // configured
        engine.Reactions.Choose(pending.Id, "accept");
        Assert.Equal(6, engine.ModuleRegistry.ResolveInt(bob, "health", "hp"));

        // ...and the next attack's deadline default is accept, not dodge
        engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "attack", "bob"));
        pending = Assert.Single(engine.Reactions.Pending);
        Assert.Equal("accept", engine.Reactions.EffectiveDefault(pending).Id);
        ExpireReactions(engine);
        Assert.Equal(2, engine.ModuleRegistry.ResolveInt(bob, "health", "hp"));
    }

    [Fact]
    public void Reaction_RememberedChoiceUnavailable_FallsBackToTheConfiguredDefault()
    {
        var engine = NewEngine();
        SetStat(engine, "alice", "strength", 10);
        SetStat(engine, "bob", "agility", 20); // dodge beats 10
        engine.World.SetFieldOverride("alice", "combatant", "damageDice", Core.World.World.ToJson(0));
        engine.World.SetFieldOverride("alice", "combatant", "damageBonus", Core.World.World.ToJson(4));
        var bob = engine.World.GetObject("bob");

        // Bob blocks with a shield once (remembered)...
        AddWornItem(engine, "roundshield", "shield", "bob");
        engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "attack", "bob"));
        var pending = Assert.Single(engine.Reactions.Pending);
        engine.Reactions.Choose(pending.Id, "block"); // blocked: 0+2 vs 10 — the hit lands
        Assert.Equal(6, engine.ModuleRegistry.ResolveInt(bob, "health", "hp"));

        // ...but with the shield gone, the deadline falls back to dodge
        engine.World.SetFieldOverride("roundshield", "wearable", "worn", Core.World.World.ToJson(false));
        engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "attack", "bob"));
        pending = Assert.Single(engine.Reactions.Pending);
        Assert.Equal("dodge", engine.Reactions.EffectiveDefault(pending).Id);
        ExpireReactions(engine);
        Assert.Equal(6, engine.ModuleRegistry.ResolveInt(bob, "health", "hp")); // dodged
    }

    [Fact]
    public void NpcDefender_RandomPolicy_ResolvesImmediately()
    {
        var engine = NewEngine();
        engine.Random = new Random(0);
        SetStat(engine, "alice", "strength", 10);
        engine.World.SetFieldOverride("bob", "agent", "policy", Core.World.World.ToJson("random"));
        engine.World.SetFieldOverride("alice", "combatant", "damageDice", Core.World.World.ToJson(0));
        engine.World.SetFieldOverride("alice", "combatant", "damageBonus", Core.World.World.ToJson(3));

        // Bob's options are dodge (agility 0 → loses to 10) and accept —
        // either way the hit lands, synchronously with the park
        engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "attack", "bob"));
        Assert.Empty(engine.Reactions.Pending);
        Assert.Equal(7, engine.ModuleRegistry.ResolveInt(engine.World.GetObject("bob"), "health", "hp"));
    }
}
