using AEngine.Core.Runtime;

namespace AEngine.Tests;

/// <summary>
/// NPC level of detail: agents in a player's room or an adjacent room act
/// every turn; remote agents start new work only every `npcLodFactor`
/// turns (rules module, default 10). A factor of 1 disables throttling.
/// </summary>
public class LodTests
{
    private static GameEngine NewEngine(int lodFactor)
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        engine.Random = new Random(42);
        engine.ModuleRegistry.LoadJson($$"""
        [
          {
            "id": "rules", "name": "Rules",
            "fields": [
              { "name": "diceCount", "type": "int", "default": 0 },
              { "name": "diceSides", "type": "int", "default": 0 },
              { "name": "npcLodFactor", "type": "int", "default": {{lodFactor}} }
            ],
            "affordances": []
          }
        ]
        """);
        engine.World.CreateObject("rules", Core.World.World.RootId, "rules");
        engine.World.AddModule("rules", "rules");
        // Carol is remote: a cave linked to nothing (Bob stays next door in
        // room_b — adjacent to Alice in room_a even with the door closed)
        engine.World.CreateObject("room_c", Core.World.World.RootId, "Remote Cave");
        engine.World.AddModule("room_c", "room");
        engine.World.CreateObject("carol", "room_c", "Carol");
        engine.World.AddModule("carol", "agent");
        engine.World.AddModule("carol", "can_speak");
        engine.World.SetFieldOverride("carol", "agent", "policy", Core.World.World.ToJson("random"));
        return engine;
    }

    private static (int Bob, int Carol) RunTurns(GameEngine engine, int turns)
    {
        // action counts (outcome queue), not memory sizes: snapshots
        // collapse in memory and say-slots add entries, both of which
        // made memory a misleading rate proxy
        var alice = engine.World.GetObject("alice");
        var (bob, carol) = (0, 0);
        for (var i = 0; i < turns; i++)
        {
            engine.TurnManager.PerformAction(alice, TestWorlds.Find(engine, "alice", "wait"));
            // two pumps per round, like the CLI: complete in-flight
            // selections, then grant the next round's
            engine.TurnManager.RunNpcTurns();
            engine.TurnManager.RunNpcTurns();
            bob += engine.TurnManager.DrainOutcomes("bob").Count;
            carol += engine.TurnManager.DrainOutcomes("carol").Count;
        }
        return (bob, carol);
    }

    [Fact]
    public void RemoteAgent_ActsAtReducedRate_AdjacentAtFullRate()
    {
        var (bob, carol) = RunTurns(NewEngine(lodFactor: 3), 12);
        Assert.True(carol > 0, "the remote agent still acts occasionally");
        Assert.True(carol <= bob / 2, $"remote Carol acted {carol} times vs adjacent Bob's {bob}");
    }

    [Fact]
    public void LodFactorOne_EveryoneActsAtFullRate()
    {
        var (bob, carol) = RunTurns(NewEngine(lodFactor: 1), 12);
        Assert.True(carol >= bob - 1, $"factor 1: Carol {carol} should track Bob {bob}");
    }

    [Fact]
    public void RemoteAgent_JoinsFullLod_WhenThePlayerArrives()
    {
        var engine = NewEngine(lodFactor: 3);
        // Carol's cave is unlinked, so bring the "player" adjacency to her:
        // another player-policy agent in her room
        engine.World.CreateObject("pat", "room_c", "Pat");
        engine.World.AddModule("pat", "agent"); // default policy: player
        var (bob, carol) = RunTurns(engine, 6);
        Assert.True(carol >= bob - 1,
            $"with a player in her room, Carol should track Bob's full rate (Carol {carol}, Bob {bob})");
    }
}
