using AEngine.Core.Runtime;
using AEngine.Core.World;
using AEngine.Llm;

namespace AEngine.Tests;

/// <summary>
/// Regression: a portable agent (one with the portable module) who gets
/// picked up and dropped must keep acting afterwards.
/// </summary>
public class PortableAgentTests
{
    [Fact]
    public void PortableAgent_Random_ActsAgainAfterBeingTakenAndDropped()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.Random = new Random(0);
        engine.World.AddModule("bob", "portable");
        engine.World.MoveObject("bob", "room_a"); // same room as Alice
        var alice = engine.World.GetObject("alice");

        var takeBob = engine.ActionResolver.Resolve(alice)
            .First(a => a.Verb == "take" && a.TargetId == "bob");
        Assert.True(engine.TurnManager.PerformAction(alice, takeBob).Success);

        engine.TurnManager.RunNpcTurns(); // Bob carried: start/skip
        engine.TurnManager.RunNpcTurns();

        var dropBob = engine.ActionResolver.Resolve(alice)
            .First(a => a.Verb == "drop" && a.TargetId == "bob");
        Assert.True(engine.TurnManager.PerformAction(alice, dropBob).Success);

        var turn = engine.TurnManager.Turn;
        engine.TurnManager.RunNpcTurns(); // start selection
        engine.TurnManager.RunNpcTurns(); // execute
        Assert.True(engine.TurnManager.Turn > turn); // Bob acted again
    }

    [Fact]
    public void PortableAgent_Llm_ActsAgainAfterMidFlightTakeAndDrop()
    {
        var llm = new SlowLlmClient();
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.World.AddModule("bob", "portable");
        engine.World.MoveObject("bob", "room_a"); // same room as Alice
        engine.PolicyRegistry.Register(new LlmPolicy(new LlmPlanner(llm, engine)));
        engine.World.SetFieldOverride("bob", "agent", "policy", World.ToJson("llm"));
        var alice = engine.World.GetObject("alice");

        engine.TurnManager.RunNpcTurns(); // start LLM selection — stays in flight
        Assert.Equal(1, llm.Started);

        // Alice picks Bob up and drops him while the selection is in flight
        var takeBob = engine.ActionResolver.Resolve(alice)
            .First(a => a.Verb == "take" && a.TargetId == "bob");
        Assert.True(engine.TurnManager.PerformAction(alice, takeBob).Success);
        var dropBob = engine.ActionResolver.Resolve(alice)
            .First(a => a.Verb == "drop" && a.TargetId == "bob");
        Assert.True(engine.TurnManager.PerformAction(alice, dropBob).Success);

        // the in-flight selection completes (look: still valid), executes
        var turnAfterDrop = engine.TurnManager.Turn;
        llm.CompleteNext("Look around");
        TickUntil(engine, () => engine.TurnManager.Turn > turnAfterDrop);

        // Bob must keep acting: a fresh selection starts and executes
        TickUntil(engine, () => llm.Started == 2);
        llm.CompleteNext("Wait");
        var turn = engine.TurnManager.Turn;
        TickUntil(engine, () => engine.TurnManager.Turn > turn);
    }

    // async policy continuations complete on the threadpool; pump NPC
    // turns until the condition holds (like the real-time ticker does)
    private static void TickUntil(GameEngine engine, Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            engine.TurnManager.RunNpcTurns();
            Thread.Sleep(5);
        }
        Assert.True(condition());
    }

    [Fact]
    public void CarriedAgent_Speech_ReachesTheCarrier()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.World.AddModule("bob", "portable");
        engine.World.MoveObject("bob", "room_a"); // same room as Alice
        var alice = engine.World.GetObject("alice");
        var bob = engine.World.GetObject("bob");

        var takeBob = engine.ActionResolver.Resolve(alice)
            .First(a => a.Verb == "take" && a.TargetId == "bob");
        Assert.True(engine.TurnManager.PerformAction(alice, takeBob).Success);
        engine.SignalBus.Drain("alice"); // ignore the pickup itself

        // Bob speaks from Alice's inventory: Alice (his carrier, same room)
        // must hear him
        var say = TestWorlds.Find(engine, "bob", "say");
        Assert.True(engine.TurnManager.PerformAction(bob, say, "Put me down!").Success);
        Assert.Contains(engine.SignalBus.Drain("alice"),
            s => s.Text.Contains("Bob says: \"Put me down!\""));

        // and after being dropped everything still works
        var dropBob = engine.ActionResolver.Resolve(alice)
            .First(a => a.Verb == "drop" && a.TargetId == "bob");
        Assert.True(engine.TurnManager.PerformAction(alice, dropBob).Success);
        Assert.True(engine.TurnManager.PerformAction(bob, say, "Thanks.").Success);
        Assert.Contains(engine.SignalBus.Drain("alice"),
            s => s.Text.Contains("Bob says: \"Thanks.\""));
    }

    [Fact]
    public void CarriedAgent_ObservesRoomEvents_AndSeesTheRoom()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.World.AddModule("bob", "portable");
        engine.World.MoveObject("bob", "room_a"); // same room as Alice
        var alice = engine.World.GetObject("alice");
        var bob = engine.World.GetObject("bob");

        var takeBob = engine.ActionResolver.Resolve(alice)
            .First(a => a.Verb == "take" && a.TargetId == "bob");
        Assert.True(engine.TurnManager.PerformAction(alice, takeBob).Success);

        // Bob hears what happens in the room he is carried through
        var say = TestWorlds.Find(engine, "alice", "say");
        Assert.True(engine.TurnManager.PerformAction(alice, say, "Gotcha!").Success);
        Assert.Contains(engine.SignalBus.Drain("bob"),
            s => s.Text.Contains("Alice says: \"Gotcha!\""));

        // and his look shows the carrier's room, not the carrier
        var look = TestWorlds.Find(engine, "bob", "look");
        var result = engine.TurnManager.PerformAction(bob, look);
        Assert.True(result.Success);
        Assert.Contains("Room A", result.Message);
    }

    [Fact]
    public void TakeSelf_IsNotOffered()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.World.AddModule("bob", "portable");
        engine.World.MoveObject("bob", "room_a");
        var bob = engine.World.GetObject("bob");

        // the agent's own portable module must not offer picking itself up
        Assert.DoesNotContain(engine.ActionResolver.Resolve(bob),
            a => (a.Verb == "take" || a.Verb == "drop") && a.TargetId == "bob");
        Assert.DoesNotContain(engine.ActionResolver.ResolvePotential(bob),
            a => (a.Verb == "take" || a.Verb == "drop") && a.TargetId == "bob");
    }

    [Fact]
    public void TakeSelf_FailsCleanly_WhenForced()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.World.AddModule("bob", "portable");
        engine.World.MoveObject("bob", "room_a");
        var bob = engine.World.GetObject("bob");

        // even if a handler is invoked directly, taking yourself is a
        // clean failure — MoveObject(mimi, mimi) would throw on the cycle
        var result = engine.TurnManager.Execute(bob, "take", "bob");
        Assert.False(result.Success);
        Assert.Equal("room_a", bob.Parent);
    }

    [Fact]
    public void PortableAgent_Llm_KeepsActingAfterBogusSelfTakePlan()
    {
        // the reported freeze: after being dropped, the LLM planned
        // "Take the Mimi" (itself) and the agent never acted again
        var llm = new SlowLlmClient();
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.World.AddModule("bob", "portable");
        engine.World.MoveObject("bob", "room_a"); // same room as Alice
        engine.PolicyRegistry.Register(new LlmPolicy(new LlmPlanner(llm, engine)));
        engine.World.SetFieldOverride("bob", "agent", "policy", World.ToJson("llm"));
        var alice = engine.World.GetObject("alice");

        engine.TurnManager.RunNpcTurns(); // start selection 1 (in flight)

        var takeBob = engine.ActionResolver.Resolve(alice)
            .First(a => a.Verb == "take" && a.TargetId == "bob");
        Assert.True(engine.TurnManager.PerformAction(alice, takeBob).Success);
        var dropBob = engine.ActionResolver.Resolve(alice)
            .First(a => a.Verb == "drop" && a.TargetId == "bob");
        Assert.True(engine.TurnManager.PerformAction(alice, dropBob).Success);

        // the in-flight plan is a bogus self-take: no match, no crash,
        // no execution — the agent just re-plans next turn
        llm.CompleteNext("Take Bob");
        TickUntil(engine, () => llm.Started == 2);

        llm.CompleteNext("Wait");
        var turn = engine.TurnManager.Turn;
        TickUntil(engine, () => engine.TurnManager.Turn > turn);
    }

    /// <summary>An LLM client whose responses are supplied manually, one at a time.</summary>
    private sealed class SlowLlmClient : ILlmClient
    {
        private readonly Queue<TaskCompletionSource<string>> _pending = new();

        public int Started { get; private set; }

        public Task<string> CompleteAsync(IReadOnlyList<LlmMessage> messages, CancellationToken ct)
        {
            Started++;
            var tcs = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Enqueue(tcs);
            return tcs.Task;
        }

        public void CompleteNext(string response) =>
            _pending.Dequeue().SetResult(response);
    }
}
