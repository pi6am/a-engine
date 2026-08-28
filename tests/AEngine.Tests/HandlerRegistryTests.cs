using AEngine.Core.Actions;
using AEngine.Core.Runtime;
using AEngine.Core.World;

namespace AEngine.Tests;

public class HandlerRegistryTests
{
    private sealed class StubHandler(string id, string message) : IActionHandler
    {
        public string Id => id;
        public ActionResult Execute(ActionContext context) => ActionResult.Ok(message);
    }

    [Fact]
    public void Register_Duplicate_Throws()
    {
        var registry = new HandlerRegistry();
        registry.Register(new StubHandler("look", "a"));
        Assert.Throws<InvalidOperationException>(() => registry.Register(new StubHandler("look", "b")));
    }

    [Fact]
    public void Replace_SwapsHandlerAtRuntime()
    {
        var engine = GameEngine.CreateWithBuiltinHandlers();
        engine.World.CreateObject("room", World.RootId);
        engine.World.CreateObject("player", "room");
        var player = engine.World.GetObject("player");

        engine.HandlerRegistry.Replace(new StubHandler("look", "replaced"));

        var result = engine.TurnManager.Execute(player, "look");
        Assert.Equal("replaced", result.Message);
    }

    [Fact]
    public void Get_Missing_Throws()
    {
        var registry = new HandlerRegistry();
        Assert.Throws<KeyNotFoundException>(() => registry.Get("nope"));
    }
}
