using AEngine.Llm;

namespace AEngine.Tests;

/// <summary>PlanParser tolerance: numbering, bullets, prose padding, code fences.</summary>
public class PlanParserTests
{
    [Fact]
    public void NumberedPlan_StripsNumbers()
    {
        var plan = PlanParser.Parse("1. Open the desk drawer\n2. Take the brass key\n3. Go north");
        Assert.Equal(["Open the desk drawer", "Take the brass key", "Go north"], plan);
    }

    [Fact]
    public void BulletedPlan_StripsBullets()
    {
        var plan = PlanParser.Parse("- Open the cupboard\n* Take the carving knife");
        Assert.Equal(["Open the cupboard", "Take the carving knife"], plan);
    }

    [Fact]
    public void ProsePadding_IsDropped()
    {
        var plan = PlanParser.Parse("""
            Sure! Here is my plan:
            1) Open the desk drawer
            2) Take the brass key
            That should get me the key.
            """);
        Assert.Equal(["Open the desk drawer", "Take the brass key"], plan);
    }

    [Fact]
    public void CodeFences_AndBlankLines_AreDropped()
    {
        var plan = PlanParser.Parse("```\nGo north\n\n```");
        Assert.Equal(["Go north"], plan);
    }

    [Fact]
    public void LinesNotStartingWithAKnownVerb_AreDropped()
    {
        var plan = PlanParser.Parse("First, open the desk drawer.\nOpen the desk drawer");
        Assert.Equal(["Open the desk drawer"], plan);
    }

    [Fact]
    public void LabelWhoseFirstWordIsNotTheVerb_IsKept()
    {
        // the "inventory" verb's label is "Check inventory" — the LLM
        // copies labels, so labels must be accepted even when their first
        // word is not a verb
        var labels = new[] { "Look around", "Check inventory", "Go north" };
        var plan = PlanParser.Parse("Check inventory", knownLabels: labels);
        Assert.Equal(["Check inventory"], plan);
    }

    [Fact]
    public void LabelMatch_IsCaseInsensitive_AndAllowsTrailingWords()
    {
        var labels = new[] { "Check inventory" };
        Assert.Equal(
            ["check inventory"],
            PlanParser.Parse("check inventory", knownLabels: labels));
        Assert.Equal(
            ["Check inventory first"],
            PlanParser.Parse("Check inventory first", knownLabels: labels));
        // non-listed prose is still dropped
        Assert.Empty(PlanParser.Parse("Admire the scenery", knownLabels: labels));
    }
}
