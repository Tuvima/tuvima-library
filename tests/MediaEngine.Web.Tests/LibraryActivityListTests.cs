using Bunit;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Activity tab of Libraries: what is being added right now and what was added, in one list, newest first.
/// </summary>
public sealed class LibraryActivityListTests : LiveLibraryTestContext
{
    public LibraryActivityListTests()
    {
        Services.AddNativeUiServices();
    }

    [Fact]
    public void ItemsBeingAddedAndItemsAdded_AreOneListWithTheAddingRowsFirstThenTheNewestAdded()
    {
        History =
        [
            Added("Dune", DateTimeOffset.UtcNow.AddHours(-1)),
            Added("Arrival", DateTimeOffset.UtcNow.AddHours(-3)),
        ];

        var cut = RenderList(Adding("Blade Runner"));

        var rows = cut.FindAll(".history-row");
        Assert.Equal(["Blade Runner", "Dune", "Arrival"], rows.Select(row => row.QuerySelector(".history-row__identity strong")!.TextContent.Trim()).ToArray());
        Assert.Equal(["adding", "added", "added"], rows.Select(row => row.GetAttribute("data-activity-state")!).ToArray());
        Assert.Single(cut.FindAll("[data-testid='library-activity-list']"));
    }

    [Fact]
    public void ARowBeingAdded_SaysSoAndOpensItsDetailsFromItsTitle()
    {
        var cut = RenderList(Adding("Blade Runner"));

        var row = Assert.Single(cut.FindAll(".history-row"));
        Assert.Equal("Now", row.QuerySelector("time strong")!.TextContent);
        Assert.Contains("Adding to library", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("Open ingestion details for Blade Runner", cut.Find(".history-row__open").GetAttribute("aria-label"), StringComparison.Ordinal);
        Assert.Equal(5, row.QuerySelectorAll(".ingestion-facets__slot").Length);
    }

    [Fact]
    public void AnAddedRow_HasNoDetailsButtonAndSaysItWasAddedToTheLibrary()
    {
        History = [Added("Dune", DateTimeOffset.UtcNow.AddHours(-1))];

        var cut = RenderList();

        var row = Assert.Single(cut.FindAll(".history-row"));
        Assert.Equal("added", row.GetAttribute("data-activity-state"));
        Assert.Empty(row.QuerySelectorAll(".history-row__open"));
        Assert.Contains("Added to library", row.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AnItemThatIsBeingAddedAndAlreadyInTheHistory_IsShownOnceAsAdding()
    {
        var dune = Adding("Dune");
        History = [Added("Dune", DateTimeOffset.UtcNow.AddMinutes(-1), dune.GroupId)];

        var cut = RenderList(dune);

        var row = Assert.Single(cut.FindAll(".history-row"));
        Assert.Equal("adding", row.GetAttribute("data-activity-state"));
    }

    [Fact]
    public void TheSearchBox_NarrowsTheRowsBeingAddedAsWellAsTheHistory()
    {
        History = [];

        var cut = Render<RecentlyAddedPageContent>(parameters => parameters.Add(page => page.InitialSearch, "Dune"));
        Presentation = () => Task.FromResult<IngestionPresentationSnapshotDto?>(new IngestionPresentationSnapshotDto
        {
            IsRunning = true,
            CurrentMedia = [Adding("Dune"), Adding("Arrival")],
            CurrentMediaTotal = 2,
        });
        _ = Services.GetRequiredService<MediaEngine.Web.Services.Integration.IngestionLiveDashboardState>().LoadAsync();

        cut.WaitForAssertion(() =>
        {
            var row = Assert.Single(cut.FindAll(".history-row"));
            Assert.Contains("Dune", row.TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void TheFilters_AreSearchTypeStatusAndDate_WithNoSortChoice()
    {
        var cut = RenderList();

        var labels = cut.FindAll(".recently-added-filters .app-select__label, .recently-added-filters label").Select(label => label.TextContent.Trim()).ToArray();
        Assert.Contains(labels, label => label.Contains("Media type", StringComparison.Ordinal));
        Assert.Contains(labels, label => label.Contains("Status", StringComparison.Ordinal));
        Assert.Contains(labels, label => label.Contains("Date range", StringComparison.Ordinal));
        Assert.DoesNotContain(labels, label => label.Contains("Sort by", StringComparison.Ordinal));
    }

    [Fact]
    public void WhenNothingWasAddedAndNothingIsBeingAdded_TheListSaysSo()
    {
        var cut = Render<RecentlyAddedPageContent>();

        cut.WaitForAssertion(() => Assert.Contains("No recent additions found", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".history-row"));
    }

    [Fact]
    public void ThreeThousandPhotos_ShowOneCountLineAndNoFileRows()
    {
        Presentation = () => Task.FromResult<IngestionPresentationSnapshotDto?>(new IngestionPresentationSnapshotDto
        {
            IsRunning = true,
            PersonalMediaInProgress = new IngestionPersonalMediaProgressDto { Photos = 3000 },
        });

        var cut = Render<RecentlyAddedPageContent>();

        cut.WaitForAssertion(() => Assert.Equal("3,000 photos in progress", cut.Find(".recent-history__personal").TextContent));
        Assert.Empty(cut.FindAll(".history-row"));
    }

    [Theory]
    [InlineData(2300, 0, "2,300 photos in progress")]
    [InlineData(0, 12, "12 personal videos in progress")]
    [InlineData(1, 1, "1 photo and 1 personal video in progress")]
    [InlineData(2300, 12, "2,300 photos and 12 personal videos in progress")]
    public void PhotosAndPersonalVideos_AreOneCountLineWorded(int photos, int videos, string line)
    {
        Assert.Equal(line, IngestionNowText.PersonalMediaLine(new IngestionPersonalMediaProgressDto { Photos = photos, Videos = videos }));
    }

    [Fact]
    public void WithNoPhotosOrVideos_TheCountLineIsHidden()
    {
        Assert.Null(IngestionNowText.PersonalMediaLine(new IngestionPersonalMediaProgressDto()));

        var cut = RenderList(Adding("Dune"));

        Assert.Empty(cut.FindAll(".recent-history__personal"));
    }

    private IRenderedComponent<RecentlyAddedPageContent> RenderList(params IngestionMediaGroupDto[] adding)
    {
        Presentation = () => Task.FromResult<IngestionPresentationSnapshotDto?>(new IngestionPresentationSnapshotDto
        {
            IsRunning = adding.Length > 0,
            CurrentMedia = [.. adding],
            CurrentMediaTotal = adding.Length,
        });
        var cut = Render<RecentlyAddedPageContent>();
        cut.WaitForAssertion(() => Assert.True(
            cut.FindAll(".history-row").Count == adding.Length + History.Count(item => adding.All(a => a.GroupId != item.GroupId)),
            "The list has not drawn every row yet."));
        return cut;
    }

    private static IngestionMediaGroupDto Adding(string title) => new()
    {
        GroupId = Guid.NewGuid(),
        MediaType = "Movies",
        Title = title,
        Availability = "adding",
        StatusLabel = "Adding people",
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static IngestionMediaGroupDto Added(string title, DateTimeOffset addedAt, Guid? id = null) => new()
    {
        GroupId = id ?? Guid.NewGuid(),
        MediaType = "Movies",
        Title = title,
        Availability = "ready",
        StatusLabel = "Ready",
        AddedAt = addedAt,
        UpdatedAt = addedAt,
    };
}
