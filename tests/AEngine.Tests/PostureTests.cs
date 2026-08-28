using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Llm;

namespace AEngine.Tests;

/// <summary>
/// The posture system: sitting/lying on furniture and being carried are
/// containment in the world tree; affordance posture rules (postures
/// allow-list, sameSupport) gate what an agent can do.
/// </summary>
public class PostureTests
{
    private const string FurnitureModulesJson = """
    [
      {
        "id": "sittable", "name": "Sittable",
        "fields": [ { "name": "capacity", "type": "int", "default": 1 } ],
        "affordances": [
          { "verb": "sit", "handler": "sit", "postures": ["standing"],
            "signals": [ { "sense": "visual", "priority": 5, "text": "{agent} sits down on the {target}." } ] },
          { "verb": "stand", "handler": "stand", "postures": ["sitting"],
            "signals": [ { "sense": "visual", "priority": 5, "text": "{agent} gets up from the {target}." } ] }
        ]
      },
      {
        "id": "lyable", "name": "Lyable",
        "fields": [ { "name": "capacity", "type": "int", "default": 1 } ],
        "affordances": [
          { "verb": "lie", "handler": "lie", "postures": ["standing"],
            "signals": [ { "sense": "visual", "priority": 5, "text": "{agent} lies down on the {target}." } ] },
          { "verb": "stand", "handler": "stand", "postures": ["lying"] }
        ]
      },
      {
        "id": "affectionate", "name": "Affectionate",
        "fields": [],
        "affordances": [
          { "verb": "cuddle", "handler": "cuddle", "sameSupport": true }
        ]
      }
    ]
    """;

    private sealed class CuddleHandler : IActionHandler
    {
        public string Id => "cuddle";

        public ActionResult Execute(ActionContext ctx) =>
            ActionResult.Ok($"You cuddle {ctx.Target!.Name}.");
    }

