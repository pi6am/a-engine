using AEngine.Llm;

namespace AEngine.Tests;

/// <summary>
/// Room narration: per-room caching (an unchanged raw render replays the
/// cached narration with no LLM call), change-aware re-narration (the
/// prompt carries the previous raw text and narration), and raw-text
/// fallback on an empty reply.
/// </summary>
public class NarratorTests
{
    [Fact]
    public async Task UnchangedRawText_ReplaysCache_NoSecondCall()
    {
        var client = new FakeLlmClient().Enqueue("A dusty hush fills the study.");
        var narrator = new Narrator(client);

        var first = await narrator.NarrateRoomAsync("study", "Dusty Study\nA dusty study.");
        Assert.Equal("A dusty hush fills the study.", first);
        Assert.Equal(0, client.Remaining);

        // same raw text: no LLM call, same narration (a second call would
        // throw on the empty queue)
        var second = await narrator.NarrateRoomAsync("study", "Dusty Study\nA dusty study.");
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task ChangedRawText_Renarrates_WithHistoryInThePrompt()
    {
        var client = new FakeLlmClient()
            .Enqueue("A dusty hush fills the study.")
            .Enqueue("The drawer hangs open, disturbingly.");
        var narrator = new Narrator(client);

        await narrator.NarrateRoomAsync("study", "Dusty Study\nA dusty study.");
        var second = await narrator.NarrateRoomAsync(
            "study", "Dusty Study\nA dusty study.\nYou see: desk drawer (open)");

        Assert.Equal("The drawer hangs open, disturbingly.", second);
        var user = client.LastMessages!.Last(m => m.Role == "user").Content;
        Assert.Contains("Dusty Study\nA dusty study.", user); // previous raw
        Assert.Contains("A dusty hush fills the study.", user); // previous narration
        Assert.Contains("desk drawer (open)", user);            // new raw
    }

    [Fact]
    public async Task Cache_IsPerRoom()
    {
        var client = new FakeLlmClient()
            .Enqueue("Study prose.")
            .Enqueue("Hallway prose.");
        var narrator = new Narrator(client);

        Assert.Equal("Study prose.", await narrator.NarrateRoomAsync("study", "raw study"));
        Assert.Equal("Hallway prose.", await narrator.NarrateRoomAsync("hallway", "raw hallway"));
        // both rooms replay from cache
        Assert.Equal("Study prose.", await narrator.NarrateRoomAsync("study", "raw study"));
        Assert.Equal("Hallway prose.", await narrator.NarrateRoomAsync("hallway", "raw hallway"));
    }

    [Fact]
    public async Task EmptyReply_FallsBackToRaw()
    {
        var client = new FakeLlmClient().Enqueue("   ");
        var narrator = new Narrator(client);

        Assert.Equal("raw study", await narrator.NarrateRoomAsync("study", "raw study"));
    }
}
