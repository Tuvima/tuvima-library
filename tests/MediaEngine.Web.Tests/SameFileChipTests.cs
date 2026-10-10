using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Details;

namespace MediaEngine.Web.Tests;

public sealed class SameFileChipTests : AsyncBunitContext
{
    private static SequenceCoverageLinkViewModel Link(string id, string? label) => new() { Id = id, PositionLabel = label, Title = $"Episode {label}" };

    [Fact]
    public void NoSiblingsRendersNothing()
    {
        var cut = Render<SameFileChip>(p => p.Add(c => c.Links, []));
        Assert.Equal(string.Empty, cut.Markup.Trim());
    }

    [Fact]
    public void SingleSiblingNamesTheEpisodeInPlainText()
    {
        var cut = Render<SameFileChip>(p => p.Add(c => c.Links, [Link("a", "2")]));
        Assert.Contains("Same file as Episode 2", cut.Markup);
        Assert.Empty(cut.FindAll("button"));
    }

    [Fact]
    public void SeveralSiblingsAreListedInOrder()
    {
        var cut = Render<SameFileChip>(p => p.Add(c => c.Links, [Link("a", "2"), Link("b", "3")]).Add(c => c.CanHighlight, true));
        Assert.Contains("Same file as Episodes 2, 3", cut.Find("button").TextContent);
    }

    [Fact]
    public void VisibleSiblingMakesTheChipAnAccessiblePressableButtonThatRaisesTheCallback()
    {
        var raised = 0;
        var cut = Render<SameFileChip>(p => p
            .Add(c => c.Links, [Link("a", "1")])
            .Add(c => c.CanHighlight, true)
            .Add(c => c.IsHighlighting, true)
            .Add(c => c.OnHighlight, () => raised++));
        var button = cut.Find("button.same-file-chip");
        Assert.Equal("true", button.GetAttribute("aria-pressed"));
        Assert.Contains("Same file as Episode 1", button.GetAttribute("aria-label"));
        button.Click();
        Assert.Equal(1, raised);
    }

    [Fact]
    public void MissingNumberStillReadsAsAFriendlySentence()
    {
        var cut = Render<SameFileChip>(p => p.Add(c => c.Links, [Link("a", null)]));
        Assert.Contains("Same file as another episode", cut.Markup);
    }
}
