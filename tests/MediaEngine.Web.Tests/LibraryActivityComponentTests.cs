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
    public void ProgressCard_WhileWorking_ShowsTheSlimCard_WithTheIconTheTitleInProgressTheLineTheBarAndThePill()
    {
        var cut = Render<LibraryActivityProgressCard>(parameters => parameters
            .Add(card => card.Queue, new IngestionQueueProgressDto { State = "working", FilesFound = 538, FilesDone = 427, Progress = 0.79, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-16).AddSeconds(-5) })
            .Add(card => card.Presentation, new IngestionPresentationSnapshotDto { IsRunning = true, NeedReviewCount = 2 }));

        Assert.Single(cut.FindAll("[data-testid='activity-progress-card']"));
        Assert.Empty(cut.FindAll("[data-testid='activity-up-to-date']"));
        Assert.NotNull(cut.Find(".lact-progress__icon .app-sync-icon .is-spinning"));
        Assert.Contains("Updating your library", cut.Find(".lact-progress__title").TextContent, StringComparison.Ordinal);
        Assert.Equal("In progress", Normalize(cut.Find("[data-testid='activity-in-progress']").TextContent));
        Assert.Equal("427 of 538 files processed · 16 min elapsed", Normalize(cut.Find("[data-testid='activity-progress-counts']").TextContent));
        var pill = cut.Find("a.lact-progress__pill");
        Assert.Equal("/settings/review", pill.GetAttribute("href"));
        Assert.Equal("2 need review", Normalize(pill.TextContent));
        var bar = cut.Find("[data-testid='activity-progress-bar']");
        Assert.Equal("79%", Normalize(bar.QuerySelector(".app-progress-bar__value")!.TextContent));
    }

    [Fact]
    public void ProgressCard_NeverShowsTheOldCountsByKindOrTheReadyProcessingAndQueuedNumbers()
    {
        var cut = Render<LibraryActivityProgressCard>(parameters => parameters
            .Add(card => card.Queue, new IngestionQueueProgressDto
            {
                State = "working",
                FilesFound = 40,
                FilesDone = 15,
                Progress = 0.4,
                Tally = [new IngestionQueueTypeTallyDto { MediaType = "Movies", Files = 40, FilesDone = 15, StepsTotal = 90, StepsCompleted = 36 }],
            })
            .Add(card => card.Presentation, new IngestionPresentationSnapshotDto { IsRunning = true, ReadyGroups = 10, ActiveOperations = 9, QueuedOperations = 10, NeedReviewCount = 3 }));

        var text = Normalize(cut.Find("[data-testid='activity-progress-card']").TextContent);
        foreach (var gone in new[] { "ready", "processing", "queued", "movies", "% processed" })
        {
            Assert.DoesNotContain(gone, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ProgressCard_WithOneAreaChosen_ShowsOnlyThatAreasFilesAndPercent_ButTheWholeNeedReviewNumber()
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
        var presentation = new IngestionPresentationSnapshotDto { IsRunning = true, NeedReviewCount = 11 };

        var whole = Render<LibraryActivityProgressCard>(parameters => parameters.Add(card => card.Queue, queue).Add(card => card.Presentation, presentation));
        Assert.Equal("40%", Normalize(whole.Find(".app-progress-bar__value").TextContent));
        Assert.StartsWith("15 of 40 files processed", Normalize(whole.Find("[data-testid='activity-progress-counts']").TextContent), StringComparison.Ordinal);
        Assert.Equal("11 need review", Normalize(whole.Find("a.lact-progress__pill").TextContent));

        var watch = Render<LibraryActivityProgressCard>(parameters => parameters
            .Add(card => card.Queue, queue)
            .Add(card => card.Presentation, presentation)
            .Add(card => card.MediaTypes, new HashSet<string>(["Movies", "TV", "Videos"], StringComparer.Ordinal)));

        // (45 + 90) of 180 steps; the Watch files that are not finished keep it under 100.
        Assert.Equal("75%", Normalize(watch.Find(".app-progress-bar__value").TextContent));
        Assert.StartsWith("15 of 20 files processed", Normalize(watch.Find("[data-testid='activity-progress-counts']").TextContent), StringComparison.Ordinal);
        // The need-review number is the Needs Review list's own count (D28), so a chip never narrows it.
        Assert.Equal("11 need review", Normalize(watch.Find("a.lact-progress__pill").TextContent));
    }

    [Fact]
    public void ProgressCard_WithAChipPickedForAnAreaWithNothingOfItsOwnToReview_StillShowsTheWholeNeedReviewNumber()
    {
        var cut = Render<LibraryActivityProgressCard>(parameters => parameters
            .Add(card => card.Queue, new IngestionQueueProgressDto
            {
                State = "working",
                FilesFound = 10,
                Tally = [new IngestionQueueTypeTallyDto { MediaType = "Books", Files = 4, StepsTotal = 36, StepsCompleted = 9 }],
            })
            .Add(card => card.Presentation, new IngestionPresentationSnapshotDto { IsRunning = true, NeedReviewCount = 5 })
            .Add(card => card.MediaTypes, new HashSet<string>(["Books", "Comics"], StringComparer.Ordinal)));

        Assert.Equal("5 need review", Normalize(cut.Find("a.lact-progress__pill").TextContent));
        Assert.Equal("25%", Normalize(cut.Find(".app-progress-bar__value").TextContent));
    }

    [Fact]
    public void ProgressCard_WhenNothingWaitsInNeedsReview_ShowsNoPill()
    {
        var cut = Render<LibraryActivityProgressCard>(parameters => parameters
            .Add(card => card.Queue, new IngestionQueueProgressDto { State = "working", FilesFound = 10 })
            .Add(card => card.Presentation, new IngestionPresentationSnapshotDto { IsRunning = true, NeedReviewCount = 0 }));

        Assert.Empty(cut.FindAll("a.lact-progress__pill"));
        Assert.Empty(cut.FindAll("[data-testid='activity-need-review']"));
    }

    [Fact]
    public void ProgressCard_WithOneTitleToReview_SaysOneNeedsReview_AndAHostForNonAdministratorsCanHideThePill()
    {
        var presentation = new IngestionPresentationSnapshotDto { IsRunning = true, NeedReviewCount = 1 };
        var queue = new IngestionQueueProgressDto { State = "working", FilesFound = 10, FilesDone = 2, Progress = 0.2 };

        var one = Render<LibraryActivityProgressCard>(parameters => parameters.Add(card => card.Queue, queue).Add(card => card.Presentation, presentation));
        Assert.Equal("1 needs review", Normalize(one.Find("a.lact-progress__pill").TextContent));

        var hidden = Render<LibraryActivityProgressCard>(parameters => parameters
            .Add(card => card.Queue, queue)
            .Add(card => card.Presentation, presentation)
            .Add(card => card.ShowReview, false));
        Assert.Empty(hidden.FindAll("a.lact-progress__pill"));
    }

    [Fact]
    public void ProgressCard_WithTheTotalNotKnownYet_ShowsFilesFoundOverAMovingBarAndNoPercent()
    {
        var cut = Render<LibraryActivityProgressCard>(parameters => parameters
            .Add(card => card.Presentation, new IngestionPresentationSnapshotDto { IsRunning = true, FilesDiscovered = 412 }));

        Assert.Equal("412 files found", Normalize(cut.Find("[data-testid='activity-progress-counts']").TextContent));
        Assert.NotNull(cut.Find("[data-testid='activity-progress-bar'].app-progress-bar--indeterminate"));
        Assert.Empty(cut.FindAll(".app-progress-bar__value"));
        Assert.DoesNotContain("elapsed", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgressCard_HasNoViewActivityButton_WorkingOrIdle()
    {
        var working = new IngestionQueueProgressDto { State = "working", FilesFound = 10, FilesDone = 2, Progress = 0.2 };

        var busy = Render<LibraryActivityProgressCard>(parameters => parameters.Add(card => card.Queue, working));
        var idle = Render<LibraryActivityProgressCard>();

        Assert.Empty(busy.FindAll("a[href='/settings/library-activity']"));
        Assert.Empty(idle.FindAll("a[href='/settings/library-activity']"));
        Assert.DoesNotContain("View activity", busy.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("View activity", idle.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgressCard_StacksOnAPhone_IconAndTitleThenTheLineThenThePillThenTheBar_WithNothingHidden()
    {
        var css = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MediaEngine.Web", "Components", "Settings", "LibraryActivity", "LibraryActivityProgressCard.razor.css"));
        var phone = css[css.IndexOf("@media (max-width: 600px)", StringComparison.Ordinal)..];

        Assert.Contains("grid-template-areas: \"icon head\" \"line line\" \"actions actions\" \"bar bar\";", phone, StringComparison.Ordinal);
        Assert.DoesNotContain("display: none", phone, StringComparison.Ordinal);
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
