using AEngine.Core.Actions;
using AEngine.Core.Runtime;

namespace AEngine.Tests;

/// <summary>
/// RPG stage 5: grappling. Grapple (opposed, gated on the affordance)
/// hauls the victim into forced carrying — the carried-posture rules
/// restrict them; the grappler gets release/choke on their victim, the
/// victim gets escape (opposed by the carrier, rolled in-handler). Choke
/// is a no-roll unarmed attack that ignores armor.
/// </summary>
public class GrappleTests
{
    private const string GrappleModulesJson = """
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
        "id": "grappleable", "name": "Grappleable",
        "fields": [],
        "affordances": [
          {
            "verb": "grapple", "handler": "grapple",
            "check": { "stat": "strength", "skill": "brawling", "opposed": { "stat": "agility" } },
            "signals": [
              { "sense": "visual", "priority": 10, "text": "{agent} seizes the {target}!" }
            ],
            "failSignals": [
              { "sense": "visual", "priority": 10, "text": "{agent} grabs at the {target}, who slips free." }
            ]
          },
          {
            "verb": "release", "handler": "release",
            "signals": [
              { "sense": "visual", "priority": 5, "text": "{agent} releases the {target}." }
            ]
          }
        ]
      },
      {
        "id": "grappler", "name": "Grappler",
        "fields": [],
        "affordances": [
          {
            "verb": "escape", "handler": "escape",
            "signals": [
              { "sense": "visual", "priority": 10, "text": "{agent} breaks free!" }
            ],
            "failSignals": [
              { "sense": "visual", "priority": 10, "text": "{agent} struggles." }
            ]
          }
        ]
      },
      {
        "id": "chokeable", "name": "Chokeable",
        "fields": [],
        "affordances": [
          {
            "verb": "choke", "handler": "choke",
            "signals": [
              { "sense": "visual", "priority": 10, "text": "{agent} chokes the {target}!" }
            ]
          }
        ]
      }
    ]
    """;

