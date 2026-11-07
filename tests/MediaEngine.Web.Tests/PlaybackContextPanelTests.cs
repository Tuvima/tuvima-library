using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class PlaybackContextPanelTests : AsyncBunitContext
{
    public PlaybackContextPanelTests() { JSInterop.Mode = JSRuntimeMode.Loose; Services.AddLogging(); Services.AddLocalization(); Services.AddNativeUiServices(); }

    [Fact]
    public void DesktopIdentityKeepsTheCompleteLongTitleWhileArtworkCanYieldAtShortHeights()
    {
        const string title = "The Collected Voyages of the Northern Cartographer: Across Uncharted Seas, Through Forgotten Cities, and Beyond the Last Recorded Horizon";
        var snapshot = new ListenPlaybackSnapshot
        {
            Queue = [new() { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), Title = title,
            MediaType = "Audiobook", CoverUrl = "/api/v1/images/test?size=m", AuthorName = "Source author" }],
            CurrentIndex = 0
        };
        var cut = Render<PlaybackDesktopScene>(parameters => parameters.Add(p => p.Snapshot, snapshot).Add(p => p.Commands, new RecordingSink()).Add(p => p.PanelKey, "history"));
        Assert.Equal(title, cut.Find("h1").TextContent);
        Assert.Equal("/api/v1/images/test?size=m", cut.Find("img[data-image-display='desktop-now-playing-artwork']").GetAttribute("src"));
        Assert.Single(cut.FindAll(".playback-desktop__stack > .playback-desktop__art"));
        Assert.Single(cut.FindAll(".playback-desktop__stack > .playback-desktop__identity"));
        Assert.Empty(cut.FindAll("button[aria-label='Close player']"));
        Assert.Single(cut.FindAll("[role='tablist']"));
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }
        var css = File.ReadAllText(Path.Combine(directory!.FullName, "src/MediaEngine.Web/Components/Listen/PlaybackDesktopScene.razor.css"));
        Assert.DoesNotContain("line-clamp", css);
        Assert.Contains("overflow-wrap:anywhere", css);
    }

    [Fact]
    public void TheNowPlayingSceneDoesNotRepeatWhereTheQueueCameFromBesideTheCover()
    {
        var song = new ListenQueueItem { QueueEntryId = Guid.NewGuid(), WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), MediaType = "Music", Title = "Song" };
        var snapshot = new ListenPlaybackSnapshot { Queue = [song, song with { QueueEntryId = Guid.NewGuid() }], CurrentIndex = 0, SourceLabel = "Northern Lights" };

        var scene = Render<PlaybackDesktopScene>(p => p.Add(x => x.Snapshot, snapshot).Add(x => x.Commands, new RecordingSink()).Add(x => x.PanelKey, "queue"));
        var card = Render<PlaybackPanelCard>(p => p.Add(x => x.Snapshot, snapshot).Add(x => x.Commands, new RecordingSink()).Add(x => x.PanelKey, "queue"));

        Assert.NotEmpty(scene.FindAll("[data-queue-heading]"));
        Assert.Empty(scene.FindAll("[data-queue-source]"));
        Assert.Single(card.FindAll("[data-queue-source]"));
    }

    [Fact]
    public void ThePopOutPlayerLeavesOutTheChaptersTimelineChoiceThatLivesInTheMainWindow()
    {
        var asset = Guid.NewGuid();
        var chapters = Enumerable.Range(0, 3).Select(i => new PlaybackChapterDto { Index = i, AssetId = asset, Title = $"Chapter {i + 1}", StartSeconds = i * 60d, EndSeconds = (i + 1) * 60d }).ToList();
        var snapshot = new ListenPlaybackSnapshot
        {
            ProfileId = Guid.NewGuid(),
            Queue = [new() { WorkId = Guid.NewGuid(), AssetId = asset, MediaType = "Audiobook", Title = "Book", Chapters = chapters }],
            CurrentIndex = 0,
            Experience = PlayerExperienceModes.Audiobook,
            DurationSeconds = 180,
        };
        var playback = new PlaybackSessionController(null!, null!, preferences: new Preferences(snapshot.ProfileId!.Value));
        Services.AddSingleton(playback);
        Services.AddSingleton(provider => new PlaybackTimelineScope(provider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(), playback));

        var main = Render<PlaybackPanelCard>(p => p.Add(x => x.Snapshot, snapshot).Add(x => x.Commands, new RecordingSink()).Add(x => x.PanelKey, "chapters"));
        var popOut = Render<PlaybackPanelCard>(p => p.Add(x => x.Snapshot, snapshot).Add(x => x.Commands, new RecordingSink()).Add(x => x.PanelKey, "chapters").Add(x => x.ShowChaptersFooter, false));

        Assert.NotEmpty(main.FindAll("[data-chapters-footer]"));
        Assert.Empty(popOut.FindAll("[data-chapters-footer]"));
    }

    [Fact]
    public void RestoredBookSceneRendersTheBoundedCanonicalCoverForBothSharpArtAndAtmosphere()
    {
        var bookId = Guid.NewGuid();
        var snapshot = new ListenPlaybackSnapshot
        {
            Queue = [new() { WorkId = Guid.NewGuid(), AudiobookWorkId = bookId,
            AssetId = Guid.NewGuid(), Title = "Book", MediaType = "Audiobook",
            CoverUrl = "/engine-image/stream/22222222-2222-2222-2222-222222222222/cover" }],
            CurrentIndex = 0
        };
        var cut = Render<PlaybackDesktopScene>(parameters => parameters.Add(p => p.Snapshot, snapshot).Add(p => p.Commands, new RecordingSink()).Add(p => p.PanelKey, "history"));
        var bounded = $"/engine-image/stream/entity/work/{bookId:D}/cover?size=m";
        Assert.Equal(bounded, cut.Find("img[data-image-display='desktop-now-playing-artwork']").GetAttribute("src"));
        Assert.Contains(bounded, cut.FindComponent<AppCssElement>().Instance.Css ?? string.Empty);
        Assert.DoesNotContain(snapshot.Queue[0].CoverUrl!, cut.Markup);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MusicSceneLinksArtistAndAlbumWhileTitleRemainsPlain(bool hasArtistIdentity)
    {
        var album = Guid.NewGuid(); var artist = Guid.NewGuid();
        var item = new ListenQueueItem { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), MediaType = "Music", Title = "Track title", Subtitle = "Artist & Ensemble", Album = "Album title", AlbumWorkId = album, ArtistPersonId = hasArtistIdentity ? artist : null };
        var snapshot = new ListenPlaybackSnapshot { CurrentIndex = 0, Queue = [item] };
        var cut = Render<PlaybackDesktopScene>(p => p.Add(c => c.Snapshot, snapshot).Add(c => c.Commands, new RecordingSink()).Add(c => c.PanelKey, "queue"));
        Assert.Empty(cut.FindAll("h1 a"));
        if (hasArtistIdentity)
        {
            Assert.Equal($"/details/person/{artist:D}", cut.Find(".playback-desktop__byline a").GetAttribute("href"));
        }
        else
        {
            Assert.Contains("Artist &amp; Ensemble", cut.Markup);
        }
        Assert.Equal($"/details/musicalbum/{album:D}?context=listen", cut.Find(".playback-desktop__secondary a").GetAttribute("href"));
        var tabs = cut.FindAll("[role='tab']");
        Assert.Equal("Lyrics", tabs[0].GetAttribute("aria-label"));
        Assert.Equal(string.Empty, tabs[0].TextContent.Trim());
        Assert.Equal(new[] { "Up Next", "History" }, tabs.Skip(1).Select(e => e.TextContent.Trim()).ToArray());
    }

    private sealed class Preferences(Guid profile) : IUserPlaybackPreferencesAccessor
    {
        public Guid? ActiveProfileId => profile;
        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) => Task.FromResult<UserPlaybackSettingsDto?>(UserPlaybackSettingsDto.CreateDefaults(profile));
        public void UpdateCache(UserPlaybackSettingsDto settings) { }
        public void Invalidate() { }
    }

    private sealed class RecordingSink : IPlaybackCommandSink, IPlaybackIdentityNavigationSink
    {
        public ListenPlaybackCommandReplyDto? Reply { get; set; } = new();
        public Task<ListenPlaybackCommandReplyDto?> NavigateIdentityAsync(ListenPlaybackSnapshot snapshot, string kind, Guid id, CancellationToken ct = default) => Task.FromResult<ListenPlaybackCommandReplyDto?>(new());
        public List<(ListenPlaybackSnapshot Snapshot, ListenPlaybackCommandDto Command)> Commands { get; } = [];
        public bool Supports(string action, ListenPlaybackSnapshot snapshot) => true;
        public Task<ListenPlaybackCommandReplyDto?> SendAsync(ListenPlaybackSnapshot snapshot, ListenPlaybackCommandDto command, CancellationToken ct = default)
        { Commands.Add((snapshot, command)); return Task.FromResult(Reply); }
    }
}
