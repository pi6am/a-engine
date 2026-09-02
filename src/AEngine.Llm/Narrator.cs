namespace AEngine.Llm;

/// <summary>
/// LLM narration (/narrate): rewrites raw engine text into prose. Room
/// narration (room|all) rewrites the raw "look" render with a per-room
/// cache: an unchanged raw description replays the cached narration
/// without an LLM call; a changed one is narrated with the previous raw
/// text and narration as history, so the prose stays consistent and calls
/// out what changed. Event narration (actions|all) rewrites a batch of
/// raw action outcomes and observations (one batch per player input in
/// turn-based mode, one per world-clock tick in real-time) — no cache,
/// events don't repeat. Purely presentational — the engine and agent
/// memory always see the raw render.
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

    private const string EventsSystemPrompt = """
        You narrate a text adventure. You receive raw event lines the game
        engine produced — what the player's character did and what they
        observed happening around them — and rewrite them as vivid but
        concise prose for the player, second person ("you"). Rules:
        - Keep every fact accurate: who did what, to whom, with what, and
          how it turned out, including any numbers. Never invent events,
          objects, or people, and never drop an event.
        - Speech is verbatim: when someone says something, quote their
          exact words unchanged — never paraphrase, summarize, or
          translate them.
        - Merge the lines into flowing prose — one short paragraph, two
          when the events are many — in the order they happened. Do not
          restate them as a list.
        - Reply with the narration only: no commentary, no quoting of the
          raw lines.
        """;

    private sealed record RoomNarration(string Raw, string Narration);

    private readonly ILlmClient _client;
    private readonly string _roomSystemPrompt;
    private readonly string _eventsSystemPrompt;
    private readonly Dictionary<string, RoomNarration> _rooms = new(StringComparer.Ordinal);

    /// <param name="playerName">The player character's name. Raw engine text
    /// names them ("Max's bed"); the narration must render them as "you".</param>
    public Narrator(ILlmClient client, string? playerName = null)
    {
        _client = client;
        // the same second-person rule for both prompts: the player character
        // is always "you"/"your", never named — their possessions included
        var playerRule = string.IsNullOrWhiteSpace(playerName)
            ? ""
            : $"""
               - The player's character is named "{playerName}". Always refer
                 to them as "you"/"your", never by name — their possessions
                 included ("your bed", not "{playerName}'s bed").
               """;
        _roomSystemPrompt = SystemPrompt + playerRule;
        _eventsSystemPrompt = EventsSystemPrompt + playerRule;
    }

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
        return [LlmMessage.System(_roomSystemPrompt), LlmMessage.User(user)];
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

    /// <summary>Build the event-narration request messages (exposed for tests).</summary>
    public IReadOnlyList<LlmMessage> BuildEventMessages(IReadOnlyList<string> rawLines) =>
        [LlmMessage.System(_eventsSystemPrompt),
         LlmMessage.User($"The events read:\n\n{string.Join('\n', rawLines)}\n\nNarrate them. Paraphrase related events and make them flow together, as long as you preserve the sense of what occurred. Use synonyms to introduce variation, again preserving the sense.")];

    /// <summary>
    /// Narrate a batch of raw event lines (action outcomes and
    /// observations) as one prose block — one LLM call per batch, so the
    /// caller controls the batching window. Returns null on an empty batch
    /// or an empty reply (callers fall back to the raw lines); throws on
    /// transport errors.
    /// </summary>
    public async Task<string?> NarrateEventsAsync(IReadOnlyList<string> rawLines, CancellationToken ct = default)
    {
        if (rawLines.Count == 0)
            return null;
        var reply = (await _client.CompleteAsync(BuildEventMessages(rawLines), ct)
            .ConfigureAwait(false)).Trim();
        return reply.Length == 0 ? null : reply;
    }
}
