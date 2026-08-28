using AEngine.Core.Actions;
using AEngine.Core.World;

namespace AEngine.Core.Runtime;

/// <summary>
/// Turn-based turn manager (stage 1): each performed action advances the
/// turn counter and flushes due scheduled actions.
/// </summary>
public sealed class TurnManager
{
    private readonly GameEngine _engine;

    public TurnManager(GameEngine engine) => _engine = engine;

    public int Turn { get; private set; }

    /// <summary>Execute an action for an agent and advance the turn.</summary>
    public ActionResult PerformAction(WorldObject agent, AvailableAction action)
    {
        lock (_engine.SyncRoot)
        {
            var result = Execute(agent, action.HandlerId, action.TargetId);
            AdvanceTurn();
            return result;
        }
    }

    /// <summary>Execute a handler by id without advancing the turn.</summary>
    public ActionResult Execute(WorldObject agent, string handlerId, string? targetId = null)
    {
        lock (_engine.SyncRoot)
        {
            var handler = _engine.HandlerRegistry.Get(handlerId);
            var ctx = new ActionContext
            {
                World = _engine.World,
                Modules = _engine.ModuleRegistry,
                Agent = agent,
                Target = targetId is null ? null : _engine.World.GetObject(targetId),
            };
            return handler.Execute(ctx);
        }
    }

    private void AdvanceTurn()
    {
        Turn++;
        foreach (var scheduled in _engine.Scheduler.CollectDue(Turn))
        {
            if (!_engine.World.HasObject(scheduled.AgentId))
                continue;
            var agent = _engine.World.GetObject(scheduled.AgentId);
            if (scheduled.TargetId is not null && !_engine.World.HasObject(scheduled.TargetId))
                continue;
            Execute(agent, scheduled.HandlerId, scheduled.TargetId);
        }
    }
}
