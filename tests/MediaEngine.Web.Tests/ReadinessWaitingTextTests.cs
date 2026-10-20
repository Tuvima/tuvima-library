using Bunit;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A row that is not Ready yet says what it is waiting for, and the words are identical in the Status column, its
/// tooltip, the open button's accessible name and the details drawer.
/// </summary>
public sealed class ReadinessWaitingTextTests : AsyncBunitContext
{
    public ReadinessWaitingTextTests()
    {
        Services.AddNativeUiServices();
        Services.AddSingleton(EngineApiClientStub.Create(_ => { }));
        JSInterop.Mode = JSRuntimeMode.Loose;
        Render<AppPopoverHost>();
    }

    [Theory]
    [InlineData("Movies", new[] { "matching" }, "Waiting for matching")]
    [InlineData("Movies", new[] { "matching", "artwork" }, "Waiting for matching and artwork")]
    [InlineData("TV", new[] { "placing" }, "Waiting for its show")]
    [InlineData("TV", new[] { "matching", "placing" }, "Waiting for matching and its show")]
    [InlineData("Music", new[] { "placing", "artwork" }, "Waiting for its album and artwork")]
    [InlineData("TV", new[] { "matching", "placing", "artwork" }, "Waiting for matching, its show and artwork")]
    [InlineData("Movies", new[] { "artwork" }, "Waiting for artwork")]
    public void TheKeys_BecomePlainWords(string mediaType, string[] keys, string expected)
    {
        Assert.Equal(expected, ReadinessWaitingText.Text(mediaType, keys));
    }

    [Theory]
    [InlineData("Movies", new string[0])]
    [InlineData("Movies", new[] { "placing" })]
    [InlineData("Books", new[] { "somethingNew" })]
    public void NothingToSay_ReturnsNull(string mediaType, string[] keys)
    {
        Assert.Null(ReadinessWaitingText.Text(mediaType, keys));
    }

    [Fact]
    public void NotReadyRow_ShowsWhatItWaitsForInTheStatusColumnItsTooltipAndItsOpenButtonName()
    {
        var item = Item("Movies", "adding", ["matching", "artwork"]);

        var cut = Render<IngestionMediaCard>(parameters => parameters.Add(component => component.Item, item));

        var status = cut.Find(".ingestion-media-list-row__status");
        Assert.Equal("Waiting for matching and artwork", status.TextContent.Trim());
        Assert.Equal("Waiting for matching and artwork", status.GetAttribute("title"));
        Assert.Contains("Waiting for matching and artwork", cut.Find("button.ingestion-media-list-row__open").GetAttribute("aria-label"));
    }

    [Fact]
    public void TvRowWaitingForPlacing_SaysItsShow_AndMusicSaysItsAlbum()
    {
        var tv = Render<IngestionMediaCard>(parameters => parameters.Add(component => component.Item, Item("TV", "adding", ["placing"])));
        var music = Render<IngestionMediaCard>(parameters => parameters.Add(component => component.Item, Item("Music", "adding", ["placing"])));

        Assert.Equal("Waiting for its show", tv.Find(".ingestion-media-list-row__status").TextContent.Trim());
        Assert.Equal("Waiting for its album", music.Find(".ingestion-media-list-row__status").TextContent.Trim());
    }

    [Fact]
    public void ReadyRow_ShowsNoWaitingText()
    {
        var cut = Render<IngestionMediaCard>(parameters => parameters.Add(component => component.Item, Item("Movies", "ready", [])));

        Assert.DoesNotContain("Waiting for", cut.Markup, StringComparison.Ordinal);
        Assert.Equal("Ready to browse", cut.Find(".ingestion-media-list-row__status").TextContent.Trim());
    }

    [Fact]
    public void RowInReview_KeepsItsOwnStatusEvenIfReasonsArrive()
    {
        var cut = Render<IngestionMediaCard>(parameters => parameters.Add(component => component.Item, Item("Movies", "review", ["matching"])));

        Assert.Equal("Adding", cut.Find(".ingestion-media-list-row__status").TextContent.Trim());
        Assert.DoesNotContain("Waiting for matching", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Drawer_SaysTheSameWordsAsTheRow()
    {
        var item = Item("TV", "adding", ["matching", "placing"]);

        var drawer = Render<IngestionMediaDrawer>(parameters => parameters.Add(component => component.Item, item));
        var row = Render<IngestionMediaCard>(parameters => parameters.Add(component => component.Item, item));

        var text = row.Find(".ingestion-media-list-row__status").TextContent.Trim();
        Assert.Equal("Waiting for matching and its show", text);
        Assert.Contains(text, drawer.Find(".ingestion-drawer__progress span").TextContent);
    }

    [Fact]
    public void FacetDash_ComesOnlyFromNotApplicable()
    {
        var item = Item("Movies", "adding", ["matching"]);
        item.People = IngestionFacetStateDto.NotApplicable("People");
        item.TextTracks = new() { State = "pending", Label = "Subtitles queued" };

        var cut = Render<IngestionMediaCard>(parameters => parameters.Add(component => component.Item, item));

        var slots = cut.FindAll(".ingestion-media-list-row__details .ingestion-facets__slot");
        Assert.NotNull(slots[2].QuerySelector(".ingestion-facet.is-none"));
        Assert.Null(slots[4].QuerySelector(".ingestion-facet.is-none"));
        Assert.Equal("Subtitles queued", slots[4].QuerySelector(".ingestion-facet")!.GetAttribute("aria-label"));
    }

    [Fact]
    public void ArtworkSearchThatFoundNothing_ShowsItsLabelAsASettledSymbol()
    {
        var item = Item("Movies", "adding", ["matching"]);
        item.Artwork = new() { State = "complete", Label = "No artwork found" };

        var cut = Render<IngestionMediaCard>(parameters => parameters.Add(component => component.Item, item));

        var artwork = cut.FindAll(".ingestion-media-list-row__details .ingestion-facets__slot")[0];
        Assert.Null(artwork.QuerySelector(".is-none"));
        Assert.Equal("No artwork found", artwork.QuerySelector(".ingestion-facet")!.GetAttribute("aria-label"));
        Assert.Equal("No artwork found", artwork.QuerySelector("[data-playback-tooltip]")!.GetAttribute("data-playback-tooltip"));
    }

    private static IngestionMediaGroupDto Item(string mediaType, string availability, string[] waitingFor) => new()
    {
        Title = $"{mediaType} sample",
        MediaType = mediaType,
        Availability = availability,
        StatusLabel = availability == "ready" ? "Ready" : "Adding",
        WaitingFor = [.. waitingFor],
        FileCount = 1,
        Artwork = new() { State = "pending", Label = "Artwork queued" },
        Metadata = new() { State = "pending", Label = "Metadata queued" },
        People = new() { State = "pending", Label = "People queued" },
        Relationships = new() { State = "pending", Label = "Relationships queued" },
        TextTracks = new() { State = "pending", Label = "Subtitles queued" },
    };
}