    // diceless rules (0d0): opposed checks resolve as bonus vs bonus
    private static GameEngine NewEngine()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.ModuleRegistry.LoadJson(GrappleModulesJson);
        engine.World.CreateObject("rules", Core.World.World.RootId, "rules");
        engine.World.AddModule("rules", "rules");
        foreach (var id in new[] { "alice", "bob" })
        {
            engine.World.AddModule(id, "stats");
            engine.World.AddModule(id, "skills");
            engine.World.AddModule(id, "health");
            engine.World.AddModule(id, "combatant");
            engine.World.AddModule(id, "grappleable");
            engine.World.AddModule(id, "grappler");
            engine.World.AddModule(id, "chokeable");
        }
        engine.World.MoveObject("bob", "room_a"); // same room as Alice
        return engine;
    }

    private static void SetStat(GameEngine engine, string id, string stat, int value) =>
        Stats.Set(engine.World, engine.ModuleRegistry, engine.World.GetObject(id), "stats", stat, value);

    private static void GrappleBob(GameEngine engine)
    {
        SetStat(engine, "alice", "strength", 14);
        SetStat(engine, "bob", "agility", 5); // 14 vs 5: the grapple lands
        var result = engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "grapple", "bob"));
        Assert.True(result.Success);
    }

    [Fact]
    public void Grapple_Success_ForcesCarrying_AndRestrictsTheVictim()
    {
        var engine = NewEngine();
        GrappleBob(engine);
        var bob = engine.World.GetObject("bob");
        Assert.Equal("alice", bob.Parent);
        Assert.Equal(Postures.Carried, Postures.Of(engine.World, engine.ModuleRegistry, bob));

        // the grappled victim keeps only their own verbs plus escape
        var victimActions = engine.ActionResolver.Resolve(bob);
        Assert.DoesNotContain(victimActions, a => a.Verb == "go" || a.Verb == "take");
        var escape = Assert.Single(victimActions, a => a.Verb == "escape");
        Assert.Equal("Break free", escape.Label);

        // and the victim still shows in the grappler's own look
        var look = engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "look"));
        Assert.Contains("Bob (carried by you)", look.Message);

        // the grappler gets release and choke on their victim, not grapple
        var grapplerActions = engine.ActionResolver.Resolve(engine.World.GetObject("alice"));
        Assert.Contains(grapplerActions, a => a.Verb == "release" && a.TargetId == "bob");
        Assert.Contains(grapplerActions, a => a.Verb == "choke" && a.TargetId == "bob");
        Assert.DoesNotContain(grapplerActions, a => a.Verb == "grapple" && a.TargetId == "bob");
    }

    [Fact]
    public void Grapple_Failure_IsObservable()
    {
        var engine = NewEngine();
        SetStat(engine, "alice", "strength", 3);
        SetStat(engine, "bob", "agility", 20); // 3 vs 20: Bob slips free

        var result = engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "grapple", "bob"));
        Assert.False(result.Success);
        Assert.Equal("room_a", engine.World.GetObject("bob").Parent);
        Assert.Contains(engine.SignalBus.Drain("bob"),
            s => s.Text == "Alice grabs at the Bob, who slips free.");
    }

    [Fact]
    public void Escape_BreaksFree_WhenTheCheckPasses()
    {
        var engine = NewEngine();
        GrappleBob(engine);
        SetStat(engine, "bob", "strength", 12);
        SetStat(engine, "alice", "agility", 5); // 12 vs 5: Bob breaks out
        var bob = engine.World.GetObject("bob");

        var result = engine.TurnManager.PerformAction(bob, TestWorlds.Find(engine, "bob", "escape"));
        Assert.True(result.Success);
        Assert.Equal("You break free of Alice.", result.Message);
        Assert.Equal("room_a", bob.Parent);
        Assert.Contains(engine.SignalBus.Drain("alice"), s => s.Text == "Bob breaks free!");
    }

    [Fact]
    public void Escape_CanFail()
    {
        var engine = NewEngine();
        GrappleBob(engine);
        SetStat(engine, "bob", "strength", 3);
        SetStat(engine, "alice", "agility", 20); // 3 vs 20: held fast
        var bob = engine.World.GetObject("bob");

        var result = engine.TurnManager.PerformAction(bob, TestWorlds.Find(engine, "bob", "escape"));
        Assert.False(result.Success);
        Assert.Equal("You struggle against Alice, but can't break free.", result.Message);
        Assert.Equal("alice", bob.Parent);
        Assert.Contains(engine.SignalBus.Drain("alice"), s => s.Text == "Bob struggles.");
    }

    [Fact]
    public void Escape_FromAnIncapacitatedCarrier_AutoSucceeds()
    {
        var engine = NewEngine();
        GrappleBob(engine);
        Damage.Apply(engine.World, engine.ModuleRegistry, engine.World.GetObject("alice"), 99);
        SetStat(engine, "alice", "agility", 20); // incapacitated: no resistance

        var result = engine.TurnManager.PerformAction(
            engine.World.GetObject("bob"), TestWorlds.Find(engine, "bob", "escape"));
        Assert.True(result.Success);
        Assert.Equal("room_a", engine.World.GetObject("bob").Parent);
    }

    [Fact]
    public void Choke_IsANoRollUnarmedAttack_ThatIgnoresArmor()
    {
        var engine = NewEngine();
        GrappleBob(engine);
        engine.World.SetFieldOverride("alice", "combatant", "damageBonus", Core.World.World.ToJson(3));
        engine.World.SetFieldOverride("alice", "combatant", "damageDice", Core.World.World.ToJson(0));
        var bob = engine.World.GetObject("bob");

        var result = engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "choke", "bob"));
        Assert.True(result.Success);
        Assert.Equal("You choke the Bob for 3 damage.", result.Message);
        Assert.Equal(7, engine.ModuleRegistry.ResolveInt(bob, "health", "hp"));
    }

    [Fact]
    public void Choke_EnoughDamage_Incapacitates_WithoutDroppingTheVictim()
    {
        var engine = NewEngine();
        GrappleBob(engine);
        engine.World.SetFieldOverride("alice", "combatant", "damageBonus", Core.World.World.ToJson(50));
        engine.World.SetFieldOverride("alice", "combatant", "damageDice", Core.World.World.ToJson(0));
        var bob = engine.World.GetObject("bob");

        var result = engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "choke", "bob"));
        Assert.True(result.Success);
        Assert.EndsWith("Bob is incapacitated!", result.Message);
        // a carried victim stays in the grappler's grasp (not knocked prone)
        Assert.Equal("alice", bob.Parent);
        Assert.True(Health.IsIncapacitated(engine.World, engine.ModuleRegistry, bob));
    }

    [Fact]
    public void Release_SetsTheVictimDown_Standing()
    {
        var engine = NewEngine();
        GrappleBob(engine);
        var bob = engine.World.GetObject("bob");

        var result = engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "release", "bob"));
        Assert.True(result.Success);
        Assert.Equal("You release the Bob.", result.Message);
        Assert.Equal("room_a", bob.Parent);
        Assert.Equal(Postures.Standing, Postures.Of(engine.World, engine.ModuleRegistry, bob));
        // free again: no escape, and grapple is offered once more
        Assert.DoesNotContain(engine.ActionResolver.Resolve(bob), a => a.Verb == "escape");
        Assert.Contains(engine.ActionResolver.Resolve(engine.World.GetObject("alice")),
            a => a.Verb == "grapple" && a.TargetId == "bob");
    }

    [Fact]
    public void Escape_NotOffered_WhenNotCarried()
    {
        var engine = NewEngine();
        Assert.DoesNotContain(engine.ActionResolver.Resolve(engine.World.GetObject("alice")),
            a => a.Verb == "escape");
    }
}
