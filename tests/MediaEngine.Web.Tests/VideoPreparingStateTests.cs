using System.Net;
using System.Net.Http.Json;
using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Player;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>Playback path WP4 (spec section 4): every row of the player's waiting-state table, on the rules, the component and the session.</summary>
public sealed class VideoPreparingStateTests : AsyncBunitContext
{
    private static PlaybackPathDto Path(string rung, string phase, string label, int? percent = null, string ranOn = PlaybackEncodePlaces.None, string? detail = null) => new()
    {
        Rung = rung,
        Phase = phase,
        WaitingLabel = label,
        PercentPrepared = percent,
        RanOn = ranOn,
        FailureDetail = detail,
    };

    // ---- The rules: what each row of the table becomes ----

    [Fact]
    public void Repackaging_ShowsTheEnginesLineAndNoPercent()
    {
        var view = PlaybackWaitingRules.ForPreparing(Path(PlaybackRungs.Repackage, PlaybackPathPhases.Working, "Getting this ready · about a few seconds"));

        Assert.Equal(PlaybackWaitingKind.Working, view.Kind);
        Assert.Equal("Getting this ready · about a few seconds", view.Line);
        Assert.Null(view.Percent);
        Assert.False(view.ExpectSoon);
    }

    [Fact]
    public void ConvertingSound_ShowsTheEnginesLine()
    {
        var view = PlaybackWaitingRules.ForPreparing(Path(PlaybackRungs.ConvertSound, PlaybackPathPhases.Working, "Getting this ready · converting sound"));

        Assert.Equal(PlaybackWaitingKind.Working, view.Kind);
        Assert.Equal("Getting this ready · converting sound", view.Line);
    }

    [Fact]
    public void EncodingOnTheGraphicsCard_ShowsTheLineAndExpectsToStartSoonUntilAPercentIsKnown()
    {
        var path = Path(PlaybackRungs.EncodePicture, PlaybackPathPhases.Working, "Preparing picture · using your graphics card", ranOn: PlaybackEncodePlaces.GraphicsCard);

        var before = PlaybackWaitingRules.ForPreparing(path);
        var after = PlaybackWaitingRules.ForPreparing(path with { PercentPrepared = 22 });

        Assert.Equal("Preparing picture · using your graphics card", before.Line);
        Assert.True(before.ExpectSoon);
        Assert.Null(before.Percent);
        Assert.Equal(22, after.Percent);
        Assert.False(after.ExpectSoon);
    }

    [Fact]
    public void EncodingOnTheProcessor_KeepsTheEnginesReasonAndClampsThePercent()
    {
        var view = PlaybackWaitingRules.ForPreparing(Path(PlaybackRungs.EncodePicture, PlaybackPathPhases.Working,
            "Preparing picture · using processor: graphics card unavailable", percent: 140, ranOn: PlaybackEncodePlaces.Processor));

        Assert.Equal("Preparing picture · using processor: graphics card unavailable", view.Line);
        Assert.Equal(100, view.Percent);
    }

    [Fact]
    public void WaitingForASlot_IsItsOwnKindWithTheEnginesLine()
    {
        var view = PlaybackWaitingRules.ForPreparing(Path(PlaybackRungs.EncodePicture, PlaybackPathPhases.WaitingForSlot, "Waiting for another title to finish preparing"));

        Assert.Equal(PlaybackWaitingKind.WaitingForSlot, view.Kind);
        Assert.Equal("Waiting for another title to finish preparing", view.Line);
        Assert.False(view.ExpectSoon);
    }

    [Fact]
    public void AFailedPath_CarriesTheSentenceAndTheEnginesTextForShowDetails()
    {
        var view = PlaybackWaitingRules.ForPreparing(Path(PlaybackRungs.EncodePicture, PlaybackPathPhases.Failed, "This title could not be prepared", detail: "ffmpeg exited with code 1"));

        Assert.Equal(PlaybackWaitingKind.Failed, view.Kind);
        Assert.Equal("This title could not be prepared", view.Line);
        Assert.Equal("ffmpeg exited with code 1", view.Detail);
    }

    [Fact]
    public void AFailureWithNoPath_FallsBackToThePlayersMessageAsItsDetail()
    {
        var view = PlaybackWaitingRules.ForFailure(null, "The Engine has not finished preparing a compatible video stream.");

        Assert.Equal(PlaybackWaitingKind.Failed, view.Kind);
        Assert.False(view.HasLine);
        Assert.Equal("The Engine has not finished preparing a compatible video stream.", view.Detail);
    }

