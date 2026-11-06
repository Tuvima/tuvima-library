using Bunit;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Web.Components.Settings.LibraryActivity;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>The Library activity page's pieces (spec library-activity): the progress card, the idle line and the words used for a batch.</summary>
public sealed class LibraryActivityComponentTests : LiveLibraryTestContext
{
    public LibraryActivityComponentTests()
    {
        Services.AddNativeUiServices();
    }

    [Fact]
    public void ProgressCard_WhileWorking_ShowsOneBarAndTheReviewLink()
    {
        var cut = Render<LibraryActivityProgressCard>(parameters => parameters
            .Add(card => card.Queue, new IngestionQueueProgressDto { State = "working", FilesFound = 10, FilesDone = 4 })
            .Add(card => card.Presentation, new IngestionPresentationSnapshotDto { IsRunning = true, ReviewGroups = 3 }));

        Assert.Single(cut.FindAll("[data-testid='activity-progress-card']"));
        Assert.Empty(cut.FindAll("[data-testid='activity-up-to-date']"));
        Assert.Contains("/settings/review", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgressCard_WhenNothingRuns_SaysEverythingIsUpToDateWithTheLastScan()
    {
        var cut = Render<LibraryActivityProgressCard>(parameters => parameters
            .Add(card => card.Queue, new IngestionQueueProgressDto { State = "idle" })
            .Add(card => card.LastCompleted, DateTimeOffset.UtcNow.AddMinutes(-5)));

        Assert.Empty(cut.FindAll("[data-testid='activity-progress-card']"));
        var line = cut.Find("[data-testid='activity-up-to-date']");
        Assert.Contains("up to date", line.TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Last completed scan", line.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgressCard_WithNoLastScan_SaysOnlyThatEverythingIsUpToDate()
    {
        var cut = Render<LibraryActivityProgressCard>();

        Assert.DoesNotContain("Last completed scan", cut.Find("[data-testid='activity-up-to-date']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Words_AFinishedBatchNamesItsItemsInPlainEnglish()
    {
        Assert.Equal("1 movie", LibraryActivityText.Counts([("Movies", 1)]));
        Assert.EndsWith("2 movies", LibraryActivityText.Counts([("Movies", 2), ("TV", 14)]), StringComparison.Ordinal);
        Assert.Equal("5 new movies", LibraryActivityText.NewLabel("Movies", 5));
    }

    [Fact]
    public void Words_MonthHeadingsAndBubblesUseTheMonthName()
    {
        var when = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

        Assert.Contains("2026", LibraryActivityText.MonthHeading(when), StringComparison.Ordinal);
        Assert.Matches("^[A-Z][a-z]{2} \\d+$", LibraryActivityText.Bubble(when));
    }
}
