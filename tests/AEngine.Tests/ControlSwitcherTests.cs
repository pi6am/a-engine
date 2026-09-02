using AEngine.Cli;
using AEngine.Core.Runtime;
using AEngine.Core.Scenarios;

namespace AEngine.Tests;

/// <summary>
/// /control POV switching: the POV agent becomes externally driven
/// (policy "player"), the true player idles ("none") while you're away,
/// and a hijacked NPC's original policy is restored on release.
/// </summary>
public class ControlSwitcherTests
{
    private static GameEngine LoadTavern()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "scenarios", "tavern", "world.json")))
            dir = dir.Parent;
        if (dir is null)
            throw new DirectoryNotFoundException("Could not locate scenarios/tavern.");
        var engine = GameEngine.CreateWithBuiltinHandlers();
        ScenarioLoader.LoadFrom(engine, Path.Combine(dir.FullName, "scenarios", "tavern"));
        return engine;
    }

    private static string Policy(GameEngine engine, string id) =>
        engine.ModuleRegistry.ResolveString(engine.World.GetObject(id), "agent", "policy") ?? "<unset>";

    [Fact]
    public void SwitchToNpc_PlayerIdlesAndPovBecomesExternallyDriven()
    {
        var engine = LoadTavern();
        var control = new ControlSwitcher(engine, "player");

        Assert.True(control.TrySwitch("nix", out _));
        Assert.Equal("nix", control.CurrentId);
        Assert.Equal("none", Policy(engine, "player"));
        Assert.Equal("player", Policy(engine, "nix"));
        // bystanders untouched
        Assert.Equal("auto", Policy(engine, "brann"));
    }

    [Fact]
    public void SwitchBack_RestoresOriginalPolicies()
    {
        var engine = LoadTavern();
        var control = new ControlSwitcher(engine, "player");
        Assert.True(control.TrySwitch("nix", out _));

        Assert.True(control.TrySwitch("player", out _));
        Assert.Equal("player", control.CurrentId);
        Assert.Equal("player", Policy(engine, "player"));
        Assert.Equal("auto", Policy(engine, "nix")); // original restored
    }

    [Fact]
    public void SwitchBetweenNpcs_RestoresFirstAndPlayerStaysIdle()
    {
        var engine = LoadTavern();
        var control = new ControlSwitcher(engine, "player");
        Assert.True(control.TrySwitch("nix", out _));

        Assert.True(control.TrySwitch("gorra", out _));
        Assert.Equal("gorra", control.CurrentId);
        Assert.Equal("auto", Policy(engine, "nix"));
        Assert.Equal("player", Policy(engine, "gorra"));
        Assert.Equal("none", Policy(engine, "player")); // still away
    }

    [Fact]
    public void SwitchToUnknownNonAgentOrSelf_Fails()
    {
        var engine = LoadTavern();
        var control = new ControlSwitcher(engine, "player");

        Assert.False(control.TrySwitch("nobody", out var unknown));
        Assert.Contains("No agent", unknown);

        Assert.False(control.TrySwitch("bar", out var nonAgent)); // a room, not an agent
        Assert.Contains("No agent", nonAgent);

        Assert.False(control.TrySwitch("player", out var self));
        Assert.Contains("already controlling", self);
        Assert.Equal("player", control.CurrentId);
    }

    [Fact]
    public void SwitchIsCaseInsensitiveById()
    {
        var engine = LoadTavern();
        var control = new ControlSwitcher(engine, "player");
        Assert.True(control.TrySwitch("NIX", out _));
        Assert.Equal("nix", control.CurrentId);
    }

    [Fact]
    public void SwitchToIncapacitatedAgent_Fails()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        // hp 0 <= incapacitatedAt 0 → Bob is out cold
        engine.ModuleRegistry.LoadJson("""
        [ { "id": "health", "name": "Health",
            "fields": [ { "name": "hp", "type": "int", "default": 0 },
                        { "name": "incapacitatedAt", "type": "int", "default": 0 } ] } ]
        """);
        engine.World.AddModule("bob", "health");

        var control = new ControlSwitcher(engine, "alice");
        Assert.False(control.TrySwitch("bob", out var message));
        Assert.Contains("incapacitated", message);
        Assert.Equal("alice", control.CurrentId);
    }

    [Fact]
    public void InertPlayerIsNotDrivenByNpcTurns()
    {
        var engine = LoadTavern();
        var control = new ControlSwitcher(engine, "player");
        Assert.True(control.TrySwitch("nix", out _));

        // the idle player ("none") resolves to no registered policy —
        // RunNpcTurns must skip them, and the driven POV ("player") is
        // never auto-run either
        for (var i = 0; i < 3; i++)
            engine.TurnManager.RunNpcTurns();
        Assert.Equal("none", Policy(engine, "player"));
        Assert.Equal("player", Policy(engine, "nix"));
    }
}
