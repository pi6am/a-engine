namespace AEngine.Llm;

/// <summary>
/// Canned-response ILlmClient for tests and offline development. Each call
/// dequeues the next queued response; calls with an empty queue throw.
/// </summary>
public sealed class FakeLlmClient : ILlmClient
{
    private readonly Queue<string> _responses = new();

    /// <summary>Messages from the most recent call, for assertions.</summary>
    public IReadOnlyList<LlmMessage>? LastMessages { get; private set; }

    public int Remaining => _responses.Count;

    public FakeLlmClient Enqueue(string response)
    {
        _responses.Enqueue(response);
        return this;
    }

    public Task<string> CompleteAsync(IReadOnlyList<LlmMessage> messages, CancellationToken ct)
    {
        LastMessages = messages;
        if (_responses.Count == 0)
            throw new InvalidOperationException("FakeLlmClient: no canned response queued.");
        return Task.FromResult(_responses.Dequeue());
    }
}
