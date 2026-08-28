using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AEngine.Core.Runtime;
using AEngine.Core.World;

namespace AEngine.DebugServer;

/// <summary>
/// Debug REST API hosting the engine's world state, built on
/// <see cref="HttpListener"/> (base class library — no dependencies), bound
/// to loopback only. Opt-in (the CLI enables it via --debug-api),
/// unauthenticated — never expose it beyond localhost. All world access is
/// serialized through <see cref="GameEngine.SyncRoot"/>, shared with the
/// REPL's TurnManager.
/// </summary>
public sealed class DebugServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly GameEngine _engine;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Route> _routes = [];
    private Task? _loop;

    /// <summary>The bound port.</summary>
    public int Port { get; }

    /// <summary>Listening address.</summary>
    public Uri Address => new($"http://127.0.0.1:{Port}/");

    /// <summary>
    /// Build the server. Port 0 picks a free ephemeral port (found by probing
    /// with a TcpListener — a tiny race, acceptable for a dev tool).
    /// </summary>
    public DebugServer(GameEngine engine, int port = 5050)
    {
        _engine = engine;
        Port = port == 0 ? FindFreePort() : port;
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        MapRoutes();
    }

    private static int FindFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>Start accepting requests on a background task.</summary>
    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(ListenLoopAsync);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Close(); // unblocks GetContextAsync
        try { _loop?.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { /* listener closed mid-request */ }
        _cts.Dispose();
    }

    private async Task ListenLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (HttpListenerException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            try
            {
                await HandleAsync(context);
            }
            catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
            {
                // client disconnected or listener stopped mid-response
            }
        }
    }

    // ---- routing ----

    private sealed record ApiResponse(int Status, object? Body = null)
    {
        public static readonly ApiResponse NoContent = new(204);
    }

    private sealed record Route(
        string Method,
        string[] Segments,
        Func<HttpListenerRequest, Dictionary<string, string>, Task<ApiResponse>> Handler);

    private void MapRoutes()
    {
        _routes.Add(new("GET", S("api", "health"),
            (_, _) => Task.FromResult(new ApiResponse(200, new { status = "ok" }))));

        _routes.Add(new("GET", S("api", "engine"), (_, _) => Locked(() =>
        {
            return Ok(new
            {
                timeMode = _engine.TimeMode,
                currentTurn = _engine.TurnManager.Turn,
                pendingActions = _engine.Scheduler.Pending.Select(a => new
                {
                    wakeTurn = a.WakeTurn,
                    agentId = a.AgentId,
                    handlerId = a.HandlerId,
                    targetId = a.TargetId,
                }),
            });
        })));

        _routes.Add(new("GET", S("api", "world", "tree"), (_, _) => Locked(() =>
            Ok(TreeNode(_engine.World.GetObject(World.RootId))))));

        _routes.Add(new("GET", S("api", "objects"), (_, _) => Locked(() =>
            Ok(_engine.World.Objects.Values
                .OrderBy(o => o.Id, StringComparer.Ordinal)
                .Select(o => new
                {
                    id = o.Id,
                    name = o.Name,
                    parent = o.Parent,
                    modules = o.Modules.Select(m => m.ModuleId),
                })))));

        _routes.Add(new("GET", S("api", "objects", "{id}"), (_, v) => Locked(() =>
            Ok(ObjectDetail(_engine.World.GetObject(v["id"]))))));

        _routes.Add(new("POST", S("api", "objects"), async (req, _) =>
        {
            var body = await ReadJson<CreateObjectRequest>(req)
                ?? throw new ArgumentException("Request body must be a JSON object with 'id' and 'parentId'.");
            return await Locked(() =>
            {
                var obj = _engine.World.CreateObject(
                    body.Id, body.ParentId, body.Name ?? "", body.Description ?? "");
                return new ApiResponse(201, ObjectDetail(obj));
            });
        }));

        _routes.Add(new("DELETE", S("api", "objects", "{id}"), (_, v) => Locked(() =>
        {
            _engine.World.DestroyObject(v["id"]);
            return ApiResponse.NoContent;
        })));

        _routes.Add(new("POST", S("api", "objects", "{id}", "move"), async (req, v) =>
        {
            var body = await ReadJson<MoveObjectRequest>(req)
                ?? throw new ArgumentException("Request body must be a JSON object with 'parentId'.");
            return await Locked(() =>
            {
                _engine.World.MoveObject(v["id"], body.ParentId);
                return Ok(ObjectDetail(_engine.World.GetObject(v["id"])));
            });
        }));

        _routes.Add(new("PUT", S("api", "objects", "{id}", "attributes", "{name}"), async (req, v) =>
        {
            var value = await ReadJsonElement(req);
            return await Locked(() =>
            {
                _engine.World.SetAttribute(v["id"], v["name"], value);
                return Ok(ObjectDetail(_engine.World.GetObject(v["id"])));
            });
        }));

        _routes.Add(new("DELETE", S("api", "objects", "{id}", "attributes", "{name}"), (_, v) => Locked(() =>
        {
            return _engine.World.RemoveAttribute(v["id"], v["name"])
                ? ApiResponse.NoContent
                : new ApiResponse(404, new { error = $"Object '{v["id"]}' has no attribute '{v["name"]}'." });
        })));

        _routes.Add(new("PUT", S("api", "objects", "{id}", "modules", "{moduleId}"), async (req, v) =>
        {
            var body = await ReadJson<OverridesRequest>(req);
            return await Locked(() =>
            {
                if (!_engine.ModuleRegistry.Has(v["moduleId"]))
                    throw new KeyNotFoundException($"No module with id '{v["moduleId"]}'.");
                _engine.World.AddModule(v["id"], v["moduleId"]);
                if (body?.Overrides is { } overrides)
                    foreach (var (field, value) in overrides)
                        _engine.World.SetFieldOverride(v["id"], v["moduleId"], field, value);
                return Ok(ObjectDetail(_engine.World.GetObject(v["id"])));
            });
        }));

        _routes.Add(new("DELETE", S("api", "objects", "{id}", "modules", "{moduleId}"), (_, v) => Locked(() =>
        {
            var obj = _engine.World.GetObject(v["id"]);
            if (!obj.HasModule(v["moduleId"]))
                return new ApiResponse(404,
                    new { error = $"Object '{v["id"]}' does not have module '{v["moduleId"]}' attached." });
            _engine.World.RemoveModule(v["id"], v["moduleId"]);
            return ApiResponse.NoContent;
        })));

        _routes.Add(new("PUT", S("api", "objects", "{id}", "modules", "{moduleId}", "fields", "{field}"),
            async (req, v) =>
        {
            var value = await ReadJsonElement(req);
            return await Locked(() =>
            {
                _engine.World.SetFieldOverride(v["id"], v["moduleId"], v["field"], value);
                return Ok(ObjectDetail(_engine.World.GetObject(v["id"])));
            });
        }));

        _routes.Add(new("GET", S("api", "modules"), (_, _) => Locked(() =>
            Ok(_engine.ModuleRegistry.Modules.Values
                .OrderBy(m => m.Id, StringComparer.Ordinal)
                .Select(m => new
                {
                    id = m.Id,
                    name = m.Name,
                    fields = m.Fields.Select(f => new { name = f.Name, type = f.Type, @default = f.Default }),
                    affordances = m.Affordances.Select(a => new { verb = a.Verb, handler = a.Handler, requires = a.Requires }),
                })))));

        _routes.Add(new("GET", S("api", "actions"), (req, _) => Locked(() =>
        {
            var agentId = req.QueryString["agentId"];
            if (string.IsNullOrEmpty(agentId))
                return new ApiResponse(400, new { error = "Query parameter 'agentId' is required." });
            var agent = _engine.World.GetObject(agentId);
            return Ok(_engine.ActionResolver.Resolve(agent).Select(a => new
            {
                verb = a.Verb,
                targetId = a.TargetId,
                label = a.Label,
                handlerId = a.HandlerId,
            }));
        })));

        _routes.Add(new("POST", S("api", "actions", "execute"), async (req, _) =>
        {
            var body = await ReadJson<ExecuteActionRequest>(req);
            if (body is null || string.IsNullOrWhiteSpace(body.AgentId) || string.IsNullOrWhiteSpace(body.Verb))
                throw new ArgumentException("Request body must be a JSON object with 'agentId' and 'verb'.");
            return await Locked(() =>
            {
                var agent = _engine.World.GetObject(body.AgentId); // unknown agent -> 404
                var action = _engine.ActionResolver.Resolve(agent).FirstOrDefault(a =>
                    a.Verb == body.Verb && a.TargetId == body.TargetId);
                if (action is null)
                    return new ApiResponse(404, new
                    {
                        error = $"Agent '{body.AgentId}' has no available action " +
                                $"'{body.Verb}' on target '{body.TargetId}'.",
                    });
                // PerformAction locks SyncRoot itself (re-entrant here) and advances the turn.
                var result = _engine.TurnManager.PerformAction(agent, action);
                return Ok(new
                {
                    success = result.Success,
                    message = result.Message,
                    turn = _engine.TurnManager.Turn,
                });
            });
        }));
    }

    private static string[] S(params string[] segments) => segments;

    private static ApiResponse Ok(object body) => new(200, body);

    /// <summary>Run world access under the engine lock, then lift to a task.</summary>
    private Task<ApiResponse> Locked(Func<ApiResponse> work)
    {
        lock (_engine.SyncRoot)
            return Task.FromResult(work());
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var segments = request.Url!.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();

        ApiResponse response;
        if (request.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            // CORS preflight for the future web client
            response = ApiResponse.NoContent;
        }
        else
        {
            try
            {
                var (route, values) = Match(request.HttpMethod, segments);
                if (route is null)
                {
                    var pathMatches = _routes.Any(r => SegmentsMatch(r.Segments, segments) is not null);
                    response = pathMatches
                        ? new ApiResponse(405, new { error = $"Method {request.HttpMethod} not allowed on this path." })
                        : new ApiResponse(404, new { error = "Unknown endpoint." });
                }
                else
                {
                    response = await route.Handler(request, values!);
                }
            }
            catch (KeyNotFoundException ex)
            {
                response = new ApiResponse(404, new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                response = new ApiResponse(409, new { error = ex.Message });
            }
            catch (JsonException ex)
            {
                response = new ApiResponse(400, new { error = $"Invalid JSON: {ex.Message}" });
            }
            catch (ArgumentException ex)
            {
                response = new ApiResponse(400, new { error = ex.Message });
            }
        }

        await SendAsync(context, response);
    }

    private (Route? Route, Dictionary<string, string>? Values) Match(string method, string[] segments)
    {
        foreach (var route in _routes)
        {
            if (!route.Method.Equals(method, StringComparison.OrdinalIgnoreCase))
                continue;
            if (SegmentsMatch(route.Segments, segments) is { } values)
                return (route, values);
        }
        return (null, null);
    }

    private static Dictionary<string, string>? SegmentsMatch(string[] pattern, string[] segments)
    {
        if (pattern.Length != segments.Length)
            return null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < pattern.Length; i++)
        {
            var p = pattern[i];
            if (p.Length >= 2 && p[0] == '{' && p[^1] == '}')
                values[p[1..^1]] = segments[i];
            else if (!p.Equals(segments[i], StringComparison.Ordinal))
                return null;
        }
        return values;
    }

    // ---- request bodies ----

    private static async Task<string> ReadBodyAsync(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        return await reader.ReadToEndAsync();
    }

    /// <summary>Read the request body as a raw JSON value (400 on bad JSON).</summary>
    private static async Task<JsonElement> ReadJsonElement(HttpListenerRequest request)
    {
        var body = await ReadBodyAsync(request);
        if (string.IsNullOrWhiteSpace(body))
            throw new ArgumentException("Request body must contain a JSON value.");
        return JsonSerializer.Deserialize<JsonElement>(body, JsonOptions);
    }

    private static async Task<T?> ReadJson<T>(HttpListenerRequest request)
    {
        var body = await ReadBodyAsync(request);
        return string.IsNullOrWhiteSpace(body)
            ? default
            : JsonSerializer.Deserialize<T>(body, JsonOptions);
    }

    // ---- responses ----

    private static async Task SendAsync(HttpListenerContext context, ApiResponse response)
    {
        var res = context.Response;
        res.StatusCode = response.Status;
        // permissive CORS for the future browser-based debug client
        res.Headers["Access-Control-Allow-Origin"] = "*";
        res.Headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, DELETE, OPTIONS";
        res.Headers["Access-Control-Allow-Headers"] = "Content-Type";

        if (response.Body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response.Body, JsonOptions));
            res.ContentType = "application/json; charset=utf-8";
            res.ContentLength64 = bytes.Length;
            await res.OutputStream.WriteAsync(bytes);
        }
        res.Close();
    }

    // ---- payload shaping ----

    private object TreeNode(WorldObject obj) => new
    {
        id = obj.Id,
        name = obj.Name,
        children = obj.Children.Select(childId => TreeNode(_engine.World.GetObject(childId))),
    };

    private object ObjectDetail(WorldObject obj) => new
    {
        id = obj.Id,
        name = obj.Name,
        description = obj.Description,
        parent = obj.Parent,
        children = obj.Children,
        attributes = obj.Attributes,
        modules = obj.Modules.Select(attachment => new
        {
            moduleId = attachment.ModuleId,
            overrides = attachment.Overrides,
            fields = _engine.ModuleRegistry.Has(attachment.ModuleId)
                ? _engine.ModuleRegistry.Get(attachment.ModuleId).Fields.ToDictionary(
                    f => f.Name,
                    f => _engine.ModuleRegistry.ResolveField(obj, attachment.ModuleId, f.Name))
                : null,
        }),
    };

    private sealed record CreateObjectRequest(
        string Id, string ParentId, string? Name = null, string? Description = null);

    private sealed record MoveObjectRequest(string ParentId);

    private sealed record OverridesRequest(Dictionary<string, JsonElement>? Overrides = null);

    private sealed record ExecuteActionRequest(string? AgentId, string? Verb, string? TargetId = null);
}
