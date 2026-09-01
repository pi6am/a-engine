using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.Scenarios;
using AEngine.Core.World;
using AEngine.Llm;
using CoreWorld = AEngine.Core.World.World;

namespace AEngine.Tests;

/// <summary>
/// Integration: load scenarios/tavern and play the stage-1 loop —
/// arrive, pour and drink until tipsy, need to pee, use the restroom,
/// hit the bursting wall, go home. Plus a smoke test that the autonomous
/// cast acts without exploding.
/// </summary>
public class TavernPlaythroughTests
{
    private static string FindScenarioDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scenarios", "tavern");
            if (File.Exists(Path.Combine(candidate, "world.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate scenarios/tavern.");
    }

    private static GameEngine NewEngine(int seed = 7)
    {
        var engine = GameEngine.CreateWithBuiltinHandlers();
        engine.Random = new Random(seed);
        var dir = FindScenarioDir();
        ScenarioLoader.LoadInto(
            engine,
            Path.Combine(dir, "modules.json"),
            Path.Combine(dir, "world.json"));
        return engine;
    }

    private static ActionResult Do(GameEngine engine, string verb, string? targetId = null) =>
        engine.TurnManager.PerformAction(
            engine.World.GetObject("player"), TestWorlds.Find(engine, "player", verb, targetId));

    /// <summary>The newest clone of a template: the highest tpl_x_N suffix.</summary>
    private static string LatestClone(GameEngine engine, string templateId)
    {
        var best = templateId;
        var bestN = 0;
        foreach (var id in engine.World.Objects.Keys)
        {
            if (!id.StartsWith(templateId + "_", StringComparison.Ordinal))
                continue;
            var suffix = id[(templateId.Length + 1)..];
            if (int.TryParse(suffix, out var n) && n > bestN)
            {
                bestN = n;
                best = id;
            }
        }
        return best;
    }

    private static bool HasCond(GameEngine engine, string agentId, string kind) =>
        Conditions.Has(engine.World, engine.ModuleRegistry, engine.World.GetObject(agentId), kind);

    private static double Alcohol(GameEngine engine, string agentId) =>
        engine.ModuleRegistry.ResolveDouble(engine.World.GetObject(agentId), "metabolism", "alcohol");

    private static double Bladder(GameEngine engine, string agentId) =>
        engine.ModuleRegistry.ResolveDouble(engine.World.GetObject(agentId), "metabolism", "bladder");

    [Fact]
    public void ScenarioLoads_WithInitialConditionsFromStartingValues()
    {
        var engine = NewEngine();
        // the loader's initial upkeep sync derives conditions from starting
        // field values: the elf is drunk, the goblin and one orc tipsy
        Assert.True(HasCond(engine, "lythienne", "drunk"));
        Assert.True(HasCond(engine, "nix", "tipsy"));
        Assert.True(HasCond(engine, "gorra", "tipsy"));
        Assert.False(HasCond(engine, "thakra", "tipsy"));
        Assert.False(HasCond(engine, "brann", "tipsy"));
        Assert.Equal("street", engine.World.GetObject("player").Parent);
    }

    [Fact]
    public void Look_ShowsSeatedCastWithVisibleConditions()
    {
        var engine = NewEngine();
        Do(engine, "open", "front_door_street_side");
        Do(engine, "go", "front_door_street_side");

        // the player starts knowing nobody: the cast renders incognito
        var look = Do(engine, "look");
        Assert.Contains("a silver-haired elf woman (sitting on the booth by the window, drunk)", look.Message);
        Assert.Contains("a short green-skinned goblin woman (sitting on the middle bar stool, tipsy)", look.Message);
        Assert.Contains("a brawny orc woman with tattooed knuckles (sitting on the left chair at the corner table, tipsy)", look.Message);
        Assert.Contains("a lean orc woman doing sums on a napkin (sitting on the right chair at the corner table)", look.Message);
        Assert.Contains("an enormous gray-skinned troll", look.Message);
    }

    [Fact]
    public void NamesAreLearned_FromOverheardSpeech()
    {
        var engine = NewEngine();
        Do(engine, "open", "front_door_street_side");
        Do(engine, "go", "front_door_street_side");
        var player = engine.World.GetObject("player");

        // pre-populated: the orcs know each other, so Gorra can address
        // Thakra by name — and the player overhears it
        var gorra = engine.World.GetObject("gorra");
        var action = PlanExecutor.MatchAvailableOrPotential(
            engine, gorra, "Say to Thakra: \"Thakra, you know what you did.\"");
        Assert.Equal("thakra", action!.TargetId);
        engine.TurnManager.PerformAction(gorra, action, action.Text);

        // the mention taught the player Thakra's name — but nobody else's
        Assert.True(AEngine.Core.Actions.Knowledge.KnowsName(
            engine.ModuleRegistry, player, engine.World.GetObject("thakra")));
        Assert.False(AEngine.Core.Actions.Knowledge.KnowsName(
            engine.ModuleRegistry, player, engine.World.GetObject("nix")));
        var look = Do(engine, "look");
        Assert.Contains("Thakra the orc (sitting on the right chair at the corner table)", look.Message);
        Assert.Contains("a short green-skinned goblin woman (sitting on the middle bar stool, tipsy)", look.Message);
    }

    [Fact]
    public void SpawnTargetCounter_OffersNoPut_TablesDo()
    {
        var engine = NewEngine();
        Do(engine, "open", "front_door_street_side");
        Do(engine, "go", "front_door_street_side");
        var player = engine.World.GetObject("player");

        // hold something first so put entries could exist at all
        Do(engine, "pour", "ale_tap");
        Do(engine, "take", LatestClone(engine, "tpl_ale"));

        var verbs = engine.ActionResolver.Resolve(player).ToList();
        // the bar counter is the spawn target — no put onto it
        Assert.DoesNotContain(verbs, a => a.Verb == "put" && a.TargetId == "bar_counter");
        // the corner table is a general surface with puttable
        Assert.Contains(verbs, a => a.Verb == "put" && a.TargetId == "corner_table");
        Assert.StartsWith("Put the mug of Green Gullet ale onto the corner table",
            verbs.First(a => a.Verb == "put" && a.TargetId == "corner_table").Label);
    }

    [Fact]
    public void FullLoop_PourDrinkTipsy_RestroomBursting_GoHome()
    {
        var engine = NewEngine();

        // enter the bar
        Do(engine, "open", "front_door_street_side");
        Assert.Equal("tavern", Do(engine, "go", "front_door_street_side").Outcome == ActionOutcome.Success
            ? engine.World.GetObject("player").Parent
            : "street");

        // pour an ale (a single ale slot on the shared counter), take it, drink it
        var pour = Do(engine, "pour", "ale_tap");
        Assert.Equal(ActionOutcome.Success, pour.Outcome);
        Assert.Contains("mug of Green Gullet ale now sits on the bar counter", pour.Message);
        var ale = LatestClone(engine, "tpl_ale");
        Assert.StartsWith("tpl_ale_", ale);

        Do(engine, "take", ale);
        var drink = Do(engine, "drink", ale);
        Assert.Equal(ActionOutcome.Success, drink.Outcome);
        Assert.Equal("You drink the mug of Green Gullet ale.", drink.Message);
        // the mug visibly becomes an empty mug — the state is legible,
        // not hidden in a field
        Assert.Equal("empty mug", engine.World.GetObject(ale).Name);
        // the vessel stays behind, empty; clearing is offered, drinking isn't
        var player = engine.World.GetObject("player");
        Assert.DoesNotContain(engine.ActionResolver.Resolve(player),
            a => a.Verb == "drink" && a.TargetId == ale);
        Assert.Contains(engine.ActionResolver.Resolve(player),
            a => a.Verb == "clear" && a.TargetId == ale);

        // keep pouring and drinking (tap cycles) until tipsy — capacity 1.0,
        // tipsy at 0.25; two ales is plenty
        for (var i = 0; i < 3 && !HasCond(engine, "player", "tipsy"); i++)
        {
            Do(engine, "pour", "ale_tap");
            ale = LatestClone(engine, "tpl_ale");
            Do(engine, "take", ale);
            Do(engine, "drink", ale);
        }
        Assert.True(HasCond(engine, "player", "tipsy"));
        Assert.True(Alcohol(engine, "player") > 0.25);
        var look = Do(engine, "look");
        Assert.Contains("You feel warm and loose-tongued.", look.Message);

        // food sobers: stew from the kitchen (the swing door is open)
        Do(engine, "go", "kitchen_door_tavern_side");
        Do(engine, "cook", "stove");
        Do(engine, "take", LatestClone(engine, "tpl_stew"));
        var before = Alcohol(engine, "player");
        Do(engine, "eat", LatestClone(engine, "tpl_stew"));
        Assert.True(Alcohol(engine, "player") < before);

        // back to the bar; force the bladder past bursting to exercise the
        // execution gate, then relieve
        Do(engine, "go", "swing_door_kitchen_side");
        engine.World.SetFieldOverride("player", "metabolism", "bladder",
            CoreWorld.ToJson(0.96));
        engine.TurnManager.EvaluateUpkeep();
        Assert.True(HasCond(engine, "player", "bursting"));

        // the drink is still LISTED while bursting — and fails loudly
        Do(engine, "pour", "ale_tap");
        ale = LatestClone(engine, "tpl_ale");
        Do(engine, "take", ale);
        player = engine.World.GetObject("player");
        Assert.Contains(engine.ActionResolver.Resolve(player),
            a => a.Verb == "drink" && a.TargetId == ale);
        var refused = Do(engine, "drink", ale);
        Assert.Equal(ActionOutcome.Failure, refused.Outcome);
        Assert.Equal(
            "Your bladder is bursting — you couldn't keep another drop down.",
            refused.Message);

        // the restroom: door closed, open then go, then use the urinal
        Do(engine, "open", "mens_door_tavern_side");
        Do(engine, "go", "mens_door_tavern_side");
        var relief = Do(engine, "use", "urinal");
        Assert.Equal(ActionOutcome.Success, relief.Outcome);
        // near-zero (the action's own upkeep refills a trickle from
        // still-burning alcohol — the kidneys don't stop)
        Assert.True(Bladder(engine, "player") < 0.1,
            $"expected a drained bladder, got {Bladder(engine, "player")}");
        // the action's own upkeep pass detached the bladder conditions
        Assert.False(HasCond(engine, "player", "needs_to_pee"));
        Assert.False(HasCond(engine, "player", "bursting"));
        Assert.Contains(engine.SignalBus.Drain("player"),
            s => s.Text.Contains("new person"));

        // drinks go down again after relief
        var again = Do(engine, "drink", ale);
        Assert.Equal(ActionOutcome.Success, again.Outcome);

        // go home: street, then the night bus ends the game (the shared
        // door state means the restroom-side door is already open)
        Do(engine, "go", "mens_door_restroom_side");
        Do(engine, "go", "front_door_tavern_side");
        var leave = Do(engine, "leave", "bus_stop");
        Assert.Equal(ActionOutcome.Success, leave.Outcome);
        Assert.NotNull(engine.GameOver);
        Assert.Contains("night bus", engine.GameOver);
    }

    [Fact]
    public void AutonomousCast_ActsWithoutExploding()
    {
        var engine = NewEngine(seed: 42);
        Do(engine, "open", "front_door_street_side");
        Do(engine, "go", "front_door_street_side");

        // rounds of player idling while the cast acts
        var signals = 0;
        for (var round = 0; round < 40; round++)
        {
            Do(engine, "wait");
            engine.TurnManager.RunNpcTurns();
            signals += engine.SignalBus.Drain("player").Count;
        }
        Assert.True(signals > 0, "expected the cast to act perceptibly");
        // nobody lost their conditions, nobody exploded the world
        Assert.True(HasCond(engine, "lythienne", "drunk") ||
                    Alcohol(engine, "lythienne") < 0.5 * 0.8);
        Assert.True(engine.World.HasObject("ale_tap"));
    }
}
