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
/// fresh plan is requested on the next selection. New observed signals
/// (anything pending in the agent's signal queue — the queue is drained
/// into the context each time a plan is made) interrupt the cached plan
/// and trigger an immediate re-plan, so agents stay responsive to being
/// spoken to or otherwise interrupted — but only once the agent's speech
/// track is clear: while a Say is still playing out, interruptions wait
/// (the signals stay pending) and cached non-speech steps keep executing,
/// so a long utterance doesn't strand the rest of the plan ("Say …, Go
/// up" still goes up) and conversation turn-taking emerges naturally.
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
        // a companion-slot offer (speech-only or body-only, from the
        // turn-based say-alongside-act rule): only matching plan lines
        // dequeue — the rest wait for their own slot or round
        var speechSlot = actions.Count > 0 && actions.All(a => IsSpeech(engine, a));
        var bodySlot = actions.Count > 0 && actions.All(a => !IsSpeech(engine, a));

        // mid-utterance: no re-planning (pending signals keep) and no new
        // speech, but cached non-speech steps still execute
        if (engine.TurnManager.Turn < engine.TurnManager.SpeechBusyUntilTurn(agent.Id))
        {
            if (_cachedPlans.TryGetValue(agent.Id, out var talking) && talking.Count > 0)
            {
                // the next line is more talk, waiting its turn — show it
                // as words being held back, not as talking
                if (PlanExecutor.TryParseSpeech(talking.Peek(), out _, out _, out _))
                {
                    if (bodySlot)
                        return null; // not this slot's line
                    engine.TurnManager.NotePendingSpeech(agent.Id);
                }
                else
                {
                    if (speechSlot)
                        return null; // not this slot's line
                    var line = talking.Dequeue();
                    var match = PlanExecutor.MatchAvailableOrPotential(engine, agent, line);
                    if (match is not null)
                        return match;
                    _cachedPlans.Remove(agent.Id); // stale — re-plan once speech clears
                }
            }
            return null;
        }

        var interrupted = engine.SignalBus.Peek(agent.Id).Count > 0;
        if (_cachedPlans.TryGetValue(agent.Id, out var steps))
        {
            if (!interrupted)
            {
                if (steps.Count > 0)
                {
                    // slot offers only dequeue their kind of line
                    if ((speechSlot && !PlanExecutor.TryParseSpeech(steps.Peek(), out _, out _, out _)) ||
                        (bodySlot && PlanExecutor.TryParseSpeech(steps.Peek(), out _, out _, out _)))
                        return null;
                    var line = steps.Dequeue();
                    var match = PlanExecutor.MatchAvailableOrPotential(engine, agent, line);
                    if (match is not null)
                        return match;
                }
                // stale plan (or exhausted) — discard the remainder, re-plan next time
                _cachedPlans.Remove(agent.Id);
                return null;
            }
            // interrupted: drop the rest of the plan and re-plan now
            _cachedPlans.Remove(agent.Id);
        }

        // slot offers don't trigger fresh planning — a plan without a
        // matchable line simply yields the slot
        if (speechSlot || bodySlot)
            return null;

        var plan = await _planner.CreatePlanAsync(
            agent,
            "Choose your next actions. If someone spoke to you recently, consider responding " +
            "with Say. When you have nothing to do, prefer Wait over Look around. Don't " +
            "repeat an Examine you already remember unless something has changed.",
            npc: true, ct).ConfigureAwait(false);
        if (plan.Count == 0)
            return null;
        if (plan.Count > 1)
            _cachedPlans[agent.Id] = new Queue<string>(plan.Skip(1));
        return PlanExecutor.MatchAvailableOrPotential(engine, agent, plan[0]);
    }

    private static bool IsSpeech(GameEngine engine, AvailableAction action)
    {
        if (!engine.ModuleRegistry.Has(action.ModuleId))
            return false;
        return engine.ModuleRegistry.Get(action.ModuleId).Affordances
            .FirstOrDefault(a => a.Verb == action.Verb)?.Speech == true;
    }

    /// <summary>Ask the LLM for an in-character reaction to a telegraphed action.</summary>
    public Task<string?> ChooseReactionAsync(
        GameEngine engine, WorldObject defender,
        PendingReaction reaction, CancellationToken ct) =>
        _planner.ChooseReactionAsync(defender, reaction, ct);
}
