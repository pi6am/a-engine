using AEngine.Core.World;
using AEngine.Llm;

namespace AEngine.Tests;

/// <summary>
/// Agent memory: observed signals and own action results are recorded per
/// agent, bounded by the data-driven memoryLength field. The wait verb
/// passes a turn without any policy/LLM involvement.
/// </summary>
public class AgentMemoryTests
{
    [Fact]
    public void Wait_IsOffered_AndConsumesTurn()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        var alice = engine.World.GetObject("alice");

        var wait = engine.ActionResolver.Resolve(alice).FirstOrDefault(a => a.Verb == "wait");
        Assert.NotNull(wait);
        Assert.Equal("Wait", wait.Label);
        Assert.Equal("alice", wait.TargetId); // self-targeted, like look

        var turn = engine.TurnManager.Turn;
        var result = engine.TurnManager.PerformAction(alice, wait);
        Assert.True(result.Success);
        Assert.Equal("You wait.", result.Message);
        Assert.Equal(turn + 1, engine.TurnManager.Turn);
    }

    [Fact]
    public void ObservedSignals_AreRecordedInMemory()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        var alice = engine.World.GetObject("alice");
        var bob = engine.World.GetObject("bob");

        // audio crosses the closed door: Bob observes Alice's speech
        var say = TestWorlds.Find(engine, "alice", "say");
        Assert.True(engine.TurnManager.PerformAction(alice, say, "hello there").Success);

        Assert.Contains(engine.Memory.Recall("bob"), e => e.Contains("Alice says: \"hello there\""));
        // the actor's own speech is remembered as her own action
        Assert.Contains(engine.Memory.Recall("alice"), e => e == "You say: \"hello there\"");
    }

    [Fact]
    public void OwnActionResults_AreRecordedInMemory()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        var alice = engine.World.GetObject("alice");

        var take = TestWorlds.Find(engine, "alice", "take", "apple");
        Assert.True(engine.TurnManager.PerformAction(alice, take).Success);
        // failures are remembered too — they inform the next plan
        var goClosed = TestWorlds.Find(engine, "alice", "go", "door_a");
        Assert.False(engine.TurnManager.PerformAction(alice, goClosed).Success);

        var memory = engine.Memory.Recall("alice");
        Assert.Contains("You take the apple.", memory);
        Assert.Contains("The wooden door is closed.", memory);
    }

    [Fact]
    public void Look_IsRecordedCompactly()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        var alice = engine.World.GetObject("alice");

        var look = TestWorlds.Find(engine, "alice", "look");
        Assert.True(engine.TurnManager.PerformAction(alice, look).Success);

        var memory = engine.Memory.Recall("alice");
        Assert.Equal(["You look around."], memory);
    }

    [Fact]
    public void Memory_TruncatesAtConfiguredCapacity()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        var bob = engine.World.GetObject("bob");
        engine.World.SetFieldOverride("bob", "agent", "memoryLength", World.ToJson(3));

        for (var i = 1; i <= 5; i++)
            engine.Memory.Record(bob, $"event {i}");

        Assert.Equal(["event 3", "event 4", "event 5"], engine.Memory.Recall("bob"));
    }
}
