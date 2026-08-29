using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Llm;

namespace AEngine.Tests;

/// <summary>
/// Clothing/armor: worn garments are children of the agent with
/// wearable.worn set; garments occupy body regions (at most one garment
/// per region, distinct regions stack); the agent's body module declares
/// which regions exist (no body module = can't wear anything).
/// </summary>
public class ClothingTests
{
    private const string ClothingModulesJson = """
    [
      {
        "id": "body", "name": "Body",
        "fields": [ { "name": "regions", "type": "list", "default": ["head", "top", "outer", "bottom", "feet"] } ],
        "affordances": []
      },
      {
        "id": "wearable", "name": "Wearable",
        "fields": [
          { "name": "regions", "type": "list", "default": [] },
          { "name": "worn", "type": "bool", "default": false }
        ],
        "affordances": [
          { "verb": "wear", "handler": "wear",
            "signals": [ { "sense": "visual", "priority": 5, "text": "{agent} puts on the {target}." } ] },
          { "verb": "remove", "handler": "remove",
            "signals": [ { "sense": "visual", "priority": 5, "text": "{agent} takes off the {target}." } ] }
        ]
      }
    ]
    """;

    private static GameEngine NewEngine()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.ModuleRegistry.LoadJson(ClothingModulesJson);
        engine.World.AddModule("alice", "body");
        engine.World.AddModule("bob", "body");
        return engine;
    }

    private static void AddGarment(
        GameEngine engine, string id, string name, string roomId, params string[] regions)
    {
        engine.World.CreateObject(id, roomId, name);
        engine.World.AddModule(id, "portable");
        engine.World.AddModule(id, "wearable");
        engine.World.SetFieldOverride(id, "wearable", "regions", Core.World.World.ToJson(regions));
    }

    [Fact]
    public void Wear_HeldGarment_BecomesWorn_AndIsObservable()
    {
        var engine = NewEngine();
        AddGarment(engine, "apron", "apron", "room_a", "top");
        engine.World.MoveObject("bob", "room_a"); // Bob watches
        var alice = engine.World.GetObject("alice");

        // wearing straight from the floor fails — pick it up first
        var fromFloor = engine.TurnManager.Execute(alice, "wear", "apron");
        Assert.False(fromFloor.Success);
        Assert.Contains("pick up", fromFloor.Message);

        Assert.True(engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "take", "apron")).Success);
        var wear = TestWorlds.Find(engine, "alice", "wear", "apron");
        var result = engine.TurnManager.PerformAction(alice, wear);
        Assert.True(result.Success);
        Assert.Equal("You put on the apron.", result.Message);
        Assert.True(Clothing.IsWorn(engine.ModuleRegistry, engine.World.GetObject("apron")));

        Assert.Contains(engine.SignalBus.Drain("bob"), s => s.Text == "Alice puts on the apron.");

        // wearing it again is a noop, and remove is now offered instead
        Assert.Equal(ActionOutcome.Noop, engine.TurnManager.Execute(alice, "wear", "apron").Outcome);
        var actions = engine.ActionResolver.Resolve(alice);
        Assert.DoesNotContain(actions, a => a.Verb == "wear" && a.TargetId == "apron");
        Assert.Contains(actions, a => a.Verb == "remove" && a.TargetId == "apron" && a.Label == "Take off the apron");
    }

    [Fact]
    public void Wear_OneGarmentPerRegion_ButDistinctRegionsStack()
    {
        var engine = NewEngine();
        AddGarment(engine, "shirt", "shirt", "room_a", "top");
        AddGarment(engine, "tee", "tee shirt", "room_a", "top");
        AddGarment(engine, "coat", "coat", "room_a", "outer");
        AddGarment(engine, "armor", "suit of armor", "room_a", "top", "bottom");
        AddGarment(engine, "pants", "pants", "room_a", "bottom");
        var alice = engine.World.GetObject("alice");
        foreach (var id in new[] { "shirt", "tee", "coat", "armor", "pants" })
            engine.World.MoveObject(id, "alice");

        Assert.True(engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "wear", "shirt")).Success);

        // a second top garment conflicts, naming the region
        var conflict = engine.TurnManager.Execute(alice, "wear", "tee");
        Assert.False(conflict.Success);
        Assert.Contains("on your top", conflict.Message);

        // the coat uses a distinct region and stacks over the shirt
        Assert.True(engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "wear", "coat")).Success);

        // a full suit of armor conflicts with the shirt (top) — and after
        // the shirt comes off, with the pants (bottom)
        Assert.False(engine.TurnManager.Execute(alice, "wear", "armor").Success);
        Assert.True(engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "remove", "shirt")).Success);
        Assert.True(engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "wear", "pants")).Success);
        Assert.False(engine.TurnManager.Execute(alice, "wear", "armor").Success);
        Assert.True(engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "remove", "pants")).Success);
        Assert.True(engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "wear", "armor")).Success);
    }

    [Fact]
    public void Wear_RequiresMatchingBodyRegions()
    {
        var engine = NewEngine();
        AddGarment(engine, "saddle", "saddle", "room_b", "back");
        AddGarment(engine, "shirt", "shirt", "room_b", "top");
        var bob = engine.World.GetObject("bob");

        // Bob is a horse for the day: a back and nothing else
        engine.World.SetFieldOverride("bob", "body", "regions", Core.World.World.ToJson(new[] { "back" }));
        engine.World.MoveObject("saddle", "bob");
        engine.World.MoveObject("shirt", "bob");

        Assert.True(engine.TurnManager.PerformAction(bob, TestWorlds.Find(engine, "bob", "wear", "saddle")).Success);
        var shirtResult = engine.TurnManager.Execute(bob, "wear", "shirt");
        Assert.False(shirtResult.Success);
        Assert.Contains("doesn't fit", shirtResult.Message);

        // an agent with no body module at all can't wear anything and is
        // never offered the verb
        engine.World.CreateObject("carol", "room_a", "Carol");
        engine.World.AddModule("carol", "agent");
        AddGarment(engine, "hat", "hat", "room_a", "head");
        engine.World.MoveObject("hat", "carol");
        var carol = engine.World.GetObject("carol");
        Assert.DoesNotContain(engine.ActionResolver.Resolve(carol),
            a => a.Verb == "wear" && a.TargetId == "hat");
        Assert.False(engine.TurnManager.Execute(carol, "wear", "hat").Success);
    }

    [Fact]
    public void Remove_ReturnsToInventory_AndWornItemsCantBeDropped()
    {
        var engine = NewEngine();
        AddGarment(engine, "apron", "apron", "room_a", "top");
        var alice = engine.World.GetObject("alice");
        engine.World.MoveObject("apron", "alice");
        Assert.True(engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "wear", "apron")).Success);

        // worn items are not offered for dropping, and a forced drop fails
        Assert.DoesNotContain(engine.ActionResolver.Resolve(alice),
            a => a.Verb == "drop" && a.TargetId == "apron");
        Assert.False(engine.TurnManager.Execute(alice, "drop", "apron").Success);

        // removing keeps it in inventory, unworn and droppable
        var remove = TestWorlds.Find(engine, "alice", "remove", "apron");
        Assert.True(engine.TurnManager.PerformAction(alice, remove).Success);
        var apron = engine.World.GetObject("apron");
        Assert.Equal("alice", apron.Parent);
        Assert.False(Clothing.IsWorn(engine.ModuleRegistry, apron));
        Assert.Contains(engine.ActionResolver.Resolve(alice), a => a.Verb == "drop" && a.TargetId == "apron");
        Assert.Equal(ActionOutcome.Noop, engine.TurnManager.Execute(alice, "remove", "apron").Outcome);
    }

    [Fact]
    public void DressedAgents_ShowInLookAndContext_ButNotTheListing()
    {
        var engine = NewEngine();
        AddGarment(engine, "apron", "apron", "room_a", "top");
        AddGarment(engine, "hat", "chef's hat", "room_a", "head");
        engine.World.MoveObject("bob", "room_a");
        engine.World.MoveObject("apron", "alice");
        engine.World.MoveObject("hat", "alice");
        var alice = engine.World.GetObject("alice");
        var bob = engine.World.GetObject("bob");
        Assert.True(engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "wear", "apron")).Success);
        Assert.True(engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "wear", "hat")).Success);

        // Bob's look: compact listing, then a dressed line per agent
        var bobLook = engine.TurnManager.PerformAction(bob, TestWorlds.Find(engine, "bob", "look")).Message;
        Assert.Contains("You see: ", bobLook);
        Assert.DoesNotContain("Alice (wearing", bobLook);
        Assert.Contains("Alice is wearing an apron, a chef's hat.", bobLook);

        // Alice sees herself dressed, observer-relative
        var aliceLook = engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "look")).Message;
        Assert.Contains("You are wearing an apron, a chef's hat.", aliceLook);

        // the LLM context mirrors look
        var context = new AgentContextBuilder(engine).BuildContext(bob, npc: true);
        Assert.Contains("Alice is wearing an apron, a chef's hat.", context);

        // inventory splits worn from carried
        engine.World.MoveObject("apple", "alice");
        var inventory = engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "inventory")).Message;
        Assert.Contains("You are wearing: an apron, a chef's hat", inventory);
        Assert.Contains("You are carrying: an apple", inventory);
    }
}
