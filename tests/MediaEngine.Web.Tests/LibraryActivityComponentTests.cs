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
    public void ProgressCard_WithOneAreaChosen_ShowsOnlyThatAreasPercentCountsAndReviewNumber()
    {
        var queue = new IngestionQueueProgressDto
        {
            State = "working",
            FilesFound = 40,
            FilesDone = 15,
            Progress = 0.4,
            Tally =
            [
                new IngestionQueueTypeTallyDto { MediaType = "Movies", Files = 10, FilesDone = 5, StepsTotal = 90, StepsCompleted = 45 },
                new IngestionQueueTypeTallyDto { MediaType = "TV", Files = 10, FilesDone = 10, StepsTotal = 90, StepsCompleted = 90 },
                new IngestionQueueTypeTallyDto { MediaType = "Music", Files = 20, FilesDone = 0, StepsTotal = 180, StepsCompleted = 0 },
            ],
        };
        var presentation = new IngestionPresentationSnapshotDto
        {
            IsRunning = true,
            ReadyGroups = 10,
            ReviewGroups = 11,
            ActiveOperations = 9,
            QueuedOperations = 10,
            ByType =
            [
                new IngestionPresentationTypeDto { MediaType = "Movies", ReadyGroups = 3, ReviewGroups = 2, ActiveOperations = 1, QueuedOperations = 4 },
                new IngestionPresentationTypeDto { MediaType = "Music", ReadyGroups = 7, ReviewGroups = 9, ActiveOperations = 8, QueuedOperations = 6 },
            ],
        };

        var whole = Render<LibraryActivityProgressCard>(parameters => parameters.Add(card => card.Queue, queue).Add(card => card.Presentation, presentation));
        Assert.Contains("40% processed", whole.Find("[data-testid='activity-progress-percent']").TextContent, StringComparison.Ordinal);
        Assert.Contains("Review 11 items", whole.Find("a.lact-summary__review").TextContent, StringComparison.Ordinal);

        var watch = Render<LibraryActivityProgressCard>(parameters => parameters
            .Add(card => card.Queue, queue)
            .Add(card => card.Presentation, presentation)
            .Add(card => card.MediaTypes, new HashSet<string>(["Movies", "TV", "Videos"], StringComparer.Ordinal)));

        // (45 + 90) of 180 steps; the Watch files that are not finished keep it under 100.
        Assert.Contains("75% processed", watch.Find("[data-testid='activity-progress-percent']").TextContent, StringComparison.Ordinal);
        var counts = watch.Find("[data-testid='activity-progress-counts']").TextContent;
        Assert.Contains("3 ready", counts, StringComparison.Ordinal);
        Assert.Contains("2 need review", counts, StringComparison.Ordinal);
        Assert.Contains("1 processing", counts, StringComparison.Ordinal);
        Assert.Contains("4 queued", counts, StringComparison.Ordinal);
        Assert.Contains("10 movies", counts, StringComparison.Ordinal);
        Assert.DoesNotContain("tracks", counts, StringComparison.Ordinal);
        Assert.Contains("Review 2 items", watch.Find("a.lact-summary__review").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgressCard_WithAnAreaThatHasNothingToReview_HidesTheReviewLinkEvenWhenTheLibraryHasSome()
    {
        var cut = Render<LibraryActivityProgressCard>(parameters => parameters
            .Add(card => card.Queue, new IngestionQueueProgressDto
            {
                State = "working",
                FilesFound = 10,
                Tally = [new IngestionQueueTypeTallyDto { MediaType = "Books", Files = 4, StepsTotal = 36, StepsCompleted = 9 }],
            })
            .Add(card => card.Presentation, new IngestionPresentationSnapshotDto
            {
                IsRunning = true,
                ReviewGroups = 5,
                ByType = [new IngestionPresentationTypeDto { MediaType = "Movies", ReviewGroups = 5 }],
            })
            .Add(card => card.MediaTypes, new HashSet<string>(["Books", "Comics"], StringComparer.Ordinal)));

        Assert.Empty(cut.FindAll("a.lact-summary__review"));
        Assert.Contains("25% processed", cut.Find("[data-testid='activity-progress-percent']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgressCard_NamesFilesNobodyPickedUp_WhetherTheRunIsWorkingOrDone()
    {
        var waiting = new IngestionQueueProgressDto { State = "idle", FilesWaiting = 3, WaitingFor = ["unclaimed"] };

        var idle = Render<LibraryActivityProgressCard>(parameters => parameters.Add(card => card.Queue, waiting));
        Assert.Equal("Some files are waiting · 3 files are waiting", Normalize(idle.Find("[data-testid='activity-files-waiting']").TextContent));
        Assert.Single(idle.FindAll("[data-testid='activity-up-to-date']"));

        waiting.State = "working";
        waiting.FilesFound = 10;
        var working = Render<LibraryActivityProgressCard>(parameters => parameters.Add(card => card.Queue, waiting));
        Assert.Single(working.FindAll("[data-testid='activity-files-waiting']"));

        waiting.FilesWaiting = 1;
        var one = Render<LibraryActivityProgressCard>(parameters => parameters.Add(card => card.Queue, waiting));
        Assert.Equal("Some files are waiting · 1 file is waiting", Normalize(one.Find("[data-testid='activity-files-waiting']").TextContent));
    }

    [Fact]
    public void ProgressCard_SaysNothingAboutWaitingFilesWhenNoneAreWaitingOrAToolIsMissing()
    {
        var none = Render<LibraryActivityProgressCard>(parameters => parameters.Add(card => card.Queue, new IngestionQueueProgressDto { State = "idle" }));
        Assert.Empty(none.FindAll("[data-testid='activity-files-waiting']"));

        var tool = Render<LibraryActivityProgressCard>(parameters => parameters
            .Add(card => card.Queue, new IngestionQueueProgressDto { State = "waiting", FilesWaiting = 2, WaitingFor = ["ffmpeg"] }));
        Assert.Empty(tool.FindAll("[data-testid='activity-files-waiting']"));
    }

    private static string Normalize(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

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
