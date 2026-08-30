using AEngine.Core.Runtime;
using AEngine.Core.Scenarios;

namespace AEngine.Tests;

/// <summary>
/// Image card codec: PNG zTXt / JPEG COM embed, extract, strip, and the
/// scenario-source wiring. Test images are minimal hand-built byte
/// sequences (chunk CRCs are zero; the reader ignores them and the writer
/// recomputes them).
/// </summary>
public class ImageCardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aengine-tests-" + Guid.NewGuid().ToString("N"));

    public ImageCardTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly Dictionary<string, string> Docs = new()
    {
        ["modules.json"] = """{ "name": "card", "modules": [ { "id": "openable", "name": "Openable", "fields": [] } ] }""",
        ["world.json"] = """{ "name": "card", "world": [ { "id": "room", "name": "Room", "children": [ { "id": "box", "name": "Box", "modules": [ "openable" ] } ] } ] }""",
    };

    internal static byte[] MinimalPng()
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(output, "IHDR", [0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0]); // 1x1 RGBA
        WriteChunk(output, "IEND", []);
        return output.ToArray();

        static void WriteChunk(MemoryStream output, string type, byte[] data)
        {
            output.Write([(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length]);
            output.Write(System.Text.Encoding.ASCII.GetBytes(type));
            output.Write(data);
            output.Write([0, 0, 0, 0]); // CRC: zero — the reader ignores it
        }
    }

    internal static byte[] MinimalJpeg()
    {
        using var output = new MemoryStream();
        output.Write([0xFF, 0xD8]); // SOI
        var jfif = System.Text.Encoding.ASCII.GetBytes("JFIF\0").Concat(new byte[9]).ToArray();
        output.Write([0xFF, 0xE0, 0, (byte)(jfif.Length + 2)]); // APP0
        output.Write(jfif);
        output.Write([0xFF, 0xDA, 0, 4, 1, 2]); // SOS (2 bytes of header data)
        output.Write([0x11, 0x22, 0x33]);       // fake entropy data
        output.Write([0xFF, 0xD9]);             // EOI
        return output.ToArray();
    }

    [Fact]
    public void Detect_SniffsMagicBytes()
    {
        Assert.Equal(ImageCard.Format.Png, ImageCard.Detect(MinimalPng()));
        Assert.Equal(ImageCard.Format.Jpeg, ImageCard.Detect(MinimalJpeg()));
        Assert.Null(ImageCard.Detect([0x50, 0x4B, 3, 4]));       // zip
        Assert.Null(ImageCard.Detect([1, 2, 3]));
    }

    [Fact]
    public void Png_EmbedExtract_RoundTrips()
    {
        var card = ImageCard.Embed(MinimalPng(), ImageCard.Format.Png, Docs, "My Scenario");

        Assert.Equal(ImageCard.Format.Png, ImageCard.Detect(card));
        var extracted = ImageCard.Extract(card, ImageCard.Format.Png);
        Assert.NotNull(extracted);
        Assert.Equal(Docs, extracted);
    }

    [Fact]
    public void Png_Strip_RemovesCardData_KeepsImageChunks()
    {
        var original = MinimalPng();
        var stripped = ImageCard.Strip(ImageCard.Embed(original, ImageCard.Format.Png, Docs, "T"), ImageCard.Format.Png);

        Assert.Null(ImageCard.Extract(stripped, ImageCard.Format.Png));
        Assert.Equal(original.Length, stripped.Length); // only our chunks were added, so they were all removed
    }

    [Fact]
    public void Png_EmbedTwice_LatestWins()
    {
        var first = ImageCard.Embed(MinimalPng(), ImageCard.Format.Png, Docs);
        var second = ImageCard.Embed(first, ImageCard.Format.Png,
            new Dictionary<string, string> { ["world.json"] = "{}" });

        var extracted = ImageCard.Extract(second, ImageCard.Format.Png);
        Assert.NotNull(extracted);
        Assert.Single(extracted);
        Assert.Equal("{}", extracted["world.json"]);
    }

    [Fact]
    public void Jpeg_EmbedExtract_RoundTrips_AndKeepsJfifFirst()
    {
        var card = ImageCard.Embed(MinimalJpeg(), ImageCard.Format.Jpeg, Docs, "My Scenario");

        Assert.Equal(Docs, ImageCard.Extract(card, ImageCard.Format.Jpeg));
        // the JFIF APP0 segment still leads, so decoders keep recognizing it
        Assert.Equal(0xE0, card[3]);
        Assert.Equal(0xFF, card[2]);
    }

    [Fact]
    public void Jpeg_LargePayload_SplitsAcrossComSegments()
    {
        // guid soup compresses poorly, so the compressed payload still
        // exceeds one COM segment's 64KB limit
        var docs = new Dictionary<string, string>
        {
            ["world.json"] = string.Concat(Enumerable.Range(0, 6000).Select(_ => Guid.NewGuid().ToString("N"))),
        };

        var card = ImageCard.Embed(MinimalJpeg(), ImageCard.Format.Jpeg, docs);

        Assert.Equal(docs, ImageCard.Extract(card, ImageCard.Format.Jpeg));
        var markerCount = CountOccurrences(card, System.Text.Encoding.ASCII.GetBytes("aengine-card "));
        Assert.True(markerCount >= 2, $"expected multiple COM segments, found {markerCount}");

        static int CountOccurrences(byte[] haystack, byte[] needle)
        {
            var count = 0;
            for (var i = 0; i <= haystack.Length - needle.Length; i++)
                if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                    count++;
            return count;
        }
    }

    [Fact]
    public void Extract_NoCardData_ReturnsNull()
    {
        Assert.Null(ImageCard.Extract(MinimalPng(), ImageCard.Format.Png));
        Assert.Null(ImageCard.Extract(MinimalJpeg(), ImageCard.Format.Jpeg));
    }

    [Fact]
    public void LoadFrom_ImageCard_LoadsTheScenario()
    {
        var card = Path.Combine(_dir, "card-scenario.jpeg");
        File.WriteAllBytes(card, ImageCard.Embed(MinimalJpeg(), ImageCard.Format.Jpeg, Docs));

        var engine = new GameEngine();
        var name = ScenarioLoader.LoadFrom(engine, card);

        Assert.Equal("card", name);
        Assert.True(engine.ModuleRegistry.Has("openable"));
        Assert.Equal("room", engine.World.GetObject("box").Parent);
    }

    [Fact]
    public void LoadFrom_ImageWithoutCardData_Throws()
    {
        var bare = Path.Combine(_dir, "bare.png");
        File.WriteAllBytes(bare, MinimalPng());

        var ex = Assert.Throws<InvalidDataException>(() => ScenarioLoader.LoadFrom(new GameEngine(), bare));
        Assert.Contains("no embedded scenario data", ex.Message);
    }
}