    [Fact]
    public void ANeverReportedPath_StillGetsAWorkingStateAndNeverAnEmptyPlayer()
    {
        var view = PlaybackWaitingRules.ForPreparing(null);

        Assert.Equal(PlaybackWaitingKind.Working, view.Kind);
        Assert.False(view.HasLine);
    }

    // ---- The component ----

    [Fact]
    public void Working_ShowsTheTitleTheEnginesLineAndNeverTheOldGenericText()
    {
        var cut = Render<VideoPreparingState>(p => p
            .Add(c => c.View, PlaybackWaitingRules.ForPreparing(Path(PlaybackRungs.ConvertSound, PlaybackPathPhases.Working, "Getting this ready · converting sound")))
            .Add(c => c.Title, "Dune: Part One"));

        Assert.Contains("Dune: Part One", cut.Markup);
        Assert.Equal("Getting this ready · converting sound", cut.Find("[data-video-preparing-line]").TextContent.Trim());
        Assert.DoesNotContain("Preparing video", cut.Markup);
        Assert.Empty(cut.FindAll("[data-video-preparing-cancel]"));
        Assert.Empty(cut.FindAll("[data-video-preparing-percent]"));
    }

    [Fact]
    public void Working_WithoutAnyEngineLine_ShowsTheNeutralLine()
    {
        var cut = Render<VideoPreparingState>(p => p.Add(c => c.View, PlaybackWaitingRules.ForPreparing(null)));

        Assert.Equal(PlaybackWaitingRules.NeutralLine, cut.Find("[data-video-preparing-line]").TextContent.Trim());
    }

    [Fact]
    public void EncodingWithAPercent_ShowsARealProgressBar()
    {
        var cut = Render<VideoPreparingState>(p => p.Add(c => c.View,
            PlaybackWaitingRules.ForPreparing(Path(PlaybackRungs.EncodePicture, PlaybackPathPhases.Working, "Preparing picture · using your graphics card", percent: 22, ranOn: PlaybackEncodePlaces.GraphicsCard))));

        Assert.Equal("22", cut.Find("[role=progressbar]").GetAttribute("aria-valuenow"));
        Assert.Contains("22% prepared", cut.Markup);
        Assert.Empty(cut.FindAll("[data-video-preparing-note]"));
    }

    [Fact]
    public void EncodingBeforeAPercentIsKnown_SaysPlaybackStartsInAMoment()
    {
        var cut = Render<VideoPreparingState>(p => p.Add(c => c.View,
            PlaybackWaitingRules.ForPreparing(Path(PlaybackRungs.EncodePicture, PlaybackPathPhases.Working, "Preparing picture · using your graphics card", ranOn: PlaybackEncodePlaces.GraphicsCard))));

        Assert.Equal("Playback starts in a moment", cut.Find("[data-video-preparing-note]").TextContent.Trim());
        Assert.Empty(cut.FindAll("[role=progressbar]"));
    }

    [Fact]
    public void WaitingForASlot_OffersACancelThatGoesBack()
    {
        var cancelled = 0;
        var cut = Render<VideoPreparingState>(p => p
            .Add(c => c.View, PlaybackWaitingRules.ForPreparing(Path(PlaybackRungs.EncodePicture, PlaybackPathPhases.WaitingForSlot, "Waiting for another title to finish preparing")))
            .Add(c => c.OnCancel, () => { cancelled++; }));

        Assert.Equal("Waiting for another title to finish preparing", cut.Find("[data-video-preparing-line]").TextContent.Trim());
        cut.Find("[data-video-preparing-cancel]").Click();

        Assert.Equal(1, cancelled);
    }

    [Fact]
    public void Failed_ShowsOneSentenceTryAgainAndHidesTheEnginesTextUntilShowDetails()
    {
        var retried = 0;
        var cut = Render<VideoPreparingState>(p => p
            .Add(c => c.View, PlaybackWaitingRules.ForPreparing(Path(PlaybackRungs.EncodePicture, PlaybackPathPhases.Failed, "This title could not be prepared", detail: "ffmpeg exited with code 1")))
            .Add(c => c.OnRetry, () => { retried++; }));

        Assert.Equal("This title could not be prepared", cut.Find("[data-video-preparing-line]").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-video-preparing-detail-text]"));
        Assert.DoesNotContain("ffmpeg exited", cut.Markup);

        cut.Find("[data-video-preparing-details]").Click();
        Assert.Equal("ffmpeg exited with code 1", cut.Find("[data-video-preparing-detail-text]").TextContent.Trim());
        Assert.Contains("Hide details", cut.Find("[data-video-preparing-details]").TextContent);

        cut.Find("[data-video-preparing-retry]").Click();
        Assert.Equal(1, retried);
        Assert.Empty(cut.FindAll("[data-video-preparing-detail-text]"));
    }