    private static GameEngine NewEngine()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.ModuleRegistry.LoadJson(FurnitureModulesJson);
        engine.HandlerRegistry.Register(new CuddleHandler());
        return engine;
    }

    private static void AddChair(GameEngine engine, string id = "chair", string roomId = "room_a")
    {
        engine.World.CreateObject(id, roomId, "chair");
        engine.World.AddModule(id, "sittable");
    }

    private static void AddBed(GameEngine engine, string id = "bed", string roomId = "room_a", int capacity = 2)
    {
        engine.World.CreateObject(id, roomId, "bed");
        engine.World.AddModule(id, "sittable");
        engine.World.AddModule(id, "lyable");
        engine.World.SetFieldOverride(id, "sittable", "capacity", Core.World.World.ToJson(capacity));
        engine.World.SetFieldOverride(id, "lyable", "capacity", Core.World.World.ToJson(capacity));
    }

    [Fact]
    public void Sit_MovesAgentOntoChair_AndHidesGo()
    {
        var engine = NewEngine();
        AddChair(engine);
        var alice = engine.World.GetObject("alice");

        var sit = TestWorlds.Find(engine, "alice", "sit", "chair");
        var result = engine.TurnManager.PerformAction(alice, sit);
        Assert.True(result.Success);
        Assert.Equal("You sit down on the chair.", result.Message);
        Assert.Equal("chair", alice.Parent);
        Assert.Equal(Postures.Sitting, Postures.Of(engine.World, engine.ModuleRegistry, alice));

        // while seated: no portal traversal, but same-room reach stays
        var seated = engine.ActionResolver.Resolve(alice);
        Assert.DoesNotContain(seated, a => a.Verb == "go");
        Assert.DoesNotContain(seated, a => a.Verb == "sit" || a.Verb == "lie");
        Assert.Contains(seated, a => a.Verb == "stand" && a.TargetId == "chair");
        Assert.Contains(seated, a => a.Verb == "open" && a.TargetId == "chest");
        Assert.Contains(seated, a => a.Verb == "take" && a.TargetId == "apple");
    }

    [Fact]
    public void Sit_IsObservable_LookListingSignalsAndLlmContext()
    {
        var engine = NewEngine();
        AddChair(engine);
        engine.World.MoveObject("bob", "room_a"); // Bob watches Alice sit
        var alice = engine.World.GetObject("alice");

        var sit = TestWorlds.Find(engine, "alice", "sit", "chair");
        Assert.True(engine.TurnManager.PerformAction(alice, sit).Success);

        // Bob sees it happen and the chair's occupant shows in his look
        Assert.Contains(engine.SignalBus.Drain("bob"),
            s => s.Text == "Alice sits down on the chair.");
        var bobLook = engine.TurnManager.PerformAction(
            engine.World.GetObject("bob"), TestWorlds.Find(engine, "bob", "look"));
        Assert.Contains("Alice (sitting on the chair)", bobLook.Message);

        // Alice's own look and LLM context report her posture
        var aliceLook = engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "look"));
        Assert.Contains("You are sitting on the chair.", aliceLook.Message);
        var context = new AgentContextBuilder(engine).BuildContext(alice, npc: false);
        Assert.Contains("You are sitting on the chair.", context);
        Assert.DoesNotContain(context.Split('\n'), line => line.StartsWith("- Go "));
    }

    [Fact]
    public void Sit_NoopWhenAlreadySitting_AndCapacityIsEnforced()
    {
        var engine = NewEngine();
        AddBed(engine, capacity: 2);
        var alice = engine.World.GetObject("alice");
        var bob = engine.World.GetObject("bob");
        engine.World.MoveObject("bob", "room_a");

        Assert.True(engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "sit", "bed")).Success);
        Assert.True(engine.TurnManager.PerformAction(bob, TestWorlds.Find(engine, "bob", "lie", "bed")).Success);

        // sitting again on the same seat is a noop, not a failure
        var again = engine.TurnManager.Execute(alice, "sit", "bed");
        Assert.Equal(ActionOutcome.Noop, again.Outcome);

        // a third agent doesn't fit
        engine.World.CreateObject("carol", "room_a", "Carol");
        engine.World.AddModule("carol", "agent");
        var full = engine.TurnManager.Execute(engine.World.GetObject("carol"), "sit", "bed");
        Assert.False(full.Success);
        Assert.Contains("no room", full.Message);
    }

    [Fact]
    public void Stand_ReturnsAgentToRoom_AndRestoresGo()
    {
        var engine = NewEngine();
        AddChair(engine);
        var alice = engine.World.GetObject("alice");
        Assert.True(engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "sit", "chair")).Success);

        var stand = TestWorlds.Find(engine, "alice", "stand", "chair");
        var result = engine.TurnManager.PerformAction(alice, stand);
        Assert.True(result.Success);
        Assert.Equal("You get up from the chair.", result.Message);
        Assert.Equal("room_a", alice.Parent);
        Assert.Equal(Postures.Standing, Postures.Of(engine.World, engine.ModuleRegistry, alice));
        Assert.Contains(engine.ActionResolver.Resolve(alice), a => a.Verb == "go");

        // standing while already standing is a noop
        Assert.Equal(ActionOutcome.Noop, engine.TurnManager.Execute(alice, "stand", "chair").Outcome);
    }

    [Fact]
    public void Bed_OffersSitAndLie_StandMatchesTheCurrentPosture()
    {
        var engine = NewEngine();
        AddBed(engine);
        var alice = engine.World.GetObject("alice");

        var standing = engine.ActionResolver.Resolve(alice);
        Assert.Contains(standing, a => a.Verb == "sit" && a.TargetId == "bed");
        Assert.Contains(standing, a => a.Verb == "lie" && a.TargetId == "bed");
        Assert.DoesNotContain(standing, a => a.Verb == "stand");

        Assert.True(engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "lie", "bed")).Success);
        Assert.Equal(Postures.Lying, Postures.Of(engine.World, engine.ModuleRegistry, alice));

        // exactly one stand: lyable's, gated to lying — sittable's is hidden
        var lying = engine.ActionResolver.Resolve(alice);
        Assert.Single(lying, a => a.Verb == "stand" && a.TargetId == "bed");
        Assert.Contains("You are lying on the bed.",
            engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "look")).Message);
    }

    [Fact]
    public void Cuddle_RequiresSameSupport()
    {
        var engine = NewEngine();
        AddBed(engine);
        AddChair(engine);
        engine.World.AddModule("alice", "affectionate");
        engine.World.AddModule("bob", "affectionate");
        engine.World.MoveObject("bob", "room_a");
        var alice = engine.World.GetObject("alice");
        var bob = engine.World.GetObject("bob");

        // Alice on the bed, Bob on the chair: no cuddle across furniture
        Assert.True(engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "lie", "bed")).Success);
        Assert.True(engine.TurnManager.PerformAction(bob, TestWorlds.Find(engine, "bob", "sit", "chair")).Success);
        Assert.DoesNotContain(engine.ActionResolver.Resolve(alice),
            a => a.Verb == "cuddle" && a.TargetId == "bob");

        // Bob joins her on the bed: cuddle appears and works
        Assert.True(engine.TurnManager.PerformAction(bob, TestWorlds.Find(engine, "bob", "stand", "chair")).Success);
        Assert.True(engine.TurnManager.PerformAction(bob, TestWorlds.Find(engine, "bob", "sit", "bed")).Success);
        var cuddle = TestWorlds.Find(engine, "alice", "cuddle", "bob");
        var result = engine.TurnManager.PerformAction(alice, cuddle);
        Assert.True(result.Success);
        Assert.Equal("You cuddle Bob.", result.Message);
    }

    [Fact]
    public void Carried_OnlySelfVerbsAreOffered_UntilPutDown()
    {
        var engine = NewEngine();
        AddChair(engine);
        engine.World.AddModule("bob", "portable");
        engine.World.MoveObject("bob", "room_a");
        var alice = engine.World.GetObject("alice");
        var bob = engine.World.GetObject("bob");

        // Bob is sitting when Alice picks him up: the posture clears
        Assert.True(engine.TurnManager.PerformAction(bob, TestWorlds.Find(engine, "bob", "sit", "chair")).Success);
        Assert.True(engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "take", "bob")).Success);
        Assert.Equal(Postures.Carried, Postures.Of(engine.World, engine.ModuleRegistry, bob));

        // while carried: only his own verbs (look/inventory/wait/say)
        var carried = engine.ActionResolver.Resolve(bob);
        Assert.NotEmpty(carried);
        Assert.All(carried, a => Assert.Contains(a.Verb, new[] { "look", "inventory", "wait", "say" }));
        Assert.Contains("You are being carried by Alice.",
            engine.TurnManager.PerformAction(bob, TestWorlds.Find(engine, "bob", "look")).Message);

        // dropped: back to standing with full actions
        Assert.True(engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "drop", "bob")).Success);
        Assert.Equal(Postures.Standing, Postures.Of(engine.World, engine.ModuleRegistry, bob));
        var free = engine.ActionResolver.Resolve(bob);
        Assert.Contains(free, a => a.Verb == "go");
        Assert.Contains(free, a => a.Verb == "sit" && a.TargetId == "chair");
    }
}
