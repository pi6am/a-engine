using AEngine.Core.Modules;
using AEngine.Core.World;
using CoreWorld = AEngine.Core.World.World;

namespace AEngine.Tests;

public class ModuleRegistryTests
{
    private static ModuleDefinition Openable() => new()
    {
        Id = "openable",
        Name = "Openable",
        Fields = [new FieldDefinition { Name = "open", Type = FieldType.Bool, Default = World.ToJson(false) }],
    };

    [Fact]
    public void Register_Duplicate_Throws()
    {
        var registry = new ModuleRegistry();
        registry.Register(Openable());
        Assert.Throws<InvalidOperationException>(() => registry.Register(Openable()));
    }

    [Fact]
    public void Update_ReplacesDefinition()
    {
        var registry = new ModuleRegistry();
        registry.Register(Openable());
        registry.Update(new ModuleDefinition { Id = "openable", Name = "Openable v2" });

        Assert.Equal("Openable v2", registry.Get("openable").Name);
    }

    [Fact]
    public void Update_Missing_Throws()
    {
        var registry = new ModuleRegistry();
        Assert.Throws<KeyNotFoundException>(() => registry.Update(Openable()));
    }

    [Fact]
    public void Unregister_RemovesDefinition()
    {
        var registry = new ModuleRegistry();
        registry.Register(Openable());
        registry.Unregister("openable");
        Assert.False(registry.Has("openable"));
        Assert.Throws<KeyNotFoundException>(() => registry.Unregister("openable"));
    }

    [Fact]
    public void ResolveField_OverrideWinsOverDefault()
    {
        var registry = new ModuleRegistry();
        registry.Register(Openable());

        var world = new CoreWorld();
        world.CreateObject("box", CoreWorld.RootId);
        world.AddModule("box", "openable");

        // default
        Assert.False(registry.ResolveBool(world.GetObject("box"), "openable", "open"));

        // object override
        world.SetFieldOverride("box", "openable", "open", World.ToJson(true));
        Assert.True(registry.ResolveBool(world.GetObject("box"), "openable", "open"));
    }

    [Fact]
    public void LoadJson_ParsesModulesWithFieldsAndAffordances()
    {
        const string json = """
        [
          {
            "id": "portal",
            "name": "Portal",
            "fields": [
              { "name": "stateRef", "type": "ref", "default": null },
              { "name": "capacity", "type": "int", "default": 4 },
              { "name": "label", "type": "string", "default": "door" }
            ],
            "affordances": [ { "verb": "go", "handler": "go", "requires": "open" } ]
          }
        ]
        """;

        var registry = new ModuleRegistry();
        registry.LoadJson(json);

        var portal = registry.Get("portal");
        Assert.Equal(FieldType.Ref, portal.GetField("stateRef")!.Type);
        Assert.Equal(FieldType.Int, portal.GetField("capacity")!.Type);
        Assert.Equal(FieldType.String, portal.GetField("label")!.Type);
        Assert.Equal(4, portal.GetField("capacity")!.Default.GetInt32());
        Assert.Equal("go", portal.Affordances[0].Handler);
        Assert.Equal("open", portal.Affordances[0].Requires);
    }
}
