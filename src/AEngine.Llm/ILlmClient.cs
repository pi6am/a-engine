namespace AEngine.Llm;

/// <summary>
/// Connection settings for an OpenAI-compatible chat-completions endpoint
/// (KoboldCPP, llama.cpp, OpenRouter, Kimi, DeepSeek, ...).
/// </summary>
public sealed record LlmOptions
{
    /// <summary>Server root, e.g. "http://127.0.0.1:5001" — "/v1/chat/completions" is appended.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>Model name sent in the request (some servers ignore it).</summary>
    public required string Model { get; init; }

    /// <summary>Optional API key, sent as a Bearer token when set.</summary>
    public string? ApiKey { get; init; }

    public int? MaxTokens { get; init; }
    public double? Temperature { get; init; }

    /// <summary>HTTP timeout for a single completion call, in seconds.</summary>
    public int TimeoutSeconds { get; init; } = 60;
}

/// <summary>A single chat message.</summary>
public sealed record LlmMessage(string Role, string Content)
{
    public static LlmMessage System(string content) => new("system", content);
    public static LlmMessage User(string content) => new("user", content);
}

/// <summary>Minimal chat-completions client abstraction (test seam).</summary>
public interface ILlmClient
{
    /// <summary>Return the assistant's reply content for the given conversation.</summary>
    Task<string> CompleteAsync(IReadOnlyList<LlmMessage> messages, CancellationToken ct);
}
