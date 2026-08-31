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
    private const string PlanningInstructions = """
        You receive a description of your
        current situation and a list of available actions, then a request.
        Reply with a short plan: one action per line, each copied EXACTLY as
        it appears in the available actions list. No numbering, no bullets,
        no explanations, nothing else. If an action you want is not listed
        (for example because a door is locked), plan the prerequisite steps
        first (take a key, unlock, open) — some actions only appear once
        earlier steps succeed. Exception: the Say action is parameterized —
        replace {speech} with the exact words you want to say, in quotes or
        not, e.g. Say: "Hello there." — and when it appears with [to name],
        you may keep or drop that part to choose who you address. Likewise,
        when Attack appears with [in the {part}], replace {part} with a body
        part name to aim (e.g. Attack the guard in the head) or drop the
        bracketed part entirely for an unaimed blow.
        """;

    /// <summary>
    /// The planning system prompt. NPCs get an identity framing ("You ARE
    /// the old cook...") with their character inline — without it, small
    /// models lose track of who "you" is and hold conversations with
    /// themselves. The player plans as themselves.
    /// </summary>
    private string SystemPromptFor(WorldObject agent, bool npc)
    {
        if (!npc)
            return "You are playing a text adventure. " + PlanningInstructions;
        var character = _engine.ModuleRegistry.ResolveString(agent, "agent", "character");
        var identity = $"You are {agent.Name}, a character in a text adventure game.";
        if (!string.IsNullOrWhiteSpace(character))
            identity += $" {character}";
        return identity + " Stay in character.\n" + PlanningInstructions;
    }

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
            LlmMessage.System(SystemPromptFor(agent, npc)),
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
        string[] verbs;
        lock (_engine.SyncRoot)
        {
            var actions = _engine.ActionResolver.Resolve(agent);
            labels = actions.Select(a => a.Label).ToArray();
            verbs = actions.Select(a => a.Verb).Distinct().ToArray();
        }
        return PlanParser.Parse(reply, knownVerbs: verbs, knownLabels: labels);
    }

    private const string ReactionSystemPrompt = """
        You are {name}, a character in a text adventure. Something is about
        to happen to your character. You receive a description of your
        current situation, what is happening, and a list of ways to react.
        Reply with EXACTLY ONE option, copied as listed. No explanations,
        nothing else. Choose in character, based on your goals and nature.
        """;

    /// <summary>
    /// Ask the LLM how the agent reacts to a telegraphed action; returns
    /// the chosen option id, or null (the default) when the reply matches
    /// nothing.
    /// </summary>
    public async Task<string?> ChooseReactionAsync(
        WorldObject agent, PendingReaction reaction, CancellationToken ct = default)
    {
        var context = new AgentContextBuilder(_engine).BuildContext(agent, npc: true);
        var options = string.Join("\n", reaction.Options.Select(o => "- " + o.Label));
        var messages = new[]
        {
            LlmMessage.System(ReactionSystemPrompt.Replace("{name}", agent.Name, StringComparison.Ordinal)),
            LlmMessage.User(
                $"{context}\n\n{reaction.Announcement}\nHow do you react? Options:\n{options}"),
        };
        var reply = (await _client.CompleteAsync(messages, ct).ConfigureAwait(false)).Trim();
        if (reply.Length == 0)
            return null;
        // tolerant match: exact id/label first, then containment either way
        var option = reaction.Options.FirstOrDefault(o =>
                         o.Label.Equals(reply, StringComparison.OrdinalIgnoreCase) ||
                         o.Id.Equals(reply, StringComparison.OrdinalIgnoreCase))
                     ?? reaction.Options.FirstOrDefault(o =>
                         reply.Contains(o.Label, StringComparison.OrdinalIgnoreCase) ||
                         o.Label.Contains(reply, StringComparison.OrdinalIgnoreCase));
        return option?.Id;
    }
}
