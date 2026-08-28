namespace AEngine.Core.Runtime;

/// <summary>An action scheduled to run at a future turn.</summary>
public sealed record ScheduledAction(int WakeTurn, string AgentId, string HandlerId, string? TargetId);

/// <summary>
/// Minimal priority queue of (wakeTurn, agentId, action), ordered by wake
/// turn. Present so long-running actions have a home.
/// </summary>
public sealed class Scheduler
{
    private readonly PriorityQueue<ScheduledAction, int> _queue = new();

    public int Count => _queue.Count;

    public void Schedule(ScheduledAction action) => _queue.Enqueue(action, action.WakeTurn);

    /// <summary>Dequeue all actions due at or before <paramref name="turn"/>, in wake-turn order.</summary>
    public IReadOnlyList<ScheduledAction> CollectDue(int turn)
    {
        var due = new List<ScheduledAction>();
        while (_queue.Count > 0 && _queue.Peek().WakeTurn <= turn)
            due.Add(_queue.Dequeue());
        return due;
    }
}