    [Fact]
    public void Failed_WithNoEngineSentence_SaysThePlainSentenceAndOffersNoDetailsWhenThereAreNone()
    {
        var cut = Render<VideoPreparingState>(p => p.Add(c => c.View, PlaybackWaitingRules.ForFailure(null, null)));

        Assert.Equal(PlaybackWaitingRules.NeutralFailure, cut.Find("[data-video-preparing-line]").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-video-preparing-details]"));
        Assert.Single(cut.FindAll("[data-video-preparing-retry]"));
    }

    // ---- The session: the same state on every client that plays through the controller ----

    [Fact]
    public async Task AFailedManifest_LeavesTheFailedStateWithTheEnginesDetail()
    {
        var service = new PlaybackSessionController(null!, null!);
        var item = Video("Dune: Part One") with
        {
            StreamUrl = null,
            Manifest = new PlaybackManifestDto
            {
                HlsStatus = "failed",
                RecommendedDelivery = PlaybackDeliveryModes.Hls,
                PlaybackPath = Path(PlaybackRungs.EncodePicture, PlaybackPathPhases.Failed, "This title could not be prepared", detail: "encoder start-up failed"),
            },
        };

        await service.PlayVideoAsync(item);

        Assert.NotNull(service.CurrentWaiting);
        Assert.Equal(PlaybackWaitingKind.Failed, service.CurrentWaiting!.Kind);
        Assert.Equal("encoder start-up failed", service.CurrentWaiting.Detail);
        Assert.False(string.IsNullOrWhiteSpace(service.CurrentError));
    }

    [Fact]
    public async Task APreparingManifest_ShowsTheEnginesLineWhileItWaitsThenClearsWhenPlaybackIsReady()
    {
        var handler = new ManifestHandler(new PlaybackManifestDto { HlsStatus = "streaming", RecommendedDelivery = PlaybackDeliveryModes.Hls, HlsUrl = "/stream/hls/x/master.m3u8" });
        var api = new EngineApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") }, NullLogger<EngineApiClient>.Instance);
        var service = new PlaybackSessionController(null!, api);
        var seen = new List<PlaybackWaitingView?>();
        service.Changed += _ => seen.Add(service.CurrentWaiting);
        var item = Video("Dune: Part One") with
        {
            StreamUrl = null,
            Manifest = new PlaybackManifestDto
            {
                HlsStatus = "preparing",
                RecommendedDelivery = PlaybackDeliveryModes.Hls,
                PlaybackPath = Path(PlaybackRungs.EncodePicture, PlaybackPathPhases.WaitingForSlot, "Waiting for another title to finish preparing"),
            },
        };

        await service.PlayVideoAsync(item);

        Assert.Contains(seen, view => view is { Kind: PlaybackWaitingKind.WaitingForSlot } && view.Line == "Waiting for another title to finish preparing");
        Assert.Null(service.CurrentWaiting);
        Assert.Null(service.CurrentError);
    }

    [Fact]
    public async Task TryAgain_AsksTheEngineAfreshAndLeavesTheFailedState()
    {
        var handler = new ManifestHandler(new PlaybackManifestDto { HlsStatus = "streaming", RecommendedDelivery = PlaybackDeliveryModes.Hls, HlsUrl = "/stream/hls/x/master.m3u8" });
        var api = new EngineApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") }, NullLogger<EngineApiClient>.Instance);
        var service = new PlaybackSessionController(null!, api);
        var item = Video("Dune: Part One") with
        {
            StreamUrl = null,
            Manifest = new PlaybackManifestDto { HlsStatus = "failed", RecommendedDelivery = PlaybackDeliveryModes.Hls },
        };
        await service.PlayVideoAsync(item);
        Assert.Equal(PlaybackWaitingKind.Failed, service.CurrentWaiting?.Kind);

        await service.RetryPreparationAsync();

        Assert.Equal(1, handler.ManifestRequests);
        Assert.Null(service.CurrentWaiting);
        Assert.Null(service.CurrentError);
        Assert.Equal("streaming", service.CurrentItem!.Manifest!.HlsStatus);
    }

    private static ListenQueueItem Video(string title) => new()
    {
        WorkId = Guid.NewGuid(),
        AssetId = Guid.NewGuid(),
        MediaType = "Movie",
        Title = title,
        Year = "2021",
    };

    private sealed class ManifestHandler(PlaybackManifestDto manifest) : HttpMessageHandler
    {
        public int ManifestRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/manifest", StringComparison.Ordinal))
            {
                ManifestRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(manifest) });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new PlayerStateDto()) });
        }
    }
}
