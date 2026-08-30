namespace AEngine.Llm;

/// <summary>
/// LLM room narration (/narrate room|all): rewrites the raw "look" render
/// into prose. Per-room cache: an unchanged raw description replays the
/// cached narration without an LLM call; a changed one is narrated with
/// the previous raw text and narration as history, so the prose stays
/// consistent and calls out what changed. Purely presentational — the
/// engine and agent memory always see the raw render.
/// </summary>
public sealed class Narrator
{
    private const string SystemPrompt = """
        You narrate a text adventure. You receive the raw room description
        the game engine produced and rewrite it as vivid but concise prose
        for the player, second person ("you"). Rules:
        - Keep every fact accurate: the exits with their directions and
          open/closed state, the objects present and their state, who is
          here and what they wear or carry, your own posture. Never invent
          objects, exits, people, or events.
        - Be concise: one paragraph of two to six sentences. Atmosphere is
          welcome, but the facts come first and flavor must never drown
          them.
        - Reply with the narration only: no title, no commentary, no
          quoting of the raw description.
        """;

    private sealed record RoomNarration(string Raw, string Narration);

    private readonly ILlmClient _client;
    private readonly Dictionary<string, RoomNarration> _rooms = new(StringComparer.Ordinal);

    public Narrator(ILlmClient client) => _client = client;

    /// <summary>Build the narration request messages (exposed for tests).</summary>
    public IReadOnlyList<LlmMessage> BuildMessages(
        string? previousRaw, string? previousNarration, string raw)
    {
        var user = previousRaw is null || previousNarration is null
            ? $"""
               The room description reads:

               {raw}

               Narrate it.
               """
            : $"""
               An earlier description of this same room read:

               {previousRaw}

               Your narration of it was:

               {previousNarration}

               The room description now reads:

               {raw}

               Narrate it again. Keep the phrasing of anything unchanged
               consistent with your earlier narration, and naturally call
               out what has changed.
               """;
        return [LlmMessage.System(SystemPrompt), LlmMessage.User(user)];
    }

    /// <summary>
    /// Narrate a room's raw look render. An unchanged raw text replays the
    /// cached narration (no LLM call). Returns the raw text unchanged when
    /// the LLM replies with nothing; throws on transport errors (callers
    /// fall back to the raw render).
    /// </summary>
    public async Task<string> NarrateRoomAsync(string roomId, string raw, CancellationToken ct = default)
    {
        _rooms.TryGetValue(roomId, out var previous);
        if (previous is not null && previous.Raw == raw)
            return previous.Narration;
        var reply = (await _client.CompleteAsync(
                BuildMessages(previous?.Raw, previous?.Narration, raw), ct)
            .ConfigureAwait(false)).Trim();
        if (reply.Length == 0)
            return raw;
        _rooms[roomId] = new RoomNarration(raw, reply);
        return reply;
    }
}
