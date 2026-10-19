using Bunit;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A Now-panel row has the same five cells in the same order for every media type: Title, Type, Step, Status, Details.
/// </summary>
public sealed class IngestionMediaCardTests : AsyncBunitContext
{
    private static readonly string[] CellClasses =
    [
        "ingestion-media-list-row__identity",
        "ingestion-media-list-row__format",
        "ingestion-media-list-row__step",
        "ingestion-media-list-row__status",
        "ingestion-media-list-row__details",
    ];

    public IngestionMediaCardTests()
    {
        Services.AddNativeUiServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Render<AppPopoverHost>();
    }

    [Fact]
    public void ActiveGroup_ShowsTitleTypeStepStatusAndDetailsAndOpensFromTheTitle()
    {
        var item = new IngestionMediaGroupDto
        {
            Title = "A Night at the Opera",
            Subtitle = "Queen",
            MediaType = "Music",
            Availability = "finishing",
            StatusLabel = "Adding lyrics",
            CurrentGateKey = "enriched",
            CurrentGateLabel = "Enriching details",
            ChildUnit = "tracks",
            ChildCompleted = 5,
            FileCount = 6,
            Artwork = new() { State = "complete", Label = "Artwork complete" },
            TextTracks = new() { State = "active", Label = "Lyrics active" },
        };
        IngestionMediaGroupDto? opened = null;
        var cut = Render<IngestionMediaCard>(parameters => parameters
            .Add(component => component.Item, item)
            .Add(component => component.OnOpen, value => opened = value));

        Assert.Equal(item.Title, cut.Find(".ingestion-media-list-row__copy strong").TextContent);
        Assert.Equal("Queen", cut.Find(".ingestion-media-list-row__copy small").TextContent);
        Assert.Contains("Album", cut.Find(".ingestion-media-list-row__format").TextContent);
        Assert.Contains("5 tracks added", cut.Find(".ingestion-media-list-row__format").TextContent);
        Assert.Equal("Enriching details", cut.Find(".ingestion-media-list-row__step").TextContent);
        Assert.Equal("Adding lyrics", cut.Find(".ingestion-media-list-row__status").TextContent.Trim());
        cut.Find("button.ingestion-media-list-row__open").Click();
        Assert.Same(item, opened);
    }

    [Fact]
    public void ReadyGroup_StillShowsItsStatusText()
    {
        var cut = Render<IngestionMediaCard>(parameters => parameters.Add(component => component.Item, new()
        {
            Title = "The Quiet Mind",
            MediaType = "Audiobooks",
            Availability = "ready",
            StatusLabel = "Ready",
            ChildUnit = "files",
            ChildCompleted = 12,
            FileCount = 12,
        }));

        Assert.Equal("Ready to browse", cut.Find(".ingestion-media-list-row__status").TextContent.Trim());
        Assert.Contains("12 files added", cut.Find(".ingestion-media-list-row__format").TextContent);
        // With no gate left the Step column says the status instead.
        Assert.Equal("Ready to browse", cut.Find(".ingestion-media-list-row__step").TextContent);
    }

    [Fact]
    public void MissingContributor_DoesNotInventPersistentRowMetadata()
    {
        var cut = Render<IngestionMediaCard>(parameters => parameters.Add(component => component.Item, new()
        {
            Title = "The Quiet Mind",
            Subtitle = "Books",
            MediaType = "Books",
            Availability = "review",
            StatusLabel = "Needs review",
            FileCount = 1,
        }));

        Assert.Equal(string.Empty, cut.Find(".ingestion-media-list-row__copy small").TextContent);
    }

    [Fact]
    public void DiscoveredFiles_AreNotReportedAsAddedBeforeTheyFinish()
    {
        var cut = Render<IngestionMediaCard>(parameters => parameters.Add(component => component.Item, new()
        {
            Title = "New album",
            MediaType = "Music",
            StatusLabel = "Identifying",
            ChildUnit = "tracks",
            FileCount = 8,
            ChildCompleted = 0,
        }));

        Assert.DoesNotContain("8 tracks added", cut.Markup);
    }

