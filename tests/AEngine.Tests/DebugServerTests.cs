using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AEngine.Core.Runtime;
using AEngine.Core.Scenarios;

namespace AEngine.Tests;

/// <summary>
/// Exercises the debug REST API end to end: a real server on an ephemeral
/// port backed by an engine loaded with the MVP scenario, driven by HttpClient.
/// </summary>
public class DebugServerTests : IDisposable
{
    private readonly GameEngine _engine;
    private readonly AEngine.DebugServer.DebugServer _server;
    private readonly HttpClient _http;

    public DebugServerTests()
    {
        _engine = GameEngine.CreateWithBuiltinHandlers();
        var dir = FindScenarioDir();
        ScenarioLoader.LoadInto(
            _engine,
            Path.Combine(dir, "modules.json"),
            Path.Combine(dir, "world.json"));
        _server = new AEngine.DebugServer.DebugServer(_engine, port: 0);
        _server.Start();
        _http = new HttpClient { BaseAddress = _server.Address };
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    private static string FindScenarioDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scenarios", "mvp");
            if (File.Exists(Path.Combine(candidate, "world.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate scenarios/mvp.");
    }

    private async Task<JsonElement> GetJson(string url)
    {
        using var doc = await _http.GetFromJsonAsync<JsonDocument>(url);
        return doc!.RootElement.Clone();
    }

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var body = await GetJson("/api/health");
        Assert.Equal("ok", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Tree_ReflectsScenarioState()
    {
        var tree = await GetJson("/api/world/tree");

        Assert.Equal("world", tree.GetProperty("id").GetString());
        var roomA = tree.GetProperty("children").EnumerateArray()
            .First(c => c.GetProperty("id").GetString() == "room_a");
        Assert.Equal("Dusty Study", roomA.GetProperty("name").GetString());
        var roomAChildren = roomA.GetProperty("children").EnumerateArray()
            .Select(c => c.GetProperty("id").GetString()).ToList();
        Assert.Contains("player", roomAChildren);
        Assert.Contains("desk", roomAChildren);
        Assert.Contains("door_a_side", roomAChildren);
    }

    [Fact]
    public async Task ObjectsList_ContainsAllScenarioObjects()
    {
        var objects = await GetJson("/api/objects");

        var ids = objects.EnumerateArray().Select(o => o.GetProperty("id").GetString()).ToList();
        Assert.Contains("world", ids);
        Assert.Contains("room_a", ids);
        Assert.Contains("room_b", ids);
        Assert.Contains("player", ids);
        Assert.Contains("door_1_state", ids);
    }

    [Fact]
    public async Task ObjectDetail_ExposesSharedDoorStateRef()
    {
        var detail = await GetJson("/api/objects/door_a_side");

        var portal = detail.GetProperty("modules").EnumerateArray()
            .First(m => m.GetProperty("moduleId").GetString() == "portal");
        // resolved field value: the per-object override, pointing at the shared state object
        Assert.Equal("door_1_state", portal.GetProperty("fields").GetProperty("stateRef").GetString());
        Assert.Equal("north", portal.GetProperty("fields").GetProperty("direction").GetString());

        var state = await GetJson("/api/objects/door_1_state");
        var doorstate = state.GetProperty("modules").EnumerateArray()
            .First(m => m.GetProperty("moduleId").GetString() == "doorstate");
        Assert.False(doorstate.GetProperty("fields").GetProperty("open").GetBoolean());
        Assert.True(doorstate.GetProperty("fields").GetProperty("locked").GetBoolean());
    }

    [Fact]
    public async Task MutationRoundTrip_CreateAttributeMoveModuleFieldOverride()
    {
        // create
        var create = await _http.PostAsJsonAsync("/api/objects", new
        {
            id = "lamp",
            parentId = "room_a",
            name = "brass lamp",
            description = "A small brass lamp.",
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        // create appears in list and detail
        var objects = await GetJson("/api/objects");
        Assert.Contains(objects.EnumerateArray(), o => o.GetProperty("id").GetString() == "lamp");

        // set attribute
        var putAttr = await _http.PutAsJsonAsync("/api/objects/lamp/attributes/lit", true);
        Assert.Equal(HttpStatusCode.OK, putAttr.StatusCode);
        var lamp = await GetJson("/api/objects/lamp");
        Assert.True(lamp.GetProperty("attributes").GetProperty("lit").GetBoolean());

        // remove attribute
        var delAttr = await _http.DeleteAsync("/api/objects/lamp/attributes/lit");
        Assert.Equal(HttpStatusCode.NoContent, delAttr.StatusCode);
        lamp = await GetJson("/api/objects/lamp");
        Assert.False(lamp.GetProperty("attributes").TryGetProperty("lit", out _));

        // move
        var move = await _http.PostAsJsonAsync("/api/objects/lamp/move", new { parentId = "room_b" });
        Assert.Equal(HttpStatusCode.OK, move.StatusCode);
        lamp = await GetJson("/api/objects/lamp");
        Assert.Equal("room_b", lamp.GetProperty("parent").GetString());

        // attach module with overrides
        var attach = await _http.PutAsJsonAsync("/api/objects/lamp/modules/container",
            new { overrides = new Dictionary<string, int> { ["capacity"] = 2 } });
        Assert.Equal(HttpStatusCode.OK, attach.StatusCode);
        lamp = await GetJson("/api/objects/lamp");
        var container = lamp.GetProperty("modules").EnumerateArray()
            .First(m => m.GetProperty("moduleId").GetString() == "container");
        Assert.Equal(2, container.GetProperty("fields").GetProperty("capacity").GetInt32());

        // field override is reflected in resolved fields
        var setField = await _http.PutAsJsonAsync(
            "/api/objects/lamp/modules/container/fields/capacity", 5);
        Assert.Equal(HttpStatusCode.OK, setField.StatusCode);
        lamp = await GetJson("/api/objects/lamp");
        container = lamp.GetProperty("modules").EnumerateArray()
            .First(m => m.GetProperty("moduleId").GetString() == "container");
        Assert.Equal(5, container.GetProperty("fields").GetProperty("capacity").GetInt32());

        // detach module
        var detach = await _http.DeleteAsync("/api/objects/lamp/modules/container");
        Assert.Equal(HttpStatusCode.NoContent, detach.StatusCode);
        lamp = await GetJson("/api/objects/lamp");
        Assert.Empty(lamp.GetProperty("modules").EnumerateArray());

        // destroy
        var destroy = await _http.DeleteAsync("/api/objects/lamp");
        Assert.Equal(HttpStatusCode.NoContent, destroy.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/objects/lamp")).StatusCode);
    }

    [Fact]
    public async Task MutationViaApi_IsVisibleToEngine()
    {
        await _http.PutAsJsonAsync("/api/objects/door_1_state/modules/doorstate/fields/open", true);

        Assert.True(_engine.ModuleRegistry.ResolveBool(
            _engine.World.GetObject("door_1_state"), "doorstate", "open"));
    }

    [Fact]
    public async Task Errors_UnknownIds_Return404()
    {
        Assert.Equal(HttpStatusCode.NotFound,
            (await _http.GetAsync("/api/objects/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _http.PutAsJsonAsync("/api/objects/nope/attributes/x", 1)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _http.DeleteAsync("/api/objects/nope")).StatusCode);
        // attaching an unregistered module
        Assert.Equal(HttpStatusCode.NotFound,
            (await _http.PutAsync("/api/objects/player/modules/nope", null)).StatusCode);
    }

    [Fact]
    public async Task ObjectMemory_ReturnsAgentMemories_And400ForItems()
    {
        // give the player something to remember
        var player = _engine.World.GetObject("player");
        _engine.TurnManager.PerformAction(player,
            _engine.ActionResolver.Resolve(player).First(a => a.Verb == "look"));

        var body = await GetJson("/api/objects/player/memory");
        Assert.Equal("player", body.GetProperty("agentId").GetString());
        var entries = body.GetProperty("entries").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("You look around.", entries);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await _http.GetAsync("/api/objects/desk/memory")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _http.GetAsync("/api/objects/nope/memory")).StatusCode);
    }

    [Fact]
    public async Task Errors_InvariantViolations_Return409()
    {
        // cycle: moving room_a under its own child
        var cycle = await _http.PostAsJsonAsync("/api/objects/room_a/move", new { parentId = "desk" });
        Assert.Equal(HttpStatusCode.Conflict, cycle.StatusCode);
        Assert.Contains("cycle", (await cycle.Content.ReadAsStringAsync()).ToLowerInvariant());

        // duplicate id on create
        var dupe = await _http.PostAsJsonAsync("/api/objects",
            new { id = "room_a", parentId = "world" });
        Assert.Equal(HttpStatusCode.Conflict, dupe.StatusCode);

        // root guards
        Assert.Equal(HttpStatusCode.Conflict,
            (await _http.DeleteAsync("/api/objects/world")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await _http.PostAsJsonAsync("/api/objects/world/move", new { parentId = "room_a" })).StatusCode);
    }

    [Fact]
    public async Task EngineEndpoint_ReportsTurnAndPendingActions()
    {
        lock (_engine.SyncRoot)
        {
            _engine.Scheduler.Schedule(new ScheduledAction(3, "player", "look", "player"));
            _engine.TurnManager.Execute(_engine.World.GetObject("player"), "look", "player");
        }

        var engine = await GetJson("/api/engine");
        Assert.Equal("turnBased", engine.GetProperty("timeMode").GetString());
        Assert.Equal(0, engine.GetProperty("currentTurn").GetInt32()); // Execute does not advance
        var pending = engine.GetProperty("pendingActions").EnumerateArray().ToList();
        Assert.Single(pending);
        Assert.Equal(3, pending[0].GetProperty("wakeTurn").GetInt32());
        Assert.Equal("player", pending[0].GetProperty("agentId").GetString());
        Assert.Equal("look", pending[0].GetProperty("handlerId").GetString());
    }

    [Fact]
    public async Task ModulesEndpoint_ListsRegistryWithAffordances()
    {
        var modules = await GetJson("/api/modules");

        var portal = modules.EnumerateArray().First(m => m.GetProperty("id").GetString() == "portal");
        Assert.Contains(portal.GetProperty("fields").EnumerateArray(),
            f => f.GetProperty("name").GetString() == "stateRef"
              && f.GetProperty("type").GetString() == "ref");
        Assert.Contains(portal.GetProperty("affordances").EnumerateArray(),
            a => a.GetProperty("verb").GetString() == "go"
              && a.GetProperty("handler").GetString() == "go");
    }

    [Fact]
    public async Task ActionsEndpoint_ReturnsStructuredMenu()
    {
        var actions = await GetJson("/api/actions?agentId=player");

        Assert.Contains(actions.EnumerateArray(),
            a => a.GetProperty("verb").GetString() == "look"
              && a.GetProperty("handlerId").GetString() == "look");
        Assert.Contains(actions.EnumerateArray(),
            a => a.GetProperty("verb").GetString() == "open"
              && a.GetProperty("targetId").GetString() == "desk");

        Assert.Equal(HttpStatusCode.NotFound,
            (await _http.GetAsync("/api/actions?agentId=nope")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _http.GetAsync("/api/actions")).StatusCode);
    }

    private async Task<JsonElement> ExecuteAction(object body)
    {
        var response = await _http.PostAsJsonAsync("/api/actions/execute", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();
        return doc!.RootElement.Clone();
    }

    [Fact]
    public async Task ExecuteAction_RoundTrip_MovesPlayerAndAdvancesTurn()
    {
        // open the desk drawer, take the key
        var openDesk = await ExecuteAction(new { agentId = "player", verb = "open", targetId = "desk" });
        Assert.True(openDesk.GetProperty("success").GetBoolean());
        Assert.Equal(1, openDesk.GetProperty("turn").GetInt32());

        var take = await ExecuteAction(new { agentId = "player", verb = "take", targetId = "key" });
        Assert.True(take.GetProperty("success").GetBoolean());

        // unlock and open the door, go north
        var unlock = await ExecuteAction(new { agentId = "player", verb = "unlock", targetId = "door_a_side" });
        Assert.True(unlock.GetProperty("success").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(unlock.GetProperty("message").GetString()));

        var openDoor = await ExecuteAction(new { agentId = "player", verb = "open", targetId = "door_a_side" });
        Assert.True(openDoor.GetProperty("success").GetBoolean());

        var go = await ExecuteAction(new { agentId = "player", verb = "go", targetId = "door_a_side" });
        Assert.True(go.GetProperty("success").GetBoolean());
        Assert.Equal(5, go.GetProperty("turn").GetInt32());

        // the engine itself (and the object endpoint) reflects the move
        var player = await GetJson("/api/objects/player");
        Assert.Equal("room_b", player.GetProperty("parent").GetString());

        var engine = await GetJson("/api/engine");
        Assert.Equal(5, engine.GetProperty("currentTurn").GetInt32());
    }

    [Fact]
    public async Task ExecuteAction_FailedAction_StillReturns200AndAdvancesTurn()
    {
        // going through the locked door fails, but the turn still advances
        var go = await ExecuteAction(new { agentId = "player", verb = "go", targetId = "door_a_side" });
        Assert.False(go.GetProperty("success").GetBoolean());
        Assert.Contains("locked", go.GetProperty("message").GetString());
        Assert.Equal(1, go.GetProperty("turn").GetInt32());
    }

    [Fact]
    public async Task ExecuteAction_Unknowns_Return404()
    {
        // unknown agent
        Assert.Equal(HttpStatusCode.NotFound, (await _http.PostAsJsonAsync(
            "/api/actions/execute", new { agentId = "nope", verb = "look", targetId = "nope" })).StatusCode);

        // known agent, action not currently available (key is inside the closed drawer)
        var unavailable = await _http.PostAsJsonAsync(
            "/api/actions/execute", new { agentId = "player", verb = "take", targetId = "key" });
        Assert.Equal(HttpStatusCode.NotFound, unavailable.StatusCode);
        Assert.Contains("error", await unavailable.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ExecuteAction_BadBody_Returns400()
    {
        // missing verb
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync(
            "/api/actions/execute", new { agentId = "player" })).StatusCode);
        // empty body
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _http.PostAsync("/api/actions/execute", null)).StatusCode);
        // malformed JSON
        using var content = new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _http.PostAsync("/api/actions/execute", content)).StatusCode);
    }
}
