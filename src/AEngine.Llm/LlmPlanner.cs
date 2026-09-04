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
    /// <summary>
    /// The synthetic plan line for impossible requests ("Eat the brass
    /// key" — nothing maps): advertised to the player's planner alongside
    /// the real actions, chosen only when nothing matches, and rendered
    /// through <see cref="ExplainImpossibilityAsync"/> into in-fiction
    /// prose. Wording is machine-facing and easy to iterate on.
    /// </summary>
    public const string CannotDoThat = "You Cannot Do That";

    private const string ImpossibilityInstructions = """
        If the request cannot be performed — no listed action matches it
        and no plausible prerequisite would make it possible (say, "eat
        the brass key" when eating a key is not something this world
        does) — reply with exactly one line:
        You Cannot Do That
        Never choose it when a listed action matches, even awkwardly;
        prefer planning real steps.
        """;

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
        not, e.g. Say: "Hello there." — and when it appears as Say to name,
        you may keep or drop the addressee to choose who you address.
        Likewise, when Attack appears with [in the {part}], replace {part}
        with a body
        part name to aim (e.g. Attack the guard in the head) or drop the
        bracketed part entirely for an unaimed blow.
        """;

    /// <summary>
    /// The planning system prompt. NPCs get an identity framing ("You ARE
    /// the old cook...") with their character inline — without it, small
    /// models lose track of who "you" is and hold conversations with
    /// themselves. The player plans as themselves and may be told the
    /// request is impossible.
    /// </summary>
    private string SystemPromptFor(WorldObject agent, bool npc)
    {
        var instructions = PlanningInstructions;
        if (!npc)
            instructions += "\n" + ImpossibilityInstructions;
        if (!npc)
            return "You are playing a text adventure. " + instructions;
        var character = _engine.ModuleRegistry.ResolveString(agent, "agent", "character");
        var identity = $"You are {agent.Name}, a character in a text adventure game.";
        if (!string.IsNullOrWhiteSpace(character))
            identity += $" {character}";
        return identity + " Stay in character.\n" + instructions;
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
            // the impossibility sentinel rides as a known label for the
            // player, so it survives reply parsing
            if (!npc)
                labels = labels.Append(CannotDoThat).ToArray();
            verbs = actions.Select(a => a.Verb).Distinct().ToArray();
        }
        return PlanParser.Parse(reply, knownVerbs: verbs, knownLabels: labels);
    }

    private const string ImpossibilitySystemPrompt = """
        You are the narrator of a text adventure. The player tried
        something that is not possible here — no such action exists in
        their situation. Explain, briefly and in the story's voice, why it
        doesn't happen: one or two sentences, second person, addressed to
        the player. Ground it in what they can actually perceive (the
        request and the situation given); never invent new capabilities,
        never reveal hidden state, never suggest exact commands. If the
        thing doesn't exist, say so in-world; if it exists but can't be
        used that way, say that instead.
        """;

    /// <summary>
    /// Narrate why an impossible request doesn't happen ("You Cannot Do
    /// That" plans). Falls back to a plain line when the LLM call fails.
    /// </summary>
    public async Task<string> ExplainImpossibilityAsync(
        WorldObject agent, string request, CancellationToken ct = default)
    {
        var context = new AgentContextBuilder(_engine).BuildContext(agent, npc: false);
        var messages = new[]
        {
            LlmMessage.System(ImpossibilitySystemPrompt),
            LlmMessage.User(
                $"{context}\n\nThe player tried: {request}\nWhy is this not possible?"),
        };
        try
        {
            var reply = (await _client.CompleteAsync(messages, ct).ConfigureAwait(false)).Trim();
            return reply.Length > 0 ? reply : "That's not something you can do.";
        }
        catch
        {
            return "That's not something you can do.";
        }
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
