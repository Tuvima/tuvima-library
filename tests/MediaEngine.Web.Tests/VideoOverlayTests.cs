using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Player;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Resources;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace MediaEngine.Web.Tests;

/// <summary>Unified player v2 WP12 (D25, D25a; mockup screen 14; issue 707): the paused details, the rating card, the end card and the Picture-in-picture row.</summary>
public sealed class VideoOverlayTests : AsyncBunitContext
{
    private IStringLocalizer<SharedStrings> L => Services.GetRequiredService<IStringLocalizer<SharedStrings>>();

    public VideoOverlayTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
    }

    private static ListenQueueItem Episode() => new()
    {
        WorkId = Guid.NewGuid(),
        AssetId = Guid.NewGuid(),
        MediaType = "TV",
        Title = "Shimmer and Shine",
        Album = "Shimmer and Shine",
        SeasonNumber = "1",
        EpisodeNumber = "1",
        EpisodeTitle = "The First Wish",
    };

    private static ListenQueueItem Film() => new() { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), MediaType = "Movie", Title = "The Glass Harbor", Year = "2019" };

    private static ListenQueueItem FolderVideo() => new()
    {
        WorkId = Guid.NewGuid(),
        AssetId = Guid.NewGuid(),
        MediaType = "Videos",
        Title = "Ball handling basics",
        Album = "Basketball course",
        SeasonNumber = "1",
        EpisodeNumber = "3",
        EpisodeTitle = "Ball handling",
    };

    // ---- Paused details content per media (D25) ----

    [Fact]
    public void PausedDetails_ForAnEpisodeSayTheShowTheSeasonAndEpisodeTheNameTheLengthAndTheDescription()
    {
        var content = VideoPausedDetailsPresenter.Build(Episode(), "Shimmer and Shine", "Leah meets Shimmer and Shine.", 22 * 60 + 5, L);

        Assert.NotNull(content);
        Assert.Equal("Shimmer and Shine", content.Title);
        Assert.Equal("S1 E1 · The First Wish · 22 min", content.Line);
        Assert.Equal("Leah meets Shimmer and Shine.", content.Description);
    }

    [Fact]
    public void PausedDetails_ForAFilmSayTheYearAndTheLengthAndNothingElse()
    {
        var content = VideoPausedDetailsPresenter.Build(Film(), null, "Not used for a film.", 112 * 60, L);

        Assert.NotNull(content);
        Assert.Equal("The Glass Harbor", content.Title);
        Assert.Equal("2019 · 1h 52m", content.Line);
        Assert.Null(content.Description);
    }

    [Fact]
    public void PausedDetails_ForAFolderVideoNeverUseASeasonOrAnEpisodeCode()
    {
        var content = VideoPausedDetailsPresenter.Build(FolderVideo(), "Basketball course", null, 12 * 60, L);

        Assert.NotNull(content);
        Assert.Equal("Basketball course", content.Title);
        Assert.Equal("3 · Ball handling · 12 min", content.Line);
        Assert.DoesNotContain("S1", content.Line, StringComparison.Ordinal);
        Assert.False(MediaEngine.Domain.Services.VideoFolderWording.UsesTvWording(content.Line!));
    }

    [Fact]
    public void PausedDetails_WithoutAKnownLengthLeaveItOutInsteadOfShowingZero()
    {
        var content = VideoPausedDetailsPresenter.Build(Episode(), "Shimmer and Shine", null, 0, L);

        Assert.Equal("S1 E1 · The First Wish", content!.Line);
        Assert.Null(content.Description);
        Assert.Null(VideoPausedDetailsPresenter.Build(null, null, null, 0, L));
    }

    [Fact]
    public void PausedDetails_Component_ShowsTheContentWhenVisibleAndMarksItHiddenOtherwise()
    {
        var content = new VideoPausedDetailsContent("Shimmer and Shine", "S1 E1 · The First Wish · 22 min", "A short description.");

        var shown = Render<VideoPausedDetails>(p => p.Add(c => c.Content, content).Add(c => c.Visible, true));
        Assert.Equal("true", shown.Find("[data-paused-details]").GetAttribute("data-visible"));
        Assert.Equal("Shimmer and Shine", shown.Find(".video-paused-details__title").TextContent);
        Assert.Equal("S1 E1 · The First Wish · 22 min", shown.Find(".video-paused-details__line").TextContent);
        Assert.Equal("A short description.", shown.Find(".video-paused-details__description").TextContent);

        var hidden = Render<VideoPausedDetails>(p => p.Add(c => c.Content, content).Add(c => c.Visible, false));
        Assert.Equal("false", hidden.Find("[data-paused-details]").GetAttribute("data-visible"));
        Assert.Empty(Render<VideoPausedDetails>(p => p.Add(c => c.Visible, true)).FindAll("[data-paused-details]"));
    }

    // ---- Rating card (issue 707) ----

    [Theory]
    [InlineData("TV-MA", "Rated TV-MA")]
    [InlineData(" PG-13 ", "Rated PG-13")]
    public void RatingCard_SaysTheContentRating(string contentRating, string expected)
    {
        var cut = Render<VideoRatingCard>(p => p.Add(c => c.ContentRating, contentRating).Add(c => c.Playing, false));

        Assert.Equal(expected, cut.Find("[data-rating-card] strong").TextContent);
        Assert.Equal("true", cut.Find("[data-rating-card]").GetAttribute("data-paused"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RatingCard_IsHiddenWhenTheTitleHasNoContentRating(string? contentRating)
    {
        var cut = Render<VideoRatingCard>(p => p.Add(c => c.ContentRating, contentRating));

        Assert.Empty(cut.FindAll("[data-rating-card]"));
    }

    [Theory]
    [InlineData("15")]
    [InlineData("12A")]
    [InlineData("18")]
    public void ContentRating_KeepsWholeNumberCertificates(string certificate)
    {
        var film = new LibraryItemDetailViewModel { CanonicalValues = [new() { Key = "content_rating", Value = certificate }] };

        Assert.Equal(certificate, VideoContentRating.Of(film));
    }

    [Fact]
    public void ContentRating_ForAFilmAnEpisodeAndAnUnratedVideoIsNeverTheAudienceScore()
    {
        var film = new LibraryItemDetailViewModel
        {
            Rating = "7.80",
            CanonicalValues = [new() { Key = "rating", Value = "7.80" }, new() { Key = "content_rating", Value = "PG-13" }],
        };
        var episode = new LibraryItemDetailViewModel
        {
            Rating = "8.1",
            CanonicalValues = [new() { Key = "rating", Value = "8.1" }, new() { Key = "certification", Value = "TV-MA" }],
        };
        var unrated = new LibraryItemDetailViewModel { Rating = "6.21", CanonicalValues = [new() { Key = "rating", Value = "6.21" }] };
        var scoreFiledAsRating = new LibraryItemDetailViewModel { CanonicalValues = [new() { Key = "content_rating", Value = "6.21" }] };

        Assert.Equal("PG-13", VideoContentRating.Of(film));
        Assert.Equal("TV-MA", VideoContentRating.Of(episode));
        Assert.Null(VideoContentRating.Of(unrated));
        Assert.Null(VideoContentRating.Of(scoreFiledAsRating));
    }

    // ---- End card (D25a) ----

    private static VideoOwnedEpisode NextItem() => new(Guid.NewGuid(), "Lost in the Lamp", null, null, null, "s1", "Season 1", 1, 2);

    [Fact]
    public void EndCard_ForAnEpisodeSaysNextEpisodeAndForALessonSaysNextLesson()
    {
        var episode = Render<VideoEndCard>(p => p.Add(c => c.Next, NextItem()).Add(c => c.Lesson, false));
        var lesson = Render<VideoEndCard>(p => p.Add(c => c.Next, NextItem()).Add(c => c.Lesson, true));

        Assert.Equal("Lost in the Lamp", episode.Find("[data-end-card-title]").TextContent);
        Assert.Equal("Next episode", episode.Find("[data-end-card-kind]").TextContent);
        Assert.Equal("Next lesson", lesson.Find("[data-end-card-kind]").TextContent);
        Assert.DoesNotContain("episode", lesson.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Up next", episode.Find(".video-end-card__title").TextContent);
    }

    [Fact]
    public void EndCard_ShowsTheHomeworkBlockOnlyWhenItIsGivenFiles()
    {
        var without = Render<VideoEndCard>(p => p.Add(c => c.Next, NextItem()).Add(c => c.Lesson, true));
        var with = Render<VideoEndCard>(p => p.Add(c => c.Next, NextItem()).Add(c => c.Lesson, true)
            .Add(c => c.HomeworkFiles, [new MediaEngine.Contracts.Details.DetailExtraCardViewModel { Id = Guid.NewGuid().ToString("D"), Title = "Worksheet 3", RoleLabel = "Worksheet", FileBadge = "PDF" }]));

        Assert.Empty(without.FindAll("[data-end-card-homework]"));
        Assert.Contains("Homework for this lesson", with.Find("[data-end-card-homework]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Worksheet 3", with.Find("[data-end-card-homework]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void EndCard_ShowsTheCountdownOnlyWhileAutoplayIsOnAndOffersPlayNowAndCancel()
    {
        var plays = 0;
        var cancels = 0;
        var cut = Render<VideoEndCard>(p => p.Add(c => c.Next, NextItem()).Add(c => c.AutoAdvance, true).Add(c => c.RemainingSeconds, 7.2)
            .Add(c => c.PlayNow, EventCallback.Factory.Create(this, () => plays++))
            .Add(c => c.Cancel, EventCallback.Factory.Create(this, () => cancels++)));

        Assert.Equal("Starts in 8 seconds", cut.Find(".video-end-card__countdown").TextContent);
        cut.Find("[data-end-card-play]").Click();
        cut.Find("[data-end-card-cancel]").Click();
        Assert.Equal(1, plays);
        Assert.Equal(1, cancels);
        Assert.Equal("Play now", cut.Find("[data-end-card-play]").TextContent.Trim());
        Assert.Equal("Cancel", cut.Find("[data-end-card-cancel]").TextContent.Trim());

        var manual = Render<VideoEndCard>(p => p.Add(c => c.Next, NextItem()).Add(c => c.AutoAdvance, false));
        Assert.Empty(manual.FindAll(".video-end-card__countdown"));
    }

    // ---- The host: paused details hide while a panel is open, and the Picture-in-picture row ----

    private (IRenderedComponent<VideoPlaybackHost> Cut, PlaybackTransientToolCoordinator Tools, RecordingJsRuntime Js) RenderHost(ListenQueueItem item, bool canPictureInPicture, MediaEngine.Web.Services.Theming.DeviceContextService? device = null)
    {
        var api = EngineApiClientStub.Create(_ => { });
        var playback = new PlaybackSessionController(null!, api, preferences: new FixturePreferences());
        playback.RestoreState(new()
        {
            Queue = [item],
            CurrentIndex = 0,
            Experience = PlayerExperienceModes.Video,
            IsVideoExpanded = true,
            IsPlaying = false,
            DurationSeconds = 22 * 60 + 5,
        });
        var tools = new PlaybackTransientToolCoordinator(playback);
        var js = new RecordingJsRuntime { CanPictureInPicture = canPictureInPicture };
        Services.AddSingleton(api);
        Services.AddSingleton(playback);
        Services.AddSingleton(new VideoPresentationResolver(api, null!, playback));
        Services.AddSingleton(tools);
        Services.AddSingleton(new MediaEngine.Web.Services.Integration.UniverseStateContainer());
        Services.AddSingleton<ShellActivityState>();
        Services.AddSingleton<IUserPlaybackPreferencesAccessor>(new FixturePreferences());
        Services.AddSingleton<Microsoft.JSInterop.IJSRuntime>(js);
        if (device is not null)
        {
            Services.AddSingleton(device);
        }
        Render<AppPopoverHost>();
        return (Render<VideoPlaybackHost>(), tools, js);
    }

    private static MediaEngine.Web.Services.Theming.DeviceContextService NewDevice() => new(EngineApiClientStub.Create(_ => { }));

    // ---- One phone rule with the audio bar (PlaybackFormFactor) ----

    [Theory]
    [InlineData("phone", 390, true)]
    [InlineData("phone", 844, true)]
    [InlineData("desktop", 600, true)]
    [InlineData("desktop", 720, true)]
    [InlineData("desktop", 721, false)]
    [InlineData("tablet", 800, false)]
    [InlineData("desktop", 1280, false)]
    public async Task Host_DrawsThePhoneOverlayExactlyWhenTheAudioBarDrawsItsPhoneForm(string formFactor, int width, bool phone)
    {
        var device = NewDevice();
        await device.SwitchDeviceAsync(new MediaEngine.Web.Services.Theming.DeviceContextSnapshot(formFactor == "phone" ? "mobile" : "web", formFactor, formFactor != "desktop"));
        device.SetViewportWidth(width);

        var (cut, _, _) = RenderHost(Episode(), canPictureInPicture: false, device);

        Assert.Equal(phone, cut.FindAll("[data-video-phone-header]").Count == 1);
        Assert.Equal(phone, cut.FindAll(".playback-shell--phone").Count == 1);
    }

    [Fact]
    public async Task Host_SwitchesToThePhoneOverlayWhenTheFormFactorArrivesAfterItRenderedAndBackWhenItLeaves()
    {
        var device = NewDevice();
        var (cut, _, _) = RenderHost(Episode(), canPictureInPicture: false, device);
        Assert.Empty(cut.FindAll("[data-video-phone-header]"));

        await cut.InvokeAsync(() => device.SwitchDeviceAsync(new MediaEngine.Web.Services.Theming.DeviceContextSnapshot("mobile", "phone", true)));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-video-phone-header]")));

        await cut.InvokeAsync(() => device.SwitchDeviceAsync(new MediaEngine.Web.Services.Theming.DeviceContextSnapshot("web", "desktop", false)));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-video-phone-header]")));
    }

    [Fact]
    public async Task Host_PhoneHeaderHoldsTheItemMenuAndTheBarDoesNotRepeatTheTitle()
    {
        var device = NewDevice();
        await device.SwitchDeviceAsync(new MediaEngine.Web.Services.Theming.DeviceContextSnapshot("mobile", "phone", true));
        var (cut, _, _) = RenderHost(Episode(), canPictureInPicture: false, device);

        Assert.Single(cut.FindAll("[data-video-phone-header] [data-playback-identity-actions]"));
        Assert.Empty(cut.FindAll("[data-video-phone-header] [aria-label='Add to My List']"));
        Assert.Empty(cut.FindAll(".playback-shell--phone .playback-identity"));
        Assert.Contains("Shimmer and Shine", cut.Find(".video-playback-host__phone-title").TextContent);
    }

    [Fact]
    public void Host_ShowsThePausedDetailsOfAnEpisodeAndTheContentRatingCard()
    {
        var (cut, _, _) = RenderHost(Episode() with { ContentRating = "TV-MA" }, canPictureInPicture: false);

        Assert.Equal("true", cut.Find("[data-paused-details]").GetAttribute("data-visible"));
        Assert.Equal("S1 E1 \u00B7 The First Wish \u00B7 22 min", cut.Find(".video-paused-details__line").TextContent);
        Assert.Equal("Rated TV-MA", cut.Find("[data-rating-card] strong").TextContent);
    }

    [Fact]
    public async Task Host_HidesThePausedDetailsWhileAPanelIsOpenAndBringsThemBackWhenItCloses()
    {
        var (cut, tools, _) = RenderHost(Episode(), canPictureInPicture: false);
        Assert.Equal("true", cut.Find("[data-paused-details]").GetAttribute("data-visible"));

        await cut.InvokeAsync(() => tools.Open("video-captions"));
        cut.WaitForAssertion(() => Assert.Equal("false", cut.Find("[data-paused-details]").GetAttribute("data-visible")));

        await cut.InvokeAsync(() => tools.Close());
        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("[data-paused-details]").GetAttribute("data-visible")));
    }

    [Fact]
    public void Host_ShowsNoRatingCardForAnUnratedVideo()
    {
        var (cut, _, _) = RenderHost(Film(), canPictureInPicture: false);

        Assert.Empty(cut.FindAll("[data-rating-card]"));
        Assert.Equal("2019 \u00B7 22 min", cut.Find(".video-paused-details__line").TextContent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Host_OffersThePictureInPictureRowOnlyWhenTheBrowserSupportsIt(bool supported)
    {
        var (cut, _, _) = RenderHost(Episode(), supported);
        var settings = cut.FindComponent<PlaybackSettingsPanel>();
        await settings.InvokeAsync(() => settings.FindComponent<PlaybackPanel>().Instance.OpenAsync(true));

        cut.WaitForElement("[data-playback-settings]");
        Assert.Equal(supported, cut.FindAll("[data-psp-row='picture-in-picture']").Count == 1);
    }

    [Fact]
    public void Host_FullscreenTogglesTheContainerThatHoldsTheBarAndNotTheVideoElement()
    {
        var (cut, _, js) = RenderHost(Episode(), canPictureInPicture: false);

        var section = cut.Find("section.video-playback-host");
        Assert.NotEmpty(section.QuerySelectorAll(".playback-shell"));
        Assert.NotEmpty(section.QuerySelectorAll("video"));
        cut.Find("button[aria-label='Fullscreen']").Click();

        var call = Assert.Single(js.Calls, entry => entry.Identifier == "listenPlayback.toggleFullscreen");
        var target = Assert.IsType<ElementReference>(call.Args![0]);
        var video = Assert.IsType<ElementReference>(call.Args[1]);
        Assert.NotEqual(video.Id, target.Id);
    }

    private sealed class FixturePreferences : IUserPlaybackPreferencesAccessor
    {
        public Guid? ActiveProfileId { get; } = Guid.NewGuid();
        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) => Task.FromResult<UserPlaybackSettingsDto?>(null);
        public void UpdateCache(UserPlaybackSettingsDto settings) { }
        public void Invalidate() { }
    }

    private sealed class RecordingJsRuntime : Microsoft.JSInterop.IJSRuntime, Microsoft.JSInterop.IJSObjectReference
    {
        public bool CanPictureInPicture { get; set; }
        public List<(string Identifier, object?[]? Args)> Calls { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellation, object?[]? args)
        {
            Calls.Add((identifier, args));
            if (identifier == "import")
            {
                return ValueTask.FromResult((TValue)(object)this);
            }
            if (identifier == "listenPlayback.canPictureInPicture")
            {
                return ValueTask.FromResult((TValue)(object)CanPictureInPicture);
            }
            if (identifier == "readVideoState")
            {
                return ValueTask.FromResult(System.Text.Json.JsonSerializer.Deserialize<TValue>("{\"Position\":0,\"Duration\":180,\"Paused\":true,\"Muted\":false,\"Volume\":1,\"Speed\":1,\"TextTracks\":[],\"AudioTracks\":[]}")!);
            }
            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
