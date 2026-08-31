using AEngine.Core.Runtime;

namespace AEngine.Tests;

/// <summary>
/// The generic quest verbs built for the Nail scenario: trade (barter a
/// held item for another agent's ware, per the ware module's `wants`
/// field), ritual (a requirements-gated service on the target's `ritual`
/// module: required items, consumed items, removed modules, epilogue,
/// ends-game), and custom affordance labels.
/// </summary>
public class TradeRitualTests
{
    private const string TradeRitualModulesJson = """
    [
      {
        "id": "ware", "name": "Ware",
        "fields": [
          { "name": "wants", "type": "string", "default": "" },
          { "name": "refusal", "type": "string", "default": "" },
          { "name": "trader", "type": "string", "default": "" }
        ],
        "affordances": [
          {
            "verb": "trade", "handler": "trade", "label": "Barter for the {target}",
            "signals": [ { "sense": "visual", "priority": 5, "text": "{agent} barters for the {target}." } ]
          }
        ]
      },
      {
        "id": "ritual", "name": "Ritual",
        "fields": [
          { "name": "requiresItems", "type": "list", "default": [] },
          { "name": "consumesItems", "type": "list", "default": [] },
          { "name": "removesModules", "type": "list", "default": [] },
          { "name": "epilogue", "type": "string", "default": "" },
          { "name": "endsGame", "type": "bool", "default": false }
        ],
        "affordances": [
          {
            "verb": "unbrand", "handler": "ritual", "label": "Ask {target} to unmark you", "othersOnly": true,
            "signals": [ { "sense": "visual", "priority": 5, "text": "{agent} undergoes the rite." } ]
          },
          {
            "verb": "perform", "handler": "ritual", "label": "Perform the rite on {target}", "targetOthers": true,
            "signals": [ { "sense": "visual", "priority": 5, "text": "{agent} performs the rite on the {target}." } ]
          }
        ]
      },
      { "id": "cursed", "name": "Cursed", "fields": [], "affordances": [] }
    ]
    """;

