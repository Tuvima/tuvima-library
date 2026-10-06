using MediaEngine.Web.Components.Shared;
using Bunit;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Web.Components.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class IngestionMediaCardTests : AsyncBunitContext
{
    public IngestionMediaCardTests()
    {
        Services.AddNativeUiServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Render<AppPopoverHost>();
    }

    [Fact]
    public void ActiveGroup_SeparatesWorkFromIdentityAndDistinguishesFilesFromTracks()
    {
        var item = new IngestionMediaGroupDto
        {
            Title = "A Night at the Opera",
            Subtitle = "Queen",
            MediaType = "Music",
            Availability = "finishing",
            StatusLabel = "Adding lyrics",
            ProgressPercent = 50,
            CurrentGateKey = "enriched",
            CurrentGateLabel = "Enriching details",
            ProgressGates =
            [
                new() { Key = "identified", Label = "Identified", State = "complete" },
                new() { Key = "matched", Label = "Matched metadata", State = "complete" },
                new() { Key = "enriched", Label = "Enriching details", State = "active" },
                new() { Key = "ready", Label = "Ready in library", State = "pending" },
            ],
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

        Assert.Equal(item.Title, cut.Find(".ingestion-media-card__title").TextContent);
        Assert.Single(cut.FindAll(".ingestion-media-card__progress"));
        Assert.Contains("Enriching details", cut.Find("button.ingestion-media-card").GetAttribute("title"));
        Assert.DoesNotContain("Queen", cut.Markup);
        Assert.DoesNotContain("tracks added", cut.Markup);
        Assert.DoesNotContain("ingestion-facet", cut.Markup);
        cut.Find("button.ingestion-media-card").Click();
        Assert.Same(item, opened);
    }

    [Theory]
    [InlineData("ready", "Ready")]
    [InlineData("review", "Needs review")]
    [InlineData("failed", "Failed")]
    public void MissingContributor_DoesNotInventPersistentTileMetadata(string availability, string status)
    {
        var cut = Render<IngestionMediaCard>(parameters => parameters.Add(component => component.Item, new()
        {
            Title = "The Quiet Mind",
            Subtitle = "Books",
            MediaType = "Books",
            Availability = availability,
            StatusLabel = status,
            FileCount = 1,
        }));

        Assert.Equal("The Quiet Mind", cut.Find(".ingestion-media-card__title").TextContent);
        Assert.DoesNotContain("Books", cut.Markup);
        Assert.DoesNotContain("1 file", cut.Markup);
        Assert.DoesNotContain("ingestion-facet", cut.Markup);
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

        Assert.DoesNotContain("8 files", cut.Markup);
        Assert.DoesNotContain("8 tracks added", cut.Markup);
    }

    [Fact]
    public void ListMode_UsesCompactColumnsAndReadyStateIsOnlyACheckmark()
    {
        var cut = Render<IngestionMediaCard>(parameters => parameters
            .Add(component => component.DisplayMode, "list")
            .Add(component => component.Item, new()
            {
                Title = "The Quiet Mind",
                Subtitle = "Daniel Brooks",
                MediaType = "Audiobooks",
                Availability = "ready",
                StatusLabel = "Ready",
                ChildUnit = "files",
                ChildCompleted = 12,
                FileCount = 12,
                Artwork = new() { State = "complete", Label = "Artwork complete" },
                Metadata = new() { State = "complete", Label = "Metadata complete" },
            }));

        Assert.Single(cut.FindAll("button.ingestion-media-list-row"));
        Assert.Equal("The Quiet Mind", cut.Find(".ingestion-media-list-row__copy strong").TextContent);
        Assert.Equal("Daniel Brooks", cut.Find(".ingestion-media-list-row__copy small").TextContent);
        Assert.Contains("Audiobook", cut.Find(".ingestion-media-list-row__format").TextContent);
        Assert.Contains("12 files added", cut.Find(".ingestion-media-list-row__format").TextContent);
        Assert.Empty(cut.FindAll(".ingestion-media-list-row__status span"));
        Assert.Empty(cut.Find(".ingestion-media-list-row__status").TextContent.Trim());
    }
}
