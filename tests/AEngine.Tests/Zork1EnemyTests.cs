using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.Scenarios;
using AEngine.Core.World;

namespace AEngine.Tests;

/// <summary>
/// The enemy agents: LLM-driven when an endpoint is attached, inert
/// otherwise. Their hard rules are data and stay deterministic — the
/// thief's territory fence, his certain lift from an adventurer's hands,
/// and the cyclops's rising, eventually fatal temper.
/// </summary>
public class Zork1EnemyTests
{
    private static GameEngine NewEngine(int seed = 42)
    {
        var engine = GameEngine.CreateWithBuiltinHandlers();
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scenarios", "zork1");
            if (Directory.Exists(Path.Combine(candidate, "world")))
            {
                ScenarioLoader.LoadFrom(engine, candidate);
                engine.Random = new Random(seed);
                return engine;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate scenarios/zork1.");
    }

    private static AEngine.Cli.WalkthroughResult RunScript(
        GameEngine engine, params string[] lines) =>
        AEngine.Cli.Walkthrough.Run(engine, "player", lines);

    [Fact]
    public void Thief_CannotLeave_HisTerritory_ByListingOrByForce()
    {
        var engine = NewEngine();
        var world = engine.World;
        var thief = world.GetObject("thief");
        world.MoveObject("thief", "cellar");

        // the way up to the living room never lists for him...
        Assert.DoesNotContain(engine.ActionResolver.Resolve(thief),
            a => a.Verb == "go" && a.TargetId == "cellar_up");
        // ...while the maze passages below do
        Assert.Contains(engine.ActionResolver.Resolve(thief),
            a => a.Verb == "go" && a.TargetId != "cellar_up");

        // the fence holds even against a forced march (no resolver list)
        var up = engine.ActionResolver.ResolvePotential(thief)
            .FirstOrDefault(a => a.Verb == "go" && a.TargetId == "cellar_up");
        if (up is not null)
        {
            var forced = engine.TurnManager.PerformAction(thief, up);
            Assert.False(forced.Success);
        }
        Assert.Equal("cellar", thief.Parent);

        // and the passage still works for everyone else
        var player = world.GetObject("player");
        world.MoveObject("player", "cellar");
        world.SetFieldOverride("trapdoor_state", "doorstate", "open", World.ToJson(true));
        Assert.Contains(engine.ActionResolver.Resolve(player),
            a => a.Verb == "go" && a.TargetId == "cellar_up");
    }

    [Fact]
    public void Thief_FenceSpans_TheOriginalSacredBoundary()
    {
        var engine = NewEngine();
        var world = engine.World;
        var thief = world.GetObject("thief");

        // spot-check the frontier: temple, dam base, deep mines, the house
        foreach (var (room, portal) in new[]
                 {
                     ("torch_room", "tor_south"), ("egypt_room", "eg_west"),
                     ("dam_room", "dam_down"), ("mine_1", "m1_north"),
                 })
        {
            world.MoveObject("thief", room);
            Assert.DoesNotContain(engine.ActionResolver.Resolve(thief),
                a => a.Verb == "go" && a.TargetId == portal);
        }
    }

    [Fact]
    public void Thief_MovesFreely_WithinTheUnderground()
    {
        var engine = NewEngine();
        var world = engine.World;
        var thief = world.GetObject("thief");
        world.MoveObject("thief", "maze_1");
        // the maze is dark and its hazards don't spare burglars either
        world.SetFieldOverride("thief", "agent", "alwaysLit", World.ToJson(true));

        var go = engine.ActionResolver.Resolve(thief)
            .First(a => a.Verb == "go");
        Assert.True(engine.TurnManager.PerformAction(thief, go).Success);
        Assert.NotEqual("maze_1", thief.Parent);
    }

    [Fact]
    public void Thief_CanLiftTreasure_FromTheAdventurer()
    {
        var engine = NewEngine();
        var world = engine.World;
        var thief = world.GetObject("thief");
        var player = world.GetObject("player");
        world.MoveObject("thief", "west_of_house");
        world.MoveObject("egg", "player"); // the prize, in plain sight

        // the lift lists for him, certain as the original's ROB
        var steal = engine.ActionResolver.Resolve(thief)
            .Single(a => a.Verb == "steal" && a.TargetId == "egg");
        Assert.StartsWith("Steal the jewel-encrusted egg", steal.Label);
        Assert.True(engine.TurnManager.PerformAction(thief, steal).Success);
        Assert.Equal("thief", world.GetObject("egg").Parent);
        Assert.Contains(engine.SignalBus.Drain(player.Id),
            s => s.Text.Contains("quietly lifts the jewel-encrusted egg"));

        // pickpocketing is the thief's signature: the player never lists it
        world.MoveObject("thief", "player"); // thief's own egg back in play
        Assert.DoesNotContain(engine.ActionResolver.Resolve(player),
            a => a.Verb == "steal");
    }

    [Fact]
    public void CyclopsWrath_RisesWhileLingeredAt_AndBandsAttach()
    {
        var engine = NewEngine();
        var world = engine.World;
        var player = world.GetObject("player");
        var cyclops = world.GetObject("cyclops");
        world.MoveObject("player", "cyclops_room");
        LightTheRoom(world);

        Assert.True(RunScript(engine, "Wait", "Wait").Success);
        Assert.Equal(2, engine.ModuleRegistry.ResolveInt(cyclops, "wrath", "level"));
        Assert.True(Conditions.Has(world, engine.ModuleRegistry, cyclops, "grumpy"));
        Assert.False(Conditions.Has(world, engine.ModuleRegistry, cyclops, "furious"));

        Assert.True(RunScript(engine, "Wait", "Wait").Success);
        Assert.True(Conditions.Has(world, engine.ModuleRegistry, cyclops, "furious"));
    }

    [Fact]
    public void CyclopsWrath_EatsTheLingeringTrickster_AtTheBoil()
    {
        var engine = NewEngine();
        var world = engine.World;
        var player = world.GetObject("player");
        world.MoveObject("player", "cyclops_room");
        LightTheRoom(world);

        // six turns of games and trickery
        Assert.True(RunScript(engine,
            "Wait", "Wait", "Wait", "Wait", "Wait", "Wait").Success);
        Assert.True(Conditions.Has(world, engine.ModuleRegistry, player, "dead"));
        // and the death flow did its work: a ghost at the gates
        Assert.Equal("entrance_to_hades", world.RoomOf("player").Id);
    }

    [Fact]
    public void CyclopsWrath_CoolsWhenNobodyLingers()
    {
        var engine = NewEngine();
        var world = engine.World;
        var cyclops = world.GetObject("cyclops");
        world.MoveObject("player", "cyclops_room");
        Assert.True(RunScript(engine, "Wait", "Wait").Success);
        Assert.Equal(2, engine.ModuleRegistry.ResolveInt(cyclops, "wrath", "level"));

        // gone away, the temper subsides
        world.MoveObject("player", "west_of_house");
        Assert.True(RunScript(engine, "Wait").Success);
        Assert.Equal(1, engine.ModuleRegistry.ResolveInt(cyclops, "wrath", "level"));
    }

    [Fact]
    public void CyclopsWrath_LeavesTheUlyssesRouteAlone()
    {
        // the walkthrough's business with the cyclops takes two turns and
        // must never trip the clock
        var engine = NewEngine();
        var world = engine.World;
        var cyclops = world.GetObject("cyclops");
        world.MoveObject("player", "cyclops_room");
        LightTheRoom(world);
        Assert.True(RunScript(engine, "Say: ulysses", "Go up").Success);
        Assert.True(engine.ModuleRegistry.ResolveInt(cyclops, "wrath", "level") < 4);
        Assert.False(Conditions.Has(world, engine.ModuleRegistry,
            world.GetObject("player"), "dead"));
    }

    [Fact]
    public void SleepingCyclops_IsDrivenByNothing()
    {
        // the drink-and-collapse chain attaches cond_unconscious, and
        // incapacitated agents get no policy turns at all — asleep means
        // silent even with an LLM attached
        var engine = NewEngine();
        var world = engine.World;
        var cyclops = world.GetObject("cyclops");
        Conditions.Attach(world, engine.ModuleRegistry, cyclops, "cond_unconscious");
        world.MoveObject("cyclops", "west_of_house"); // asleep beside you

        Assert.Equal("none", engine.ModuleRegistry.ResolveString(
            world.GetObject("bat"), "agent", "policy"));
        Assert.True(Health.IsIncapacitated(world, engine.ModuleRegistry, cyclops));
        engine.TurnManager.RunNpcTurns(); // nothing happens, nothing throws
    }

    [Fact]
    public void Troll_CanAttackTheAdventurer_AndCannotScoreHim()
    {
        var engine = NewEngine();
        var world = engine.World;
        var player = world.GetObject("player");
        var troll = world.GetObject("troll");
        world.MoveObject("player", "troll_room");
        LightTheRoom(world);

        // facing each other: the troll gets his swing...
        var trollMenu = engine.ActionResolver.Resolve(troll);
        Assert.Contains(trollMenu, a => a.Verb == "attack" && a.TargetId == "player");
        // ...but scoring is the adventurer's own self action, not his
        Assert.DoesNotContain(trollMenu, a => a.Verb == "score");

        // and the adventurer keeps score for himself without swinging at
        // thin air (attack is others-only)
        var playerMenu = engine.ActionResolver.Resolve(player);
        Assert.Contains(playerMenu, a => a.Verb == "score" && a.TargetId == "player");
        Assert.DoesNotContain(playerMenu, a => a.Verb == "attack" && a.TargetId == "player");
        Assert.Contains(playerMenu, a => a.Verb == "attack" && a.TargetId == "troll");
    }

    [Fact]
    public async System.Threading.Tasks.Task Troll_UnderLlmPolicy_StrikesWhenPlanned()
    {
        // regression: the troll never attacked autonomously — the
        // adventurer wasn't attackable, so no swing was ever in his menu
        var llm = new FakeLlm("troll");
        var engine = NewEngine();
        engine.PolicyRegistry.Register(new AEngine.Llm.LlmPolicy(
            new AEngine.Llm.LlmPlanner(llm, engine)));
        var world = engine.World;
        world.MoveObject("player", "troll_room");
        LightTheRoom(world);

        llm.Enqueue("Attack the adventurer");
        for (var i = 0; i < 6; i++)
        {
            engine.TurnManager.RunNpcTurns();
            await System.Threading.Tasks.Task.Delay(50);
        }
        // the swing happened: the troll's own memory holds the outcome
        // (a miss or a wound — the dice decide, but the axe was swung)
        Assert.Contains(engine.Memory.Recall("troll"),
            m => m.Contains("swing") || m.Contains("staggers") || m.Contains("wound"));
    }

    [Fact]
    public async System.Threading.Tasks.Task DeadTroll_NeitherSpeaksNorActs()
    {
        // regression: the slain troll kept his policy turns — "Me axe!
        // Me take!" from beyond the grave
        var llm = new FakeLlm("troll");
        var engine = NewEngine();
        engine.PolicyRegistry.Register(new AEngine.Llm.LlmPolicy(
            new AEngine.Llm.LlmPlanner(llm, engine)));
        var world = engine.World;
        var player = world.GetObject("player");
        var troll = world.GetObject("troll");
        world.MoveObject("player", "troll_room");
        LightTheRoom(world);

        // slain: the death blow's condition, the corpse still in the room
        Conditions.Attach(world, engine.ModuleRegistry, troll, "cond_dead");

        // speech no longer resolves for the dead — the menu has no Say
        Assert.DoesNotContain(engine.ActionResolver.Resolve(troll),
            a => a.Verb == "say");

        // and no policy turn happens, however the plan reads
        llm.Enqueue("Say: \"Me no dead!\"\nWait");
        var turn = engine.TurnManager.Turn;
        for (var i = 0; i < 6; i++)
        {
            engine.TurnManager.RunNpcTurns();
            await System.Threading.Tasks.Task.Delay(50);
        }
        Assert.Equal(turn, engine.TurnManager.Turn);
        Assert.Empty(engine.SignalBus.Drain(player.Id));
    }

    /// <summary>The cyclops room is dark; carry a lit lamp like the walkthrough does.</summary>
    private static void LightTheRoom(AEngine.Core.World.World world)
    {
        world.MoveObject("lamp", "player");
        world.SetFieldOverride("lamp", "lightsource", "on", World.ToJson(true));
    }

    [Fact]
    public async System.Threading.Tasks.Task Thief_UnderLlmPolicy_RobsTheAdventurer()
    {
        // the full loop with a scripted LLM: the policy plans, the plan
        // matches current availability, the thief lifts the goods
        var llm = new FakeLlm();
        var engine = NewEngine();
        engine.PolicyRegistry.Register(new AEngine.Llm.LlmPolicy(
            new AEngine.Llm.LlmPlanner(llm, engine)));
        var world = engine.World;
        var thief = world.GetObject("thief");
        var player = world.GetObject("player");
        world.MoveObject("thief", "west_of_house");
        world.MoveObject("egg", "player");
        world.SetFieldOverride("thief", "agent", "alwaysLit", World.ToJson(true));
        Assert.Equal("llm", engine.ModuleRegistry.ResolveString(thief, "agent", "policy"));

        llm.Enqueue("Steal the jewel-encrusted egg from the adventurer");
        engine.TurnManager.RunNpcTurns(); // start the selection
        await System.Threading.Tasks.Task.Delay(50);
        engine.TurnManager.RunNpcTurns(); // execute it

        Assert.Equal("thief", world.GetObject("egg").Parent);
        var heard = engine.SignalBus.Drain(player.Id);
        Assert.Contains(heard, s => s.Text.Contains("quietly lifts the jewel-encrusted egg"));
    }

    [Fact]
    public async System.Threading.Tasks.Task Troll_UnderLlmPolicy_AnswersAndIsHeard()
    {
        // regression: the LLM planned speech for the troll, but without
        // can_speak there was no say affordance to match — the plan died
        // silently and the player heard nothing
        var llm = new FakeLlm("troll");
        var engine = NewEngine();
        engine.PolicyRegistry.Register(new AEngine.Llm.LlmPolicy(
            new AEngine.Llm.LlmPlanner(llm, engine)));
        var world = engine.World;
        var player = world.GetObject("player");
        world.MoveObject("player", "troll_room");
        LightTheRoom(world);

        llm.Enqueue("Say: \"Grog guard. You no pass.\"\nWait");
        for (var i = 0; i < 6; i++)
        {
            engine.TurnManager.RunNpcTurns();
            await System.Threading.Tasks.Task.Delay(50);
        }
        Assert.Contains(engine.SignalBus.Drain(player.Id),
            s => s.Text.Contains("troll says: \"Grog guard. You no pass.\""));
    }

    /// <summary>
    /// A minimal scripted client: queued plans, served to whichever agent
    /// is asking (matched by the "You are &lt;name&gt;" system prompt);
    /// everyone else gets inert filler (a real server answers each call
    /// separately, so the fake must too).
    /// </summary>
    private sealed class FakeLlm(string agent = "thief") : AEngine.Llm.ILlmClient
    {
        private readonly Queue<string> _responses = new();

        public void Enqueue(string response) => _responses.Enqueue(response);

        public System.Threading.Tasks.Task<string> CompleteAsync(
            IReadOnlyList<AEngine.Llm.LlmMessage> messages,
            System.Threading.CancellationToken ct) =>
            System.Threading.Tasks.Task.FromResult(
                messages.Any(m => m.Content.Contains($"You are {agent}",
                    StringComparison.OrdinalIgnoreCase))
                    ? (_responses.Count > 0 ? _responses.Dequeue() : "")
                    : "..."); // unparseable: the other agents idle this round
    }
}
