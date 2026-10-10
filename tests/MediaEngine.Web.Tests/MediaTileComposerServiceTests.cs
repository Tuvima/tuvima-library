using MediaEngine.Contracts.Display;
using MediaEngine.Domain;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.MediaTiles;

namespace MediaEngine.Web.Tests;

public sealed class MediaTileComposerServiceTests
{
    [Fact]
    public void PlaylistComposition_CentralizesVisibilityAndArtworkPolicy()
    {
        var playlist = new ManagedCollectionViewModel
        {
            Id = Guid.Parse("99999999-9999-9999-9999-999999999999"),
            Name = "Road Trip",
            Description = "Songs for the highway",
            CollectionType = "Playlist",
            ItemCount = 1,
            CoverArtworkUrl = "/stream/artwork/99999999-9999-9999-9999-999999999999",
            CreatedAt = DateTimeOffset.Parse("2026-01-02T03:04:05Z"),
        };

        Assert.True(MediaTileComposerService.IsUserVisiblePlaylist(playlist));
        Assert.True(MediaTileComposerService.IsUserVisiblePlaylist(new ManagedCollectionViewModel
        {
            Name = "Favorites",
            CollectionType = "Playlist",
        }));

        var tile = MediaTileComposerService.FromPlaylist(playlist);

        Assert.Equal("1 item", tile.Subtitle);
        Assert.Equal("/listen/music/playlists/99999999-9999-9999-9999-999999999999", tile.NavigationUrl);
        Assert.Equal(MediaTileShape.Square, tile.Shape);
        Assert.Equal(MediaTileHoverMode.GlowOnly, tile.HoverMode);
        Assert.True(tile.IsCollection);
        Assert.Contains("size=s", tile.TileImageUrl, StringComparison.Ordinal);
        Assert.Contains("size=m", tile.HoverImageUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void FromDisplayCard_PreservesCompactDisplayHintsAndProgress()
    {
        var assetId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var workId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var resumeAction = new DisplayActionDto("readAsset", "Continue Reading", WorkId: workId, AssetId: assetId, WebUrl: $"/read/{assetId}");
        var card = new DisplayCardDto(
            Id: workId,
            WorkId: workId,
            AssetId: assetId,
            CollectionId: null,
            MediaType: "Book",
            GroupingType: "work",
            Title: "Dune",
            Subtitle: "Frank Herbert",
            Facts: ["Frank Herbert", "Science Fiction"],
            Artwork: new DisplayArtworkDto(
                CoverUrl: "http://localhost:61495/stream/11111111-1111-1111-1111-111111111111/cover",
                CoverSmallUrl: "http://localhost:61495/stream/artwork/11111111-1111-1111-1111-111111111111?size=s",
                CoverMediumUrl: "http://localhost:61495/stream/artwork/11111111-1111-1111-1111-111111111111?size=m",
                CoverLargeUrl: "http://localhost:61495/stream/artwork/11111111-1111-1111-1111-111111111111?size=l",
                SquareUrl: null,
                SquareSmallUrl: null,
                SquareMediumUrl: null,
                SquareLargeUrl: null,
                BannerUrl: null,
                BannerSmallUrl: null,
                BannerMediumUrl: null,
                BannerLargeUrl: null,
                BackgroundUrl: null,
                BackgroundSmallUrl: null,
                BackgroundMediumUrl: null,
                BackgroundLargeUrl: null,
                LogoUrl: null,
                CoverWidthPx: 1000,
                CoverHeightPx: 1500,
                SquareWidthPx: null,
                SquareHeightPx: null,
                BannerWidthPx: null,
                BannerHeightPx: null,
                BackgroundWidthPx: null,
                BackgroundHeightPx: null,
                AccentColor: "#5DCAA5"),
            PreferredShape: "portrait",
            Presentation: "book",
            TileTextMode: "coverOnly",
            PreviewPlacement: "bottom",
            Progress: new DisplayProgressDto(32, "32%", DateTimeOffset.Parse("2026-04-24T12:00:00Z"), resumeAction),
            Actions: [resumeAction, new DisplayActionDto("openWork", "Details", WorkId: workId, WebUrl: $"/book/{workId}")],
            Flags: new DisplayCardFlagsDto(IsPlayable: false, IsReadable: true, IsCollection: false, IsFavorite: false),
            SortTimestamp: DateTimeOffset.Parse("2026-04-24T12:00:00Z"))
        {
            Description = "1965 science fiction novel",
            SortYear = 1965,
        };

        var mapped = MediaTileComposerService.FromDisplayCard(card);

        Assert.Equal(MediaTileTextMode.CoverOnly, mapped.TileTextMode);
        Assert.Equal(MediaTilePreviewPlacement.Bottom, mapped.PreviewPlacement);
        Assert.Equal(["Frank Herbert", "Science Fiction"], mapped.HoverFacts);
        Assert.Equal(32, mapped.ProgressPct);
        Assert.Equal("http://localhost:61495/stream/artwork/11111111-1111-1111-1111-111111111111?size=s", mapped.TileImageUrl);
        Assert.Equal("http://localhost:61495/stream/artwork/11111111-1111-1111-1111-111111111111?size=s 213w, http://localhost:61495/stream/artwork/11111111-1111-1111-1111-111111111111?size=m 640w", mapped.TileImageSrcSet);
        Assert.Equal("http://localhost:61495/stream/artwork/11111111-1111-1111-1111-111111111111?size=m", mapped.HoverImageUrl);
        Assert.Equal("1965 science fiction novel", mapped.Description);
        Assert.Equal(assetId, mapped.AssetId);
        Assert.Equal($"/read/{assetId}", mapped.PrimaryNavigationUrl);
        Assert.Equal("Continue Reading", mapped.PrimaryActionLabel);
        Assert.Equal(1965, mapped.SortYear);
    }

    [Fact]
    public void FromDisplayCard_UsesManagedBaseCoverWhenSizedRenditionsAreUnavailable()
    {
        var albumId = Guid.Parse("33333333-1111-1111-1111-333333333333");
        var coverUrl = "http://localhost:61495/stream/33333333-1111-1111-1111-333333333333/cover";
        var card = new DisplayCardDto(
            Id: albumId,
            WorkId: null,
            AssetId: null,
            CollectionId: albumId,
            MediaType: "Music",
            GroupingType: "album",
            Title: "Abbey Road",
            Subtitle: "The Beatles",
            Facts: ["1969", "17 tracks"],
            Artwork: new DisplayArtworkDto(
                CoverUrl: coverUrl,
                CoverSmallUrl: null,
                CoverMediumUrl: null,
                CoverLargeUrl: null,
                SquareUrl: null,
                SquareSmallUrl: null,
                SquareMediumUrl: null,
                SquareLargeUrl: null,
                BannerUrl: null,
                BannerSmallUrl: null,
                BannerMediumUrl: null,
                BannerLargeUrl: null,
                BackgroundUrl: null,
                BackgroundSmallUrl: null,
                BackgroundMediumUrl: null,
                BackgroundLargeUrl: null,
                LogoUrl: null,
                CoverWidthPx: 600,
                CoverHeightPx: 600,
                SquareWidthPx: null,
                SquareHeightPx: null,
                BannerWidthPx: null,
                BannerHeightPx: null,
                BackgroundWidthPx: null,
                BackgroundHeightPx: null,
                AccentColor: "#7C3AED"),
            PreferredShape: "square",
            Presentation: "album",
            TileTextMode: "caption",
            PreviewPlacement: "smart",
            Progress: null,
            Actions: [new DisplayActionDto("openCollection", "Explore", CollectionId: albumId, WebUrl: $"/listen/music/albums/{albumId}")],
            Flags: new DisplayCardFlagsDto(true, false, true, false),
            SortTimestamp: DateTimeOffset.Parse("2026-04-24T12:00:00Z"));

        var mapped = MediaTileComposerService.FromDisplayCard(card);

        Assert.Equal(MediaTileShape.Square, mapped.Shape);
        Assert.Equal(coverUrl, mapped.TileImageUrl);
        Assert.Equal(coverUrl, mapped.HoverImageUrl);
    }

    [Fact]
    public void FromDisplayCard_AlbumSurfaceAlwaysOpensAlbumDetailWhenResumeTrackIsPresent()
    {
        var albumId = Guid.NewGuid();
        var trackId = Guid.NewGuid();
        var albumRoute = $"/details/musicalbum/{albumId:D}?context=listen";
        var card = new DisplayCardDto(
            Id: albumId,
            WorkId: trackId,
            AssetId: Guid.NewGuid(),
            CollectionId: albumId,
            MediaType: "Music",
            GroupingType: "album",
            Title: "Heroes",
            Subtitle: "David Bowie",
            Facts: ["1977", "8 tracks"],
            Artwork: EmptyArtwork("#7C3AED"),
            PreferredShape: "square",
            Presentation: "album",
            TileTextMode: "caption",
            PreviewPlacement: "smart",
            Progress: null,
            Actions:
            [
                new DisplayActionDto("playAsset", "Resume", trackId, WebUrl: $"/listen/music?browse=songs&track={trackId:D}"),
                new DisplayActionDto("playAlbum", "Play Album", trackId, CollectionId: albumId, WebUrl: albumRoute),
            ],
            Flags: new DisplayCardFlagsDto(true, false, true, false),
            SortTimestamp: DateTimeOffset.Parse("2026-07-14T12:00:00Z"));

        var mapped = MediaTileComposerService.FromDisplayCard(card);

        Assert.Equal(albumRoute, mapped.DetailsNavigationUrl);
    }

    [Fact]
    public void FromDisplayCard_MapsWatchBadgesToTileFieldsAndLogos()
    {
        var workId = Guid.Parse("44444444-1111-1111-1111-444444444444");
        var action = new DisplayActionDto("playAsset", "Play", WorkId: workId, WebUrl: $"/watch/movie/{workId}");
        var card = new DisplayCardDto(
            Id: workId,
            WorkId: workId,
            AssetId: null,
            CollectionId: null,
            MediaType: "Movie",
            GroupingType: "work",
            Title: "Arrival",
            Subtitle: "2016",
            Facts: ["2016", "Science Fiction"],
            Artwork: EmptyArtwork("#60A5FA"),
            PreferredShape: "landscape",
            Presentation: "movie",
            TileTextMode: "caption",
            PreviewPlacement: "smart",
            Progress: null,
            Actions: [action],
            Flags: new DisplayCardFlagsDto(true, false, false, false),
            SortTimestamp: DateTimeOffset.Parse("2026-04-24T12:00:00Z"))
        {
            Badges = [new DisplayCardBadgeDto("quality", "4K"), new DisplayCardBadgeDto("source", "HBO")],
        };

        var mapped = MediaTileComposerService.FromDisplayCard(card);

        Assert.Equal("4K", mapped.QualityBadge);
        Assert.Equal("HBO", mapped.SourceBadgeLabel);
        Assert.Contains("max", mapped.SourceLogoUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Book")]
    [InlineData("Comic")]
    [InlineData("Audiobook")]
    [InlineData("Music")]
    public void FromDisplayCard_MapsReadableAndAudioTilesToExpandedHover(string mediaType)
    {
        var workId = Guid.Parse("44444444-9999-1111-1111-444444444444");
        var action = new DisplayActionDto("openWork", "Open", WorkId: workId, WebUrl: $"/details/{workId}");
        var card = new DisplayCardDto(
            Id: workId,
            WorkId: workId,
            AssetId: null,
            CollectionId: null,
            MediaType: mediaType,
            GroupingType: "work",
            Title: "Hover Candidate",
            Subtitle: "Creator",
            Facts: ["Creator", "Genre"],
            Artwork: EmptyArtwork("#5DCAA5"),
            PreferredShape: mediaType is "Audiobook" or "Music" ? "square" : "portrait",
            Presentation: mediaType.ToLowerInvariant(),
            TileTextMode: "caption",
            PreviewPlacement: "smart",
            Progress: null,
            Actions: [action],
            Flags: new DisplayCardFlagsDto(mediaType is "Audiobook" or "Music", mediaType is "Book" or "Comic", false, false),
            SortTimestamp: DateTimeOffset.Parse("2026-04-24T12:00:00Z"));

        var mapped = MediaTileComposerService.FromDisplayCard(card);

        Assert.Equal(MediaTileHoverMode.Expanded, mapped.HoverMode);
    }

    [Fact]
    public void FromDisplayCard_MapsCollectionArtworkIntoBackdropAndPreviewImages()
    {
        var collectionId = Guid.Parse("66666666-1111-1111-1111-666666666666");
        var action = new DisplayActionDto("openCollection", "Explore", CollectionId: collectionId, WebUrl: $"/collection/{collectionId}");
        var card = new DisplayCardDto(
            Id: collectionId,
            WorkId: null,
            AssetId: null,
            CollectionId: collectionId,
            MediaType: "Movie",
            GroupingType: "smartShelf",
            Title: "Sci-Fi Favorites",
            Subtitle: "12 titles",
            Facts: ["12 titles"],
            Artwork: new DisplayArtworkDto(
                CoverUrl: "/cover.jpg",
                CoverSmallUrl: "/cover-s.jpg",
                CoverMediumUrl: "/cover-m.jpg",
                CoverLargeUrl: "/cover-l.jpg",
                SquareUrl: null,
                SquareSmallUrl: null,
                SquareMediumUrl: null,
                SquareLargeUrl: null,
                BannerUrl: null,
                BannerSmallUrl: null,
                BannerMediumUrl: null,
                BannerLargeUrl: null,
                BackgroundUrl: "/background.jpg",
                BackgroundSmallUrl: "/background-s.jpg",
                BackgroundMediumUrl: "/background-m.jpg",
                BackgroundLargeUrl: "/background-l.jpg",
                LogoUrl: null,
                CoverWidthPx: 1000,
                CoverHeightPx: 1500,
                SquareWidthPx: null,
                SquareHeightPx: null,
                BannerWidthPx: null,
                BannerHeightPx: null,
                BackgroundWidthPx: 1920,
                BackgroundHeightPx: 1080,
                AccentColor: "#60A5FA"),
            PreferredShape: "landscape",
            Presentation: "collection",
            TileTextMode: "coverOnly",
            PreviewPlacement: "smart",
            Progress: null,
            Actions: [action],
            Flags: new DisplayCardFlagsDto(true, false, true, false),
            SortTimestamp: DateTimeOffset.Parse("2026-04-24T12:00:00Z"));

        var mapped = MediaTileComposerService.FromDisplayCard(card);

        Assert.True(mapped.IsCollection);
        Assert.True(mapped.UseLandscapeGroupTile);
        Assert.Equal(MediaTileShape.Landscape, mapped.Shape);
        Assert.Equal(MediaTileSurfaceKind.BannerLandscape, mapped.SurfaceKind);
        Assert.Equal("/cover-s.jpg", mapped.TileImageUrl);
        Assert.Contains("/cover-s.jpg", mapped.PreviewImages);
        Assert.Contains("/background-s.jpg", mapped.PreviewImages);
    }

    [Fact]
    public void FromDisplayCard_UsesCinematicMediaTileForTvSeries()
    {
        var collectionId = Guid.Parse("77777777-1111-1111-1111-777777777777");
        var card = new DisplayCardDto(
            Id: collectionId,
            WorkId: null,
            AssetId: null,
            CollectionId: collectionId,
            MediaType: "TV",
            GroupingType: "tvSeries",
            Title: "The Expanse",
            Subtitle: "6 seasons",
            Facts: ["6 seasons"],
            Artwork: new DisplayArtworkDto(
                CoverUrl: "/shows/expanse-cover.jpg",
                CoverSmallUrl: "/shows/expanse-cover-s.jpg",
                CoverMediumUrl: "/shows/expanse-cover-m.jpg",
                CoverLargeUrl: "/shows/expanse-cover-l.jpg",
                SquareUrl: null, SquareSmallUrl: null, SquareMediumUrl: null, SquareLargeUrl: null,
                BannerUrl: null, BannerSmallUrl: null, BannerMediumUrl: null, BannerLargeUrl: null,
                BackgroundUrl: "/episodes/still.jpg",
                BackgroundSmallUrl: "/episodes/still-s.jpg",
                BackgroundMediumUrl: "/episodes/still-m.jpg",
                BackgroundLargeUrl: "/episodes/still-l.jpg",
                LogoUrl: null,
                CoverWidthPx: 1000, CoverHeightPx: 1500,
                SquareWidthPx: null, SquareHeightPx: null,
                BannerWidthPx: null, BannerHeightPx: null,
                BackgroundWidthPx: 1920, BackgroundHeightPx: 1080,
                AccentColor: "#8b5cf6"),
            PreferredShape: "portrait",
            Presentation: "tvSeries",
            TileTextMode: "coverOnly",
            PreviewPlacement: "smart",
            Progress: null,
            Actions: [new DisplayActionDto("openSeries", "Details", CollectionId: collectionId, WebUrl: $"/watch/tv/{collectionId}")],
            Flags: new DisplayCardFlagsDto(true, false, true, false),
            SortTimestamp: DateTimeOffset.Parse("2026-07-11T12:00:00Z"));

        var mapped = MediaTileComposerService.FromDisplayCard(card);

        Assert.False(mapped.UseLandscapeGroupTile);
        Assert.Equal(collectionId, mapped.WorkId);
        Assert.Equal(MediaTileShape.Portrait, mapped.Shape);
        Assert.Equal(MediaTileSurfaceKind.CoverPortrait, mapped.SurfaceKind);
        Assert.Equal(MediaTileHoverLayout.BannerPopover, mapped.HoverLayout);
        Assert.Equal(MediaTileHoverMode.Expanded, mapped.HoverMode);
        Assert.Equal("/shows/expanse-cover-s.jpg", mapped.TileImageUrl);
        Assert.Equal("/episodes/still-m.jpg", mapped.HoverImageUrl);
    }

    [Fact]
    public void FromDisplayCard_UsesShowPosterAtRestAndEpisodeStillForPeek()
    {
        var episodeId = Guid.Parse("88888888-1111-1111-1111-888888888888");
        var card = new DisplayCardDto(
            Id: episodeId,
            WorkId: episodeId,
            AssetId: Guid.NewGuid(),
            CollectionId: null,
            MediaType: "TV",
            GroupingType: "work",
            Title: "Episode Three",
            Subtitle: "Continue · S1 E3",
            Facts: ["2026", "45m"],
            Artwork: new DisplayArtworkDto(
                CoverUrl: "/shows/poster.jpg", CoverSmallUrl: "/shows/poster-s.jpg", CoverMediumUrl: "/shows/poster-m.jpg", CoverLargeUrl: "/shows/poster-l.jpg",
                SquareUrl: null, SquareSmallUrl: null, SquareMediumUrl: null, SquareLargeUrl: null,
                BannerUrl: null, BannerSmallUrl: null, BannerMediumUrl: null, BannerLargeUrl: null,
                BackgroundUrl: "/episodes/3.jpg",
                BackgroundSmallUrl: "/episodes/3-s.jpg",
                BackgroundMediumUrl: "/episodes/3-m.jpg",
                BackgroundLargeUrl: "/episodes/3-l.jpg",
                LogoUrl: null,
                CoverWidthPx: null, CoverHeightPx: null,
                SquareWidthPx: null, SquareHeightPx: null,
                BannerWidthPx: null, BannerHeightPx: null,
                BackgroundWidthPx: 1920, BackgroundHeightPx: 1080,
                AccentColor: "#8b5cf6"),
            PreferredShape: "landscape",
            Presentation: "default",
            TileTextMode: "coverOnly",
            PreviewPlacement: "smart",
            Progress: null,
            Actions:
            [
                new DisplayActionDto("playAsset", "Resume S1 E3", episodeId, WebUrl: $"/watch/player/{episodeId:D}"),
                new DisplayActionDto("openWork", "Details", episodeId, WebUrl: $"/watch/tv/show/{Guid.NewGuid():D}?episode={episodeId:D}"),
            ],
            Flags: new DisplayCardFlagsDto(true, false, false, false),
            SortTimestamp: DateTimeOffset.Parse("2026-07-14T12:00:00Z"));

        var mapped = MediaTileComposerService.FromDisplayCard(card);

        Assert.Equal(MediaTileShape.Portrait, mapped.Shape);
        Assert.Equal(MediaTileSurfaceKind.CoverPortrait, mapped.SurfaceKind);
        Assert.Equal("/shows/poster-s.jpg", mapped.TileImageUrl);
        Assert.Equal("/episodes/3-m.jpg", mapped.HoverImageUrl);
        Assert.Equal("Resume S1 E3", mapped.PrimaryActionLabel);
        Assert.Equal(card.Actions[1].WebUrl, mapped.DetailsNavigationUrl);
    }

    [Fact]
    public void FromDisplayCard_MapsReadSeriesCollectionsToExpandedHover()
    {
        var collectionId = Guid.Parse("66666666-2222-2222-2222-666666666666");
        var action = new DisplayActionDto("openSeries", "Open Series", CollectionId: collectionId, WebUrl: $"/details/bookseries/{collectionId}");
        var card = new DisplayCardDto(
            Id: collectionId,
            WorkId: null,
            AssetId: null,
            CollectionId: collectionId,
            MediaType: "Book",
            GroupingType: "bookSeries",
            Title: "The Expanse",
            Subtitle: "9 titles",
            Facts: ["9 titles", "Science Fiction"],
            Artwork: EmptyArtwork("#5DCAA5"),
            PreferredShape: "portrait",
            Presentation: "bookSeries",
            TileTextMode: "caption",
            PreviewPlacement: "smart",
            Progress: null,
            Actions: [action],
            Flags: new DisplayCardFlagsDto(false, true, true, false),
            SortTimestamp: DateTimeOffset.Parse("2026-04-24T12:00:00Z"));

        var mapped = MediaTileComposerService.FromDisplayCard(card);

        Assert.True(mapped.IsCollection);
        Assert.Equal(MediaTilePresentation.BookSeries, mapped.Presentation);
        Assert.Equal(MediaTileHoverMode.Expanded, mapped.HoverMode);
        Assert.Equal("Open Series", mapped.PrimaryActionLabel);
        Assert.True(mapped.UseLandscapeGroupTile);
    }

    [Fact]
    public void FromDisplayCard_MapsOrderedSeriesPreviewItemsToLandscapeGroupTile()
    {
        var collectionId = Guid.Parse("66666666-3333-3333-3333-666666666666");
        var workId = Guid.Parse("77777777-3333-3333-3333-777777777777");
        var action = new DisplayActionDto("openSeries", "Open Series", CollectionId: collectionId, WebUrl: $"/details/bookseries/{collectionId}");
        var card = new DisplayCardDto(
            Id: collectionId,
            WorkId: null,
            AssetId: null,
            CollectionId: collectionId,
            MediaType: "Book",
            GroupingType: "bookSeries",
            Title: "Foundation Series",
            Subtitle: "6 titles",
            Facts: ["6 titles", "Science Fiction"],
            Artwork: new DisplayArtworkDto(
                CoverUrl: null,
                CoverSmallUrl: null,
                CoverMediumUrl: null,
                CoverLargeUrl: null,
                SquareUrl: null,
                SquareSmallUrl: null,
                SquareMediumUrl: null,
                SquareLargeUrl: null,
                BannerUrl: null,
                BannerSmallUrl: null,
                BannerMediumUrl: null,
                BannerLargeUrl: null,
                BackgroundUrl: "/art/foundation-bg.jpg",
                BackgroundSmallUrl: "/art/foundation-bg-s.jpg",
                BackgroundMediumUrl: "/art/foundation-bg-m.jpg",
                BackgroundLargeUrl: "/art/foundation-bg-l.jpg",
                LogoUrl: null,
                CoverWidthPx: null,
                CoverHeightPx: null,
                SquareWidthPx: null,
                SquareHeightPx: null,
                BannerWidthPx: null,
                BannerHeightPx: null,
                BackgroundWidthPx: 1920,
                BackgroundHeightPx: 1080,
                AccentColor: "#5DCAA5"),
            PreferredShape: "portrait",
            Presentation: "bookSeries",
            TileTextMode: "caption",
            PreviewPlacement: "smart",
            Progress: null,
            Actions: [action],
            Flags: new DisplayCardFlagsDto(false, true, true, false),
            SortTimestamp: DateTimeOffset.Parse("2026-04-24T12:00:00Z"))
        {
            PreviewItems =
            [
                new DisplayCardPreviewItemDto(workId, null, "Foundation", "/covers/1-s.jpg", "portrait", "1", Description: "The first novel.", Facts: ["Isaac Asimov", "1951", "★ 4.4"]),
                new DisplayCardPreviewItemDto(Guid.NewGuid(), null, "Foundation and Empire", "/covers/2-s.jpg", "portrait", "2"),
                new DisplayCardPreviewItemDto(Guid.NewGuid(), null, "Second Foundation", "/covers/3-s.jpg", "portrait", "3"),
                new DisplayCardPreviewItemDto(Guid.NewGuid(), null, "Foundation's Edge", "/covers/4-s.jpg", "portrait", "4"),
            ],
            PreviewTotalCount = 6,
            GroupSummary = new DisplayGroupSummaryDto
            {
                OwnedCount = 6,
                EarliestYear = 1951,
                LatestYear = 1993,
                MediaCounts = [new DisplayGroupMediaCountDto("Books", 6)],
            },
        };

        var mapped = MediaTileComposerService.FromDisplayCard(card);

        Assert.True(mapped.IsCollection);
        Assert.True(mapped.UseLandscapeGroupTile);
        Assert.Equal(MediaTileShape.Landscape, mapped.Shape);
        Assert.Equal(MediaTileSurfaceKind.BannerLandscape, mapped.SurfaceKind);
        Assert.Equal(MediaTileHoverLayout.BannerPopover, mapped.HoverLayout);
        Assert.Null(mapped.TileImageUrl);
        Assert.Equal(6, mapped.PreviewTotalCount);
        Assert.Equal(1951, mapped.GroupSummary?.EarliestYear);
        Assert.Equal(1993, mapped.GroupSummary?.LatestYear);
        Assert.Equal("Books", Assert.Single(mapped.MediaCounts).Label);
        Assert.Equal(["1", "2", "3", "4"], mapped.ArtworkStackItems.Select(item => item.Position));
        Assert.Equal(["/covers/1-s.jpg", "/covers/2-s.jpg", "/covers/3-s.jpg", "/covers/4-s.jpg"], mapped.ArtworkStackItems.Select(item => item.ImageUrl));
        Assert.Equal("The first novel.", mapped.ArtworkStackItems[0].Description);
        Assert.Equal(["Isaac Asimov", "1951", "★ 4.4"], mapped.ArtworkStackItems[0].Facts);
    }

    [Fact]
    public void FromDisplayPage_UsesDisplayHeroAndShelvesWithoutLegacyComposition()
    {
        var workId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var action = new DisplayActionDto("playAsset", "Play", WorkId: workId, WebUrl: $"/watch/movies/{workId}");
        var card = new DisplayCardDto(
            Id: workId,
            WorkId: workId,
            AssetId: null,
            CollectionId: null,
            MediaType: "Movie",
            GroupingType: "work",
            Title: "Arrival",
            Subtitle: null,
            Facts: ["2016", "Science Fiction"],
            Artwork: new DisplayArtworkDto(
                CoverUrl: "/cover.jpg",
                CoverSmallUrl: "/cover-s.jpg",
                CoverMediumUrl: "/cover-m.jpg",
                CoverLargeUrl: "/cover-l.jpg",
                SquareUrl: null,
                SquareSmallUrl: null,
                SquareMediumUrl: null,
                SquareLargeUrl: null,
                BannerUrl: null,
                BannerSmallUrl: null,
                BannerMediumUrl: null,
                BannerLargeUrl: null,
                BackgroundUrl: "/background.jpg",
                BackgroundSmallUrl: "/background-s.jpg",
                BackgroundMediumUrl: "/background-m.jpg",
                BackgroundLargeUrl: "/background-l.jpg",
                LogoUrl: null,
                CoverWidthPx: null,
                CoverHeightPx: null,
                SquareWidthPx: null,
                SquareHeightPx: null,
                BannerWidthPx: null,
                BannerHeightPx: null,
                BackgroundWidthPx: null,
                BackgroundHeightPx: null,
                AccentColor: "#60A5FA"),
            PreferredShape: "landscape",
            Presentation: "movie",
            TileTextMode: "caption",
            PreviewPlacement: "smart",
            Progress: null,
            Actions: [action],
            Flags: new DisplayCardFlagsDto(true, false, false, false),
            SortTimestamp: DateTimeOffset.Parse("2026-04-24T12:00:00Z"));
        var page = new DisplayPageDto(
            Key: "watch",
            Title: "Watch",
            Subtitle: "Movies and shows",
            Hero: new DisplayHeroDto("Arrival", null, "Featured from your library", card.Artwork, null, [action])
            {
                Facts = card.Facts,
                Tagline = "Why are they here?",
            },
            Shelves: [new DisplayShelfDto("movies", "Movies in your library", null, [card], null)],
            Catalog: [card]);

        var mapped = MediaTileComposerService.FromDisplayPage(page);

        Assert.Equal("watch", mapped.Key);
        Assert.Equal("Arrival", mapped.Hero?.Title);
        Assert.Equal("Why are they here?", mapped.Hero?.Tagline);
        Assert.Single(mapped.Shelves);
        Assert.Equal("movies", mapped.Shelves[0].Key);
        Assert.Equal(MediaTileShelfKind.Standard, mapped.Shelves[0].Kind);
        Assert.Single(mapped.Catalog);
        Assert.Equal(["2016", "Science Fiction"], mapped.Hero?.MetaPills);
        Assert.Equal(["Arrival"], mapped.Spotlights.Select(slide => slide.Title));
        Assert.Equal(["2016", "Science Fiction"], mapped.Catalog[0].HoverFacts);
        Assert.Equal("/cover-s.jpg", mapped.Catalog[0].TileImageUrl);
        Assert.Equal(MediaTileShape.Portrait, mapped.Catalog[0].Shape);
        var continueTile = MediaTileComposerService.FromDisplayCard(card, MediaTileContext.ContinueWatching);
        Assert.Equal(MediaTileShape.Landscape, continueTile.Shape);
        Assert.Equal("/background-s.jpg", continueTile.TileImageUrl);
        Assert.Equal("/background-m.jpg", mapped.Catalog[0].HoverImageUrl);
        Assert.False(mapped.Catalog[0].IsSettling);
        Assert.True(MediaTileComposerService.FromDisplayCard(card with { IsSettling = true }, MediaTileContext.Default).IsSettling);
    }

    [Fact]
    public void FromDisplayPage_MapsEngineSpotlightsWithoutRebuildingCandidates()
    {
        var cards = Enumerable.Range(1, 6)
            .Select(index =>
            {
                var workId = Guid.Parse($"55555555-0000-0000-0000-{index:000000000000}");
                var action = new DisplayActionDto("playAsset", "Play", WorkId: workId, WebUrl: $"/watch/movie/{workId}");
                return new DisplayCardDto(
                    Id: workId,
                    WorkId: workId,
                    AssetId: null,
                    CollectionId: null,
                    MediaType: "Movie",
                    GroupingType: "work",
                    Title: $"Movie {index}",
                    Subtitle: "2016",
                    Facts: [$"Fact {index}"],
                    Artwork: EmptyArtwork("#60A5FA"),
                    PreferredShape: "landscape",
                    Presentation: "movie",
                    TileTextMode: "caption",
                    PreviewPlacement: "smart",
                    Progress: null,
                    Actions: [action],
                    Flags: new DisplayCardFlagsDto(true, false, false, false),
                    SortTimestamp: DateTimeOffset.Parse("2026-04-24T12:00:00Z"));
            })
            .ToList();
        var page = new DisplayPageDto(
            Key: "home",
            Title: "Home",
            Subtitle: null,
            Hero: new DisplayHeroDto(cards[0].Title, cards[0].Subtitle, "Jump Back In", cards[0].Artwork, null, cards[0].Actions)
            {
                Facts = cards[0].Facts,
            },
            Shelves: [new DisplayShelfDto("continue", "Jump Back In", null, cards, null)],
            Catalog: cards)
        { Spotlights = cards.Take(5).Reverse().Select(card => new DisplayHeroDto(card.Title, card.Subtitle, "Featured Content", card.Artwork, card.Progress, card.Actions) { Id = card.Id, Facts = card.Facts }).ToList() };

        var mapped = MediaTileComposerService.FromDisplayPage(page);

        Assert.Equal(5, mapped.Spotlights.Count);
        Assert.Equal(["Movie 5", "Movie 4", "Movie 3", "Movie 2", "Movie 1"], mapped.Spotlights.Select(slide => slide.Title));
        Assert.Equal(["Fact 4"], mapped.Spotlights[1].MetaPills);
    }

    private static DisplayArtworkDto EmptyArtwork(string? accentColor) =>
        new(
            CoverUrl: null,
            CoverSmallUrl: null,
            CoverMediumUrl: null,
            CoverLargeUrl: null,
            SquareUrl: null,
            SquareSmallUrl: null,
            SquareMediumUrl: null,
            SquareLargeUrl: null,
            BannerUrl: null,
            BannerSmallUrl: null,
            BannerMediumUrl: null,
            BannerLargeUrl: null,
            BackgroundUrl: null,
            BackgroundSmallUrl: null,
            BackgroundMediumUrl: null,
            BackgroundLargeUrl: null,
            LogoUrl: null,
            CoverWidthPx: null,
            CoverHeightPx: null,
            SquareWidthPx: null,
            SquareHeightPx: null,
            BannerWidthPx: null,
            BannerHeightPx: null,
            BackgroundWidthPx: null,
            BackgroundHeightPx: null,
            AccentColor: accentColor);

    private static WorkViewModel CreateWork(
        Guid id,
        string mediaType,
        string title,
        string creator,
        string year,
        Guid? collectionId = null,
        DateTimeOffset? createdAt = null,
        string? coverUrl = null,
        string? backgroundUrl = null,
        string? bannerUrl = null,
        string? logoUrl = null,
        IReadOnlyDictionary<string, string>? canonicalExtras = null)
    {
        var canonicalValues = new List<CanonicalValueViewModel>
        {
            CreateCanonical("title", title),
            CreateCanonical("author", creator),
            CreateCanonical("year", year),
            CreateCanonical("release_year", year),
        };

        if (!string.IsNullOrWhiteSpace(coverUrl))
        {
            canonicalValues.Add(CreateCanonical("cover", coverUrl));
        }

        if (!string.IsNullOrWhiteSpace(backgroundUrl))
        {
            canonicalValues.Add(CreateCanonical("background", backgroundUrl));
        }

        if (!string.IsNullOrWhiteSpace(bannerUrl))
        {
            canonicalValues.Add(CreateCanonical("banner", bannerUrl));
        }

        if (!string.IsNullOrWhiteSpace(logoUrl))
        {
            canonicalValues.Add(CreateCanonical("logo", logoUrl));
        }

        if (canonicalExtras is not null)
        {
            foreach (var entry in canonicalExtras)
            {
                canonicalValues.Add(CreateCanonical(entry.Key, entry.Value));
            }
        }

        return new WorkViewModel
        {
            Id = id,
            CollectionId = collectionId,
            MediaType = mediaType,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            CanonicalValues = canonicalValues,
            ResolvedCoverUrl = coverUrl,
            ResolvedBackgroundUrl = backgroundUrl,
            ResolvedBannerUrl = bannerUrl,
            ResolvedLogoUrl = logoUrl,
        };
    }

    private static JourneyItemViewModel CreateJourneyItem(
        Guid workId,
        Guid assetId,
        string mediaType,
        string title,
        string? author,
        DateTimeOffset lastAccessed,
        Guid? collectionId = null,
        string? collectionDisplayName = null,
        string? coverUrl = null,
        string? backgroundUrl = null,
        string? bannerUrl = null,
        string? heroUrl = null,
        string? logoUrl = null,
        IReadOnlyDictionary<string, string>? extendedProperties = null) =>
        new()
        {
            WorkId = workId,
            AssetId = assetId,
            MediaType = mediaType,
            Title = title,
            Author = author,
            CollectionId = collectionId,
            CollectionDisplayName = collectionDisplayName,
            LastAccessed = lastAccessed,
            CoverUrl = coverUrl,
            BackgroundUrl = backgroundUrl,
            BannerUrl = bannerUrl,
            HeroUrl = heroUrl,
            LogoUrl = logoUrl,
            ProgressPct = 42,
            ExtendedProperties = extendedProperties?.ToDictionary(pair => pair.Key, pair => pair.Value) ?? [],
        };

    private static CanonicalValueViewModel CreateCanonical(string key, string value) =>
        new()
        {
            Key = key,
            Value = value,
            LastScoredAt = DateTimeOffset.UtcNow,
        };
}
