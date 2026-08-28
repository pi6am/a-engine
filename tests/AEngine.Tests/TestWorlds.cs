using AEngine.Core.Actions;
using AEngine.Core.Runtime;

namespace AEngine.Tests;

/// <summary>
/// In-code two-room world shared by the signal, policy, and NPC-turn
/// tests: room_a (Alice, chest, apple, door side) — closed wooden door —
/// room_b (Bob, pear, door side). Bob runs the "random" policy.
/// </summary>
internal static class TestWorlds
{
    public const string ModulesJson = """
    [
      { "id": "room", "name": "Room", "fields": [], "affordances": [] },
      {
        "id": "agent", "name": "Agent",
        "fields": [
          { "name": "policy", "type": "string", "default": "player" },
          { "name": "memoryLength", "type": "int", "default": 25 }
        ],
        "affordances": [
          { "verb": "look", "handler": "look", "repeatBackoff": true },
          { "verb": "inventory", "handler": "inventory" },
          { "verb": "wait", "handler": "wait", "repeatBackoff": true }
        ]
      },
      {
        "id": "can_speak", "name": "Can speak",
        "fields": [],
        "affordances": [
          {
            "verb": "say", "handler": "say", "prompt": "Say what?",
            "signals": [
              { "sense": "audible", "priority": 10, "text": "{agent} says: \"{arg}\"" },
              { "sense": "visual", "priority": 1, "text": "{agent}'s lips move." }
            ]
          }
        ]
      },
      {
        "id": "portal", "name": "Portal",
        "fields": [
          { "name": "stateRef", "type": "ref", "default": null },
          { "name": "direction", "type": "string", "default": "" },
          { "name": "to", "type": "ref", "default": null },
          { "name": "transmitVisual", "type": "string", "default": "whenOpen" },
          { "name": "transmitAudio", "type": "string", "default": "always" }
        ],
        "affordances": [
          {
            "verb": "go", "handler": "go",
            "postures": ["standing"],
            "signals": [
              { "sense": "visual", "priority": 10, "scope": "departure", "text": "{agent} exits through the {exitPortal} to the {exitDirection}." },
              { "sense": "audible", "priority": 5, "scope": "departure", "text": "You hear {agent} leave through the {exitPortal}." },
              { "sense": "visual", "priority": 10, "scope": "arrival", "text": "{agent} enters from the {entryPortal} to the {entryDirection}." },
              { "sense": "audible", "priority": 5, "scope": "arrival", "text": "You hear footsteps approaching from the {entryPortal}." }
            ]
          },
          {
            "verb": "open", "handler": "open",
            "signals": [
              { "sense": "visual", "priority": 10, "text": "{agent} opens the {target}." },
              { "sense": "audible", "priority": 5, "text": "You hear the sound of wood sliding on wood." }
            ]
          },
          {
            "verb": "close", "handler": "close",
            "signals": [
              { "sense": "visual", "priority": 10, "text": "{agent} closes the {target}." },
              { "sense": "audible", "priority": 5, "text": "You hear a door thud shut." }
            ]
          }
        ]
      },
      {
        "id": "doorstate", "name": "Shared door state",
        "fields": [
          { "name": "open", "type": "bool", "default": false },
          { "name": "locked", "type": "bool", "default": false }
        ],
        "affordances": []
      },
      {
        "id": "openable", "name": "Openable",
        "fields": [ { "name": "open", "type": "bool", "default": false } ],
        "affordances": [
          {
            "verb": "open", "handler": "open",
            "signals": [
              { "sense": "visual", "priority": 10, "text": "{agent} opens the {target}." },
              { "sense": "audible", "priority": 5, "text": "You hear something creak open." }
            ]
          },
          { "verb": "close", "handler": "close" }
        ]
      },
      { "id": "container", "name": "Container", "fields": [], "affordances": [] },
      {
        "id": "portable", "name": "Portable", "fields": [],
        "affordances": [
          {
            "verb": "take", "handler": "take",
            "signals": [ { "sense": "visual", "priority": 5, "text": "{agent} picks up the {target}." } ]
          },
          { "verb": "drop", "handler": "drop" }
        ]
      }
    ]
    """;

    public static GameEngine NewEngine()
    {
        var engine = GameEngine.CreateWithBuiltinHandlers();
        engine.ModuleRegistry.LoadJson(ModulesJson);
        return engine;
    }

    public static GameEngine NewTwoRoomEngine()
    {
        var engine = NewEngine();
        var world = engine.World;
        world.CreateObject("room_a", Core.World.World.RootId, "Room A");
        world.CreateObject("room_b", Core.World.World.RootId, "Room B");
        world.CreateObject("door_state", Core.World.World.RootId, "shared door state");
        world.AddModule("room_a", "room");
        world.AddModule("room_b", "room");
        world.AddModule("door_state", "doorstate");

        world.CreateObject("alice", "room_a", "Alice");
        world.AddModule("alice", "agent");
        world.AddModule("alice", "can_speak");
        world.CreateObject("bob", "room_b", "Bob");
        world.AddModule("bob", "agent");
        world.AddModule("bob", "can_speak");
        world.SetFieldOverride("bob", "agent", "policy", Core.World.World.ToJson("random"));

        world.CreateObject("chest", "room_a", "chest");
        world.AddModule("chest", "container");
        world.AddModule("chest", "openable");

        world.CreateObject("apple", "room_a", "apple");
        world.AddModule("apple", "portable");
        world.CreateObject("pear", "room_b", "pear");
        world.AddModule("pear", "portable");

        AddPortalSide(world, "door_a", "room_a", "north", "room_b");
        AddPortalSide(world, "door_b", "room_b", "south", "room_a");
        return engine;
    }

    private static void AddPortalSide(
        Core.World.World world, string id, string roomId, string direction, string to)
    {
        world.CreateObject(id, roomId, "wooden door");
        world.AddModule(id, "portal");
        world.SetFieldOverride(id, "portal", "stateRef", Core.World.World.ToJson("door_state"));
        world.SetFieldOverride(id, "portal", "direction", Core.World.World.ToJson(direction));
        world.SetFieldOverride(id, "portal", "to", Core.World.World.ToJson(to));
    }

    public static AvailableAction Find(
        GameEngine engine, string agentId, string verb, string? targetId = null)
    {
        var agent = engine.World.GetObject(agentId);
        return engine.ActionResolver.Resolve(agent).FirstOrDefault(a =>
                a.Verb == verb && (targetId is null || a.TargetId == targetId))
            ?? throw new InvalidOperationException($"No '{verb}' action for '{targetId}'.");
    }
}
