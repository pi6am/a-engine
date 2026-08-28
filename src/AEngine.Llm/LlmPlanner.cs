using AEngine.Core.Runtime;
using AEngine.Core.World;

namespace AEngine.Llm;

/// <summary>
/// Turns an agent's context plus a request into LLM messages and parses
/// the reply into a plan: an ordered list of action lines. For the player
/// the request is their free text; for an NPC it is "choose your next
/// actions".
/// </summary>
public sealed class LlmPlanner
{
    private const string SystemPrompt = """
        You are playing a text adventure. You receive a description of your
        current situation and a list of available actions, then a request.
        Reply with a short plan: one action per line, each copied EXACTLY as
        it appears in the available actions list. No numbering, no bullets,
        no explanations, nothing else. If an action you want is not listed
        (for example because a door is locked), plan the prerequisite steps
        first (take a key, unlock, open) — some actions only appear once
        earlier steps succeed. Exception: the Say action is parameterized —
        replace {speech} with the exact words you want to say, in quotes or
        not, e.g. Say: "Hello there." — and when it appears with [to name],
        you may keep or drop that part to choose who you address.
        """;

    private readonly ILlmClient _client;
    private readonly GameEngine _engine;

    public LlmPlanner(ILlmClient client, GameEngine engine)
    {
        _client = client;
        _engine = engine;
    }

    /// <summary>Build the messages for a planning request (exposed for tests).</summary>
    public IReadOnlyList<LlmMessage> BuildMessages(WorldObject agent, string request, bool npc)
    {
        var context = new AgentContextBuilder(_engine).BuildContext(agent, npc);
        return
        [
            LlmMessage.System(SystemPrompt),
            LlmMessage.User(context + "\n\nRequest: " + request),
        ];
    }

    /// <summary>Ask the LLM for a plan and parse the reply into action lines.</summary>
    public async Task<IReadOnlyList<string>> CreatePlanAsync(
        WorldObject agent, string request, bool npc, CancellationToken ct = default)
    {
        var reply = await _client.CompleteAsync(BuildMessages(agent, request, npc), ct)
            .ConfigureAwait(false);
        string[] labels;
        lock (_engine.SyncRoot)
            labels = _engine.ActionResolver.Resolve(agent).Select(a => a.Label).ToArray();
        return PlanParser.Parse(reply, knownLabels: labels);
    }
}
