using AEngine.Core.Actions;

namespace AEngine.Tests;

/// <summary>
/// The basic flavor handler interpolates the affordance's verb into its
/// message ("Touch the red flower" -> "You touch the red flower."), so the
/// verb must ride the ActionContext from PerformAction.
/// </summary>
public class BasicHandlerTests
{
    [Fact]
    public void BasicHandler_InterpolatesTheAffordanceVerb()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        var alice = engine.World.GetObject("alice");

        var action = new AvailableAction("smell", "apple", "Smell the apple", "basic", "portable");
        var result = engine.TurnManager.PerformAction(alice, action);
        Assert.True(result.Success);
        Assert.Equal("You smell the apple.", result.Message);
    }

    [Fact]
    public void BasicHandler_FallsBack_WhenInvokedWithoutAVerb()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        var alice = engine.World.GetObject("alice");

        // scheduler-style invocation: no affordance, no verb
        var result = engine.TurnManager.Execute(alice, "basic", "apple");
        Assert.True(result.Success);
        Assert.Equal("You touch the apple.", result.Message);
    }

    [Fact]
    public void BasicHandler_DoesNotDoubleArticles()
    {
        var engine = TestWorlds.NewTwoRoomEngine();
        var alice = engine.World.GetObject("alice");
        engine.World.GetObject("apple").Name = "the golden apple";

        var result = engine.TurnManager.Execute(alice, "basic", "apple");
        Assert.Equal("You touch the golden apple.", result.Message);
    }
}
