using AEngine.Core.World;

namespace AEngine.Tests;

public class WorldTests
{
    [Fact]
    public void CreateObject_AddsUnderParent_InOrder()
    {
        var world = new World();
        world.CreateObject("a", World.RootId, "A");
        world.CreateObject("b", World.RootId, "B");

        Assert.Equal(new[] { "a", "b" }, world.GetObject(World.RootId).Children);
        Assert.Equal(World.RootId, world.GetObject("a").Parent);
    }

    [Fact]
    public void CreateObject_DuplicateId_Throws()
    {
        var world = new World();
        world.CreateObject("a", World.RootId);
        Assert.Throws<InvalidOperationException>(() => world.CreateObject("a", World.RootId));
    }

    [Fact]
    public void DestroyObject_RemovesSubtreeRecursively()
    {
        var world = new World();
        world.CreateObject("a", World.RootId);
        world.CreateObject("b", "a");
        world.CreateObject("c", "b");

        world.DestroyObject("a");

        Assert.False(world.HasObject("a"));
        Assert.False(world.HasObject("b"));
        Assert.False(world.HasObject("c"));
        Assert.Empty(world.GetObject(World.RootId).Children);
    }

    [Fact]
    public void DestroyObject_Root_Throws()
    {
        var world = new World();
        Assert.Throws<InvalidOperationException>(() => world.DestroyObject(World.RootId));
    }

    [Fact]
    public void MoveObject_ReparentsObject()
    {
        var world = new World();
        world.CreateObject("a", World.RootId);
        world.CreateObject("b", World.RootId);
        world.CreateObject("c", "a");

        world.MoveObject("c", "b");

        Assert.Equal("b", world.GetObject("c").Parent);
        Assert.DoesNotContain("c", world.GetObject("a").Children);
        Assert.Contains("c", world.GetObject("b").Children);
    }

    [Fact]
    public void MoveObject_UnderOwnDescendant_Throws()
    {
        var world = new World();
        world.CreateObject("a", World.RootId);
        world.CreateObject("b", "a");
        world.CreateObject("c", "b");

        Assert.Throws<InvalidOperationException>(() => world.MoveObject("a", "c"));
        Assert.Throws<InvalidOperationException>(() => world.MoveObject("a", "a"));
    }

    [Fact]
    public void MoveObject_Root_Throws()
    {
        var world = new World();
        world.CreateObject("a", World.RootId);
        Assert.Throws<InvalidOperationException>(() => world.MoveObject(World.RootId, "a"));
    }

    [Fact]
    public void Modules_CanBeAttachedAndRemoved()
    {
        var world = new World();
        world.CreateObject("a", World.RootId);
        world.AddModule("a", "portable");
        world.AddModule("a", "portable"); // idempotent

        Assert.True(world.GetObject("a").HasModule("portable"));
        Assert.Single(world.GetObject("a").Modules);

        world.SetFieldOverride("a", "portable", "weight", World.ToJson(3));
        Assert.Equal(3, world.GetObject("a").GetModule("portable")!.Overrides["weight"].GetInt32());

        world.RemoveModule("a", "portable");
        Assert.False(world.GetObject("a").HasModule("portable"));
    }

    [Fact]
    public void SetFieldOverride_WithoutModule_Throws()
    {
        var world = new World();
        world.CreateObject("a", World.RootId);
        Assert.Throws<InvalidOperationException>(
            () => world.SetFieldOverride("a", "portable", "x", World.ToJson(1)));
    }
}
