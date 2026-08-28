using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AEngine.Llm;

/// <summary>
/// ILlmClient over the OpenAI chat-completions schema. BaseUrl may be given
/// with or without the /v1 suffix (e.g. http://host:5000 or
/// http://host:5000/v1); the request goes to {base}/chat/completions.
/// Base-class-library only: HttpClient + System.Text.Json.
/// </summary>
public sealed class OpenAiCompatibleClient : ILlmClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly LlmOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public OpenAiCompatibleClient(LlmOptions options, HttpClient? http = null)
    {
        _options = options;
        _http = http ?? new HttpClient();
        _ownsHttp = http is null;
        if (_ownsHttp)
            _http.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds));
    }

    public async Task<string> CompleteAsync(IReadOnlyList<LlmMessage> messages, CancellationToken ct)
    {
        var request = new ChatRequest
        {
            Model = _options.Model,
            Messages = messages.Select(m => new ChatMessage { Role = m.Role, Content = m.Content }).ToArray(),
            Temperature = _options.Temperature,
            MaxTokens = _options.MaxTokens,
        };
        var baseUrl = _options.BaseUrl.TrimEnd('/');
        var url = baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? baseUrl + "/chat/completions"
            : baseUrl + "/v1/chat/completions";

        // Some local servers (KoboldCPP) answer HTTP/1.0-style: they close the
        // connection after each response without saying so. HttpClient then pools
        // a dead connection and the next send fails with "response ended
        // prematurely". Retry once — the retry opens a fresh connection.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await SendOnceAsync(url, request, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt == 0)
            {
                // stale pooled connection — retry once
            }
        }
    }

    private async Task<string> SendOnceAsync(string url, ChatRequest request, CancellationToken ct)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(request, options: JsonOptions),
        };
        if (!string.IsNullOrEmpty(_options.ApiKey))
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        using var response = await _http.SendAsync(httpRequest, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"LLM endpoint returned {(int)response.StatusCode} ({response.ReasonPhrase}): {body}");
        }

        ChatResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ChatResponse>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"LLM endpoint returned malformed JSON: {ex.Message}. Body: {body}");
        }
        var content = parsed?.Choices is { Length: > 0 } choices
            ? choices[0].Message?.Content
            : null;
        return content ?? throw new InvalidOperationException(
            $"LLM endpoint returned no choices[0].message.content. Body: {body}");
    }

    private sealed class ChatRequest
    {
        [JsonPropertyName("model")] public string? Model { get; set; }
        [JsonPropertyName("messages")] public ChatMessage[]? Messages { get; set; }
        [JsonPropertyName("temperature")] public double? Temperature { get; set; }
        [JsonPropertyName("max_tokens")] public int? MaxTokens { get; set; }
    }

    private sealed class ChatMessage
    {
        [JsonPropertyName("role")] public string? Role { get; set; }
        [JsonPropertyName("content")] public string? Content { get; set; }
    }

    private sealed class ChatResponse
    {
        [JsonPropertyName("choices")] public Choice[]? Choices { get; set; }
    }

    private sealed class Choice
    {
        [JsonPropertyName("message")] public ChatMessage? Message { get; set; }
    }
}
