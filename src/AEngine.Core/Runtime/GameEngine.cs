using AEngine.Core.Actions;
using AEngine.Core.Modules;

namespace AEngine.Core.Runtime;

/// <summary>
/// Top-level runtime: world, module registry, handler registry,
/// scheduler, plus the turn manager and action resolver.
/// </summary>
public sealed class GameEngine
{
    public World.World World { get; }
    public ModuleRegistry ModuleRegistry { get; }
    public HandlerRegistry HandlerRegistry { get; }
    public Scheduler Scheduler { get; }
    public TurnManager TurnManager { get; }
    public ActionResolver ActionResolver { get; }

    /// <summary>Config surface for future real-time support; stage 1 is turn-based only.</summary>
    public TimeMode TimeMode { get; set; } = TimeMode.TurnBased;

    public GameEngine()
    {
        World = new World.World();
        ModuleRegistry = new ModuleRegistry();
        HandlerRegistry = new HandlerRegistry();
        Scheduler = new Scheduler();
        ActionResolver = new ActionResolver(World, ModuleRegistry);
        TurnManager = new TurnManager(this);
    }

    /// <summary>Create an engine with the built-in handlers registered.</summary>
    public static GameEngine CreateWithBuiltinHandlers()
    {
        var engine = new GameEngine();
        foreach (var handler in BuiltinHandlers.All())
            engine.HandlerRegistry.Register(handler);
        return engine;
    }
}
