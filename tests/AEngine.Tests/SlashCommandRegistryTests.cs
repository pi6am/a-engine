namespace AEngine.Tests;

public class SlashCommandRegistryTests
{
    [Fact]
    public void Dispatch_BareSlash_HintsInsteadOfThrowing()
    {
        var slash = new AEngine.Cli.SlashCommandRegistry();
        slash.Register("quit", [], "quit the game", _ => true);

        // "/" and "/ " have no command word — a hint, not a crash
        Assert.False(slash.Dispatch("/"));
        Assert.False(slash.Dispatch("/   "));
    }

    [Fact]
    public void Dispatch_ResolvesNamesAndAliases_WithArgs()
    {
        var slash = new AEngine.Cli.SlashCommandRegistry();
        string[]? seen = null;
        slash.Register("timescale", ["ts"], "clock speed", args =>
        {
            seen = args;
            return false;
        });

        Assert.False(slash.Dispatch("/timescale 2"));
        Assert.NotNull(seen);
        Assert.Equal(["2"], seen!);
        Assert.False(slash.Dispatch("/TS 0.5 extra"));
        Assert.Equal(["0.5", "extra"], seen!);
    }

    [Fact]
    public void Dispatch_UnknownCommand_ReturnsFalse()
    {
        var slash = new AEngine.Cli.SlashCommandRegistry();
        Assert.False(slash.Dispatch("/nope"));
    }
}
