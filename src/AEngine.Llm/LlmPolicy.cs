using AEngine.Core.Actions;
using AEngine.Core.Policies;
using AEngine.Core.Runtime;
using AEngine.Core.World;

namespace AEngine.Llm;

/// <summary>
/// LLM-driven NPC policy (id "llm"). The first selection asks the LLM for
/// a full plan (async — fits TurnManager's skip-while-deciding pipeline),
/// caches the remaining steps, and each subsequent selection pops the next
/// step matched against the current available actions. A step that no
/// longer matches (stale world) discards the remainder of the plan; a
/// fresh plan is requested on the next selection.
/// </summary>
public sealed class LlmPolicy : IAgentPolicy
{
    public string Id => "llm";

    private readonly LlmPlanner _planner;
    private readonly Dictionary<string, Queue<string>> _cachedPlans = new(StringComparer.Ordinal);

    public LlmPolicy(LlmPlanner planner) => _planner = planner;

    public async Task<AvailableAction?> ChooseActionAsync(
        GameEngine engine, WorldObject agent,
        IReadOnlyList<AvailableAction> actions, CancellationToken ct)
    {
        if (_cachedPlans.TryGetValue(agent.Id, out var steps))
        {
            if (steps.Count > 0)
            {
                var line = steps.Dequeue();
                var match = PlanExecutor.MatchAvailableOrPotential(engine, agent, line);
                if (match is not null)
                    return match;
            }
            // stale plan (or exhausted) — discard the remainder, re-plan next time
            _cachedPlans.Remove(agent.Id);
            return null;
        }

        var plan = await _planner.CreatePlanAsync(
            agent, "Choose your next actions.", npc: true, ct).ConfigureAwait(false);
        if (plan.Count == 0)
            return null;
        if (plan.Count > 1)
            _cachedPlans[agent.Id] = new Queue<string>(plan.Skip(1));
        return PlanExecutor.MatchAvailableOrPotential(engine, agent, plan[0]);
    }
}
