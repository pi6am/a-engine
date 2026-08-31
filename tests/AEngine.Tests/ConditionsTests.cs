using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.World;
using CoreWorld = AEngine.Core.World.World;

namespace AEngine.Tests;

public class ConditionsTests
{
    private const string ModulesJson = """
    [
      {
        "id": "condition", "name": "Condition",
        "fields": [
          { "name": "kind", "type": "string", "default": "" },
          { "name": "label", "type": "string", "default": "" },
          { "name": "visible", "type": "bool", "default": true },
          { "name": "selfText", "type": "string", "default": "" },
          { "name": "traits", "type": "string", "default": "" },
          { "name": "goals", "type": "string", "default": "" },
          { "name": "statMods", "type": "map", "default": {} }
        ],
        "affordances": []
      },
      {
        "id": "stats", "name": "Stats",
        "fields": [ { "name": "values", "type": "map", "default": {} } ],
        "affordances": []
      }
    ]
    """;

    private static GameEngine NewEngine()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.ModuleRegistry.LoadJson(ModulesJson);
        var world = engine.World;

        void Template(string id, string kind, bool visible, string? selfText,
            string? traits, object? statMods)
        {
            world.CreateObject(id, CoreWorld.RootId, $"{kind} template");
            world.AddModule(id, "condition");
            world.SetFieldOverride(id, "condition", "kind", CoreWorld.ToJson(kind));
            if (!visible)
                world.SetFieldOverride(id, "condition", "visible", CoreWorld.ToJson(false));
            if (selfText is not null)
                world.SetFieldOverride(id, "condition", "selfText", CoreWorld.ToJson(selfText));
            if (traits is not null)
                world.SetFieldOverride(id, "condition", "traits", CoreWorld.ToJson(traits));
            if (statMods is not null)
                world.SetFieldOverride(id, "condition", "statMods", CoreWorld.ToJson(statMods));
        }

