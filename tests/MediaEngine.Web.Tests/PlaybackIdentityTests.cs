using Bunit;
using MediaEngine.Contracts.ProfileState;
using MediaEngine.Domain.Enums;
using MediaEngine.Web.Components.Player;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The identity zone of the persistent bar (unified player v2 D5, D23; acceptance A2 and A3): the fixed artwork slot and its placeholder, the
/// title and second line, Favorite, Like, Dislike and More beside a song, and the item actions menu.
/// </summary>
public sealed class PlaybackIdentityTests : AsyncBunitContext
{
    private static readonly Guid Profile = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ShowId = Guid.NewGuid();
    private static readonly Guid SeasonId = Guid.NewGuid();
    private readonly PlaybackTransientToolCoordinator _tools = new();
    private readonly Dictionary<(ProfileEntityKind, Guid), ProfileReactionKind> _stored = [];
    private readonly HashSet<(ProfileEntityKind, Guid)> _saved = [];

    public PlaybackIdentityTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(_tools);
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetProfileReactionAsync), args => Task.FromResult<ProfileReactionDto?>(
                _stored.TryGetValue(((ProfileEntityKind)args![0]!, (Guid)args[1]!), out var kind)
                    ? new ProfileReactionDto((ProfileEntityKind)args[0]!, (Guid)args[1]!, kind, DateTimeOffset.UtcNow) : null));
            stub.SetHandler(nameof(IEngineApiClient.SetProfileReactionAsync), args =>
            {
                _stored[((ProfileEntityKind)args![0]!, (Guid)args[1]!)] = (ProfileReactionKind)args[2]!;
                return Task.FromResult<ProfileStateMutationDto?>(null);
            });
            stub.SetHandler(nameof(IEngineApiClient.RemoveProfileReactionAsync), args =>
            {
                _stored.Remove(((ProfileEntityKind)args![0]!, (Guid)args[1]!));
                return Task.FromResult(true);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetSavedItemsAsync), _ => Task.FromResult<IReadOnlyList<ProfileSavedItemDto>>(
                [.. _saved.Select(key => new ProfileSavedItemDto(key.Item1, key.Item2, DateTimeOffset.UtcNow, null))]));
            stub.SetHandler(nameof(IEngineApiClient.GetSavedItemAsync), args => Task.FromResult(
                _saved.Contains(((ProfileEntityKind)args![0]!, (Guid)args[1]!))
                    ? new ProfileSavedItemDto((ProfileEntityKind)args[0]!, (Guid)args[1]!, DateTimeOffset.UtcNow, null) : null));
            stub.SetHandler(nameof(IEngineApiClient.SaveItemAsync), args =>
            {
                _saved.Add(((ProfileEntityKind)args![0]!, (Guid)args[1]!));
                return Task.FromResult<ProfileStateMutationDto?>(new((ProfileEntityKind)args[0]!, (Guid)args[1]!, true, null, DateTimeOffset.UtcNow));
            });
            stub.SetHandler(nameof(IEngineApiClient.RemoveSavedItemAsync), args =>
                Task.FromResult(_saved.Remove(((ProfileEntityKind)args![0]!, (Guid)args[1]!))));
        }));
        Services.AddScoped<MediaReactionService>();
        Services.AddScoped<SavedItemService>();
        Render<AppPopoverHost>();
    }

    private static ListenQueueItem Item(PlaybackBarMedia media) => new()
    {
        WorkId = Guid.NewGuid(),
        AssetId = Guid.NewGuid(),
        AlbumWorkId = media == PlaybackBarMedia.Music ? Guid.NewGuid() : null,
        ArtistPersonId = media == PlaybackBarMedia.Music ? Guid.NewGuid() : null,
        AudiobookWorkId = media == PlaybackBarMedia.Audiobook ? Guid.NewGuid() : null,
        Authors = media == PlaybackBarMedia.Audiobook ? [new PlaybackContributorIdentity("Maya Ellis", Guid.NewGuid())] : [],
        MediaType = media switch
        {
            PlaybackBarMedia.Music => "Music",
            PlaybackBarMedia.Audiobook => "Audiobook",
            PlaybackBarMedia.Tv => "TV",
            _ => "Movie",
        },
        Title = "Stay a Little Longer",
        Subtitle = "The Midnight Coast",
        Album = "Late Hours",
        Year = "2026",
        SeasonNumber = "1",
        EpisodeNumber = "1",
        EpisodeTitle = "The First Wish",
    };

    public static TheoryData<PlaybackBarMedia> AllMedia => new()
    {
        PlaybackBarMedia.Music, PlaybackBarMedia.Audiobook, PlaybackBarMedia.Tv, PlaybackBarMedia.Movie,
    };

    private IRenderedComponent<PlaybackShell> RenderShell(PlaybackBarMedia media, string? artworkSrc = null, bool poster = false, ListenQueueItem? item = null,
        Action<ComponentParameterCollectionBuilder<PlaybackShell>>? configure = null)
    {
        item ??= Item(media);
        return Render<PlaybackShell>(parameters =>
        {
            parameters.Add(shell => shell.Media, media)
                .Add(shell => shell.Identity, PlaybackBarIdentity.Build(media, item))
                .Add(shell => shell.State, new PlaybackBarState(CanPrevious: true, CanNext: true))
                .Add(shell => shell.ArtworkSrc, artworkSrc)
                .Add(shell => shell.ArtworkIsPoster, poster)
                .Add(shell => shell.IdentityActions, (RenderFragment)(builder =>
                {
                    builder.OpenComponent<PlaybackIdentityActions>(0);
                    builder.AddAttribute(1, nameof(PlaybackIdentityActions.Media), media);
                    builder.AddAttribute(2, nameof(PlaybackIdentityActions.Item), item);
                    builder.AddAttribute(3, nameof(PlaybackIdentityActions.Snapshot), new ListenPlaybackSnapshot { ProfileId = Profile });
                    builder.AddAttribute(4, nameof(PlaybackIdentityActions.ShowWorkId), media == PlaybackBarMedia.Tv ? ShowId : null);
                    builder.AddAttribute(5, nameof(PlaybackIdentityActions.SeasonId), media == PlaybackBarMedia.Tv ? SeasonId : null);
                    builder.CloseComponent();
                }));
            configure?.Invoke(parameters);
        });
    }

    private static IReadOnlyList<string> ButtonLabels(AngleSharp.Dom.IElement zone) =>
        zone.QuerySelectorAll("button").Select(button => button.GetAttribute("aria-label") ?? string.Empty).ToList();

    // ---- A2: the artwork slot -----------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllMedia))]
    public void AMissingImageIsANeutralPlaceholderInTheSameSlotAsTheImage(PlaybackBarMedia media)
    {
        var withImage = RenderShell(media, "/engine-image/stream/entity/work/1/cover?size=s");
        var without = RenderShell(media);

        var imageSlot = withImage.Find("[data-playback-artwork]");
        var placeholderSlot = without.Find("[data-playback-artwork]");
        Assert.Equal(imageSlot.ClassName, placeholderSlot.ClassName);
        Assert.Equal("placeholder", placeholderSlot.GetAttribute("data-playback-artwork"));
        Assert.NotNull(placeholderSlot.QuerySelector(".playback-identity__art-placeholder"));
        Assert.Null(placeholderSlot.QuerySelector("img"));
        Assert.NotNull(imageSlot.QuerySelector("img.playback-identity__art-image"));
    }

    [Fact]
    public void AnImageThatFailsToLoadFallsBackToThePlaceholderWithoutMovingTheText()
    {
        var cut = RenderShell(PlaybackBarMedia.Music, "/broken.png");
        var before = cut.Find(".playback-identity__title").TextContent;

        cut.Find("img.playback-identity__art-image").TriggerEvent("onerror", new Microsoft.AspNetCore.Components.Web.ErrorEventArgs());

        Assert.Equal("placeholder", cut.Find("[data-playback-artwork]").GetAttribute("data-playback-artwork"));
        Assert.Null(cut.Find("[data-playback-artwork]").QuerySelector("img"));
        Assert.Equal(before, cut.Find(".playback-identity__title").TextContent);
    }

    [Theory]
    [InlineData(PlaybackBarMedia.Music, "playback-identity__art--square")]
    [InlineData(PlaybackBarMedia.Audiobook, "playback-identity__art--square")]
    [InlineData(PlaybackBarMedia.Tv, "playback-identity__art--wide")]
    [InlineData(PlaybackBarMedia.Movie, "playback-identity__art--poster")]
    public void AudioHasASquareSlotAnEpisodeALandscapeSlotAndAMovieAPortraitSlot(PlaybackBarMedia media, string expectedClass)
    {
        var slot = RenderShell(media, "/cover.png").Find("[data-playback-artwork]");

        Assert.Contains(expectedClass, slot.ClassList);
        var css = File.ReadAllText(RepoPath("src", "MediaEngine.Web", "Components", "Player", "PlaybackIdentity.razor.css")).Replace("\r\n", "\n");
        Assert.Contains("height: 64px;", css, StringComparison.Ordinal);
        Assert.Contains("width: 64px;", css, StringComparison.Ordinal);
        Assert.Contains(".playback-identity__art--wide {\n    width: 114px;", css, StringComparison.Ordinal);
        Assert.Contains(".playback-identity__art--poster {\n    width: 43px;", css, StringComparison.Ordinal);
    }

    [Fact]
    public void AMovieSlotIsPortraitWithNoLandscapeBoxAndNoBlurredFillImage()
    {
        var slot = RenderShell(PlaybackBarMedia.Movie, "/poster.png", poster: true).Find("[data-playback-artwork]");

        Assert.Equal("poster", slot.GetAttribute("data-playback-artwork"));
        Assert.Contains("playback-identity__art--poster", slot.ClassList);
        Assert.DoesNotContain("playback-identity__art--wide", slot.ClassList);
        Assert.Null(slot.QuerySelector("img.playback-identity__art-fill"));
        Assert.Single(slot.QuerySelectorAll("img"));
        Assert.Equal("/poster.png", slot.QuerySelector("img.playback-identity__art-image")!.GetAttribute("src"));
        var css = File.ReadAllText(RepoPath("src", "MediaEngine.Web", "Components", "Player", "PlaybackIdentity.razor.css")).Replace("\r\n", "\n");
        Assert.Contains(".playback-identity__art--poster .playback-identity__art-image {\n    object-fit: cover;", css, StringComparison.Ordinal);
        Assert.DoesNotContain("art-fill", css, StringComparison.Ordinal);
        Assert.DoesNotContain("blur(", css, StringComparison.Ordinal);
    }

    [Fact]
    public void AMoviePlaceholderHasTheSamePortraitSlotAsItsPoster()
    {
        var slot = RenderShell(PlaybackBarMedia.Movie).Find("[data-playback-artwork]");

        Assert.Equal("placeholder", slot.GetAttribute("data-playback-artwork"));
        Assert.Contains("playback-identity__art--poster", slot.ClassList);
        Assert.DoesNotContain("playback-identity__art--wide", slot.ClassList);
    }

    [Fact]
    public void AnEpisodeWithOnlyAPosterIsAPortraitSlotNotALandscapeBox()
    {
        var slot = RenderShell(PlaybackBarMedia.Tv, "/poster.png", poster: true).Find("[data-playback-artwork]");

        Assert.Equal("poster", slot.GetAttribute("data-playback-artwork"));
        Assert.Contains("playback-identity__art--poster", slot.ClassList);
        Assert.DoesNotContain("playback-identity__art--wide", slot.ClassList);
        Assert.Null(slot.QuerySelector("img.playback-identity__art-fill"));
    }

    [Fact]
    public void AnEpisodeWithAStillIsThe114PixelLandscapeSlot()
    {
        var slot = RenderShell(PlaybackBarMedia.Tv, "/still.png", poster: false).Find("[data-playback-artwork]");

        Assert.Contains("playback-identity__art--wide", slot.ClassList);
        Assert.DoesNotContain("playback-identity__art--poster", slot.ClassList);
        Assert.Equal(64, PlaybackArtworkUrl.SlotHeightPx);
        Assert.Equal(114, PlaybackArtworkUrl.LandscapeSlotWidthPx);
        Assert.Equal(43, PlaybackArtworkUrl.PortraitSlotWidthPx);
    }

    [Fact]
    public void AnEpisodeStillFillsTheLandscapeSlotWithNoBlurredFill()
    {
        var slot = RenderShell(PlaybackBarMedia.Tv, "/still.png", poster: false).Find("[data-playback-artwork]");

        Assert.Equal("image", slot.GetAttribute("data-playback-artwork"));
        Assert.Null(slot.QuerySelector("img.playback-identity__art-fill"));
    }

    [Fact]
    public void ThePosterFlagMeansNothingInASquareSlot()
    {
        var slot = RenderShell(PlaybackBarMedia.Music, "/cover.png", poster: true).Find("[data-playback-artwork]");

        Assert.Equal("image", slot.GetAttribute("data-playback-artwork"));
        Assert.Null(slot.QuerySelector("img.playback-identity__art-fill"));
    }

    [Fact]
    public void TheArtworkBecomesAButtonThatOpensNowPlayingWhenTheHostListens()
    {
        var opened = 0;
        var cut = RenderShell(PlaybackBarMedia.Music, "/cover.png", configure: p => p.Add(shell => shell.ArtworkClick, EventCallback.Factory.Create(this, () => opened++)));

        cut.Find("button.playback-identity__art").Click();

        Assert.Equal(1, opened);
    }

    [Fact]
    public void AudioSlotsUseTheCoverAndVideoSlotsPreferTheEpisodeStillThenThePosterThenNothing()
    {
        var music = Item(PlaybackBarMedia.Music) with { CoverUrl = "/engine-image/stream/entity/work/2a8e5e9a-0000-4000-8000-000000000001/cover?size=m" };
        Assert.False(PlaybackArtworkUrl.ForBarSlot(PlaybackBarMedia.Music, music).IsPoster);
        Assert.NotNull(PlaybackArtworkUrl.ForBarSlot(PlaybackBarMedia.Music, music).Src);

        var tv = Item(PlaybackBarMedia.Tv) with { CoverUrl = "/engine-image/stream/entity/work/2a8e5e9a-0000-4000-8000-000000000002/cover?size=m" };
        var still = PlaybackArtworkUrl.ForBarSlot(PlaybackBarMedia.Tv, tv, "/stream/artwork/2a8e5e9a-0000-4000-8000-000000000003");
        Assert.False(still.IsPoster);
        Assert.Contains("2a8e5e9a-0000-4000-8000-000000000003", still.Src);

        var poster = PlaybackArtworkUrl.ForBarSlot(PlaybackBarMedia.Movie, tv);
        Assert.True(poster.IsPoster);
        Assert.NotNull(poster.Src);

        var movieWithStill = PlaybackArtworkUrl.ForBarSlot(PlaybackBarMedia.Movie, tv, "/stream/artwork/2a8e5e9a-0000-4000-8000-000000000003");
        Assert.True(movieWithStill.IsPoster);

        var none = PlaybackArtworkUrl.ForBarSlot(PlaybackBarMedia.Movie, tv with { CoverUrl = null, WorkId = Guid.Empty });
        Assert.Equal(PlaybackSlotArtwork.None, none);
        Assert.Equal(PlaybackSlotArtwork.None, PlaybackArtworkUrl.ForBarSlot(PlaybackBarMedia.Tv, null));
    }

    // ---- The text -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheTitleAndSecondLineHaveNoViewDetailsButton()
    {
        foreach (var media in new[] { PlaybackBarMedia.Music, PlaybackBarMedia.Audiobook, PlaybackBarMedia.Tv, PlaybackBarMedia.Movie })
        {
            var zone = RenderShell(media).Find("[data-playback-zone='identity']");
            Assert.DoesNotContain(ButtonLabels(zone), label => label.Contains("details", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("View details", zone.TextContent, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TvAndMovieTitlesLinkToTheirPagesOnceTheShowIsKnown()
    {
        var episode = Item(PlaybackBarMedia.Tv);
        var tv = PlaybackBarIdentity.Build(PlaybackBarMedia.Tv, episode, "Shimmer and Shine", showWorkId: ShowId);
        Assert.Equal("show", tv.TitleLinkKind);
        Assert.Equal(ShowId, tv.TitleLinkId);
        Assert.Equal("S1 E1 · The First Wish", tv.SecondaryText);
        Assert.Equal("episode", tv.Secondary[1].LinkKind);
        Assert.Equal($"/details/tvshow/{ShowId:D}?context=watch", PlaybackItemRoutes.ForVideoLink("show", ShowId, ShowId, episode));
        Assert.Equal($"/details/tvshow/{ShowId:D}?context=watch&episode={episode.WorkId:D}", PlaybackItemRoutes.ForVideoLink("episode", episode.WorkId, ShowId, episode));

        // Before the presentation context has loaded nothing links: plain text beats a link to a page that cannot be named.
        Assert.Null(PlaybackBarIdentity.Build(PlaybackBarMedia.Tv, episode, "Shimmer and Shine").TitleLinkKind);

        var movie = Item(PlaybackBarMedia.Movie);
        var film = PlaybackBarIdentity.Build(PlaybackBarMedia.Movie, movie);
        Assert.Equal("movie", film.TitleLinkKind);
        Assert.Equal("2026 · Movie", film.SecondaryText);
        Assert.Equal($"/details/work/{movie.WorkId:D}?context=watch", PlaybackItemRoutes.ForVideoLink("movie", movie.WorkId, null, movie));
        Assert.Null(PlaybackItemRoutes.ForVideoLink("movie", Guid.NewGuid(), null, movie));
    }

    // ---- A3: the actions beside the title ----------------------------------------------------------------------------------------------

    [Fact]
    public void FavoriteLikeDislikeAndMoreAreAllBesideASongTitle()
    {
        var zone = RenderShell(PlaybackBarMedia.Music).Find("[data-playback-zone='identity']");

        var actions = zone.QuerySelector(".playback-identity__actions")!;
        Assert.Equal(["Add song to Favorites", "Like", "Dislike", "More song actions"], ButtonLabels(actions));
        Assert.Equal("identity", actions.ParentElement!.GetAttribute("data-playback-zone"));
        Assert.Equal(["playback-identity__art", "playback-identity__text", "playback-identity__actions"], zone.Children.Select(child => child.ClassList.First()).ToArray());
    }

    [Fact]
    public void TheActionsKeepAtLeast240PixelsForTheIdentityZoneAndNeverShrinkAt1024()
    {
        var identityCss = File.ReadAllText(RepoPath("src", "MediaEngine.Web", "Components", "Player", "PlaybackIdentity.razor.css")).Replace("\r\n", "\n");
        var actionsCss = File.ReadAllText(RepoPath("src", "MediaEngine.Web", "Components", "Player", "PlaybackIdentityActions.razor.css")).Replace("\r\n", "\n");

        Assert.Contains(".playback-identity {\n    align-items: center;\n    display: flex;\n    gap: 12px;\n    min-width: 240px;", identityCss, StringComparison.Ordinal);
        Assert.Contains(".playback-identity__actions {\n    align-items: center;\n    display: flex;\n    flex: none;", identityCss, StringComparison.Ordinal);
        Assert.Contains("gap: 6px;", actionsCss, StringComparison.Ordinal);

        // At 1024 the bar's outer columns are (1024 - 32 padding - 24 gaps - 232 music transport) / 2 = 368 px each. The zone needs the 56 px slot,
        // two 12 px gaps, four 44 px buttons with three 6 px gaps and the 2 px margin: what is left is the room for the title.
        const int zone = (1024 - 32 - 24 - 232) / 2;
        const int fixedParts = 56 + 12 + 12 + (4 * 44 + 3 * 6) + 2;
        Assert.True(zone >= 240);
        Assert.True(zone - fixedParts > 0, "the title still has room beside the four buttons");
    }

    [Fact]
    public void TheTitleTakesOnlyItsOwnWidthSoTheActionsSitRightAfterItAsInTheMockup()
    {
        var css = File.ReadAllText(RepoPath("src", "MediaEngine.Web", "Components", "Player", "PlaybackIdentity.razor.css")).Replace("\r\n", "\n");

        Assert.Contains(".playback-identity__text {\n    flex: 0 1 auto;\n    min-width: 0;\n}", css, StringComparison.Ordinal);
        Assert.DoesNotContain(".playback-identity__text {\n    flex: 1 1 auto;", css, StringComparison.Ordinal);
        // A long title still ends in an ellipsis: the text may shrink (the 1) and its lines clip.
        Assert.Contains("text-overflow: ellipsis;", css, StringComparison.Ordinal);
    }

    [Fact]
    public void ASongTitleLinksToItsAlbumBecauseTheLibraryHasNoPageForOneSong()
    {
        var song = Item(PlaybackBarMedia.Music);

        var identity = PlaybackBarIdentity.Build(PlaybackBarMedia.Music, song);

        Assert.Equal("album", identity.TitleLinkKind);
        Assert.Equal(song.AlbumWorkId, identity.TitleLinkId);
        Assert.Equal("The Midnight Coast", identity.SecondaryText);
        Assert.Equal($"/details/musicalbum/{song.AlbumWorkId:D}?context=listen", PlaybackIdentityNavigation.Route(song, identity.TitleLinkKind, identity.TitleLinkId!.Value));
        Assert.Null(PlaybackBarIdentity.Build(PlaybackBarMedia.Music, song with { AlbumWorkId = null }).TitleLinkKind);
    }

    [Theory]
    [InlineData(PlaybackBarMedia.Audiobook)]
    [InlineData(PlaybackBarMedia.Movie)]
    public void AudiobooksAndMoviesHaveFavoriteAndMoreOnly(PlaybackBarMedia media)
    {
        var actions = RenderShell(media).Find(".playback-identity__actions");

        Assert.Equal(["Add to My List", "More actions"], ButtonLabels(actions));
    }

    [Fact]
    public void ATvEpisodeOffersFavoriteForItsShowAndMoreAlways()
    {
        var withShow = RenderShell(PlaybackBarMedia.Tv).Find(".playback-identity__actions");
        Assert.Equal(["Add to My List", "More actions"], ButtonLabels(withShow));

        var episode = Item(PlaybackBarMedia.Tv);
        var withoutShow = Render<PlaybackIdentityActions>(p => p.Add(a => a.Media, PlaybackBarMedia.Tv).Add(a => a.Item, episode));
        Assert.Equal(["More actions"], ButtonLabels(withoutShow.Find(".playback-identity-actions")));
    }

    [Fact]
    public void ThePhonesCollapsedBarDrawsNoActions()
    {
        var cut = RenderShell(PlaybackBarMedia.Music, configure: p => p.Add(shell => shell.Compact, true));

        Assert.Empty(cut.FindAll(".playback-identity__actions"));
    }

    [Fact]
    public async Task LikeClearsDislikeAndDislikeClearsLikeAndPressingAnActiveRatingRemovesIt()
    {
        var item = Item(PlaybackBarMedia.Music);
        var cut = Render<PlaybackIdentityActions>(p => p.Add(a => a.Media, PlaybackBarMedia.Music).Add(a => a.Item, item)
            .Add(a => a.Snapshot, new ListenPlaybackSnapshot { ProfileId = Profile }));

        async Task Press(string label)
        {
            await cut.InvokeAsync(() => cut.Find($"button[aria-label='{label}']").Click());
            cut.WaitForAssertion(() => { });
        }

        await Press("Like");
        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("button[aria-label='Like']").GetAttribute("aria-pressed")));
        Assert.Equal("false", cut.Find("button[aria-label='Dislike']").GetAttribute("aria-pressed"));
        Assert.Equal(ProfileReactionKind.Like, _stored[(ProfileEntityKind.Song, item.WorkId)]);

        await Press("Dislike");
        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("button[aria-label='Dislike']").GetAttribute("aria-pressed")));
        Assert.Equal("false", cut.Find("button[aria-label='Like']").GetAttribute("aria-pressed"));
        Assert.Equal(ProfileReactionKind.Dislike, _stored[(ProfileEntityKind.Song, item.WorkId)]);

        await Press("Dislike");
        cut.WaitForAssertion(() => Assert.Equal("false", cut.Find("button[aria-label='Dislike']").GetAttribute("aria-pressed")));
        Assert.Equal("false", cut.Find("button[aria-label='Like']").GetAttribute("aria-pressed"));
        Assert.Empty(_stored);
    }

    [Fact]
    public async Task FavoriteIsASavedItemAndNotAReaction_ForASongAndForEveryOtherMedia()
    {
        var song = Item(PlaybackBarMedia.Music);
        var music = Render<PlaybackIdentityActions>(p => p.Add(a => a.Media, PlaybackBarMedia.Music).Add(a => a.Item, song)
            .Add(a => a.Snapshot, new ListenPlaybackSnapshot { ProfileId = Profile }));
        await music.InvokeAsync(() => music.Find("button[aria-label='Add song to Favorites']").Click());
        music.WaitForAssertion(() => Assert.Equal("true", music.Find("button[aria-label='Remove song from Favorites']").GetAttribute("aria-pressed")));
        Assert.Contains((ProfileEntityKind.Song, song.WorkId), _saved);
        Assert.Empty(_stored);

        var book = Item(PlaybackBarMedia.Audiobook);
        var audiobook = Render<PlaybackIdentityActions>(p => p.Add(a => a.Media, PlaybackBarMedia.Audiobook).Add(a => a.Item, book)
            .Add(a => a.Snapshot, new ListenPlaybackSnapshot { ProfileId = Profile }));
        await audiobook.InvokeAsync(() => audiobook.Find("button[aria-label='Add to My List']").Click());
        audiobook.WaitForAssertion(() => Assert.Contains((ProfileEntityKind.Audiobook, book.AudiobookWorkId!.Value), _saved));

        var movie = Item(PlaybackBarMedia.Movie);
        var film = Render<PlaybackIdentityActions>(p => p.Add(a => a.Media, PlaybackBarMedia.Movie).Add(a => a.Item, movie)
            .Add(a => a.Snapshot, new ListenPlaybackSnapshot { ProfileId = Profile }));
        await film.InvokeAsync(() => film.Find("button[aria-label='Add to My List']").Click());
        film.WaitForAssertion(() => Assert.Contains((ProfileEntityKind.Movie, movie.WorkId), _saved));

        var episode = Item(PlaybackBarMedia.Tv);
        var tv = Render<PlaybackIdentityActions>(p => p.Add(a => a.Media, PlaybackBarMedia.Tv).Add(a => a.Item, episode).Add(a => a.ShowWorkId, ShowId)
            .Add(a => a.Snapshot, new ListenPlaybackSnapshot { ProfileId = Profile }));
        await tv.InvokeAsync(() => tv.Find("button[aria-label='Add to My List']").Click());
        tv.WaitForAssertion(() => Assert.Contains((ProfileEntityKind.TvShow, ShowId), _saved));
        Assert.Empty(_stored);
    }

    [Fact]
    public async Task LikeThenFavoriteKeepsBoth_UnfavoritingKeepsTheLike_AndDislikeKeepsTheFavorite()
    {
        var song = Item(PlaybackBarMedia.Music);
        var cut = Render<PlaybackIdentityActions>(p => p.Add(a => a.Media, PlaybackBarMedia.Music).Add(a => a.Item, song)
            .Add(a => a.Snapshot, new ListenPlaybackSnapshot { ProfileId = Profile }));

        await cut.InvokeAsync(() => cut.Find("button[aria-label='Like']").Click());
        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("button[aria-label='Like']").GetAttribute("aria-pressed")));
        await cut.InvokeAsync(() => cut.Find("button[aria-label='Add song to Favorites']").Click());
        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("button[aria-label='Remove song from Favorites']").GetAttribute("aria-pressed")));
        Assert.Equal("true", cut.Find("button[aria-label='Like']").GetAttribute("aria-pressed"));
        Assert.Equal(ProfileReactionKind.Like, _stored[(ProfileEntityKind.Song, song.WorkId)]);
        Assert.Contains((ProfileEntityKind.Song, song.WorkId), _saved);

        await cut.InvokeAsync(() => cut.Find("button[aria-label='Remove song from Favorites']").Click());
        cut.WaitForAssertion(() => Assert.Equal("false", cut.Find("button[aria-label='Add song to Favorites']").GetAttribute("aria-pressed")));
        Assert.Equal("true", cut.Find("button[aria-label='Like']").GetAttribute("aria-pressed"));
        Assert.Equal(ProfileReactionKind.Like, _stored[(ProfileEntityKind.Song, song.WorkId)]);
        Assert.DoesNotContain((ProfileEntityKind.Song, song.WorkId), _saved);

        await cut.InvokeAsync(() => cut.Find("button[aria-label='Add song to Favorites']").Click());
        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("button[aria-label='Remove song from Favorites']").GetAttribute("aria-pressed")));
        await cut.InvokeAsync(() => cut.Find("button[aria-label='Dislike']").Click());
        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("button[aria-label='Dislike']").GetAttribute("aria-pressed")));
        Assert.Equal("true", cut.Find("button[aria-label='Remove song from Favorites']").GetAttribute("aria-pressed"));
        Assert.Equal(ProfileReactionKind.Dislike, _stored[(ProfileEntityKind.Song, song.WorkId)]);
        Assert.Contains((ProfileEntityKind.Song, song.WorkId), _saved);
    }

    [Fact]
    public void AMovieSavedFromTheDetailsPage_ShowsAsSavedOnThePlayersHeart()
    {
        var movie = Item(PlaybackBarMedia.Movie);
        _saved.Add((ProfileEntityKind.Movie, movie.WorkId));

        var cut = Render<PlaybackIdentityActions>(p => p.Add(a => a.Media, PlaybackBarMedia.Movie).Add(a => a.Item, movie)
            .Add(a => a.Snapshot, new ListenPlaybackSnapshot { ProfileId = Profile }));

        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("button[aria-label='Remove from My List']").GetAttribute("aria-pressed")));
    }

    [Theory]
    [InlineData(PlaybackBarMedia.Audiobook)]
    [InlineData(PlaybackBarMedia.Movie)]
    public void MyListInThePlayersIsAPlusUntilTheTitleIsSaved(PlaybackBarMedia media)
    {
        // My List spec D8 and D15: one icon for saving a title everywhere; the heart is only a song's Favorite.
        var cut = Render<PlaybackIdentityActions>(p => p.Add(a => a.Media, media).Add(a => a.Item, Item(media))
            .Add(a => a.Snapshot, new ListenPlaybackSnapshot { ProfileId = Profile }));

        Assert.Equal("Add", cut.Find("button[aria-label='Add to My List'] svg").GetAttribute("data-playback-glyph"));
        Assert.Empty(cut.FindAll("svg[data-playback-glyph='Favorite']"));
    }

    [Theory]
    [InlineData(PlaybackBarMedia.Audiobook, ProfileEntityKind.Audiobook)]
    [InlineData(PlaybackBarMedia.Movie, ProfileEntityKind.Movie)]
    public void MyListInThePlayersIsATickOnceTheTitleIsSaved_NeverAHeart(PlaybackBarMedia media, ProfileEntityKind kind)
    {
        var item = Item(media);
        _saved.Add((kind, media == PlaybackBarMedia.Audiobook ? item.AudiobookWorkId ?? item.WorkId : item.WorkId));

        var cut = Render<PlaybackIdentityActions>(p => p.Add(a => a.Media, media).Add(a => a.Item, item)
            .Add(a => a.Snapshot, new ListenPlaybackSnapshot { ProfileId = Profile }));

        cut.WaitForAssertion(() => Assert.Equal("Check", cut.Find("button[aria-label='Remove from My List'] svg").GetAttribute("data-playback-glyph")));
        Assert.Empty(cut.FindAll("svg[data-playback-glyph='Favorite']"));
    }

    [Fact]
    public void ASongsFavoriteInThePlayersKeepsTheHeart()
    {
        var song = Item(PlaybackBarMedia.Music);

        var cut = Render<PlaybackIdentityActions>(p => p.Add(a => a.Media, PlaybackBarMedia.Music).Add(a => a.Item, song)
            .Add(a => a.Snapshot, new ListenPlaybackSnapshot { ProfileId = Profile }));

        Assert.Equal("Favorite", cut.Find("button[aria-label='Add song to Favorites'] svg").GetAttribute("data-playback-glyph"));
    }

    // ---- D23: the item actions menu -----------------------------------------------------------------------------------------------------

    private async Task<IReadOnlyList<(string Key, string Label, bool DividerBefore)>> OpenMenuAsync(PlaybackBarMedia media, bool withShow = true)
    {
        var item = Item(media);
        var cut = Render<PlaybackIdentityActions>(p =>
        {
            p.Add(a => a.Media, media).Add(a => a.Item, item).Add(a => a.Snapshot, new ListenPlaybackSnapshot { ProfileId = Profile });
            if (media == PlaybackBarMedia.Tv && withShow)
            {
                p.Add(a => a.ShowWorkId, ShowId).Add(a => a.SeasonId, SeasonId);
            }
        });
        var panel = cut.FindComponent<PlaybackPanel>();
        await cut.InvokeAsync(() => panel.Instance.OpenAsync(true));

        var menu = cut.WaitForElement("[data-playback-item-menu]");
        Assert.Contains("playback-panel--menu", cut.Find("[data-playback-popover-panel]").ClassList);
        Assert.Equal(item.Title, cut.Find(".playback-panel__header-line").TextContent);
        var rows = new List<(string, string, bool)>();
        foreach (var child in menu.Children)
        {
            if (child.GetAttribute("role") == "separator" || child.GetAttribute("data-playback-item-action") is null)
            {
                continue;
            }

            var divider = child.PreviousElementSibling?.GetAttribute("role") == "separator";
            rows.Add((child.GetAttribute("data-playback-item-action")!, child.TextContent.Trim(), divider));
        }
        return rows;
    }

    [Fact]
    public async Task TheMusicMenuHasTheRowsOfD23InGroupsAndNoFavoriteOrRatingRows()
    {
        var rows = await OpenMenuAsync(PlaybackBarMedia.Music);

        Assert.Equal(["Add to playlist…", "Mark as listened", "Listening history", "Go to album", "Go to artist", "Playback settings"], rows.Select(r => r.Label).ToArray());
        Assert.Equal([false, true, false, true, false, true], rows.Select(r => r.DividerBefore).ToArray());
        Assert.DoesNotContain(rows, row => row.Label.Contains("Favorite", StringComparison.OrdinalIgnoreCase)
            || row.Label.Contains("like", StringComparison.OrdinalIgnoreCase) && !row.Label.Contains("unlistened"));
    }

    [Fact]
    public async Task TheAudiobookMenuUsesFinishedAndGoesToTheBookAndAuthor()
    {
        var rows = await OpenMenuAsync(PlaybackBarMedia.Audiobook);

        Assert.Equal(["Add to playlist…", "Mark as finished", "Listening history", "Go to book", "Go to author", "Playback settings"], rows.Select(r => r.Label).ToArray());
    }

    [Fact]
    public async Task TheTvMenuGoesToTheShowSeasonAndEpisode()
    {
        var rows = await OpenMenuAsync(PlaybackBarMedia.Tv);

        Assert.Equal(["Mark as watched", "Watch history", "Go to show", "Go to season", "Go to episode"], rows.Select(r => r.Label).ToArray());
        Assert.Equal([false, false, true, false, false], rows.Select(r => r.DividerBefore).ToArray());
    }

    [Fact]
    public async Task TheMovieMenuHasMarkHistoryAndGoToMovie()
    {
        var rows = await OpenMenuAsync(PlaybackBarMedia.Movie);

        Assert.Equal(["Mark as watched", "Watch history", "Go to movie"], rows.Select(r => r.Label).ToArray());
    }

    [Fact]
    public async Task ATvMenuBeforeTheShowIsKnownLeavesOutTheRowsItCannotAimAndStillOffersTheEpisode()
    {
        var rows = await OpenMenuAsync(PlaybackBarMedia.Tv, withShow: false);

        Assert.Equal(["Mark as watched", "Watch history", "Go to episode"], rows.Select(r => r.Label).ToArray());
    }

    [Fact]
    public void TheMenuOffersTheOppositeMarkOnceTheItemIsDoneAndLeavesItOutWhenThereIsNothingToMark()
    {
        var song = Item(PlaybackBarMedia.Music);
        string[] Labels(bool? done, bool canMark) => PlaybackItemMenuCatalog.Build(new(PlaybackBarMedia.Music, song, done, canMark)).Select(row => row.Label).ToArray();

        Assert.Contains("Mark as unlistened", Labels(true, true));
        Assert.DoesNotContain("Mark as listened", Labels(true, true));
        Assert.Contains("Mark as listened", Labels(false, true));
        Assert.Contains("Mark as listened", Labels(null, true));
        Assert.DoesNotContain(Labels(null, false), label => label.StartsWith("Mark as", StringComparison.Ordinal));
        Assert.Contains("Listening history", Labels(null, false));
        Assert.Contains("Mark as unwatched", PlaybackItemMenuCatalog.Build(new(PlaybackBarMedia.Movie, Item(PlaybackBarMedia.Movie), true)).Select(row => row.Label));
        Assert.Contains("Mark as unfinished", PlaybackItemMenuCatalog.Build(new(PlaybackBarMedia.Audiobook, Item(PlaybackBarMedia.Audiobook), true)).Select(row => row.Label));
    }

    [Fact]
    public void TheGoToRowsAimAtTheRightPagesAndAMissingTargetLeavesItsRowOut()
    {
        var song = Item(PlaybackBarMedia.Music);
        var rows = PlaybackItemMenuCatalog.Build(new(PlaybackBarMedia.Music, song));
        Assert.Equal($"/details/musicalbum/{song.AlbumWorkId:D}?context=listen", rows.Single(r => r.Key == PlaybackItemActionKey.GoToAlbum).Route);
        Assert.Equal($"/details/person/{song.ArtistPersonId:D}", rows.Single(r => r.Key == PlaybackItemActionKey.GoToArtist).Route);

        var loose = PlaybackItemMenuCatalog.Build(new(PlaybackBarMedia.Music, song with { AlbumWorkId = null, ArtistPersonId = null }));
        Assert.DoesNotContain(loose, r => r.Key is PlaybackItemActionKey.GoToAlbum or PlaybackItemActionKey.GoToArtist);
        Assert.Equal("Playback settings", loose[^1].Label);
        Assert.True(loose[^1].DividerBefore);

        var tv = PlaybackItemMenuCatalog.Build(new(PlaybackBarMedia.Tv, Item(PlaybackBarMedia.Tv), ShowWorkId: ShowId, SeasonId: SeasonId));
        Assert.Equal($"/details/tvshow/{ShowId:D}?context=watch&season={SeasonId:D}", tv.Single(r => r.Key == PlaybackItemActionKey.GoToSeason).Route);
    }

    [Fact]
    public void VideoMenusNeverCarryPlaybackSettingsBecauseTheBarHasItsOwnSettingsTool()
    {
        foreach (var media in new[] { PlaybackBarMedia.Tv, PlaybackBarMedia.Movie })
        {
            Assert.DoesNotContain(PlaybackItemMenuCatalog.Build(new(media, Item(media), ShowWorkId: ShowId)), row => row.Key == PlaybackItemActionKey.PlaybackSettings);
        }
    }

    [Fact]
    public async Task AHostCanAddRowsToTheEndOfTheMenuForPlayOnAnotherDevice()
    {
        var item = Item(PlaybackBarMedia.Music);
        var cut = Render<PlaybackItemMenu>(p => p.Add(m => m.Media, PlaybackBarMedia.Music).Add(m => m.Item, item)
            .Add(m => m.ExtraRows, (RenderFragment)(b => b.AddMarkupContent(0, "<button id='extra'>Play on another device…</button>"))));
        await cut.InvokeAsync(() => cut.FindComponent<PlaybackPanel>().Instance.OpenAsync(true));

        var menu = cut.WaitForElement("[data-playback-item-menu]");
        Assert.Equal("extra", menu.LastElementChild!.Id);
    }

    [Fact]
    public async Task TheItemMenuGetsItsArrowKeysFromTheSharedRovingFocusScript()
    {
        var roving = JSInterop.SetupModule("./js/roving-focus.js");
        roving.Mode = JSRuntimeMode.Loose;
        var cut = Render<PlaybackItemMenu>(p => p.Add(m => m.Media, PlaybackBarMedia.Music).Add(m => m.Item, Item(PlaybackBarMedia.Music)));

        await cut.InvokeAsync(() => cut.FindComponent<PlaybackPanel>().Instance.OpenAsync(true));

        cut.WaitForAssertion(() => Assert.Single(roving.Invocations["attachMenu"]));
        Assert.NotNull(cut.Find("[data-playback-item-menu] [data-playback-menu-roving]"));
    }

    private static string RepoPath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine([directory!.FullName, .. parts]);
    }
}
