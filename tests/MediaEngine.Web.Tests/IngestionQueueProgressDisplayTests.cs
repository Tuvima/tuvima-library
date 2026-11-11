using Bunit;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Libraries page's status line shows the Engine's queue snapshot as sent, in the words of the shared formatter,
/// with no arithmetic of its own beyond naming how many files are left.
/// </summary>
public sealed class IngestionQueueProgressDisplayTests : LiveLibraryTestContext
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Working_SaysHowManyFilesAreBeingAddedAndHowLongIsLeft()
    {
        var snapshot = Working();
        snapshot.EstimatedSecondsRemaining = 240;

        var cut = RenderLine(snapshot);

        Assert.Equal("Adding 254 files · about 4 min left", StatusText(cut));
        Assert.Contains("is-working", cut.Find(".libs-status__text").ClassList);
    }

    [Fact]
    public void Working_WithoutAReliableEstimate_SaysHowLongItHasBeenRunning()
    {
        var snapshot = Working();
        snapshot.EstimatedSecondsRemaining = null;
        snapshot.StartedAt = DateTimeOffset.UtcNow.AddMinutes(-4).AddSeconds(-10);

        var cut = RenderLine(snapshot);

        Assert.Equal("Adding 254 files · running for 4 min", StatusText(cut));
    }

    [Fact]
    public void Working_WithOneFileLeft_UsesTheSingularNoun()
    {
        var snapshot = Working();
        snapshot.FilesFound = 10;
        snapshot.FilesDone = 9;
        snapshot.EstimatedSecondsRemaining = 20;

        Assert.Equal("Adding 1 file · less than a minute left", StatusText(RenderLine(snapshot)));
    }

    [Fact]
    public void Working_BeforeAnyFileIsFound_SaysItIsLookingForFiles()
    {
        var snapshot = Working();
        snapshot.FilesFound = 0;
        snapshot.FilesDone = 0;
        snapshot.StartedAt = null;

        Assert.Equal("Looking for files", StatusText(RenderLine(snapshot)));
    }

    [Fact]
    public void Idle_SaysUpToDateAndWhenTheLibraryLastChecked()
    {
        var snapshot = new IngestionQueueProgressDto
        {
            State = "idle",
            Progress = 1,
            LastActivityAt = DateTimeOffset.UtcNow.AddMinutes(-10).AddSeconds(-5),
        };

        var cut = RenderLine(snapshot);

        Assert.Equal("Up to date · last checked 10 min ago", StatusText(cut));
        Assert.Contains("is-idle", cut.Find(".libs-status__text").ClassList);
    }

    [Fact]
    public void Idle_WithNoActivityYet_SaysJustUpToDate()
    {
        Assert.Equal("Up to date", StatusText(RenderLine(new IngestionQueueProgressDto { State = "idle", Progress = 1 })));
    }

    [Fact]
    public void Waiting_NamesTheMissingToolsCountsTheWaitingFilesAndLinksToTheToolsPage()
    {
        var snapshot = new IngestionQueueProgressDto
        {
            State = "waiting",
            FilesFound = 40,
            FilesDone = 10,
            FilesWaiting = 30,
            WaitingFor = ["ffmpeg"],
            Progress = 0.25,
        };

        var cut = RenderLine(snapshot);

        Assert.Equal("Waiting for media tools · 30 files are waiting", StatusText(cut));
        var link = cut.Find(".libs-status a");
        Assert.Equal("/settings/tools", link.GetAttribute("href"));
        Assert.Contains("Tools and requirements", link.TextContent, StringComparison.Ordinal);
        Assert.Contains("is-waiting", cut.Find(".libs-status__text").ClassList);
    }

    [Fact]
    public void Waiting_ForAnotherTool_NamesThatTool()
    {
        var snapshot = new IngestionQueueProgressDto { State = "waiting", FilesFound = 3, FilesWaiting = 3, WaitingFor = ["pdfium"], Progress = 0.1 };

        Assert.StartsWith("Waiting for pdfium", StatusText(RenderLine(snapshot)), StringComparison.Ordinal);
    }

    [Fact]
    public void NoSnapshotYetWhileLoading_ShowsASkeletonInsteadOfAStatus()
    {
        var pending = new TaskCompletionSource<IngestionPresentationSnapshotDto?>();
        Presentation = () => pending.Task;

        var cut = Render<LibraryStatusLine>();

        Assert.Single(cut.FindAll(".libs-status .app-skeleton"));
        Assert.Empty(cut.FindAll("[data-testid='library-status-text']"));
        Assert.Equal("true", cut.Find(".libs-status").GetAttribute("aria-busy"));
        pending.SetResult(new IngestionPresentationSnapshotDto());
    }

    [Fact]
    public void CheckFoldersForChanges_AsksTheEngineToScanWhileTheLibraryIsIdle()
    {
        var cut = RenderLine(new IngestionQueueProgressDto { State = "idle", Progress = 1 });
        var button = cut.FindAll(".libs-status button").Single(b => b.TextContent.Contains("Check folders for changes", StringComparison.Ordinal));
        Assert.False(button.HasAttribute("disabled"));

        button.Click();

        cut.WaitForAssertion(() => Assert.Contains("rescan", Calls));
    }

    [Fact]
    public void CheckFoldersForChanges_IsOffWhileTheLibraryIsAlreadyAddingFiles()
    {
        var cut = RenderLine(Working());

        var button = cut.FindAll(".libs-status button").Single(b => b.TextContent.Contains("Check folders for changes", StringComparison.Ordinal));
        Assert.True(button.HasAttribute("disabled"));
    }

    [Fact]
    public void CheckFoldersForChanges_IsOffOnAPhone()
    {
        Queue = () => Task.FromResult<IngestionQueueProgressDto?>(new IngestionQueueProgressDto { State = "idle", Progress = 1 });
        var cut = Render<LibraryStatusLine>(parameters => parameters.Add(line => line.ReadOnly, true));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='library-status-text']")));

        var button = cut.FindAll(".libs-status button").Single(b => b.TextContent.Contains("Check folders for changes", StringComparison.Ordinal));
        Assert.True(button.HasAttribute("disabled"));
    }

    [Fact]
    public void EngineUnreachable_WhenTheClientReportsFailedRequestsAsNull_SaysSoWithRetry()
    {
        // The Engine client swallows request errors and returns null, so this is what production sees.
        Presentation = () => Task.FromResult<IngestionPresentationSnapshotDto?>(null);
        Queue = () => Task.FromResult<IngestionQueueProgressDto?>(null);

        var cut = Render<LibraryStatusLine>();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("The Library's engine isn't responding", cut.Find(".libs-status__text").TextContent.Trim());
            Assert.Contains("Retry", cut.Find(".libs-status button").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void EngineUnreachable_RetryRunsTheLoadAgainAndRecovers()
    {
        var failing = true;
        Presentation = () => Task.FromResult<IngestionPresentationSnapshotDto?>(failing ? null : new IngestionPresentationSnapshotDto());
        Queue = () => Task.FromResult<IngestionQueueProgressDto?>(failing ? null : Working());

        var cut = Render<LibraryStatusLine>();
        cut.WaitForAssertion(() => Assert.Equal("The Library's engine isn't responding", cut.Find(".libs-status__text").TextContent.Trim()));
        var loadsBeforeRetry = Calls.Count(call => call == "presentation");

        failing = false;
        cut.Find(".libs-status button").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.True(Calls.Count(call => call == "presentation") > loadsBeforeRetry);
            Assert.StartsWith("Adding 254 files", StatusText(cut), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void EngineUnreachable_WhenALoadThrows_AlsoSaysSo()
    {
        Presentation = () => Task.FromException<IngestionPresentationSnapshotDto?>(new HttpRequestException("down"));

        var cut = Render<LibraryStatusLine>();

        cut.WaitForAssertion(() =>
            Assert.Equal("The Library's engine isn't responding", cut.Find(".libs-status__text").TextContent.Trim()));
    }

    [Fact]
    public void EngineUnreachable_WhenTheEngineGoesOfflineAndComesBack_FollowsTheConnection()
    {
        Queue = () => Task.FromResult<IngestionQueueProgressDto?>(Working());
        var cut = Render<LibraryStatusLine>();
        cut.WaitForAssertion(() => Assert.StartsWith("Adding 254 files", StatusText(cut), StringComparison.Ordinal));
        var orchestrator = Services.GetRequiredService<UIOrchestratorService>();

        orchestrator.SetEngineConnectionState(EngineConnectionState.Offline);
        cut.WaitForAssertion(() => Assert.Equal("The Library's engine isn't responding", cut.Find(".libs-status__text").TextContent.Trim()));

        orchestrator.SetEngineConnectionState(EngineConnectionState.Online);
        cut.WaitForAssertion(() => Assert.StartsWith("Adding 254 files", StatusText(cut), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(EngineConnectionState.Reconnecting)]
    [InlineData(EngineConnectionState.LiveUpdatesDisconnected)]
    public void LiveUpdatesOff_KeepsShowingTheStatusWithoutSayingTheEngineIsNotResponding(EngineConnectionState liveUpdatesOff)
    {
        Queue = () => Task.FromResult<IngestionQueueProgressDto?>(Working());
        var cut = Render<LibraryStatusLine>();
        cut.WaitForAssertion(() => Assert.StartsWith("Adding 254 files", StatusText(cut), StringComparison.Ordinal));

        Services.GetRequiredService<UIOrchestratorService>().SetEngineConnectionState(liveUpdatesOff);

        cut.WaitForAssertion(() => Assert.StartsWith("Adding 254 files", StatusText(cut), StringComparison.Ordinal));
    }

    [Fact]
    public void QueueSnapshotPushedLater_UpdatesTheOpenStatusLine()
    {
        Queue = () => Task.FromResult<IngestionQueueProgressDto?>(Working());
        var cut = Render<LibraryStatusLine>();
        cut.WaitForAssertion(() => Assert.StartsWith("Adding 254 files", StatusText(cut), StringComparison.Ordinal));

        var next = Working();
        next.FilesFound = 628;
        next.UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(5);
        State.PushIngestionQueueProgress(next);

        cut.WaitForAssertion(() => Assert.StartsWith("Adding 374 files", StatusText(cut), StringComparison.Ordinal), TimeSpan.FromSeconds(6));
    }

    [Fact]
    public void TwoTabsSharingTheLiveState_KeepItRunningUntilTheLastOneLeaves()
    {
        Queue = () => Task.FromResult<IngestionQueueProgressDto?>(Working());
        var first = Render<LibraryStatusLine>();
        var second = Render<LibraryStatusLine>();
        first.WaitForAssertion(() => Assert.StartsWith("Adding 254 files", StatusText(first), StringComparison.Ordinal));
        var loadsBefore = Calls.Count(call => call == "queue");

        first.Dispose();
        State.PushIngestionQueueProgress(Working());
        var orchestrator = Services.GetRequiredService<UIOrchestratorService>();
        orchestrator.SetEngineConnectionState(EngineConnectionState.Offline);
        orchestrator.SetEngineConnectionState(EngineConnectionState.Online);

        // The second tab still follows the connection, so the first one leaving did not stop the shared updates.
        second.WaitForAssertion(() => Assert.True(Calls.Count(call => call == "queue") > loadsBefore), TimeSpan.FromSeconds(6));
    }

    [Theory]
    [InlineData(0, "Less than a minute left")]
    [InlineData(59, "Less than a minute left")]
    [InlineData(60, "About 1 min left")]
    [InlineData(61, "About 2 min left")]
    [InlineData(600, "About 10 min left")]
    [InlineData(3600, "About 1 hr left")]
    [InlineData(7800, "About 2 hr 10 min left")]
    public void TimeLine_WithAnEstimate_RoundsUpToWholeMinutes(int seconds, string expected)
    {
        var snapshot = Working();
        snapshot.EstimatedSecondsRemaining = seconds;

        Assert.Equal(expected, IngestionQueueProgressText.TimeLine(snapshot, Now));
    }

    [Fact]
    public void TimeLine_WithNeitherEstimateNorStart_ShowsNothing()
    {
        var snapshot = Working();
        snapshot.EstimatedSecondsRemaining = null;
        snapshot.StartedAt = null;

        Assert.Null(IngestionQueueProgressText.TimeLine(snapshot, Now));
    }

    [Theory]
    [InlineData(0, "Running for less than a minute")]
    [InlineData(59, "Running for less than a minute")]
    [InlineData(60, "Running for 1 min")]
    [InlineData(59 * 60 + 59, "Running for 59 min")]
    [InlineData(60 * 60, "Running for 1 hr")]
    [InlineData(135 * 60, "Running for 2 hr 15 min")]
    public void TimeLine_WithoutAnEstimate_FormatsElapsedTimeInWholeMinutesAndHours(int elapsedSeconds, string expected)
    {
        var snapshot = Working();
        snapshot.EstimatedSecondsRemaining = null;
        snapshot.StartedAt = Now.AddSeconds(-elapsedSeconds);

        Assert.Equal(expected, IngestionQueueProgressText.TimeLine(snapshot, Now));
    }

    [Theory]
    [InlineData(1, 0, "120 of 508 done · 1 needs review")]
    [InlineData(3, 0, "120 of 508 done · 3 need review")]
    [InlineData(1, 1, "120 of 508 done · 1 needs review · 1 failed")]
    [InlineData(0, 2, "120 of 508 done · 2 failed")]
    public void StatusLine_UsesTheRightVerbForOneOrManyFilesNeedingReview(int needsReview, int failed, string expected)
    {
        var snapshot = Working();
        snapshot.FilesDone = 120;
        snapshot.Outcomes = new IngestionQueueOutcomesDto { NeedsReview = needsReview, Failed = failed };

        Assert.Equal(expected, IngestionQueueProgressText.StatusLine(snapshot));
    }

    [Theory]
    [InlineData(1, 0, "120 of 508 done · 1 unmatched")]
    [InlineData(4, 0, "120 of 508 done · 4 unmatched")]
    [InlineData(2, 1, "120 of 508 done · 2 unmatched · 1 failed")]
    public void StatusLine_NamesFilesTheEngineCouldNotMatch_AsUnmatchedNotAsNeedingReview(int unmatched, int failed, string expected)
    {
        var snapshot = Working();
        snapshot.FilesDone = 120;
        snapshot.Outcomes = new IngestionQueueOutcomesDto { Unmatched = unmatched, Failed = failed };

        Assert.Equal(expected, IngestionQueueProgressText.StatusLine(snapshot));
    }

    [Fact]
    public void Working_BeforeAnyFileIsFound_SaysItIsLookingNeverFoundZeroFiles()
    {
        var snapshot = Working();
        snapshot.FilesFound = 0;
        snapshot.FilesDone = 0;
        snapshot.Progress = 0;

        Assert.Equal("Looking for files", IngestionQueueProgressText.Headline(snapshot));
    }

    private IRenderedComponent<LibraryStatusLine> RenderLine(IngestionQueueProgressDto queue)
    {
        Queue = () => Task.FromResult<IngestionQueueProgressDto?>(queue);
        var cut = Render<LibraryStatusLine>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='library-status-text']")));
        return cut;
    }

    private static string StatusText(IRenderedComponent<LibraryStatusLine> cut) => cut.Find("[data-testid='library-status-text']").TextContent.Trim();

    private static IngestionQueueProgressDto Working() => new()
    {
        State = "working",
        FilesFound = 508,
        FilesDone = 254,
        Progress = 0.5,
        FilesUnsorted = 29,
        Tally =
        [
            new IngestionQueueTypeTallyDto { MediaType = "Movies", Files = 212 },
            new IngestionQueueTypeTallyDto { MediaType = "TV", Files = 140 },
            new IngestionQueueTypeTallyDto { MediaType = "Music", Files = 96 },
            new IngestionQueueTypeTallyDto { MediaType = "Books", Files = 31 },
        ],
        Outcomes = new IngestionQueueOutcomesDto { Added = 250, NeedsReview = 3, Failed = 1 },
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-4),
        EstimatedSecondsRemaining = 330,
        LastActivityAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
}
