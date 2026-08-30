// AEngine.Util — developer utilities.
//
//   card pack <scenarioDir> <outputImage> [-i <inputImage>]
//       Embed the folder's modules.json/world.json into a card image
//       (.png/.jpg/.jpeg). The input image is the folder's own
//       card.png/card.jpg/card.jpeg, or -i overrides it.
//   card unpack <cardImage> <scenarioDir>
//       Extract the card's scenario documents into the folder and write a
//       metadata-stripped card image (same format as the input) beside them.
//   card info <cardImage>
//       Print the card's format, title, and embedded documents.

using AEngine.Core.Scenarios;

try
{
    switch (args)
    {
        case ["card", "pack", var scenarioDir, var outputImage, .. var rest]:
        {
            string? inputImage = null;
            for (var i = 0; i < rest.Length; i++)
            {
                if (rest[i] == "-i" && i + 1 < rest.Length)
                    inputImage = rest[++i];
                else
                    return Usage($"Unknown argument: {rest[i]}");
            }
            ScenarioCard.Pack(scenarioDir, outputImage, inputImage);
            Console.WriteLine($"Packed '{scenarioDir}' into '{outputImage}'.");
            return 0;
        }
        case ["card", "unpack", var cardImage, var scenarioDir]:
        {
            ScenarioCard.Unpack(cardImage, scenarioDir);
            Console.WriteLine($"Unpacked '{cardImage}' into '{scenarioDir}'.");
            return 0;
        }
        case ["card", "info", var cardImage]:
        {
            var info = ScenarioCard.Info(cardImage);
            Console.WriteLine($"{cardImage} ({info.Format})");
            Console.WriteLine($"Title: {info.Title ?? "(none)"}");
            Console.WriteLine("Documents:");
            foreach (var (name, text) in info.Documents)
                Console.WriteLine($"  {name} ({text.Length:N0} chars)");
            return 0;
        }
        default:
            return Usage(null);
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"card: {ex.Message}");
    return 1;
}

static int Usage(string? error)
{
    if (error is not null)
        Console.Error.WriteLine(error);
    Console.Error.WriteLine("""
        Usage:
          card pack <scenarioDir> <outputImage> [-i <inputImage>]
          card unpack <cardImage> <scenarioDir>
          card info <cardImage>
        """);
    return 1;
}
