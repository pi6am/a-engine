using AEngine.Core.Actions;
using AEngine.Core.Policies;
using AEngine.Core.Runtime;
using AEngine.Core.World;

namespace AEngine.Tests;

/// <summary>
/// The built-in random policy (determinism, menu-boundedness, say text)
/// and the TurnManager's async-ready start/skip/validate-execute flow.
/// </summary>
public class PolicyTests
{
    [Fact]
    public async Task RandomPolicy_PicksOnlyFromResolvedActions()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.Random = new Random(123);
        var bob = engine.World.GetObject("bob");
        var actions = engine.ActionResolver.Resolve(bob);
        var policy = new RandomPolicy();

        for (var i = 0; i < 100; i++)
        {
            var pick = await policy.ChooseActionAsync(
                engine, bob, actions, CancellationToken.None);
            Assert.NotNull(pick);
            // every pick is a (verb, target) the resolver actually offered
            Assert.Contains(actions, a =>
                a.Verb == pick.Verb && a.TargetId == pick.TargetId);
        }
    }

    [Fact]
    public async Task RandomPolicy_Seeded_IsDeterministic()
    {
        static async Task<List<string>> Picks(int seed)
        {
            var engine = TestWorlds.NewTwoRoomEngine();
            engine.Random = new Random(seed);
            var bob = engine.World.GetObject("bob");
            var actions = engine.ActionResolver.Resolve(bob);
            var policy = new RandomPolicy();
            var result = new List<string>();
            for (var i = 0; i < 20; i++)
            {
                var pick = await policy.ChooseActionAsync(
                    engine, bob, actions, CancellationToken.None);
                result.Add($"{pick?.Verb}:{pick?.TargetId}:{pick?.Text}");
            }
            return result;
        }

        Assert.Equal(await Picks(42), await Picks(42));
    }

    [Fact]
    public async Task RandomPolicy_Say_GetsCannedPhrase()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.Random = new Random(7);
        var bob = engine.World.GetObject("bob");
        var sayOnly = engine.ActionResolver.Resolve(bob)
            .Where(a => a.Verb == "say").ToList();
        Assert.Single(sayOnly);

        var pick = await new RandomPolicy().ChooseActionAsync(
            engine, bob, sayOnly, CancellationToken.None);
        Assert.NotNull(pick);
        Assert.Equal("say", pick.Verb);
        Assert.False(string.IsNullOrWhiteSpace(pick.Text));
    }

    [Fact]
    public void RunNpcTurns_FirstCallStartsSelection_AgentSkipsTurn()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.Random = new Random(1);
        var turn0 = engine.TurnManager.Turn;

        engine.TurnManager.RunNpcTurns(); // starts bob's selection; bob skips
        Assert.Equal(turn0, engine.TurnManager.Turn);

        engine.TurnManager.RunNpcTurns(); // random policy already finished; execute
        Assert.Equal(turn0 + 1, engine.TurnManager.Turn);
    }

    [Fact]
    public void RunNpcTurns_StaleChoice_IsDiscardedNotExecuted()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        // a policy that fabricates an action the resolver never offered
        engine.PolicyRegistry.Register(new FixedPolicy("fabricator",
            new AvailableAction("take", "ghost", "Take the ghost", "take", "portable")));
        engine.World.SetFieldOverride(
            "bob", "agent", "policy", World.ToJson("fabricator"));

        var turn0 = engine.TurnManager.Turn;
        engine.TurnManager.RunNpcTurns(); // start selection
        engine.TurnManager.RunNpcTurns(); // validate -> unavailable -> discard
        Assert.Equal(turn0, engine.TurnManager.Turn); // nothing executed
        Assert.Empty(engine.World.GetObject("bob").Children); // bob took nothing

        // slot cleared: the next call starts a fresh selection without error
        engine.TurnManager.RunNpcTurns();
        engine.TurnManager.RunNpcTurns();
        Assert.Equal(turn0, engine.TurnManager.Turn);
    }

    [Fact]
    public void RunNpcTurns_ChoiceInvalidatedByWorldChange_IsDiscarded()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        // bob's policy picks 'take the pear' — a real, currently available action
        engine.PolicyRegistry.Register(new FixedPolicy("pear-picker",
            new AvailableAction("take", "pear", "Take the pear", "take", "portable")));
        engine.World.SetFieldOverride(
            "bob", "agent", "policy", World.ToJson("pear-picker"));

        engine.TurnManager.RunNpcTurns(); // start selection
        // before the selection completes, someone else removes the pear
        engine.World.MoveObject("pear", "room_a");
        var turn0 = engine.TurnManager.Turn;
        engine.TurnManager.RunNpcTurns(); // validate -> no longer available -> discard
        Assert.Equal(turn0, engine.TurnManager.Turn);
        Assert.Equal("room_a", engine.World.GetObject("pear").Parent);
    }

    [Fact]
    public void RunNpcTurns_IncompleteSelection_SkipsUntilComplete()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        var slow = new SlowPolicy();
        engine.PolicyRegistry.Register(slow);
        engine.World.SetFieldOverride(
            "bob", "agent", "policy", World.ToJson("slow"));

        var turn0 = engine.TurnManager.Turn;
        engine.TurnManager.RunNpcTurns(); // start
        engine.TurnManager.RunNpcTurns(); // still deciding -> skip
        engine.TurnManager.RunNpcTurns(); // still deciding -> skip
        Assert.Equal(turn0, engine.TurnManager.Turn);

        // the policy finally decides (valid action: bob looks around)
        var look = TestWorlds.Find(engine, "bob", "look", "bob");
        slow.Complete(look);
        engine.TurnManager.RunNpcTurns();
        Assert.Equal(turn0 + 1, engine.TurnManager.Turn);
    }

    /// <summary>A test policy that always returns the same pre-baked action.</summary>
    private sealed class FixedPolicy(string id, AvailableAction action) : IAgentPolicy
    {
        public string Id => id;

        public Task<AvailableAction?> ChooseActionAsync(
            GameEngine engine, WorldObject agent,
            IReadOnlyList<AvailableAction> actions, CancellationToken ct) =>
            Task.FromResult<AvailableAction?>(action);
    }

    /// <summary>A test policy whose selection stays in flight until completed manually.</summary>
    private sealed class SlowPolicy : IAgentPolicy
    {
        private readonly TaskCompletionSource<AvailableAction?> _tcs = new();

        public string Id => "slow";

        public Task<AvailableAction?> ChooseActionAsync(
            GameEngine engine, WorldObject agent,
            IReadOnlyList<AvailableAction> actions, CancellationToken ct) =>
            _tcs.Task;

        public void Complete(AvailableAction action) => _tcs.SetResult(action);
    }
}
