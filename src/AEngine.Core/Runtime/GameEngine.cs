using AEngine.Core.Actions;
using AEngine.Core.Modules;
using AEngine.Core.Policies;
using AEngine.Core.Signals;

namespace AEngine.Core.Runtime;

/// <summary>
/// Top-level runtime: world, module registry, handler registry, policy
/// registry, signal bus, scheduler, plus the turn manager and action
/// resolver.
/// </summary>
public sealed class GameEngine
{
    public World.World World { get; }
    public ModuleRegistry ModuleRegistry { get; }
    public HandlerRegistry HandlerRegistry { get; }
    public PolicyRegistry PolicyRegistry { get; }
    public GateRegistry GateRegistry { get; }
    public SignalBus SignalBus { get; }
    public AgentMemory Memory { get; }
    public Scheduler Scheduler { get; }
    public ReactionManager Reactions { get; }
    public TurnManager TurnManager { get; }
    public ActionResolver ActionResolver { get; }

    /// <summary>Config surface for future real-time support; stage 1 is turn-based only.</summary>
    public TimeMode TimeMode { get; set; } = TimeMode.TurnBased;

    /// <summary>
    /// The ending text once the game has ended (a handler's epilogue, or
    /// <see cref="DefeatText"/> when the player is incapacitated); null
    /// while the game is still running. NPC turns stop once set.
    /// </summary>
    public string? GameOver { get; set; }

    /// <summary>Ending text for player incapacitation (scenario root <c>defeatText</c>).</summary>
    public string DefeatText { get; set; } = "Your journey ends here.";

    /// <summary>
    /// About blurb of the loaded scenario (scenario root <c>about</c>) —
    /// what it is, where it came from; shown by the CLI's /about.
    /// </summary>
    public string ScenarioAbout { get; set; } = "";

    /// <summary>Randomness source for built-in policies; settable (seed it in tests).</summary>
    public Random Random { get; set; } = new();

    /// <summary>
    /// Lock guarding world access. The REPL (via <see cref="TurnManager"/>) and
    /// any concurrent reader/writer (e.g. the debug HTTP server) must hold this
    /// lock while touching the world, registries, or scheduler.
    /// </summary>
    public object SyncRoot { get; } = new();

    public GameEngine()
    {
        World = new World.World();
        ModuleRegistry = new ModuleRegistry();
        HandlerRegistry = new HandlerRegistry();
        PolicyRegistry = new PolicyRegistry();
        GateRegistry = new GateRegistry();
        Memory = new AgentMemory(ModuleRegistry);
        SignalBus = new SignalBus(World, ModuleRegistry, Memory);
        Scheduler = new Scheduler();
        Reactions = new ReactionManager(this);
        ActionResolver = new ActionResolver(World, ModuleRegistry);
        TurnManager = new TurnManager(this);
    }

    /// <summary>Create an engine with the built-in handlers and policies registered.</summary>
    public static GameEngine CreateWithBuiltinHandlers()
    {
        var engine = new GameEngine();
        foreach (var handler in BuiltinHandlers.All())
            engine.HandlerRegistry.Register(handler);
        foreach (var handler in new IActionHandler[] { new SetHandler() })
            engine.HandlerRegistry.Register(handler);
        foreach (var gate in GateRegistry.Builtins())
            engine.GateRegistry.Register(gate);
        engine.PolicyRegistry.Register(new RandomPolicy());
        engine.PolicyRegistry.Register(new AutoPolicy());
        return engine;
    }
}