    [Theory]
    [InlineData("Movies")]
    [InlineData("TV")]
    [InlineData("Music")]
    [InlineData("Books")]
    [InlineData("Audiobooks")]
    public void EveryMediaType_RendersTheSameFiveCellsInTheSameOrderWithFiveDetailSlots(string mediaType)
    {
        var cut = Render<IngestionMediaCard>(parameters => parameters.Add(component => component.Item, Item(mediaType)));

        var row = cut.Find("[role='row']");
        var cells = row.QuerySelectorAll("[role='cell']").Select(cell => cell.ClassList.First()).ToArray();
        Assert.Equal(CellClasses, cells);
        Assert.Equal(5, cut.FindAll(".ingestion-media-list-row__details .ingestion-facets__slot").Count);
    }

    [Fact]
    public void ASymbolThatDoesNotApplyToTheType_IsADashInItsSlotWithTheSameWordsForTooltipAndName()
    {
        var cut = Render<IngestionMediaCard>(parameters => parameters.Add(component => component.Item, Item("Books")));

        var slots = cut.FindAll(".ingestion-media-list-row__details .ingestion-facets__slot");
        var dash = slots[4].QuerySelector(".ingestion-facet.is-none");
        Assert.NotNull(dash);
        Assert.Equal("Doesn't apply to books", dash.GetAttribute("aria-label"));
        Assert.Equal("Doesn't apply to books", slots[4].QuerySelector("[data-playback-tooltip]")!.GetAttribute("data-playback-tooltip"));
        Assert.Contains("–", dash.TextContent, StringComparison.Ordinal);
        Assert.Empty(slots.Take(4).SelectMany(slot => slot.QuerySelectorAll(".is-none")));
    }

    [Fact]
    public void ASymbolThatHasNotStartedYet_StaysADimSymbolNotADash()
    {
        var item = Item("Movies");
        item.People = IngestionFacetStateDto.NotApplicable("People");

        var cut = Render<IngestionMediaCard>(parameters => parameters.Add(component => component.Item, item));

        var people = cut.FindAll(".ingestion-media-list-row__details .ingestion-facets__slot")[2];
        Assert.Empty(people.QuerySelectorAll(".is-none"));
        var symbol = people.QuerySelector(".ingestion-facet")!;
        Assert.Equal("People", symbol.GetAttribute("aria-label"));
        Assert.Equal("People", people.QuerySelector("[data-playback-tooltip]")!.GetAttribute("data-playback-tooltip"));
    }

    [Fact]
    public void EachSymbol_UsesTheSameWordsForItsTooltipAndItsAccessibleName()
    {
        var cut = Render<IngestionMediaCard>(parameters => parameters.Add(component => component.Item, Item("Music")));

        foreach (var slot in cut.FindAll(".ingestion-media-list-row__details .ingestion-facets__slot"))
        {
            var tooltip = slot.QuerySelector("[data-playback-tooltip]")!.GetAttribute("data-playback-tooltip");
            var name = slot.QuerySelector("[aria-label]")!.GetAttribute("aria-label");
            Assert.False(string.IsNullOrWhiteSpace(tooltip));
            Assert.Equal(tooltip, name);
        }
    }

    private static IngestionMediaGroupDto Item(string mediaType) => new()
    {
        Title = $"{mediaType} sample",
        MediaType = mediaType,
        Availability = "finishing",
        StatusLabel = "Adding people",
        CurrentGateLabel = "Enriching details",
        FileCount = 3,
        Artwork = new() { State = "complete", Label = "Artwork complete" },
        Metadata = new() { State = "complete", Label = "Metadata complete" },
        People = new() { State = "active", Label = "People active" },
        Relationships = new() { State = "pending", Label = "Relationships pending" },
        TextTracks = mediaType is "Music" or "Movies" or "TV"
            ? new() { State = "pending", Label = "Subtitles pending" }
            : IngestionFacetStateDto.NotApplicable("Subtitles"),
    };
}
