using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Configuration;
using MediaEngine.Web.Services.Playback;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The persistent bar of unified player v2 (D1 to D6, D27): one shell for music, audiobooks, TV and movies, drawn from
/// <see cref="PlaybackControlCatalog"/>. Acceptance criteria A1, A7, A13, A16 and A17.
/// </summary>
public sealed class PlaybackShellTests : AsyncBunitContext
{
    private static readonly string LongTitle = new('W', 60);
    private static readonly Uri BaseUri = new("http://localhost:5016/");

    public PlaybackShellTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
    }

    private static ListenQueueItem Item(string mediaType, string title) => new()
    {
        WorkId = Guid.NewGuid(),
        AssetId = Guid.NewGuid(),
        MediaType = mediaType,
        Title = title,
        Subtitle = "An Artist",
        Album = mediaType == "TV" ? title : "An Album",
        Year = "2026",
        EpisodeTitle = "The First Wish",
        SeasonNumber = "1",
        EpisodeNumber = "1",
        AuthorName = "Maya Ellis",
    };

    private IRenderedComponent<PlaybackShell> RenderShell(PlaybackBarMedia media, string title, PlaybackBarState? state = null)
    {
        var item = Item(media switch
        {
            PlaybackBarMedia.Music => "Music",
            PlaybackBarMedia.Audiobook => "Audiobook",
            PlaybackBarMedia.Tv => "TV",
            _ => "Movie",
        }, title);
        var identity = PlaybackBarIdentity.Build(media, item, chapterTitle: media == PlaybackBarMedia.Audiobook ? "Chapter 8" : null);
        return Render<PlaybackShell>(parameters => parameters
            .Add(shell => shell.Media, media)
            .Add(shell => shell.Identity, identity)
            .Add(shell => shell.State, state ?? new PlaybackBarState(CanPrevious: true, CanNext: true, HasChapters: true, HasEpisodes: media == PlaybackBarMedia.Tv, CanBookmark: true)));
    }

    private static IReadOnlyList<string> Labels(AngleSharp.Dom.IElement zone) =>
        zone.QuerySelectorAll("button").Select(button => button.GetAttribute("aria-label") ?? string.Empty).ToList();

    public static TheoryData<PlaybackBarMedia> AllMedia => new()
    {
        PlaybackBarMedia.Music, PlaybackBarMedia.Audiobook, PlaybackBarMedia.Tv, PlaybackBarMedia.Movie,
    };

    [Theory]
    [MemberData(nameof(AllMedia))]
    public void PlayIsInTheMiddleColumnOfAThreeColumnRow_WhateverTheTitleLength(PlaybackBarMedia media)
    {
        var cut = RenderShell(media, LongTitle);

        var row = cut.Find(".playback-shell__row");
        var zones = row.Children.Select(child => child.GetAttribute("data-playback-zone")).ToList();
        Assert.Equal(["identity", "transport", "tools"], zones);
        var transport = row.Children[1];
        Assert.Single(transport.QuerySelectorAll("button[aria-label='Play']"));
        Assert.Empty(row.Children[0].QuerySelectorAll("button[aria-label='Play']"));
        Assert.Empty(row.Children[2].QuerySelectorAll("button[aria-label='Play']"));

        // There is no layout engine here, so the grid itself is checked: equal outer columns keep Play at the exact centre.
        var css = RepoFile("src", "MediaEngine.Web", "Components", "Shared", "PlaybackShell.razor.css");
        Assert.Contains("grid-template-columns: minmax(0, 1fr) auto minmax(0, 1fr);", css, StringComparison.Ordinal);
        Assert.Contains("overflow: hidden;", css, StringComparison.Ordinal);
        Assert.Contains("text-overflow: ellipsis;", css, StringComparison.Ordinal);
        Assert.Contains("white-space: nowrap;", css, StringComparison.Ordinal);
        Assert.Contains("flex-wrap: nowrap;", css, StringComparison.Ordinal);
        Assert.Contains(".playback-shell__identity { min-width: 240px; }", css, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AllMedia))]
    public void ALongTitleNeverAddsOrRemovesAControl(PlaybackBarMedia media)
    {
        var shortTitle = RenderShell(media, "Short");
        var longTitle = RenderShell(media, LongTitle);

        Assert.Equal(Labels(shortTitle.Find("[data-playback-zone='transport']")), Labels(longTitle.Find("[data-playback-zone='transport']")));
        Assert.Equal(Labels(shortTitle.Find("[data-playback-zone='tools']")), Labels(longTitle.Find("[data-playback-zone='tools']")));
        Assert.Contains(LongTitle, longTitle.Find(".playback-shell__title").TextContent);
    }

    [Fact]
    public void PreviousAndNextThatCannotActAreDisabledInPlaceNeverLeftOut()
    {
        var cut = RenderShell(PlaybackBarMedia.Movie, "The Glass Harbor", new PlaybackBarState(CanPrevious: false, CanNext: false));

        var transport = cut.Find("[data-playback-zone='transport']");
        Assert.Equal(["Previous", "Skip back 10 seconds", "Play", "Skip forward 30 seconds", "Next"], Labels(transport));
        Assert.True(transport.QuerySelector("button[aria-label='Previous']")!.HasAttribute("disabled"));
        Assert.True(transport.QuerySelector("button[aria-label='Next']")!.HasAttribute("disabled"));
        Assert.False(transport.QuerySelector("button[aria-label='Play']")!.HasAttribute("disabled"));
    }

    [Fact]
    public void AnAudiobookTvAndMovieShareOneTransportInTheSamePlaces()
    {
        foreach (var media in new[] { PlaybackBarMedia.Audiobook, PlaybackBarMedia.Tv, PlaybackBarMedia.Movie })
        {
            var transport = PlaybackControlCatalog.BuildBarTransport(media, new PlaybackBarState());
            Assert.Equal(
                [PlaybackControlKey.PreviousItem, PlaybackControlKey.SkipBack, PlaybackControlKey.PlayPause, PlaybackControlKey.SkipForward, PlaybackControlKey.NextItem],
                transport.Select(control => control.Key).ToList());
            Assert.Equal(["10", "30"], transport.Where(control => control.ValueText is not null).Select(control => control.ValueText).ToList());
        }
    }

    [Fact]
    public void TheMusicTransportIsShufflePreviousPlayNextRepeatWithNoSkipBySecondsButtons()
    {
        var transport = PlaybackControlCatalog.BuildBarTransport(PlaybackBarMedia.Music, new PlaybackBarState());

        Assert.Equal(
            [PlaybackControlKey.Shuffle, PlaybackControlKey.PreviousItem, PlaybackControlKey.PlayPause, PlaybackControlKey.NextItem, PlaybackControlKey.Repeat],
            transport.Select(control => control.Key).ToList());
        Assert.DoesNotContain(transport, control => control.Key is PlaybackControlKey.SkipBack or PlaybackControlKey.SkipForward);
    }

    [Theory]
    [InlineData(PlayerRepeatModes.Off, "Repeat off", false, null)]
    [InlineData(PlayerRepeatModes.All, "Repeat all", true, null)]
    [InlineData(PlayerRepeatModes.One, "Repeat one", true, "1")]
    public void RepeatShowsOffAllAndOneWithTheSelectedStateAndTheOneBadge(string mode, string label, bool selected, string? badge)
    {
        var repeat = PlaybackControlCatalog.BuildBarTransport(PlaybackBarMedia.Music, new PlaybackBarState(RepeatMode: mode))
            .Single(control => control.Key == PlaybackControlKey.Repeat);

        Assert.Equal(label, repeat.AriaLabel);
        Assert.Equal(selected, repeat.IsSelected);
        Assert.Equal(badge, repeat.BadgeText);
    }

    [Fact]
    public void TheToolsFollowTheOrderOfD4ForEachMedia()
    {
        static IReadOnlyList<PlaybackControlKey> Tools(PlaybackBarMedia media, PlaybackBarState state) =>
            PlaybackControlCatalog.BuildBarTools(media, state).Select(control => control.Key).ToList();
        var full = new PlaybackBarState(HasChapters: true, HasEpisodes: true, CanBookmark: true);

        Assert.Equal(
            [PlaybackControlKey.Lyrics, PlaybackControlKey.Queue, PlaybackControlKey.Bookmarks, PlaybackControlKey.Volume, PlaybackControlKey.NowPlaying, PlaybackControlKey.Close],
            Tools(PlaybackBarMedia.Music, full));
        Assert.Equal(
            [PlaybackControlKey.Chapters, PlaybackControlKey.Speed, PlaybackControlKey.SleepTimer, PlaybackControlKey.Bookmarks, PlaybackControlKey.Volume, PlaybackControlKey.NowPlaying, PlaybackControlKey.Close],
            Tools(PlaybackBarMedia.Audiobook, full));
        Assert.Equal(
            [PlaybackControlKey.Episodes, PlaybackControlKey.AudioSubtitles, PlaybackControlKey.Bookmarks, PlaybackControlKey.Volume, PlaybackControlKey.Settings, PlaybackControlKey.Fullscreen, PlaybackControlKey.Close],
            Tools(PlaybackBarMedia.Tv, full));
        Assert.Equal(
            [PlaybackControlKey.AudioSubtitles, PlaybackControlKey.Bookmarks, PlaybackControlKey.Volume, PlaybackControlKey.Settings, PlaybackControlKey.Fullscreen, PlaybackControlKey.Close],
            Tools(PlaybackBarMedia.Movie, new PlaybackBarState(CanBookmark: true)));
    }

    [Fact]
    public void AToolThatIsNotSupportedLeavesNoButtonBehind()
    {
        var tools = PlaybackControlCatalog.BuildBarTools(PlaybackBarMedia.Audiobook, new PlaybackBarState(HasChapters: false, CanBookmark: false));

        Assert.DoesNotContain(tools, control => control.Key is PlaybackControlKey.Chapters or PlaybackControlKey.Bookmarks);
    }

    [Fact]
    public void TheShellDrawsNoBrowserTitleHints_EveryControlHasAnAccessibleNameAndTheAppTooltip()
    {
        foreach (var media in new[] { PlaybackBarMedia.Music, PlaybackBarMedia.Audiobook, PlaybackBarMedia.Tv, PlaybackBarMedia.Movie })
        {
            var cut = RenderShell(media, "Title");
            Assert.Empty(cut.FindAll("[title]"));
            Assert.All(cut.FindAll("button"), button => Assert.False(string.IsNullOrWhiteSpace(button.GetAttribute("aria-label"))));
        }

        Assert.DoesNotContain("title=", LocalizedSource.ReadAllText(RepoPath("src", "MediaEngine.Web", "Components", "Shared", "PlaybackShell.razor")), StringComparison.Ordinal);
    }

    [Fact]
    public void TheShellOwnsNoMediaElement()
    {
        var cut = RenderShell(PlaybackBarMedia.Movie, "Title");

        Assert.Empty(cut.FindAll("audio, video"));
    }

    // ---- The music Previous rule (D3) ----

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void TheRestartThresholdComesFromThePlaybackClientConfigFile()
    {
        var configDirectory = RepoPath("config");

        var settings = new DashboardConfigurationReader(configDirectory).LoadPlaybackClientSettings();

        Assert.Equal(3d, settings.MusicPreviousRestartSeconds);
        Assert.Equal(10, settings.DefaultSkipBackSeconds);
        Assert.Equal(30, settings.DefaultSkipForwardSeconds);
    }

    [Fact]
    public void PreviousRestartsTheSongAfterTheThresholdAndGoesBackInsideItOrOnASecondPress()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var rule = new MusicPreviousRule(3d, clock);
        var song = Guid.NewGuid();

        // Deep into the song: restart. An immediate second press goes back one song.
        Assert.Equal(PlaybackPreviousAction.Restart, rule.Press(song, 42, hasPreviousSong: true));
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.Equal(PlaybackPreviousAction.GoToPrevious, rule.Press(song, 0, hasPreviousSong: true));

        // Inside the first three seconds: go back.
        Assert.Equal(PlaybackPreviousAction.GoToPrevious, rule.Press(Guid.NewGuid(), 2.9, hasPreviousSong: true));
        // Exactly at the threshold counts as past it.
        Assert.Equal(PlaybackPreviousAction.Restart, rule.Press(Guid.NewGuid(), 3, hasPreviousSong: true));
    }

    [Fact]
    public void ASecondPressAfterTheThresholdHasPassedRestartsAgainInsteadOfGoingBack()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var rule = new MusicPreviousRule(3d, clock);
        var song = Guid.NewGuid();

        Assert.Equal(PlaybackPreviousAction.Restart, rule.Press(song, 30, hasPreviousSong: true));
        clock.Now += TimeSpan.FromSeconds(10);

        Assert.Equal(PlaybackPreviousAction.Restart, rule.Press(song, 30, hasPreviousSong: true));
    }

    [Fact]
    public void AtTheStartOfTheQueuePreviousAlwaysRestarts()
    {
        var rule = new MusicPreviousRule(3d, new ManualClock(DateTimeOffset.UnixEpoch));

        Assert.Equal(PlaybackPreviousAction.Restart, rule.Press(Guid.NewGuid(), 0.5, hasPreviousSong: false));
        Assert.Equal(PlaybackPreviousAction.Restart, rule.Press(Guid.NewGuid(), 60, hasPreviousSong: false));
    }

    [Fact]
    public void ARestartOfOneSongNeverCountsForAnother()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var rule = new MusicPreviousRule(3d, clock);

        Assert.Equal(PlaybackPreviousAction.Restart, rule.Press(Guid.NewGuid(), 50, hasPreviousSong: true));
        Assert.Equal(PlaybackPreviousAction.Restart, rule.Press(Guid.NewGuid(), 50, hasPreviousSong: true));
    }

    [Fact]
    public void TheSessionControllerSharesOneRuleAcrossTheBarTheLockScreenAndHeadsets()
    {
        var playback = new PlaybackSessionController(null!, null!);
        playback.RestoreState(new ListenPlaybackSnapshot
        {
            Queue = [Item("Music", "One"), Item("Music", "Two")],
            CurrentIndex = 1,
            CurrentTimeSeconds = 80,
        });

        Assert.Equal(PlaybackPreviousAction.Restart, playback.PressMusicPrevious());
        Assert.Equal(PlaybackPreviousAction.GoToPrevious, playback.PressMusicPrevious());
    }

    // ---- The lock screen and system controls (D27, A17) ----

    private static PlaybackSessionContext Context(PlaybackBarMedia media, ListenQueueItem item, bool hasNext = false, bool vaultLocked = false, bool hideEpisodeTitle = false) =>
        new(media, item, BaseUri, ShowTitle: "Shimmer and Shine", ChapterTitle: "Chapter 8", SkipBackSeconds: 10, SkipForwardSeconds: 30,
            HasNextEpisode: hasNext, VaultLocked: vaultLocked, HideEpisodeTitle: hideEpisodeTitle);

    [Fact]
    public void MusicMetadataIsSongThenArtistAndAlbumWithPlayPreviousNextAndSeekOnly()
    {
        var metadata = PlaybackSessionMetadata.Create(Context(PlaybackBarMedia.Music, Item("Music", "Stay a Little Longer")))!;

        Assert.Equal("Stay a Little Longer", metadata.Title);
        Assert.Equal("An Artist · An Album", metadata.Artist);
        Assert.Equal(["play", "pause", "previoustrack", "nexttrack", "seekto"], metadata.Actions);
        Assert.Equal(["512x512", "1024x1024"], metadata.Artwork.Select(artwork => artwork.Sizes));
    }

    [Fact]
    public void AudiobookMetadataIsBookThenAuthorAndChapterWithTheProfilesSkipSeconds()
    {
        var item = Item("Audiobook", "The Way Beyond");
        var metadata = PlaybackSessionMetadata.Create(Context(PlaybackBarMedia.Audiobook, item) with { SkipBackSeconds = 15, SkipForwardSeconds = 45 })!;

        Assert.Equal("The Way Beyond", metadata.Title);
        Assert.Equal("Maya Ellis · Chapter 8", metadata.Artist);
        Assert.Equal(["play", "pause", "seekbackward", "seekforward", "previoustrack", "nexttrack", "seekto"], metadata.Actions);
        Assert.Equal(15, metadata.SkipBackSeconds);
        Assert.Equal(45, metadata.SkipForwardSeconds);
    }

    [Fact]
    public void TvMetadataIsEpisodeThenShowAndNumberAndOffersNextOnlyWhenAnEpisodeFollows()
    {
        var item = Item("TV", "Shimmer and Shine");

        var withNext = PlaybackSessionMetadata.Create(Context(PlaybackBarMedia.Tv, item, hasNext: true))!;
        var last = PlaybackSessionMetadata.Create(Context(PlaybackBarMedia.Tv, item, hasNext: false))!;

        Assert.Equal("The First Wish", withNext.Title);
        Assert.Equal("Shimmer and Shine · S1 E1", withNext.Artist);
        Assert.Contains("nexttrack", withNext.Actions);
        Assert.DoesNotContain("nexttrack", last.Actions);
        Assert.DoesNotContain("previoustrack", last.Actions);
        Assert.Contains("seekbackward", last.Actions);
    }

    [Fact]
    public void TvMetadataLeavesTheEpisodeTitleOutWhenSpoilersHidesIt()
    {
        var metadata = PlaybackSessionMetadata.Create(Context(PlaybackBarMedia.Tv, Item("TV", "Shimmer and Shine"), hideEpisodeTitle: true))!;

        Assert.DoesNotContain("The First Wish", $"{metadata.Title}{metadata.Artist}{metadata.Album}", StringComparison.Ordinal);
        Assert.Equal("Shimmer and Shine", metadata.Title);
    }

    [Fact]
    public void MovieMetadataIsTitleThenYearAndTheWordMovie()
    {
        var metadata = PlaybackSessionMetadata.Create(Context(PlaybackBarMedia.Movie, Item("Movie", "The Glass Harbor")))!;

        Assert.Equal("The Glass Harbor", metadata.Title);
        Assert.Equal("2026 · Movie", metadata.Artist);
        Assert.Equal(["play", "pause", "seekbackward", "seekforward", "seekto"], metadata.Actions);
    }

    [Fact]
    public void AVaultItemSendsNoMetadataWhileVaultIsLockedAndOnlyTheGenericWordWhileItIsOpen()
    {
        var vault = Item("Movie", "A Very Private Title") with { Manifest = new PlaybackManifestDto { IsVault = true } };

        Assert.Null(PlaybackSessionMetadata.Create(Context(PlaybackBarMedia.Movie, vault, vaultLocked: true)));
        var open = PlaybackSessionMetadata.Create(Context(PlaybackBarMedia.Movie, vault, vaultLocked: false))!;
        Assert.Equal(VaultPrivacy.GenericTitle, open.Title);
        Assert.Empty(open.Artwork);
        Assert.DoesNotContain("Private", $"{open.Title}{open.Artist}{open.Album}", StringComparison.Ordinal);
    }

    // ---- One <audio>, one <video>, and no bar markup in the big components (D1) ----

    [Fact]
    public void TheSessionHasExactlyOneAudioAndOneVideoElementAndNeitherLivesInsideATogglingBranch()
    {
        var listen = LocalizedSource.ReadAllText(RepoPath("src", "MediaEngine.Web", "Components", "Listen", "ListenNowPlayingBar.razor"));
        var video = LocalizedSource.ReadAllText(RepoPath("src", "MediaEngine.Web", "Components", "Watch", "VideoPlaybackHost.razor"));

        Assert.Single(Regex.Matches(listen, @"<audio\b"));
        Assert.Single(Regex.Matches(video, @"<video\b"));
        Assert.Empty(Regex.Matches(listen, @"<video\b"));
        Assert.Empty(Regex.Matches(video, @"<audio\b"));
        // The element is declared before the first branch that shows or hides the bar, so switching presentation never recreates it.
        Assert.True(listen.IndexOf("<audio", StringComparison.Ordinal) < listen.IndexOf("@if (Playback.HasQueue", StringComparison.Ordinal));
        Assert.True(video.IndexOf("<video", StringComparison.Ordinal) < video.IndexOf("@if (Playback.IsVideoMode && Playback.IsVideoExpanded", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Listen", "ListenNowPlayingBar.razor")]
    [InlineData("Watch", "VideoPlaybackHost.razor")]
    public void TheBigComponentsKeepTheirWiringButNoBarMarkup(string folder, string file)
    {
        var source = LocalizedSource.ReadAllText(RepoPath("src", "MediaEngine.Web", "Components", folder, file));

        Assert.Contains("<PlaybackShell", source, StringComparison.Ordinal);
        foreach (var barMarkup in new[]
                 {
                     "<PlaybackPrimaryButton", "<PlaybackRelativeSkipButton", "<PlaybackControlStrip", "<ListenTransportControls", "<PlaybackMiniPlayer",
                     "playback-video-controls", "listen-player__transport", "listen-player__actions", "listen-player__meta", " title=\"",
                 })
        {
            Assert.DoesNotContain(barMarkup, source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void StartingAnAudiobookFromTheDetailsWindowClosesTheWindowSoItCannotCoverTheBar()
    {
        var source = LocalizedSource.ReadAllText(RepoPath("src", "MediaEngine.Web", "Components", "Details", "DetailPage.razor"));
        var start = source.IndexOf("await Playback.StartAudiobookAsync(", StringComparison.Ordinal);

        Assert.True(start > 0);
        var after = source.Substring(start, Math.Min(900, source.Length - start));
        Assert.Contains("overlay.DismissAsync()", after, StringComparison.Ordinal);
    }

    private static string RepoPath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(new[] { directory?.FullName ?? throw new DirectoryNotFoundException("The repository root was not found.") }.Concat(parts).ToArray());
    }

    private static string RepoFile(params string[] parts) => LocalizedSource.ReadAllText(RepoPath(parts));
}
