using AEngine.Core.Runtime;
using AEngine.Llm;

namespace AEngine.Tests;

/// <summary>
/// Two-object verbs: put (item → container) and give (item → agent, gated
/// by the recipient's accept/decline reaction). The container/recipient is
/// the action target; the item rides as AuxTargetId and formats into
/// signal templates as {item}.
/// </summary>
public class GivePutTests
{
    // upgrades the shared test world's container (put, capacity 2) and
    // portable (give with an accept/decline reaction) modules
    private const string GivePutModulesJson = """
    [
      {
        "id": "container", "name": "Container",
        "fields": [ { "name": "capacity", "type": "int", "default": 2 } ],
        "affordances": [
          { "verb": "put", "handler": "put",
            "signals": [ { "sense": "visual", "priority": 5, "text": "{agent} puts the {item} into the {target}." } ] }
        ]
      },
      {
        "id": "portable", "name": "Portable",
        "fields": [],
        "affordances": [
          {
            "verb": "take", "handler": "take",
            "signals": [ { "sense": "visual", "priority": 5, "text": "{agent} picks up the {target}{container}." } ]
          },
          { "verb": "drop", "handler": "drop" },
          {
            "verb": "give", "handler": "give",
            "signals": [ { "sense": "visual", "priority": 5, "text": "{agent} gives the {item} to the {target}." } ],
            "failSignals": [ { "sense": "visual", "priority": 5, "text": "{target} declines the {item}." } ],
            "reaction": {
              "window": 3,
              "telegraph": "{agent} offers the {item} to the {target}.",
              "actorText": "You offer the {item} to {target}.",
              "options": [
                { "id": "accept", "label": "Accept", "noResist": true, "default": true, "text": "You accept the gift." },
                { "id": "decline", "label": "Decline", "text": "You decline the gift." }
              ]
            }
          }
        ]
      }
    ]
    """;

