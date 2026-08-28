using AEngine.Core.Runtime;
using AEngine.Core.Scenarios;
using AEngine.Core.World;

namespace AEngine.Tests;

public class ScenarioLoaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aengine-tests-" + Guid.NewGuid().ToString("N"));

    public ScenarioLoaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void LoadInto_BuildsNestedTreeWithModulesAndOverrides()
    {
        var modules = WriteFile("modules.json", """
        { "name": "test", "modules": [
          { "id": "openable", "name": "Openable",
            "fields": [ { "name": "open", "type": "bool", "default": false } ] }
        ] }
        """);
        var world = WriteFile("world.json", """
        { "name": "test", "world": [
          { "id": "room", "name": "Room", "children": [
            { "id": "box", "name": "Box", "attributes": { "color": "red" },
              "modules": [ { "module": "openable", "overrides": { "open": true } } ] }
          ] }
        ] }
        """);

        var engine = new GameEngine();
        var name = ScenarioLoader.LoadInto(engine, modules, world);

        Assert.Equal("test", name);
        Assert.True(engine.ModuleRegistry.Has("openable"));
        var box = engine.World.GetObject("box");
        Assert.Equal("room", box.Parent);
        Assert.Equal("red", box.Attributes["color"].GetString());
        Assert.True(engine.ModuleRegistry.ResolveBool(box, "openable", "open"));
    }

    [Fact]
    public void LoadInto_LaterFilesOverrideById()
    {
        var first = WriteFile("a.json", """
        { "world": [
          { "id": "room1", "name": "Old Room", "children": [
            { "id": "rock", "name": "Rock" }
          ] },
          { "id": "room2", "name": "Room Two" }
        ] }
        """);
        var second = WriteFile("b.json", """
        { "world": [
          { "id": "room1", "name": "New Room" }
        ] }
        """);

        var engine = new GameEngine();
        ScenarioLoader.LoadInto(engine, first, second);

        // overridden object keeps its place and earlier children
        Assert.Equal("New Room", engine.World.GetObject("room1").Name);
        Assert.Equal(World.RootId, engine.World.GetObject("room1").Parent);
        Assert.Equal("room1", engine.World.GetObject("rock").Parent);
        Assert.True(engine.World.HasObject("room2"));
    }

    [Fact]
    public void LoadInto_ModuleOverridesByIdToo()
    {
        var first = WriteFile("a.json", """
        { "modules": [ { "id": "openable", "name": "V1", "fields": [] } ] }
        """);
        var second = WriteFile("b.json", """
        { "modules": [ { "id": "openable", "name": "V2", "fields": [
          { "name": "open", "type": "bool", "default": true } ] } ] }
        """);

        var engine = new GameEngine();
        ScenarioLoader.LoadInto(engine, first, second);

        Assert.Equal("V2", engine.ModuleRegistry.Get("openable").Name);
        Assert.Single(engine.ModuleRegistry.Modules);
    }

    [Fact]
    public void LoadInto_SameIdTwiceInOneFile_LaterWins()
    {
        var dup = WriteFile("dup.json", """
        { "world": [
          { "id": "x", "name": "X" },
          { "id": "x", "name": "X again" }
        ] }
        """);

        var engine = new GameEngine();
        ScenarioLoader.LoadInto(engine, dup);
        Assert.Equal("X again", engine.World.GetObject("x").Name);
    }
}
