using AEngine.Core.Actions;
using AEngine.Core.Runtime;

namespace AEngine.Cli;

/// <summary>
/// POV switching for the REPL (<c>/control</c>): which agent the human's
/// input drives. The POV agent gets policy <c>"player"</c> — the marker
/// for an externally driven agent the turn manager never auto-runs — and
/// while you're away the scenario's true player goes inert
/// (<c>"none"</c>: not a registered policy, so nothing drives it, and
/// reactions against it fall to their deadline default). A hijacked
/// NPC's original policy is remembered here and restored the moment you
/// switch away from it.
/// </summary>
public sealed class ControlSwitcher
{
    private readonly GameEngine _engine;
    private readonly string _truePlayerId;
    // hijacked agent id -> policy before the takeover (restored on release)
    private readonly Dictionary<string, string> _originalPolicies = new(StringComparer.Ordinal);

    public ControlSwitcher(GameEngine engine, string truePlayerId)
    {
        _engine = engine;
        _truePlayerId = truePlayerId;
        CurrentId = truePlayerId;
    }

    /// <summary>The agent the human currently controls.</summary>
    public string CurrentId { get; private set; }

    private bool IsAway => CurrentId != _truePlayerId;

    /// <summary>
    /// Switch control to the agent with the given id (exact match, then
    /// case-insensitive). Returns false — with a user-facing reason — for
    /// unknown ids, non-agents, the current POV, or an incapacitated
    /// agent (defeat is checked against the POV; possessing a KO'd body
    /// would end the game instantly).
    /// </summary>
    public bool TrySwitch(string targetId, out string message)
    {
        lock (_engine.SyncRoot)
        {
            var target = ResolveAgent(targetId);
            if (target is null)
            {
                message = $"No agent '{targetId}' — /control with no arguments lists them.";
                return false;
            }
            if (target.Id == CurrentId)
            {
                message = $"You're already controlling {target.Name} ({target.Id}).";
                return false;
            }
            if (Health.IsIncapacitated(_engine.World, _engine.ModuleRegistry, target))
            {
                message = $"{target.Name} is incapacitated.";
                return false;
            }

            Release(CurrentId);
            TakeOver(target);
            CurrentId = target.Id;
            message = $"You are now controlling {target.Name} ({target.Id}).";
            if (IsAway && _engine.World.HasObject(_truePlayerId))
                message += $" {_engine.World.GetObject(_truePlayerId).Name} is on their own until you return (/control {_truePlayerId}).";
        }
        return true;
    }

    /// <summary>Every agent and their control state, for <c>/control</c> with no arguments.</summary>
    public IReadOnlyList<string> Describe()
    {
        lock (_engine.SyncRoot)
        {
            var lines = new List<string>();
            foreach (var obj in _engine.World.Objects.Values
                         .Where(o => o.HasModule("agent"))
                         .OrderBy(o => o.Id, StringComparer.Ordinal))
            {
                var policy = _engine.ModuleRegistry.ResolveString(obj, "agent", "policy") ?? "player";
                var note = obj.Id == CurrentId ? "  <- you"
                    : obj.Id == _truePlayerId && IsAway ? "  (idle while you're away)"
                    : "";
                lines.Add($"{obj.Id,-12} {obj.Name} ({policy}){note}");
            }
            return lines;
        }
    }

    /// <summary>Give the displaced agent back its own life: the true player idles ("none"), a hijacked NPC gets its original policy.</summary>
    private void Release(string id)
    {
        if (id == _truePlayerId)
        {
            SetPolicy(id, "none");
            return;
        }
        if (_originalPolicies.TryGetValue(id, out var original))
        {
            SetPolicy(id, original);
            _originalPolicies.Remove(id);
        }
    }

    private void TakeOver(Core.World.WorldObject target)
    {
        if (target.Id != _truePlayerId)
            _originalPolicies[target.Id] =
                _engine.ModuleRegistry.ResolveString(target, "agent", "policy") ?? "player";
        SetPolicy(target.Id, "player");
    }

    private void SetPolicy(string id, string policy) =>
        _engine.World.SetFieldOverride(id, "agent", "policy",
            Core.World.World.ToJson(policy));

    private Core.World.WorldObject? ResolveAgent(string id)
    {
        var obj = _engine.World.HasObject(id)
            ? _engine.World.GetObject(id)
            : _engine.World.Objects.Values.FirstOrDefault(
                o => o.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        return obj is not null && obj.HasModule("agent") ? obj : null;
    }
}
