using AEngine.Core.Runtime;

namespace AEngine.Cli;

/// <summary>
/// The /undo ring: snapshots pushed before each player-driven input line,
/// capped at the last <see cref="Capacity"/> (10). Popping does NOT clear
/// the ring — undo steps backward repeatedly; only crossing a game
/// boundary does (/load restores a different game, /restart reloads the
/// scenario — stepping back across a boundary would bounce between
/// worlds).
/// </summary>
public sealed class UndoHistory
{
    private readonly List<GameSerializer.SaveData> _snapshots = [];

    public int Capacity { get; }

    public UndoHistory(int capacity = 10) => Capacity = capacity;

    /// <summary>Snapshots currently held (oldest first).</summary>
    public int Count => _snapshots.Count;

    /// <summary>Push a snapshot, evicting the oldest beyond capacity.</summary>
    public void Push(GameSerializer.SaveData snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshots.Add(snapshot);
        while (_snapshots.Count > Capacity)
            _snapshots.RemoveAt(0);
    }

    /// <summary>Take back the most recent snapshot, or null when the ring is empty.</summary>
    public GameSerializer.SaveData? Pop()
    {
        if (_snapshots.Count == 0)
            return null;
        var snapshot = _snapshots[^1];
        _snapshots.RemoveAt(_snapshots.Count - 1);
        return snapshot;
    }

    /// <summary>Drop everything (a game boundary was crossed).</summary>
    public void Clear() => _snapshots.Clear();
}
