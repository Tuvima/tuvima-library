using Bunit;
using MediaEngine.Contracts.Universe;
using MediaEngine.Web.Components.Universe;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>The Universe page's contents rails and Timeline strip on their own (spec universe-contents, section 4.1 and 4.3).</summary>
public sealed class UniverseContentsComponentTests : AsyncBunitContext
{
    public UniverseContentsComponentTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
    }

    [Fact]
    public void NothingKnown_SaysSo_AndOnlyAnEditorIsToldToAddThings()
    {
        var viewer = Render(contents: null, canEdit: false);
        var editor = Render(contents: null, canEdit: true);

        Assert.Equal("Nothing is known about this world yet.", viewer.Find("[data-contents-empty]").TextContent);
        Assert.Equal("Nothing is known about this world yet. Turn on Fandom Lore or add things yourself.", editor.Find("[data-contents-empty]").TextContent);
    }

    [Fact]
    public void EachKindIsOneRail_AFactionWithALogoGetsALogoTile_AndAHandAddedThingSaysSo()
    {
        var cut = Render(new UniverseContentsDto
        {
            Sections =
            [
                Section("factions", "Factions", Tile("House Test", logo: "/stream/artwork/1?size=m"), Tile("Guild", added: true)),
                Section("species", "Species", Tile("Fremen")),
            ],
        });

        Assert.Equal(["Factions", "Species"], cut.FindAll(".universe-contents__title").Select(heading => heading.TextContent));
        Assert.Single(cut.FindAll(".universe-contents__tile--logo"));
        Assert.Equal("/stream/artwork/1?size=m", cut.Find(".universe-contents__tile--logo img").GetAttribute("src"));
        Assert.Contains("Added by you", cut.Markup);
        // A tile without a picture shows its initial rather than a broken image.
        Assert.Equal("F", cut.FindAll(".universe-contents__initial").Last().TextContent);
    }

    [Fact]
    public void AThingTheLibraryCannotPlace_ShowsItsNameOnly()
    {
        var cut = Render(new UniverseContentsDto { Sections = [Section("characters", "Characters", Tile("Cora", noPlot: true))] });

        Assert.Contains("Cora", cut.Markup);
        Assert.Contains("No plot details", cut.Markup);
    }

    [Fact]
    public void HeldBackThings_AreCountedInOneHonestLine_WithoutNamingThem()
    {
        var cut = Render(new UniverseContentsDto { Sections = [Section("events", "Events", Tile("Early"))], HiddenCount = 2 });

        Assert.Equal("2 more are held back to avoid spoilers.", cut.Find("[data-hidden-contents]").TextContent);
    }

    [Fact]
    public void TheTimeline_OpensInStoryOrder_AndSwitchesToReleaseOrder()
    {
        var cut = Render(new UniverseContentsDto
        {
            Sections = [Section("events", "Events", Tile("Early"))],
            Timeline = new UniverseTimelineDto
            {
                DefaultOrder = UniverseTimelineDto.StoryOrder,
                StoryOrderAvailable = true,
                Stops =
                [
                    Stop("Founding", story: 1015, release: 2010),
                    Stop("Long War", story: 2400, release: 1999),
                    Stop("Ending", story: 3019, release: 2005),
                ],
            },
        });

        Assert.Equal(["Founding", "Long War", "Ending"], StopNames(cut));

        cut.FindAll(".universe-contents__order button").Last().Click();

        Assert.Equal(["Long War", "Ending", "Founding"], StopNames(cut));
    }

    [Fact]
    public void WithoutEnoughStoryDates_TheTimelineOffersNoToggle()
    {
        var cut = Render(new UniverseContentsDto
        {
            Sections = [Section("events", "Events", Tile("Early"))],
            Timeline = new UniverseTimelineDto { DefaultOrder = UniverseTimelineDto.ReleaseOrder, Stops = [Stop("A", null, 2001), Stop("B", null, 2002), Stop("C", null, 2003)] },
        });

        Assert.Empty(cut.FindAll(".universe-contents__order"));
        Assert.Equal(["A", "B", "C"], StopNames(cut));
    }

    private IRenderedComponent<UniverseContents> Render(UniverseContentsDto? contents, bool canEdit = false) =>
        Render<UniverseContents>(parameters => parameters.Add(p => p.Contents, contents).Add(p => p.CanEdit, canEdit));

    private static List<string> StopNames(IRenderedComponent<UniverseContents> cut) =>
        cut.FindAll(".universe-contents__stop .universe-contents__name").Select(name => name.TextContent).ToList();

    private static UniverseContentsSectionDto Section(string key, string label, params UniverseContentsTileDto[] tiles) =>
        new() { Key = key, Label = label, Count = tiles.Length, Tiles = tiles };

    private static UniverseContentsTileDto Tile(string label, string? logo = null, bool added = false, bool noPlot = false) =>
        new() { Id = Guid.NewGuid(), Label = label, LogoUrl = logo, AddedByUser = added, NoPlotDetails = noPlot };

    private static UniverseTimelineStopDto Stop(string label, double? story, int? release) =>
        new() { Id = Guid.NewGuid(), Label = label, Kind = "events", StoryYear = story, ReleaseYear = release };
}