        Template("cond_tipsy", "tipsy", visible: true,
            selfText: "You feel warm and loose-tongued.",
            traits: "flirty, loose-tongued",
            statMods: new Dictionary<string, int> { ["charisma"] = 1 });
        Template("cond_drunk", "drunk", visible: true,
            selfText: null, // exercises the "You feel {label}." fallback
            traits: "belligerent, slurring",
            statMods: new Dictionary<string, int> { ["agility"] = -2, ["brawling"] = 1 });
        Template("cond_pee", "needs_to_pee", visible: false,
            selfText: "You need to pee.",
            traits: null, statMods: null);
        return engine;
    }

    [Fact]
    public void Attach_ClonesTemplate_AndIsIdempotent()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");

        var attached = Conditions.Attach(engine.World, engine.ModuleRegistry, alice, "cond_tipsy");

        Assert.True(Conditions.Has(engine.World, engine.ModuleRegistry, alice, "tipsy"));
        Assert.Equal("tipsy_alice", attached.Id);
        Assert.Equal(alice.Id, attached.Parent);
        Assert.Single(Conditions.Active(engine.World, alice));

        // second attach keeps the existing instance
        var again = Conditions.Attach(engine.World, engine.ModuleRegistry, alice, "cond_tipsy");
        Assert.Equal("tipsy_alice", again.Id);
        Assert.Single(Conditions.Active(engine.World, alice));
    }

    [Fact]
    public void Attach_TemplateWithoutConditionModule_Throws()
    {
        var engine = NewEngine();
        engine.World.CreateObject("apple_like", CoreWorld.RootId, "not a condition");
        var alice = engine.World.GetObject("alice");
        Assert.Throws<InvalidOperationException>(() =>
            Conditions.Attach(engine.World, engine.ModuleRegistry, alice, "apple"));
    }

    [Fact]
    public void Detach_RemovesCondition()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        Conditions.Attach(engine.World, engine.ModuleRegistry, alice, "cond_tipsy");

        Assert.True(Conditions.Detach(engine.World, engine.ModuleRegistry, alice, "tipsy"));
        Assert.False(Conditions.Has(engine.World, engine.ModuleRegistry, alice, "tipsy"));
        Assert.False(engine.World.HasObject("tipsy_alice"));
        Assert.False(Conditions.Detach(engine.World, engine.ModuleRegistry, alice, "tipsy"));
    }

    [Fact]
    public void StatMods_SumAcrossConditions_AndFeedChecks()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        engine.World.AddModule("alice", "stats");
        engine.World.SetFieldOverride("alice", "stats", "values",
            CoreWorld.ToJson(new Dictionary<string, int> { ["agility"] = 3 }));

        Assert.Equal(3, Checks.Bonus(engine.World, engine.ModuleRegistry, alice, "agility", null));

        Conditions.Attach(engine.World, engine.ModuleRegistry, alice, "cond_tipsy");
        Conditions.Attach(engine.World, engine.ModuleRegistry, alice, "cond_drunk");

        Assert.Equal(1, Checks.Bonus(engine.World, engine.ModuleRegistry, alice, "charisma", null));
        // agility 3 - 2 (drunk) = 1; a null stat contributes nothing
        Assert.Equal(1, Checks.Bonus(engine.World, engine.ModuleRegistry, alice, "agility", null));
        Assert.Equal(1, Checks.Bonus(engine.World, engine.ModuleRegistry, alice, null, "brawling"));
        Assert.Equal(0, Checks.Bonus(engine.World, engine.ModuleRegistry, alice, null, null));
    }

    [Fact]
    public void VisibleWords_RespectVisibility_AndPreferLabels()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        Conditions.Attach(engine.World, engine.ModuleRegistry, alice, "cond_drunk");
        Conditions.Attach(engine.World, engine.ModuleRegistry, alice, "cond_pee");

        // needs_to_pee is invisible; drunk has no label so the kind shows
        var words = Conditions.VisibleWords(engine.World, engine.ModuleRegistry, alice);
        Assert.Equal(["drunk"], words);
    }

    [Fact]
    public void SelfLines_UseAuthoredText_OrFallBackToLabel()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        Conditions.Attach(engine.World, engine.ModuleRegistry, alice, "cond_tipsy");
        Conditions.Attach(engine.World, engine.ModuleRegistry, alice, "cond_drunk");

        Assert.Equal(
        [
            "You feel warm and loose-tongued.",
            "You feel drunk.", // fallback: label default = kind
        ], Conditions.SelfLines(engine.World, engine.ModuleRegistry, alice));
    }

    [Fact]
    public void TraitText_JoinsActiveConditionTraits()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        Assert.Equal("", Conditions.TraitText(engine.World, engine.ModuleRegistry, alice));

        Conditions.Attach(engine.World, engine.ModuleRegistry, alice, "cond_tipsy");
        Conditions.Attach(engine.World, engine.ModuleRegistry, alice, "cond_pee");
        Assert.Equal("flirty, loose-tongued",
            Conditions.TraitText(engine.World, engine.ModuleRegistry, alice));
    }

    [Fact]
    public void RoomListing_ShowsVisibleConditions_OnAgentsAndOccupants()
    {
        var engine = NewEngine();
        var world = engine.World;
        var alice = world.GetObject("alice");

        Conditions.Attach(world, engine.ModuleRegistry, alice, "cond_drunk");
        var entries = Perception.DescribeRoomContents(
            world, engine.ModuleRegistry, world.GetObject("room_a"), "bob");
        Assert.Contains("Alice (drunk)", entries);
    }

    [Fact]
    public void Inventory_AndLook_SkipConditionsAsBelongings()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        Conditions.Attach(engine.World, engine.ModuleRegistry, alice, "cond_tipsy");

        var inventory = engine.TurnManager.Execute(alice, "inventory");
        Assert.DoesNotContain("tipsy", inventory.Message); // not a belonging
        Assert.Contains("You are carrying nothing.", inventory.Message);

        var look = engine.TurnManager.Execute(alice, "look");
        Assert.Contains("You feel warm and loose-tongued.", look.Message);
    }

    [Fact]
    public void Examine_ShowsVisibleConditionLine()
    {
        var engine = NewEngine();
        var world = engine.World;
        var alice = world.GetObject("alice");
        Conditions.Attach(world, engine.ModuleRegistry, alice, "cond_drunk");

        var examine = engine.TurnManager.Execute(alice, "examine", "alice");
        Assert.Contains("Alice looks drunk.", examine.Message);
    }
}