    private static GameEngine NewEngine()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.ModuleRegistry.LoadJson(TradeRitualModulesJson);
        engine.World.MoveObject("bob", "room_a"); // same room as Alice
        return engine;
    }

    private static void AddItem(GameEngine engine, string id, string name, string holderId)
    {
        engine.World.CreateObject(id, holderId, name);
        engine.World.AddModule(id, "portable");
    }

    private static void MakeWare(GameEngine engine, string id, string name, string holderId, string wantsId)
    {
        AddItem(engine, id, name, holderId);
        engine.World.AddModule(id, "ware");
        engine.World.SetFieldOverride(id, "ware", "wants", Core.World.World.ToJson(wantsId));
    }

    [Fact]
    public void Trade_SwapsItems_WhenActorHasTheWantedItem()
    {
        var engine = NewEngine();
        AddItem(engine, "moonpetal", "moonpetal bloom", "alice");
        MakeWare(engine, "salt", "ember salt", "bob", "moonpetal");
        var alice = engine.World.GetObject("alice");

        // offered with the custom label, and only while another agent holds it
        var trade = Assert.Single(engine.ActionResolver.Resolve(alice),
            a => a.Verb == "trade" && a.TargetId == "salt");
        Assert.Equal("Barter for the ember salt", trade.Label);

        var result = engine.TurnManager.PerformAction(alice, trade);
        Assert.True(result.Success);
        Assert.Equal("You trade the moonpetal bloom for the ember salt.", result.Message);
        Assert.Equal("alice", engine.World.GetObject("salt").Parent);
        Assert.Equal("bob", engine.World.GetObject("moonpetal").Parent);
        Assert.Contains(engine.SignalBus.Drain("bob"), s => s.Text == "Alice barters for the ember salt.");
    }

    [Fact]
    public void Trade_FailsWithoutTheWantedItem_NamingIt()
    {
        var engine = NewEngine();
        AddItem(engine, "moonpetal", "moonpetal bloom", "room_a"); // on the floor
        MakeWare(engine, "salt", "ember salt", "bob", "moonpetal");

        var result = engine.TurnManager.PerformAction(engine.World.GetObject("alice"),
            TestWorlds.Find(engine, "alice", "trade", "salt"));
        Assert.False(result.Success);
        Assert.Equal("Bob wants the moonpetal bloom in exchange.", result.Message);
        Assert.Equal("bob", engine.World.GetObject("salt").Parent);
    }

    [Fact]
    public void Trade_Refusal_SpeaksInTheHoldersVoice()
    {
        var engine = NewEngine();
        AddItem(engine, "moonpetal", "moonpetal bloom", "room_a"); // on the floor
        MakeWare(engine, "salt", "ember salt", "bob", "moonpetal");
        engine.World.SetFieldOverride("salt", "ware", "refusal",
            Core.World.World.ToJson("No bloom, no salt, friend."));

        var result = engine.TurnManager.PerformAction(engine.World.GetObject("alice"),
            TestWorlds.Find(engine, "alice", "trade", "salt"));
        Assert.False(result.Success);
        Assert.Equal("You try to barter for the ember salt.", result.Message);
        Assert.Equal("bob", engine.World.GetObject("salt").Parent);
        // the refusal is real speech: the actor hears it (and remembers
        // it), and so does the holder
        Assert.Contains(engine.SignalBus.Drain("alice"),
            s => s.Text == "Bob says: \"No bloom, no salt, friend.\"");
        Assert.Contains(engine.Memory.Recall("bob"),
            m => m == "You say: \"No bloom, no salt, friend.\"");
    }

    private const string ConsentWareJson = """
    [
      {
        "id": "ware", "name": "Ware",
        "fields": [
          { "name": "wants", "type": "string", "default": "" },
          { "name": "refusal", "type": "string", "default": "" }
        ],
        "affordances": [
          {
            "verb": "trade", "handler": "trade", "label": "Barter for the {target}",
            "signals": [ { "sense": "visual", "priority": 5, "text": "{agent} barters for the {target}." } ],
            "reaction": {
              "window": 3,
              "telegraph": "{agent} offers to barter for the {target}.",
              "actorText": "You offer to barter for the {target}.",
              "options": [
                { "id": "accept", "label": "Accept", "noResist": true, "default": true, "text": "You accept the offer." },
                { "id": "decline", "label": "Decline", "text": "You decline the offer." }
              ]
            }
          }
        ]
      }
    ]
    """;

    [Fact]
    public void Trade_ConsentGated_HolderDeclinesAndAccepts()
    {
        var engine = NewEngine();
        engine.World.SetFieldOverride("bob", "agent", "policy", Core.World.World.ToJson("player"));
        // a consent-gated trade variant: the holder reacts to the offer
        engine.ModuleRegistry.Update(Assert.Single(
            Core.Modules.ModuleRegistry.ParseJson(ConsentWareJson)));
        AddItem(engine, "moonpetal", "moonpetal bloom", "alice");
        MakeWare(engine, "salt", "ember salt", "bob", "moonpetal");
        var alice = engine.World.GetObject("alice");

        // the offer telegraphs against the holder and parks — no swap yet
        var trade = engine.ActionResolver.Resolve(alice).First(a => a.Verb == "trade" && a.TargetId == "salt");
        var offered = engine.TurnManager.PerformAction(alice, trade);
        Assert.True(offered.Success);
        Assert.Equal("You offer to barter for the ember salt.", offered.Message);
        Assert.Equal("bob", engine.World.GetObject("salt").Parent);
        Assert.Equal("Alice offers to barter for the ember salt.",
            Assert.Single(engine.SignalBus.Drain("bob")).Text);
        var pending = Assert.Single(engine.Reactions.Pending);
        Assert.Equal("bob", pending.DefenderId);

        // declined: the trade fails and nothing moves
        engine.Reactions.Choose(pending.Id, "decline");
        Assert.Equal("bob", engine.World.GetObject("salt").Parent);
        Assert.Equal("alice", engine.World.GetObject("moonpetal").Parent);
        Assert.Contains(engine.Memory.Recall("alice"),
            m => m == "Bob declines the offer.");

        // accepted: the swap goes through
        offered = engine.TurnManager.PerformAction(alice, trade);
        Assert.True(offered.Success);
        engine.Reactions.Choose(Assert.Single(engine.Reactions.Pending).Id, "accept");
        Assert.Equal("alice", engine.World.GetObject("salt").Parent);
        Assert.Equal("bob", engine.World.GetObject("moonpetal").Parent);
    }

    [Fact]
    public void Trade_TraderWare_SellsOnlyThroughTheTrader()
    {
        var engine = NewEngine();
        engine.World.CreateObject("carol", "room_a", "Carol");
        engine.World.AddModule("carol", "agent");
        AddItem(engine, "moonpetal", "moonpetal bloom", "alice");
        MakeWare(engine, "salt", "ember salt", "bob", "moonpetal");
        engine.World.SetFieldOverride("salt", "ware", "trader", Core.World.World.ToJson("bob"));
        var alice = engine.World.GetObject("alice");

        // Carol holds the ware but isn't its trader: the barter isn't
        // offered, and the handler refuses if forced
        engine.World.MoveObject("salt", "carol");
        Assert.DoesNotContain(engine.ActionResolver.Resolve(alice), a => a.Verb == "trade");
        var refused = engine.TurnManager.Execute(alice, "trade", "salt");
        Assert.False(refused.Success);
        Assert.Equal("Carol isn't trading the ember salt.", refused.Message);

        // in the trader's hands it sells normally
        engine.World.MoveObject("salt", "bob");
        Assert.Contains(engine.ActionResolver.Resolve(alice), a => a.Verb == "trade");
        var result = engine.TurnManager.PerformAction(
            alice, TestWorlds.Find(engine, "alice", "trade", "salt"));
        Assert.True(result.Success);
        Assert.Equal("alice", engine.World.GetObject("salt").Parent);
    }

    [Fact]
    public void Trade_WantedItemAlreadyGifted_StillTrades()
    {
        var engine = NewEngine();
        AddItem(engine, "moonpetal", "moonpetal bloom", "bob"); // handed over earlier
        MakeWare(engine, "salt", "ember salt", "bob", "moonpetal");

        var result = engine.TurnManager.PerformAction(engine.World.GetObject("alice"),
            TestWorlds.Find(engine, "alice", "trade", "salt"));
        Assert.True(result.Success);
        // the gift was already handed over, so the actor gives nothing now
        Assert.Equal("Bob hands you the ember salt.", result.Message);
        Assert.Equal("alice", engine.World.GetObject("salt").Parent);
        Assert.Equal("bob", engine.World.GetObject("moonpetal").Parent);
    }

    [Fact]
    public void Trade_NotOfferedForYourOwnOrRoomItems()
    {
        var engine = NewEngine();
        AddItem(engine, "moonpetal", "moonpetal bloom", "alice");
        MakeWare(engine, "salt", "ember salt", "alice", "moonpetal"); // Alice's own ware
        MakeWare(engine, "pepper", "pepper", "room_a", "moonpetal");   // on the floor

        Assert.DoesNotContain(engine.ActionResolver.Resolve(engine.World.GetObject("alice")),
            a => a.Verb == "trade");
    }

    private static GameEngine RitualEngine(out string epilogue)
    {
        var engine = NewEngine();
        epilogue = "The brand fades.";
        engine.World.AddModule("bob", "ritual");
        engine.World.SetFieldOverride("bob", "ritual", "requiresItems",
            Core.World.World.ToJson(new[] { "talon", "salt" }));
        engine.World.SetFieldOverride("bob", "ritual", "consumesItems",
            Core.World.World.ToJson(new[] { "salt" }));
        engine.World.SetFieldOverride("bob", "ritual", "removesModules",
            Core.World.World.ToJson(new[] { "cursed" }));
        engine.World.SetFieldOverride("bob", "ritual", "epilogue",
            Core.World.World.ToJson(epilogue));
        engine.World.SetFieldOverride("bob", "ritual", "endsGame", Core.World.World.ToJson(true));
        engine.World.AddModule("alice", "cursed");
        AddItem(engine, "talon", "dragon's talon", "alice");
        AddItem(engine, "salt", "ember salt", "alice");
        return engine;
    }

    [Fact]
    public void Ritual_FailsListingMissingItems()
    {
        var engine = RitualEngine(out _);
        engine.World.MoveObject("salt", "room_a"); // no longer carried

        var result = engine.TurnManager.PerformAction(engine.World.GetObject("alice"),
            TestWorlds.Find(engine, "alice", "unbrand", "bob"));
        Assert.False(result.Success);
        Assert.Equal("You ask Bob for the rite. Bob shakes their head — the rite still needs: ember salt.", result.Message);
        Assert.True(engine.World.GetObject("alice").HasModule("cursed"));
        Assert.Null(engine.GameOver);
    }

    [Fact]
    public void Ritual_Success_Consumes_Removes_AndEndsTheGame()
    {
        var engine = RitualEngine(out var epilogue);
        var alice = engine.World.GetObject("alice");

        var action = TestWorlds.Find(engine, "alice", "unbrand", "bob");
        Assert.Equal("Ask Bob to unmark you", action.Label); // custom label
        var result = engine.TurnManager.PerformAction(alice, action);
        Assert.True(result.Success);
        Assert.Equal(epilogue, result.Message);
        Assert.False(engine.World.HasObject("salt"));            // consumed
        Assert.Equal("alice", engine.World.GetObject("talon").Parent); // proof kept
        Assert.False(engine.World.GetObject("alice").HasModule("cursed"));
        Assert.Equal(epilogue, engine.GameOver);

        // game over: NPCs stop acting (Bob's random policy would otherwise act)
        var remembered = engine.Memory.Recall("bob").Count; // Bob saw the rite itself
        engine.TurnManager.RunNpcTurns();
        Assert.Equal(remembered, engine.Memory.Recall("bob").Count);
    }

    [Fact]
    public void Ritual_AcceptsItemsAlreadyHandedToTheTarget()
    {
        var engine = RitualEngine(out _);
        engine.World.MoveObject("salt", "bob"); // given ahead of time

        var result = engine.TurnManager.PerformAction(engine.World.GetObject("alice"),
            TestWorlds.Find(engine, "alice", "unbrand", "bob"));
        Assert.True(result.Success);
    }

    [Fact]
    public void Ritual_TheHostPerforms_OnASupplicant()
    {
        var engine = RitualEngine(out var epilogue); // bob hosts the rite; alice has talon+salt+cursed
        var bob = engine.World.GetObject("bob");

        // the performer-facing direction is emitted per other agent, from
        // his own list — and he never sees his own ask affordance
        var perform = Assert.Single(engine.ActionResolver.Resolve(bob), a => a.Verb == "perform");
        Assert.Equal("alice", perform.TargetId);
        Assert.Equal("Perform the rite on Alice", perform.Label);
        Assert.DoesNotContain(engine.ActionResolver.Resolve(bob), a => a.Verb == "unbrand");

        var result = engine.TurnManager.PerformAction(bob, perform);
        Assert.True(result.Success);
        Assert.Equal(epilogue, result.Message);
        Assert.False(engine.World.GetObject("alice").HasModule("cursed")); // removed from the supplicant
        Assert.False(engine.World.HasObject("salt"));
        Assert.Equal(epilogue, engine.GameOver);
        // the supplicant observes it second-person
        Assert.Contains(engine.SignalBus.Drain("alice"),
            s => s.Text == "Bob performs the rite on you.");
    }
}
