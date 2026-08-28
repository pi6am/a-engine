using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AEngine.Llm;

namespace AEngine.Tests;

/// <summary>
/// OpenAiCompatibleClient against a stub HttpListener server: request
/// shape (URL, auth header, model, messages), response parsing
/// (choices[0].message.content), and error mapping for non-200.
/// </summary>
public class LlmClientTests
{
    /// <summary>Minimal queued-response HTTP stub for the chat endpoint.</summary>
    private sealed class StubServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Queue<(int Status, string Body)> _responses = new();
        private readonly Task _loop;

        public string BaseUrl { get; }
        public string? LastMethod { get; private set; }
        public string? LastPath { get; private set; }
        public string? LastAuth { get; private set; }
        public string? LastBody { get; private set; }

        public StubServer()
        {
            var portProbe = new TcpListener(IPAddress.Loopback, 0);
            portProbe.Start();
            var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
            portProbe.Stop();

            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            _loop = Task.Run(Loop);
        }

        public void Enqueue(int status, string body)
        {
            lock (_responses)
                _responses.Enqueue((status, body));
        }

        private async Task Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (Exception) when (!_listener.IsListening)
                {
                    break;
                }

                LastMethod = ctx.Request.HttpMethod;
                LastPath = ctx.Request.Url?.AbsolutePath;
                LastAuth = ctx.Request.Headers["Authorization"];
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                LastBody = await reader.ReadToEndAsync();

                (int Status, string Body) response;
                lock (_responses)
                    response = _responses.Count > 0 ? _responses.Dequeue() : (500, "no response queued");

                var bytes = Encoding.UTF8.GetBytes(response.Body);
                ctx.Response.StatusCode = response.Status;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        }

        public void Dispose()
        {
            _listener.Close();
            try { _loop.Wait(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { /* listener closed mid-request */ }
        }
    }

    private static JsonElement LastRequestJson(StubServer server) =>
        JsonDocument.Parse(server.LastBody!).RootElement;

    [Fact]
    public async Task SendsChatRequest_ParsesReply()
    {
        using var server = new StubServer();
        server.Enqueue(200, """
            {"id":"chatcmpl-1","choices":[{"index":0,"message":{"role":"assistant","content":"Open the desk drawer"}}]}
            """);
        var client = new OpenAiCompatibleClient(new LlmOptions
        {
            BaseUrl = server.BaseUrl,
            Model = "test-model",
            ApiKey = "secret-key",
            Temperature = 0.5,
            MaxTokens = 128,
        });

        var reply = await client.CompleteAsync(
            [LlmMessage.System("you play"), LlmMessage.User("open things")], CancellationToken.None);

        Assert.Equal("Open the desk drawer", reply);

        Assert.Equal("POST", server.LastMethod);
        Assert.Equal("/v1/chat/completions", server.LastPath);
        Assert.Equal("Bearer secret-key", server.LastAuth);

        var body = LastRequestJson(server);
        Assert.Equal("test-model", body.GetProperty("model").GetString());
        Assert.Equal(0.5, body.GetProperty("temperature").GetDouble());
        Assert.Equal(128, body.GetProperty("max_tokens").GetInt32());
        var messages = body.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(2, messages.Count);
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("you play", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("open things", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task BaseUrlWithV1Suffix_DoesNotDoubleIt()
    {
        using var server = new StubServer();
        server.Enqueue(200, """{"choices":[{"message":{"content":"ok"}}]}""");
        var client = new OpenAiCompatibleClient(
            new LlmOptions { BaseUrl = server.BaseUrl + "/v1", Model = "m" });

        var reply = await client.CompleteAsync([LlmMessage.User("hi")], CancellationToken.None);

        Assert.Equal("ok", reply);
        Assert.Equal("/v1/chat/completions", server.LastPath);
    }

    [Fact]
    public async Task NoApiKey_NoAuthHeader_OptionalFieldsOmitted()
    {
        using var server = new StubServer();
        server.Enqueue(200, """{"choices":[{"message":{"content":"ok"}}]}""");
        var client = new OpenAiCompatibleClient(
            new LlmOptions { BaseUrl = server.BaseUrl, Model = "m" });

        var reply = await client.CompleteAsync([LlmMessage.User("hi")], CancellationToken.None);

        Assert.Equal("ok", reply);
        Assert.Null(server.LastAuth);
        var body = LastRequestJson(server);
        Assert.False(body.TryGetProperty("temperature", out _));
        Assert.False(body.TryGetProperty("max_tokens", out _));
    }

    [Fact]
    public async Task Non200_ThrowsWithStatusAndBody()
    {
        using var server = new StubServer();
        server.Enqueue(500, """{"error":"model exploded"}""");
        var client = new OpenAiCompatibleClient(
            new LlmOptions { BaseUrl = server.BaseUrl, Model = "m" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CompleteAsync([LlmMessage.User("hi")], CancellationToken.None));
        Assert.Contains("500", ex.Message);
        Assert.Contains("model exploded", ex.Message);
    }

    [Fact]
    public async Task ResponseWithoutChoices_Throws()
    {
        using var server = new StubServer();
        server.Enqueue(200, """{"choices":[]}""");
        var client = new OpenAiCompatibleClient(
            new LlmOptions { BaseUrl = server.BaseUrl, Model = "m" });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CompleteAsync([LlmMessage.User("hi")], CancellationToken.None));
    }
}
