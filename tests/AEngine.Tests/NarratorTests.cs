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

    [Fact]
    public async Task Events_BatchIntoOneCall_WithVerbatimSpeechInstruction()
    {
        var client = new FakeLlmClient().Enqueue("You lift the dagger. \"Stop that!\" the duelist barks.");
        var narrator = new Narrator(client);

        var result = await narrator.NarrateEventsAsync(
            ["You take the dagger.", "The arena duelist says: \"Stop that!\""]);

        Assert.Equal("You lift the dagger. \"Stop that!\" the duelist barks.", result);
        Assert.Equal(0, client.Remaining); // one call for the whole batch
        var user = client.LastMessages!.Last(m => m.Role == "user").Content;
        Assert.Contains("You take the dagger.", user);
        Assert.Contains("The arena duelist says:", user);
        var system = client.LastMessages!.First(m => m.Role == "system").Content;
        Assert.Contains("verbatim", system);
    }

    [Fact]
    public async Task Events_EmptyBatch_MakesNoCall()
    {
        var narrator = new Narrator(new FakeLlmClient()); // a call would throw

        Assert.Null(await narrator.NarrateEventsAsync([]));
    }

    [Fact]
    public async Task Events_EmptyReply_ReturnsNull()
    {
        var client = new FakeLlmClient().Enqueue("  ");
        var narrator = new Narrator(client);

        Assert.Null(await narrator.NarrateEventsAsync(["You wait."]));
    }

    [Fact]
    public async Task PlayerName_RendersAsYou_InBothPrompts()
    {
        var client = new FakeLlmClient().Enqueue("room prose").Enqueue("event prose");
        var narrator = new Narrator(client, "Max");

        await narrator.NarrateRoomAsync("bedroom", "Bedroom\nMax's bed sits here.");
        var roomSystem = client.LastMessages!.First(m => m.Role == "system").Content;
        Assert.Contains("Max", roomSystem);
        Assert.Contains("you", roomSystem);

        await narrator.NarrateEventsAsync(["Max lies down on Max's bed."]);
        var eventsSystem = client.LastMessages!.First(m => m.Role == "system").Content;
        Assert.Contains("Max", eventsSystem);
    }

    [Fact]
    public async Task NoPlayerName_PromptsStayUnchanged()
    {
        var client = new FakeLlmClient().Enqueue("prose");
        var narrator = new Narrator(client);

        await narrator.NarrateEventsAsync(["You wait."]);
        var system = client.LastMessages!.First(m => m.Role == "system").Content;
        Assert.DoesNotContain("player's character is named", system);
    }
}
