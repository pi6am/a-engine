using System.Text.Json;

namespace AEngine.Core.Scenarios;

/// <summary>
/// File-level card operations behind the AEngine.Util "card" command:
/// pack a scenario folder's JSON into an image's metadata, or unpack a
/// card image back into a scenario folder. The image bytes are always
/// copied verbatim — never recompressed.
/// </summary>
public static class ScenarioCard
{
    private static readonly string[] CardImageNames = ["card.png", "card.jpg", "card.jpeg"];

    /// <summary>Embed the scenario folder's modules.json/world.json into a
    /// card image. The input image is <paramref name="inputImagePath"/>, or
    /// the folder's own card.png/card.jpg/card.jpeg when omitted.</summary>
    public static void Pack(string scenarioDir, string outputImagePath, string? inputImagePath = null)
    {
        var worldPath = Path.Combine(scenarioDir, "world.json");
        if (!File.Exists(worldPath))
            throw new InvalidDataException($"Scenario folder '{scenarioDir}' has no world.json.");
        // modules before world, matching the directory layout's load order
        var documents = new Dictionary<string, string>(StringComparer.Ordinal);
        var modulesPath = Path.Combine(scenarioDir, "modules.json");
        if (File.Exists(modulesPath))
            documents["modules.json"] = File.ReadAllText(modulesPath);
        documents["world.json"] = File.ReadAllText(worldPath);

        inputImagePath ??= CardImageNames
            .Select(name => Path.Combine(scenarioDir, name))
            .FirstOrDefault(File.Exists)
            ?? throw new InvalidDataException(
                $"Scenario folder '{scenarioDir}' has no card image " +
                "(card.png/card.jpg/card.jpeg) — pass -i <image> to use one from elsewhere.");
        var image = File.ReadAllBytes(inputImagePath);
        var format = ImageCard.Detect(image)
            ?? throw new InvalidDataException($"Card image '{inputImagePath}' is not a PNG or JPEG.");

        // the image bytes are copied verbatim, so the output extension must
        // match the input format
        var extension = Path.GetExtension(outputImagePath).ToLowerInvariant();
        if ((format, extension) is not ((ImageCard.Format.Png, ".png") or (ImageCard.Format.Jpeg, ".jpg" or ".jpeg")))
            throw new InvalidDataException(
                $"Output '{outputImagePath}' extension does not match the {format} card image.");

        File.WriteAllBytes(outputImagePath,
            ImageCard.Embed(image, format, documents, ReadScenarioName(documents["world.json"])));
    }

    /// <summary>Extract a card image's scenario documents into a folder and
    /// write the folder's card image (same format as the input, metadata
    /// stripped — the JSON files are the source of truth).</summary>
    public static void Unpack(string cardImagePath, string scenarioDir)
    {
        var image = File.ReadAllBytes(cardImagePath);
        var format = ImageCard.Detect(image)
            ?? throw new InvalidDataException($"'{cardImagePath}' is not a PNG or JPEG card image.");
        var documents = ImageCard.Extract(image, format)
            ?? throw new InvalidDataException($"'{cardImagePath}' carries no embedded scenario data.");
        Directory.CreateDirectory(scenarioDir);
        foreach (var (name, text) in documents)
            File.WriteAllText(Path.Combine(scenarioDir, name), text);
        var extension = Path.GetExtension(cardImagePath).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg"))
            extension = format == ImageCard.Format.Png ? ".png" : ".jpeg";
        File.WriteAllBytes(Path.Combine(scenarioDir, "card" + extension), ImageCard.Strip(image, format));
    }

    private static string? ReadScenarioName(string worldJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(worldJson);
            return doc.RootElement.TryGetProperty("name", out var name) ? name.GetString() : null;
        }
        catch (JsonException)
        {
            return null; // a broken world.json fails at load time with a better error
        }
    }
}
