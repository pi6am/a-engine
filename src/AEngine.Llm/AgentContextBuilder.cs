using System.Text;
using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.World;

namespace AEngine.Llm;

/// <summary>
/// Builds the public-information context an LLM sees for an agent: the
/// room name/description, visible items (same visibility rules as `look` —
/// closed containers hide their contents), exits (open/closed only; lock
/// state is not observable), the agent's inventory, and the current action
/// menu labels. NPCs additionally get the agent module's
/// character/goals/traits fields; every agent's context carries their
/// memory of recent observations and own actions.
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
            var room = _engine.World.RoomOf(agent.Id);
            sb.AppendLine($"Location: {room.Name}");
            if (room.Description.Length > 0)
                sb.AppendLine(room.Description);
            if (Perception.PostureLine(_engine.World, _engine.ModuleRegistry, agent) is { } posture)
                sb.AppendLine(posture);
            foreach (var line in Conditions.SelfLines(_engine.World, _engine.ModuleRegistry, agent))
                sb.AppendLine(line);

            // same rendering as look: state annotations, open containers'
            // contents listed as separate entries
            var visible = Perception.DescribeRoomContents(
                _engine.World, _engine.ModuleRegistry, room, agent.Id);
            if (visible.Count > 0)
                sb.AppendLine("You see: " + string.Join(", ", visible));

            foreach (var line in Perception.DressedLines(
                         _engine.World, _engine.ModuleRegistry, room, agent.Id))
                sb.AppendLine(line);

            var exits = _engine.World.ChildrenOf(room.Id).Where(c => c.HasModule("portal")).ToList();
            if (exits.Count > 0)
            {
                var parts = exits.Select(p =>
                {
                    var dir = _engine.ModuleRegistry.ResolveString(p, "portal", "direction") ?? "somewhere";
                    var state = Perception.IsOpen(_engine.World, _engine.ModuleRegistry, p) ? "open" : "closed";
                    return $"{dir} ({p.Name}, {state})";
                });
                sb.AppendLine("Exits: " + string.Join(", ", parts));
            }

            var items = _engine.World.ChildrenOf(agent.Id)
                .Where(i => !Conditions.IsInternal(i)).ToList();
            sb.AppendLine(items.Count == 0
                ? "You are carrying nothing."
                : "You are carrying: " + string.Join(", ", items.Select(i => i.Name)));
            // remembered whereabouts of notable items not currently in
            // view (ItemReport refreshes the knowledge first — the same
            // sweep decides what not to repeat)
            var important = Knowledge.ItemReport(_engine, agent);
            if (important.Count > 0)
                sb.AppendLine("Important items: " + string.Join(", ", important));
            foreach (var line in Condition.SelfLines(_engine.World, _engine.ModuleRegistry, agent))
                sb.AppendLine(line);

            if (npc)
            {
                var character = _engine.ModuleRegistry.ResolveString(agent, "agent", "character");
                if (!string.IsNullOrWhiteSpace(character))
                    sb.AppendLine($"Character: {character}");
                var goals = _engine.ModuleRegistry.ResolveString(agent, "agent", "goals");
                var conditionGoals = Conditions.GoalText(_engine.World, _engine.ModuleRegistry, agent);
                var allGoals = string.Join(" ", new[] { goals, conditionGoals }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
                if (allGoals.Length > 0)
                    sb.AppendLine($"Goals: {allGoals}");
                var traits = _engine.ModuleRegistry.ResolveString(agent, "agent", "traits");
                var conditionTraits = Conditions.TraitText(_engine.World, _engine.ModuleRegistry, agent);
                var allTraits = string.Join(" ", new[] { traits, conditionTraits }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
                if (allTraits.Length > 0)
                    sb.AppendLine($"Traits: {allTraits}");

                // signal delivery already recorded observations into
                // memory; draining here just marks the pending queue as
                // seen (it is also LlmPolicy's re-plan interrupt signal)
                _engine.SignalBus.Drain(agent.Id);
            }

            // memory is shown to players too — displayed signals keep
            // accumulating here (capped at memoryLength) so plans made
            // after watching events unfold still know what happened. The
            // player's pending queue is NOT drained: the console display
            // path owns it.
            var memory = _engine.Memory.Recall(agent.Id);
            if (memory.Count > 0)
            {
                sb.AppendLine("Recent observations and actions (oldest first):");
                foreach (var entry in memory)
                    sb.AppendLine($"- {entry}");
            }

            var actions = _engine.ActionResolver.Resolve(agent);
            sb.AppendLine("Available actions (use these exact labels, one per line):");
            foreach (var action in actions)
                sb.AppendLine($"- {action.Label}");

            return sb.ToString().TrimEnd();
        }
    }
}
