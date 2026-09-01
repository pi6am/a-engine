using AEngine.Core.Actions;
using AEngine.Llm;

namespace AEngine.Tests;

/// <summary>
/// Parameterized speech: the say affordance lives on the can_speak module,
/// its label carries a {speech} placeholder with an optional [to X]
/// addressee (only listed when several other agents are present), and the
/// plan executor parses LLM speech lines generously (quotes optional).
/// </summary>
public class SpeechTests
{
    [Fact]
    public void SingleOtherAgent_UndirectedSayLabel()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.World.MoveObject("bob", "room_a"); // one other agent with alice

        var say = TestWorlds.Find(engine, "alice", "say");
        Assert.Equal("Say: {speech}", say.Label);
        Assert.Equal("alice", say.TargetId);
        Assert.Equal("Say what?", say.Prompt);
    }

    [Fact]
    public void MultipleOtherAgents_DirectedSayLabels()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.World.MoveObject("bob", "room_a");
        engine.World.CreateObject("carol", "room_a", "Carol");
        engine.World.AddModule("carol", "agent");
        engine.World.AddModule("carol", "can_speak");

        var says = engine.ActionResolver.Resolve(engine.World.GetObject("alice"))
            .Where(a => a.Verb == "say").ToList();
        // the broadcast entry plus one directed entry per addressee
        Assert.Equal(3, says.Count);
        Assert.Contains(says, a => a.Label == "Say: {speech}" && a.TargetId == "alice");
        Assert.Contains(says, a => a.Label == "Say [to Bob]: {speech}" && a.TargetId == "bob");
        Assert.Contains(says, a => a.Label == "Say [to Carol]: {speech}" && a.TargetId == "carol");
    }

    [Fact]
    public void AgentWithoutCanSpeak_HasNoSay()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.World.CreateObject("carol", "room_a", "Carol");
        engine.World.AddModule("carol", "agent"); // no can_speak

        var actions = engine.ActionResolver.Resolve(engine.World.GetObject("carol"));
        Assert.DoesNotContain(actions, a => a.Verb == "say");
    }

    [Fact]
    public void SpeechLine_DirectedAndQuoted_Executes()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.World.MoveObject("bob", "room_a");
        var alice = engine.World.GetObject("alice");

        var action = PlanExecutor.MatchAvailableOrPotential(
            engine, alice, "Say [to Bob]: \"Hello, how are you today?\"");
        Assert.NotNull(action);
        Assert.Equal("say", action!.Verb);
        Assert.Equal("Hello, how are you today?", action.Text);

        var result = engine.TurnManager.PerformAction(alice, action, action.Text);
        Assert.Equal(ActionOutcome.Success, result.Outcome);
        var signal = Assert.Single(engine.SignalBus.Drain("bob"));
        Assert.Equal("Alice says: \"Hello, how are you today?\"", signal.Text);
    }

    [Fact]
    public void SpeechLine_UndirectedUnquoted_Executes()
    {
        var engine = TestWorlds.NewTwoRoomEngine(); // bob in room_b: hears through the door
        var alice = engine.World.GetObject("alice");

        var action = PlanExecutor.MatchAvailableOrPotential(engine, alice, "Say: anyone there");
        Assert.NotNull(action);
        Assert.Equal("alice", action!.TargetId);
        Assert.Equal("anyone there", action.Text);

        var result = engine.TurnManager.PerformAction(alice, action, action.Text);
        Assert.Equal(ActionOutcome.Success, result.Outcome);
        var signal = Assert.Single(engine.SignalBus.Drain("bob"));
        Assert.Equal(
            "Alice says: \"anyone there\" through the wooden door to the south.",
            signal.Text);
    }

    [Fact]
    public void SpeechLine_DirectedToOneOfSeveral_PicksAddressee()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.World.MoveObject("bob", "room_a");
        engine.World.CreateObject("carol", "room_a", "Carol");
        engine.World.AddModule("carol", "agent");
        engine.World.AddModule("carol", "can_speak");
        var alice = engine.World.GetObject("alice");

        var action = PlanExecutor.MatchAvailableOrPotential(
            engine, alice, "Say [to Carol]: not you, Bob");
        Assert.NotNull(action);
        Assert.Equal("carol", action!.TargetId);
        Assert.Equal("not you, Bob", action.Text);
    }

    [Fact]
    public void SpeechLine_ViaExecute_CarriesText()
    {
        // the executor must forward the parsed speech to PerformAction
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.World.MoveObject("bob", "room_a");
        var alice = engine.World.GetObject("alice");

        var steps = new PlanExecutor(engine, alice)
            .Execute(["Say [to Bob]: \"Hello, how are you today?\""]);

        Assert.Single(steps);
        Assert.Equal(ActionOutcome.Success, steps[0].Result!.Outcome);
        Assert.Equal("You say: \"Hello, how are you today?\"", steps[0].Result!.Message);
        var signal = Assert.Single(engine.SignalBus.Drain("bob"));
        Assert.Equal("Alice says: \"Hello, how are you today?\"", signal.Text);
    }

    [Fact]
    public void SpeechLine_NoColon_Executes()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        var alice = engine.World.GetObject("alice");

        var action = PlanExecutor.MatchAvailableOrPotential(engine, alice, "Say hello world");
        Assert.NotNull(action);
        Assert.Equal("hello world", action!.Text);
    }

    [Fact]
    public void SpeechLine_QuotedWithTrailingAddressee_PicksAddressee()
    {
        // the LLM's natural paraphrase: Say: "..." to X (speech first)
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.World.MoveObject("bob", "room_a");
        engine.World.CreateObject("carol", "room_a", "Carol");
        engine.World.AddModule("carol", "agent");
        engine.World.AddModule("carol", "can_speak");
        var alice = engine.World.GetObject("alice");

        var withColon = PlanExecutor.MatchAvailableOrPotential(engine, alice, "Say: \"Hello there\" to Carol");
        Assert.NotNull(withColon);
        Assert.Equal("carol", withColon!.TargetId);
        Assert.Equal("Hello there", withColon.Text);

        var noColon = PlanExecutor.MatchAvailableOrPotential(engine, alice, "Say \"Hello there\" to Carol");
        Assert.NotNull(noColon);
        Assert.Equal("carol", noColon!.TargetId);
        Assert.Equal("Hello there", noColon.Text);
    }

    [Fact]
    public void SpeechLine_QuotedTrailingAddressee_SingleOtherAgent_StaysUndirected()
    {
        // with one other agent present the say entries are undirected; the
        // trailing addressee is parsed but doesn't disturb the speech
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.World.MoveObject("bob", "room_a");
        var alice = engine.World.GetObject("alice");

        var action = PlanExecutor.MatchAvailableOrPotential(engine, alice, "Say: \"Hello\" to Bob");
        Assert.NotNull(action);
        Assert.Equal("alice", action!.TargetId);
        Assert.Equal("Hello", action.Text);

        var result = engine.TurnManager.PerformAction(alice, action, action.Text);
        Assert.Equal("You say: \"Hello\"", result.Message);
    }

    [Fact]
    public void SpeechLine_UnquotedTrailingTo_IsPartOfTheUtterance()
    {
        // without quotes "to X" can't be told apart from the words spoken
        // ("say hello to Bob" is itself idiomatic speech) — it stays speech
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.World.MoveObject("bob", "room_a");
        var alice = engine.World.GetObject("alice");

        var action = PlanExecutor.MatchAvailableOrPotential(engine, alice, "Say hello to Bob");
        Assert.NotNull(action);
        Assert.Equal("hello to Bob", action!.Text);
    }
}