    private static GameEngine NewEngine()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.ModuleRegistry.LoadJson(GivePutModulesJson);
        engine.World.MoveObject("bob", "room_a"); // same room as Alice
        // Bob reacts deliberately (his random policy would answer at park time)
        engine.World.SetFieldOverride("bob", "agent", "policy", Core.World.World.ToJson("player"));
        return engine;
    }

    private static void Take(GameEngine engine, string agentId, string itemId)
    {
        var result = engine.TurnManager.PerformAction(
            engine.World.GetObject(agentId), TestWorlds.Find(engine, agentId, "take", itemId));
        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public void Put_ListsPerHeldItem_OnlyWhileOpen()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        Take(engine, "alice", "apple");

        // the chest starts closed: put is in the potential set (plans can
        // name it after an "open" step) but not the listed actions
        Assert.DoesNotContain(engine.ActionResolver.Resolve(alice), a => a.Verb == "put");
        Assert.Contains(engine.ActionResolver.ResolvePotential(alice), a => a.Verb == "put");

        engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "open", "chest"));
        var put = Assert.Single(engine.ActionResolver.Resolve(alice), a => a.Verb == "put");
        Assert.Equal("Put the apple into the chest", put.Label);
        Assert.Equal("chest", put.TargetId);
        Assert.Equal("apple", put.AuxTargetId);
    }

    [Fact]
    public void Put_NotOfferedWithEmptyHands()
    {
        var engine = NewEngine();
        engine.TurnManager.PerformAction(
            engine.World.GetObject("alice"), TestWorlds.Find(engine, "alice", "open", "chest"));

        Assert.DoesNotContain(engine.ActionResolver.Resolve(engine.World.GetObject("alice")),
            a => a.Verb == "put");
    }

    [Fact]
    public void Put_MovesTheItem_AndRespectsOpenStateAndCapacity()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        engine.World.CreateObject("stone", "room_a", "stone");
        engine.World.AddModule("stone", "portable");
        engine.World.CreateObject("stick", "room_a", "stick");
        engine.World.AddModule("stick", "portable");
        Take(engine, "alice", "apple");
        Take(engine, "alice", "stone");
        Take(engine, "alice", "stick");

        // closed container: the attempt fails
        var put = engine.ActionResolver.ResolvePotential(alice).First(a => a.Verb == "put");
        var closed = engine.TurnManager.PerformAction(alice, put);
        Assert.Equal(Core.Actions.ActionOutcome.Failure, closed.Outcome);
        Assert.Equal("The chest is closed.", closed.Message);

        engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "open", "chest"));
        var first = engine.TurnManager.PerformAction(alice,
            engine.ActionResolver.Resolve(alice).First(a => a.Verb == "put" && a.AuxTargetId == "apple"));
        Assert.True(first.Success);
        Assert.Equal("You put the apple into the chest.", first.Message);
        Assert.Equal("chest", engine.World.GetObject("apple").Parent);
        // observers see the item and the container named
        Assert.Contains(engine.SignalBus.Drain("bob"),
            s => s.Text == "Alice puts the apple into the chest.");

        // capacity 2: the third item no longer fits
        engine.TurnManager.PerformAction(alice,
            engine.ActionResolver.Resolve(alice).First(a => a.Verb == "put" && a.AuxTargetId == "stone"));
        var full = engine.TurnManager.PerformAction(alice,
            engine.ActionResolver.Resolve(alice).First(a => a.Verb == "put" && a.AuxTargetId == "stick"));
        Assert.Equal(Core.Actions.ActionOutcome.Failure, full.Outcome);
        Assert.Equal("The chest is full.", full.Message);
    }

    [Fact]
    public void Give_Telegraphs_AndAcceptMovesTheItem()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        Take(engine, "alice", "apple");

        var give = Assert.Single(engine.ActionResolver.Resolve(alice), a => a.Verb == "give");
        Assert.Equal("Give the apple to Bob", give.Label);
        Assert.Equal("bob", give.TargetId);      // the recipient reacts...
        Assert.Equal("apple", give.AuxTargetId); // ...the item rides along

        var parked = engine.TurnManager.PerformAction(alice, give);
        Assert.Equal("You offer the apple to Bob.", parked.Message);
        Assert.Equal("alice", engine.World.GetObject("apple").Parent); // not yet handed over

        var pending = Assert.Single(engine.Reactions.Pending);
        Assert.Equal("bob", pending.DefenderId);
        Assert.Equal("Alice offers the apple to you.", pending.Announcement);
        engine.Reactions.Choose(pending.Id, "accept");

        Assert.Equal("bob", engine.World.GetObject("apple").Parent);
        Assert.Contains(engine.Reactions.DrainResolved(),
            r => r.ActorId == "alice" && r.Message == "You give the apple to Bob.");
        Assert.Contains(engine.Memory.Recall("bob"), m => m == "You accept the gift.");
        // the recipient is the protagonist of their own perception: "you",
        // never their own name
        Assert.Contains(engine.SignalBus.Drain("bob"), s => s.Text == "Alice gives the apple to you.");
    }

    [Fact]
    public void Give_Decline_KeepsTheItem_AndFails()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        // a third party in the room still sees everyone by name
        engine.World.CreateObject("carol", "room_a", "Carol");
        engine.World.AddModule("carol", "agent");
        Take(engine, "alice", "apple");

        engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "give", "bob"));
        var pending = Assert.Single(engine.Reactions.Pending);
        engine.Reactions.Choose(pending.Id, "decline");

        Assert.Equal("alice", engine.World.GetObject("apple").Parent);
        Assert.Contains(engine.Reactions.DrainResolved(),
            r => r.ActorId == "alice" && r.Message == "Bob declines the apple.");
        // the declining recipient sees it second-person — subject position,
        // so the verb drops its third-person -s
        Assert.Contains(engine.SignalBus.Drain("bob"), s => s.Text == "you decline the apple.");
        Assert.Contains(engine.SignalBus.Drain("carol"), s => s.Text == "Bob declines the apple.");
    }

    [Fact]
    public void Give_TheDeadlineDefaultIsAccept()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        Take(engine, "alice", "apple");

        engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "give", "bob"));
        while (engine.Reactions.Pending.Count > 0)
            engine.TurnManager.Tick();

        Assert.Equal("bob", engine.World.GetObject("apple").Parent);
    }

    [Fact]
    public void Give_NotOfferedWhenAlone()
    {
        var engine = TestWorlds.NewTwoRoomEngine(); // Bob stays in room_b
        engine.ModuleRegistry.LoadJson(GivePutModulesJson);
        var alice = engine.World.GetObject("alice");
        Take(engine, "alice", "apple");

        Assert.DoesNotContain(engine.ActionResolver.Resolve(alice), a => a.Verb == "give");
    }

    [Fact]
    public void PlanLines_MatchTheConcreteLabels()
    {
        var engine = NewEngine();
        var alice = engine.World.GetObject("alice");
        Take(engine, "alice", "apple");
        engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "open", "chest"));

        var give = PlanExecutor.MatchAvailableOrPotential(engine, alice, "Give the apple to Bob");
        Assert.NotNull(give);
        Assert.Equal("give", give.Verb);
        Assert.Equal("apple", give.AuxTargetId);

        var put = PlanExecutor.MatchAvailableOrPotential(engine, alice, "Put the apple into the chest");
        Assert.NotNull(put);
        Assert.Equal("put", put.Verb);
        Assert.Equal("chest", put.TargetId);
        Assert.Equal("apple", put.AuxTargetId);
    }
}
