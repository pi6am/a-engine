using System.Text.Json;
using AEngine.Core.Modules;
using AEngine.Core.World;

namespace AEngine.Core.Runtime;

/// <summary>
/// Full-game serialization: captures everything a running game is — the
/// world tree (objects, parents, module attachments and field overrides,
/// in child order), the module registry's definitions, the engine's
/// scalars (turn clocks, busy timers, ending text, time mode), every
/// agent's memory, and the exact PRNG state — into one self-contained
/// JSON document. A save needs nothing from disk: restoring rebuilds the
/// world into a live engine with the same future ahead of it, dice
/// included. <c>/save</c>, <c>/load</c>, <c>/undo</c>, and <c>/restart</c>
/// in the CLI are all thin wrappers over <see cref="Capture"/> and
/// <see cref="Restore"/> (restart uses <see cref="Reset"/> + a scenario
/// reload).
/// <para>
/// Ephemeral transport state is deliberately not captured: undelivered
/// signals, in-flight (async) policy selections, the spectator outcome
/// queues, and pending quick-time reactions (a save taken mid-window
/// drops the telegraphed attempt; the actor's busy spell survives).
/// Everything observable — motive values, automation armed/countdown
/// state, light fuel, score cards, chatter timers — lives in module
/// fields on world objects, so the world tree carries it for free.
/// </para>
/// </summary>
public static class GameSerializer
{
    /// <summary>The save format version; a mismatched file is refused.</summary>
    public const string FormatVersion = "aengine-save-1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>One world object as serializable data (children follow in tree order).</summary>
    public sealed class WorldNodeState
    {
        public required string Id { get; init; }
        public required string Parent { get; init; }
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public string? FirstDescription { get; init; }
        public Dictionary<string, JsonElement>? Attributes { get; init; }
        public List<ModuleAttachmentState>? Modules { get; init; }
    }

    /// <summary>An attached module plus its per-object overrides.</summary>
    public sealed class ModuleAttachmentState
    {
        public required string Module { get; init; }
        public Dictionary<string, JsonElement>? Overrides { get; init; }
    }

    /// <summary>The engine-level scalars and time mode.</summary>
    public sealed class EngineState
    {
        public string? GameOver { get; init; }
        public string DefeatText { get; init; } = GameEngine.DefaultDefeatText;
        public string ScenarioAbout { get; init; } = "";
        public TimeMode TimeMode { get; init; }
    }

    /// <summary>A complete, self-contained save.</summary>
    public sealed class SaveData
    {
        public string Format { get; init; } = FormatVersion;
        public string? ScenarioName { get; init; }
        public string? ScenarioPath { get; init; }
        public DateTime SavedAtUtc { get; init; } = DateTime.UtcNow;
        /// <summary>The POV agent the saving player was controlling (a CLI convenience, nothing more).</summary>
        public string? PlayerId { get; init; }
        public int Turn { get; init; }
        public List<ModuleDefinition> Modules { get; init; } = [];
        public List<WorldNodeState> World { get; init; } = [];
        public EngineState Engine { get; init; } = new();
        public TurnManager.TurnManagerState? Turns { get; init; }
        public AgentMemory.MemoryState? Memory { get; init; }
        public Dictionary<string, JsonElement>? Random { get; init; }
    }

    /// <summary>
    /// Capture the whole game. <paramref name="scenarioName"/> and
    /// <paramref name="scenarioPath"/> are provenance labels only — the
    /// save is self-contained.
    /// </summary>
    public static SaveData Capture(
        GameEngine engine, string? scenarioName = null, string? scenarioPath = null,
        string? playerId = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        lock (engine.SyncRoot)
        {
            var world = new List<WorldNodeState>();
            Walk(engine.World, World.World.RootId, world);
            return new SaveData
            {
                ScenarioName = scenarioName,
                ScenarioPath = scenarioPath,
                PlayerId = playerId,
                Turn = engine.TurnManager.Turn,
                Modules = [.. engine.ModuleRegistry.Modules.Values.OrderBy(m => m.Id, StringComparer.Ordinal)],
                World = world,
                Engine = new EngineState
                {
                    GameOver = engine.GameOver,
                    DefeatText = engine.DefeatText,
                    ScenarioAbout = engine.ScenarioAbout,
                    TimeMode = engine.TimeMode,
                },
                Turns = engine.TurnManager.CaptureTurnState(),
                Memory = engine.Memory.Capture(),
                Random = RandomState.Capture(engine.Random).ToJson(),
            };
        }
    }

