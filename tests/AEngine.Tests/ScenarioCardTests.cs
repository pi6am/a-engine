using AEngine.Core.Scenarios;

namespace AEngine.Tests;

/// <summary>
/// The card pack/unpack file operations behind the AEngine.Util "card"
/// command: scenario folder + card image → packed card, and back.
/// </summary>
public class ScenarioCardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aengine-tests-" + Guid.NewGuid().ToString("N"));

    public ScenarioCardTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly Dictionary<string, string> Docs = new()
    {
        ["modules.json"] = """{ "modules": [ { "id": "openable", "name": "Openable", "fields": [] } ] }""",
        ["world.json"] = """{ "name": "card test", "world": [ { "id": "room", "name": "Room" } ] }""",
    };

    private string WriteScenario(string name, bool withCardImage = true)
    {
        var dir = Path.Combine(_dir, name);
        Directory.CreateDirectory(dir);
        foreach (var (file, text) in Docs)
            File.WriteAllText(Path.Combine(dir, file), text);
        if (withCardImage)
            File.WriteAllBytes(Path.Combine(dir, "card.png"), ImageCardTests.MinimalPng());
        return dir;
    }

    [Fact]
    public void PackThenUnpack_RoundTrips()
    {
        var scenario = WriteScenario("rpg");
        var card = Path.Combine(_dir, "rpg.png");
        ScenarioCard.Pack(scenario, card);

        var unpacked = Path.Combine(_dir, "unpacked");
        ScenarioCard.Unpack(card, unpacked);

        Assert.Equal(Docs["modules.json"], File.ReadAllText(Path.Combine(unpacked, "modules.json")));
        Assert.Equal(Docs["world.json"], File.ReadAllText(Path.Combine(unpacked, "world.json")));
        // the folder's card image is the input image with metadata stripped
        var folderCard = File.ReadAllBytes(Path.Combine(unpacked, "card.png"));
        Assert.Null(ImageCard.Extract(folderCard, ImageCard.Format.Png));
        Assert.Equal(ImageCardTests.MinimalPng().Length, folderCard.Length);
    }

    [Fact]
    public void Pack_MissingCardImage_ThrowsUnlessOverridden()
    {
        var scenario = WriteScenario("noimage", withCardImage: false);

        var ex = Assert.Throws<InvalidDataException>(
            () => ScenarioCard.Pack(scenario, Path.Combine(_dir, "out.png")));
        Assert.Contains("-i", ex.Message);

        // -i overrides: an image from elsewhere works
        var elsewhere = Path.Combine(_dir, "art.png");
        File.WriteAllBytes(elsewhere, ImageCardTests.MinimalPng());
        ScenarioCard.Pack(scenario, Path.Combine(_dir, "out.png"), elsewhere);
        Assert.NotNull(ImageCard.Extract(File.ReadAllBytes(Path.Combine(_dir, "out.png")), ImageCard.Format.Png));
    }

    [Fact]
    public void Pack_OutputExtensionMustMatchTheImageFormat()
    {
        var scenario = WriteScenario("mismatch");

        Assert.Throws<InvalidDataException>(
            () => ScenarioCard.Pack(scenario, Path.Combine(_dir, "out.jpg"))); // png image, jpg output
    }

    [Fact]
    public void Pack_MissingWorld_Throws()
    {
        var dir = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "card.png"), ImageCardTests.MinimalPng());

        Assert.Throws<InvalidDataException>(() => ScenarioCard.Pack(dir, Path.Combine(_dir, "out.png")));
    }

    [Fact]
    public void Unpack_JpegCard_WritesCardJpeg()
    {
        var card = Path.Combine(_dir, "shared.jpeg");
        File.WriteAllBytes(card, ImageCard.Embed(ImageCardTests.MinimalJpeg(), ImageCard.Format.Jpeg, Docs));

        var unpacked = Path.Combine(_dir, "from-jpeg");
        ScenarioCard.Unpack(card, unpacked);

        Assert.True(File.Exists(Path.Combine(unpacked, "card.jpeg")));
        Assert.Null(ImageCard.Extract(
            File.ReadAllBytes(Path.Combine(unpacked, "card.jpeg")), ImageCard.Format.Jpeg));
        Assert.Equal(Docs["world.json"], File.ReadAllText(Path.Combine(unpacked, "world.json")));
    }

    [Fact]
    public void Unpack_ImageWithoutCardData_Throws()
    {
        var bare = Path.Combine(_dir, "bare.png");
        File.WriteAllBytes(bare, ImageCardTests.MinimalPng());

        Assert.Throws<InvalidDataException>(() => ScenarioCard.Unpack(bare, Path.Combine(_dir, "nope")));
    }

    [Fact]
    public void Info_ReadsTitleAndDocuments_WithoutUnpacking()
    {
        var scenario = WriteScenario("rpg");
        var card = Path.Combine(_dir, "rpg.png");
        ScenarioCard.Pack(scenario, card);

        var info = ScenarioCard.Info(card);

        Assert.Equal(ImageCard.Format.Png, info.Format);
        Assert.Equal("card test", info.Title); // from world.json's name
        Assert.Equal(Docs, info.Documents);
        Assert.False(Directory.Exists(Path.Combine(_dir, "info-should-not-write"))); // no side effects
    }

    [Fact]
    public void Info_ImageWithoutCardData_Throws()
    {
        var bare = Path.Combine(_dir, "bare.jpg");
        File.WriteAllBytes(bare, ImageCardTests.MinimalJpeg());

        Assert.Throws<InvalidDataException>(() => ScenarioCard.Info(bare));
    }
}
