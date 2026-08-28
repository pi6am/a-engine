using System.Text;
using AEngine.Core.Runtime;
using AEngine.Core.World;

namespace AEngine.Llm;

/// <summary>
/// Builds the public-information context an LLM sees for an agent: the
/// room name/description, visible items (same visibility rules as `look` —
/// closed containers hide their contents), exits (open/closed only; lock
/// state is not observable), the agent's inventory, and the current action
/// menu labels. For NPCs additionally the agent module's character/goals
/// fields and the agent's drained signals.
/// </summary>
public sealed class AgentContextBuilder
{
    private readonly GameEngine _engine;

    public AgentContextBuilder(GameEngine engine) => _engine = engine;

    public string BuildContext(WorldObject agent, bool npc)
    {
        lock (_engine.SyncRoot)
        {
            var sb = new StringBuilder();
            var room = _engine.World.GetObject(agent.Parent);
            sb.AppendLine($"Location: {room.Name}");
            if (room.Description.Length > 0)
                sb.AppendLine(room.Description);

            var here = _engine.World.ChildrenOf(room.Id)
                .Where(c => c.Id != agent.Id && !c.HasModule("portal"))
                .ToList();
            if (here.Count > 0)
                sb.AppendLine("You see: " + string.Join(", ", here.Select(c => c.Name)));

            // contents of open containers are visible; closed ones hide theirs
            foreach (var container in here.Where(c => c.HasModule("container")))
            {
                if (!IsOpen(container))
                    continue;
                var contents = _engine.World.ChildrenOf(container.Id).ToList();
                sb.AppendLine(contents.Count == 0
                    ? $"The {container.Name} (open) is empty."
                    : $"The {container.Name} (open) contains: " +
                      string.Join(", ", contents.Select(c => c.Name)));
            }

            var exits = _engine.World.ChildrenOf(room.Id).Where(c => c.HasModule("portal")).ToList();
            if (exits.Count > 0)
            {
                var parts = exits.Select(p =>
                {
                    var dir = _engine.ModuleRegistry.ResolveString(p, "portal", "direction") ?? "somewhere";
                    var state = IsOpen(p) ? "open" : "closed";
                    return $"{dir} ({p.Name}, {state})";
                });
                sb.AppendLine("Exits: " + string.Join(", ", parts));
            }

            var items = _engine.World.ChildrenOf(agent.Id).ToList();
            sb.AppendLine(items.Count == 0
                ? "You are carrying nothing."
                : "You are carrying: " + string.Join(", ", items.Select(i => i.Name)));

            if (npc)
            {
                var character = _engine.ModuleRegistry.ResolveString(agent, "agent", "character");
                if (!string.IsNullOrWhiteSpace(character))
                    sb.AppendLine($"Character: {character}");
                var goals = _engine.ModuleRegistry.ResolveString(agent, "agent", "goals");
                if (!string.IsNullOrWhiteSpace(goals))
                    sb.AppendLine($"Goals: {goals}");

                var signals = _engine.SignalBus.Drain(agent.Id);
                if (signals.Count > 0)
                {
                    sb.AppendLine("Recent observations:");
                    foreach (var signal in signals)
                        sb.AppendLine($"- {signal.Text}");
                }
            }

            var actions = _engine.ActionResolver.Resolve(agent);
            sb.AppendLine("Available actions (use these exact labels, one per line):");
            foreach (var action in actions)
                sb.AppendLine($"- {action.Label}");

            return sb.ToString().TrimEnd();
        }
    }

    private bool IsOpen(WorldObject target)
    {
        if (target.HasModule("portal"))
        {
            var stateRef = _engine.ModuleRegistry.ResolveString(target, "portal", "stateRef");
            return stateRef is not null && _engine.World.HasObject(stateRef) &&
                _engine.ModuleRegistry.ResolveBool(
                    _engine.World.GetObject(stateRef), "doorstate", "open");
        }
        return target.HasModule("openable") &&
            _engine.ModuleRegistry.ResolveBool(target, "openable", "open");
    }
}