    /// <summary>
    /// Restore a captured save into a live engine, in place: the engine
    /// keeps its identity (registries of handlers/gates/policies, the
    /// sync root, any debug server attachment) while its state is
    /// replaced wholesale. Pending reactions and undelivered signals are
    /// dropped; the RNG resumes the captured sequence.
    /// </summary>
    public static void Restore(GameEngine engine, SaveData save)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(save);
        if (save.Format != FormatVersion)
            throw new InvalidDataException(
                $"Save format '{save.Format}' is not supported (expected '{FormatVersion}').");
        lock (engine.SyncRoot)
        {
            Reset(engine);
            foreach (var definition in save.Modules)
            {
                if (engine.ModuleRegistry.Has(definition.Id))
                    engine.ModuleRegistry.Update(definition);
                else
                    engine.ModuleRegistry.Register(definition);
            }
            // the world rebuilds in serialized (depth-first) order, so each
            // object's children re-attach in their original order
            foreach (var node in save.World)
            {
                var obj = engine.World.CreateObject(node.Id, node.Parent, node.Name, node.Description);
                obj.FirstDescription = node.FirstDescription;
                foreach (var (key, value) in node.Attributes ?? [])
                    obj.Attributes[key] = value;
                foreach (var attached in node.Modules ?? [])
                {
                    var attachment = engine.World.AddModule(node.Id, attached.Module);
                    foreach (var (field, value) in attached.Overrides ?? [])
                        attachment.Overrides[field] = value;
                }
            }
            engine.GameOver = save.Engine.GameOver;
            engine.DefeatText = save.Engine.DefeatText;
            engine.ScenarioAbout = save.Engine.ScenarioAbout;
            engine.TimeMode = save.Engine.TimeMode;
            engine.TurnManager.RestoreTurnState(save.Turns);
            if (save.Memory is not null)
                engine.Memory.Restore(save.Memory);
            if (save.Random is not null)
                RandomState.Restore(engine.Random, RandomState.Snapshot.FromJson(save.Random));
        }
    }

    /// <summary>
    /// Blank the engine to a pristine pre-scenario state: empty world and
    /// module registry, cleared memory/reactions/signals/turn clocks,
    /// default scalars. The RNG is left alone (a scenario reload reseeds,
    /// a restore overwrites it). This is both the first half of
    /// <see cref="Restore"/> and the whole of a restart.
    /// </summary>
    public static void Reset(GameEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        lock (engine.SyncRoot)
        {
            engine.Reactions.Clear(); // pendings reference objects about to die
            engine.World.Clear();
            engine.ModuleRegistry.Clear();
            engine.SignalBus.Clear();
            engine.Memory.Restore(new AgentMemory.MemoryState(
                new Dictionary<string, IReadOnlyList<AgentMemory.MemoryEntryState>>(),
                new Dictionary<string, long>()));
            engine.TurnManager.RestoreTurnState(null);
            engine.GameOver = null;
            engine.DefeatText = GameEngine.DefaultDefeatText;
            engine.ScenarioAbout = "";
            engine.TimeMode = TimeMode.TurnBased;
        }
    }

    /// <summary>Serialize a save to its JSON document form.</summary>
    public static string Write(SaveData save) => JsonSerializer.Serialize(save, JsonOptions);

    /// <summary>Read a save from its JSON document form.</summary>
    public static SaveData Read(string json) =>
        JsonSerializer.Deserialize<SaveData>(json, JsonOptions)
            ?? throw new InvalidDataException("Save JSON parsed to null.");

    /// <summary>Emit each object below <paramref name="id"/> in depth-first child order.</summary>
    private static void Walk(World.World world, string id, List<WorldNodeState> nodes)
    {
        foreach (var childId in world.GetObject(id).Children)
        {
            var obj = world.GetObject(childId);
            nodes.Add(new WorldNodeState
            {
                Id = obj.Id,
                Parent = obj.Parent,
                Name = obj.Name,
                Description = obj.Description,
                FirstDescription = obj.FirstDescription,
                Attributes = obj.Attributes.Count > 0
                    ? new Dictionary<string, JsonElement>(obj.Attributes)
                    : null,
                Modules = [..obj.Modules.Select(m => new ModuleAttachmentState
                {
                    Module = m.ModuleId,
                    Overrides = m.Overrides.Count > 0
                        ? new Dictionary<string, JsonElement>(m.Overrides)
                        : null,
                })],
            });
            Walk(world, childId, nodes);
        }
    }
}
