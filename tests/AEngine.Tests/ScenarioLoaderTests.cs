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

    private string WriteZip(string fileName, params (string Entry, string Content)[] entries)
    {
        var path = Path.Combine(_dir, fileName);
        using var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        foreach (var (entry, content) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entry).Open());
            writer.Write(content);
        }
        return path;
    }

    [Fact]
    public void LoadFrom_ZipWithArbitraryExtension_LoadsModulesAndWorld()
    {
        // shared as ".scen"; the entries sit in a nested folder — recognized
        // by magic bytes and entry file names, not the extension or layout
        var zip = WriteZip("packed.scen",
            ("bundle/modules.json", """
            { "name": "packed", "modules": [
              { "id": "openable", "name": "Openable",
                "fields": [ { "name": "open", "type": "bool", "default": false } ] }
            ] }
            """),
            ("bundle/world.json", """
            { "name": "packed", "world": [
              { "id": "room", "name": "Room", "children": [
                { "id": "box", "name": "Box", "modules": [ "openable" ] }
              ] }
            ] }
            """));

        var engine = new GameEngine();
        var name = ScenarioLoader.LoadFrom(engine, zip);

        Assert.Equal("packed", name);
        Assert.True(engine.ModuleRegistry.Has("openable"));
        Assert.Equal("room", engine.World.GetObject("box").Parent);
    }

    [Fact]
    public void LoadFrom_Directory_StillWorks()
    {
        var dir = Path.Combine(_dir, "loose");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "world.json"), """
        { "name": "loose", "world": [ { "id": "room", "name": "Room" } ] }
        """);

        var engine = new GameEngine();
        Assert.Equal("loose", ScenarioLoader.LoadFrom(engine, dir));
        Assert.True(engine.World.HasObject("room"));
    }

    [Fact]
    public void LoadFrom_ZipWithoutWorld_Throws()
    {
        var zip = WriteZip("modules-only.zip", ("modules.json", """{ "modules": [] }"""));

        var ex = Assert.Throws<InvalidDataException>(
            () => ScenarioLoader.LoadFrom(new GameEngine(), zip));
        Assert.Contains("world.json", ex.Message);
    }

    [Fact]
    public void LoadFrom_UnrecognizedFile_Throws()
    {
        var notAScenario = WriteFile("notes.txt", "just some notes");

        Assert.Throws<InvalidDataException>(
            () => ScenarioLoader.LoadFrom(new GameEngine(), notAScenario));
    }
}
