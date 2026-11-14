using Bunit;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Web.Components.Settings.LibraryActivity;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// One rule for "working" on the Activity page (spec library-activity D29, issue 880): when the Engine says a finished import has only
/// a title waiting in Needs Review and follow-up jobs open, the card says everything is up to date, Working now is gone, and the
/// Background line names the follow-up work with its count.
/// </summary>
public sealed class LibraryActivityOneRuleForWorkingTests : AsyncBunitContext
{
    private const string NamedFormat = "{0} for {1:N0} {2}";

    private static IngestionPresentationSnapshotDto FinishedImportWithPreviewPicturesOpen() => new()
    {
        IsRunning = false,
        Status = "idle",
        NeedReviewCount = 1,
        BackgroundWork =
        [
            new IngestionBackgroundWorkDto { Key = "playback.scrub_previews", Label = "Making preview pictures", Count = 12, Noun = "videos" },
        ],
    };

    public LibraryActivityOneRuleForWorkingTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    [Fact]
    public void AFinishedImportWithOneTitleInNeedsReviewAndPreviewPictureJobsOpen_ShowsEverythingUpToDate_NoWorkingNow_AndABackgroundLine()
    {
        var presentation = FinishedImportWithPreviewPicturesOpen();

        var card = Render<LibraryActivityProgressCard>(parameters => parameters
            .Add(c => c.Queue, new IngestionQueueProgressDto { State = "idle" })
            .Add(c => c.Presentation, presentation)
            .Add(c => c.LastCompleted, DateTimeOffset.UtcNow.AddMinutes(-3)));
        var names = LibraryActivityBackground.Names(presentation.BackgroundWork, [], NamedFormat);
        var working = Render<LibraryActivityWorkingNow>(parameters => parameters
            .Add(w => w.Items, presentation.CurrentMedia)
            .Add(w => w.Background, names));

        Assert.Contains("Everything is up to date", card.Find("[data-testid='activity-up-to-date']").TextContent, StringComparison.Ordinal);
        Assert.Empty(working.FindAll("[data-testid='activity-working-now']"));
        var background = working.Find("[data-testid='activity-background']");
        Assert.Equal("Background · Making preview pictures for 12 videos", string.Join(' ', background.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
    }

    [Fact]
    public void ABackgroundLine_NamesOneVideoInTheSingular_AndEveryKindOfFollowUpWork()
    {
        var names = LibraryActivityBackground.Names(
            [
                new IngestionBackgroundWorkDto { Key = "playback.scrub_previews", Label = "Making preview pictures", Count = 1, Noun = "video" },
                new IngestionBackgroundWorkDto { Key = "text_track.lyrics", Label = "Finding lyrics", Count = 40, Noun = "songs" },
            ],
            [],
            NamedFormat);

        Assert.Equal(["Making preview pictures for 1 video", "Finding lyrics for 40 songs"], names);
    }

    [Fact]
    public void TheBackgroundLine_DoesNotListFollowUpWorkTwice_ButKeepsOtherBackgroundWorkAndNeverShowsIngestion()
    {
        var now = DateTimeOffset.UtcNow;
        var shell = new[]
        {
            new ShellActivityItem("operation:1", ShellActivityKind.Maintenance, "Making preview pictures", null, null, now, "playback.scrub_previews"),
            new ShellActivityItem("operation:2", ShellActivityKind.Ai, "AI is analyzing themes", null, null, now, "ai.vibe_tags"),
            new ShellActivityItem("ingestion:queue", ShellActivityKind.Ingestion, "Processing library files", null, 40, now),
        };

        var names = LibraryActivityBackground.Names(FinishedImportWithPreviewPicturesOpen().BackgroundWork, shell, NamedFormat);

        Assert.Equal(["Making preview pictures for 12 videos", "AI is analyzing themes"], names);
    }

    [Fact]
    public void FollowUpWorkTheEngineHasNotNamedYet_StillShowsFromTheMenuIndicatorsItems()
    {
        var shell = new[]
        {
            new ShellActivityItem("operation:1", ShellActivityKind.Maintenance, "Making preview pictures", null, null, DateTimeOffset.UtcNow, "playback.scrub_previews"),
        };

        Assert.Equal(["Making preview pictures"], LibraryActivityBackground.Names(null, shell, NamedFormat));
    }
}
