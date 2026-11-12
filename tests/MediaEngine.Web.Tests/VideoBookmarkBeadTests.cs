using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Player;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

// Bookmark beads on the video timeline (video player follow-ups D4, D16): the hover card (frame, name, time and owner) and the default-on setting.
// The bead's own look is covered in PlaybackTimelineAndVolumeTests; the Bookmarks panel's frame address in BookmarksPanelTests.
public sealed class VideoBookmarkBeadCardTests : AsyncBunitContext
{
    public VideoBookmarkBeadCardTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./js/playback-seek-rail.js");
    }

    private IRenderedComponent<PlaybackSeekRail> RenderRail(PlaybackRailBookmark bookmark, string surface = "video")
    {
        var cut = Render<PlaybackSeekRail>(parameters => parameters
            .Add(component => component.Duration, 7200d)
            .Add(component => component.Surface, surface)
            .Add(component => component.Bookmarks, [bookmark]));
        cut.Find(".playback-seek-rail__bookmark-wrap").MouseEnter();
        return cut;
    }

    [Fact]
    public void TheHoverCard_ShowsTheFrameThenTheNameThenTheTime()
    {
        var id = Guid.NewGuid();
        var bookmark = new PlaybackRailBookmark(id, 4540, false, null, null, "The letter is opened", null, IsYours: true, InEveryone: true,
            FrameUrl: $"/api/v1/player/bookmarks/{id}/frame?v=abc");

        var cut = RenderRail(bookmark);

        var card = cut.Find(".playback-seek-rail__quicklook");
        var parts = card.Children.Select(child => child.ClassName ?? string.Empty).ToList();
        Assert.True(parts.FindIndex(part => part.Contains("playback-seek-rail__frame", StringComparison.Ordinal))
            < parts.FindIndex(part => part.Contains("playback-seek-rail__name", StringComparison.Ordinal)));
        Assert.True(parts.FindIndex(part => part.Contains("playback-seek-rail__name", StringComparison.Ordinal))
            < parts.FindIndex(part => part.Contains("playback-seek-rail__when", StringComparison.Ordinal)));
        Assert.Equal($"/engine-bookmark-frame/{id:D}?v=abc", card.QuerySelector(".playback-seek-rail__frame img")!.GetAttribute("src"));
        Assert.Equal("The letter is opened", card.QuerySelector(".playback-seek-rail__name")!.TextContent);
        Assert.Equal("1:15:40", card.QuerySelector(".playback-seek-rail__when")!.TextContent.Trim());
    }

    [Fact]
    public void ADefaultProfile_SeesOnlyItsOwnBeads_NeverSomeoneElses()
    {
        var mine = new PlaybackRailBookmark(Guid.NewGuid(), 60, false, "Mine", null, IsYours: true, InEveryone: true);
        var shared = new PlaybackRailBookmark(Guid.NewGuid(), 120, true, "Shared", null, OwnerName: "Alex", IsYours: false, InEveryone: true);

        Assert.Equal([mine], PlaybackRailBookmark.ForBar([mine, shared], PlaybackPreferenceValues.All));
        Assert.Equal([mine], PlaybackRailBookmark.ForBar([mine, shared], null));
        Assert.Equal([mine], PlaybackRailBookmark.ForBar([mine, shared], PlaybackPreferenceValues.Mine));
    }

    [Fact]
    public void TheHoverCard_NamesTheBookmarkByItsNote_WhenItHasNoLabel()
    {
        var bookmark = new PlaybackRailBookmark(Guid.NewGuid(), 60, false, "The reveal", null, IsYours: true);

        var cut = RenderRail(bookmark);

        Assert.Equal("The reveal", cut.Find(".playback-seek-rail__name").TextContent);
        Assert.DoesNotContain("The revealThe reveal", cut.Find(".playback-seek-rail__quicklook").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHoverCard_NamesTheOwnerOnlyForSomeoneElsesBookmark()
    {
        var someoneElses = new PlaybackRailBookmark(Guid.NewGuid(), 4540, true, "Shared", null, OwnerName: "Alex", IsYours: false, InEveryone: true);
        var mine = new PlaybackRailBookmark(Guid.NewGuid(), 4540, false, "Mine", null, OwnerName: "Me", IsYours: true, InEveryone: true);

        Assert.Equal("1:15:40 · Alex", RenderRail(someoneElses).Find(".playback-seek-rail__when").TextContent.Trim());
        Assert.Equal("1:15:40", RenderRail(mine).Find(".playback-seek-rail__when").TextContent.Trim());
    }

    [Fact]
    public void TheHoverCard_ShowsThePanelsNeutralPlaceholder_WhileTheFrameIsMissingOrCannotLoad()
    {
        var withoutFrame = new PlaybackRailBookmark(Guid.NewGuid(), 60, false, "No frame yet", null, IsYours: true);
        var missing = RenderRail(withoutFrame);
        Assert.Single(missing.FindAll(".playback-seek-rail__frame"));
        Assert.Empty(missing.FindAll(".playback-seek-rail__frame img"));

        var id = Guid.NewGuid();
        var broken = RenderRail(new PlaybackRailBookmark(id, 60, false, "Broken", null, IsYours: true, FrameUrl: $"/api/v1/player/bookmarks/{id}/frame?v=1"));
        broken.Find(".playback-seek-rail__frame img").TriggerEvent("onerror", new Microsoft.AspNetCore.Components.Web.ErrorEventArgs());
        Assert.Empty(broken.FindAll(".playback-seek-rail__frame img"));
        Assert.Single(broken.FindAll(".playback-seek-rail__frame"));

        var css = File.ReadAllText(Path.Combine(RepoRoot(), "src/MediaEngine.Web/Components/Shared/PlaybackSeekRail.razor.css"));
        Assert.Contains("background:var(--playback-state-pressed)", css, StringComparison.Ordinal); // the panel's row picture uses the same neutral fill
    }

    [Fact]
    public void TheHoverCard_OfAnAudioSurface_HasNoFrameBox()
    {
        var id = Guid.NewGuid();
        var bookmark = new PlaybackRailBookmark(id, 60, false, "Chapter note", null, IsYours: true, FrameUrl: $"/api/v1/player/bookmarks/{id}/frame?v=1");

        var cut = RenderRail(bookmark, surface: "dock");

        Assert.Empty(cut.FindAll(".playback-seek-rail__frame"));
        Assert.Empty(cut.FindAll(".playback-seek-rail__quicklook img"));
        Assert.Equal("1:00", cut.Find(".playback-seek-rail__when").TextContent.Trim());
    }

    [Fact]
    public void TheHoverCardFrame_IsSixteenByNineAboutAHundredSeventyEightPixelsWideWithFivePixelCorners()
    {
        var css = File.ReadAllText(Path.Combine(RepoRoot(), "src/MediaEngine.Web/Components/Shared/PlaybackSeekRail.razor.css"));

        Assert.Contains(".playback-seek-rail__quicklook--framed { box-sizing:border-box; width:190px;", css, StringComparison.Ordinal); // 190 less 2 x 6 px padding
        Assert.Contains("padding:6px;", css, StringComparison.Ordinal);
        Assert.Contains(".playback-seek-rail__frame { aspect-ratio:16 / 9;", css, StringComparison.Ordinal);
        Assert.Contains("border-radius:5px;", css, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("The repository root was not found.");
    }
}

public sealed class VideoBookmarkBeadHostTests : AsyncBunitContext
{
    private sealed class Preferences(UserPlaybackSettingsDto settings) : IUserPlaybackPreferencesAccessor
    {
        public Guid? ActiveProfileId { get; } = settings.ProfileId;
        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) => Task.FromResult<UserPlaybackSettingsDto?>(settings);
        public void UpdateCache(UserPlaybackSettingsDto next) { }
        public void Invalidate() { }
    }

    private void Arrange(Action<UserPlaybackSettingsDto> configure)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        var workId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var settings = UserPlaybackSettingsDto.CreateDefaults(Guid.NewGuid());
        configure(settings);
        var preferences = new Preferences(settings);
        var bookmark = new AudiobookBookmarkDto
        {
            Id = Guid.NewGuid(),
            ProfileId = settings.ProfileId,
            WorkId = workId,
            AssetId = assetId,
            PositionSeconds = 300,
            DurationSeconds = 600,
            Note = "The reveal",
            IsYours = true,
            InEveryone = true,
            CreatedAt = new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero),
        };
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.GetAudiobookBookmarksWithOutcomeAsync), _ =>
            Task.FromResult(AudiobookBookmarkOperationResult<IReadOnlyList<AudiobookBookmarkDto>>.Succeeded([bookmark]))));
        var playback = new PlaybackSessionController(null!, api, preferences: preferences);
        playback.RestoreState(new()
        {
            Queue = [new() { WorkId = workId, AssetId = assetId, Title = "Movie", MediaType = "Movie" }],
            CurrentIndex = 0,
            Experience = PlayerExperienceModes.Video,
            IsVideoExpanded = true,
            DurationSeconds = 600,
        });
        Services.AddSingleton(api);
        Services.AddSingleton(playback);
        Services.AddSingleton(new VideoPresentationResolver(api, null!, playback, preferences));
        Services.AddSingleton(new PlaybackTransientToolCoordinator(playback));
        Services.AddSingleton(new UniverseStateContainer());
        Services.AddSingleton<ShellActivityState>();
        Services.AddSingleton<IUserPlaybackPreferencesAccessor>(preferences);
        Render<AppPopoverHost>();
    }

    [Fact]
    public void ADefaultProfile_SeesBookmarkBeadsOnTheBar()
    {
        Arrange(_ => { });

        var cut = Render<VideoPlaybackHost>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("button.playback-seek-rail__bookmark")));
    }

    [Fact]
    public void AProfileThatTurnedTheBeadsOff_SeesNone()
    {
        Arrange(settings =>
        {
            settings.General.ShowBookmarksOnBar = false;
            settings.Subtitles.SubtitleSize = PlaybackPreferenceValues.Large; // Waiting for this proves the profile has loaded before the absence of beads is checked.
        });

        var cut = Render<VideoPlaybackHost>();

        cut.WaitForAssertion(() => Assert.Equal("Large", cut.Find("section.video-playback-host").GetAttribute("data-subtitle-size")));
        Assert.Empty(cut.FindAll("button.playback-seek-rail__bookmark"));
    }
}
