using MediaEngine.Api.Services.Display;

namespace MediaEngine.Api.Tests;

public sealed class DisplayCardBuilderSeriesPreviewTests
{
    [Fact]
    public void BuildCollectionCards_UsesOrderedPreviewItemsForReadSeries()
    {
        var collectionId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var createdAt = DateTimeOffset.Parse("2026-06-01T12:00:00Z");
        var works = new[]
        {
            CreateWork(collectionId, "Book", "Book Five", "5", "/covers/5-s.jpg", createdAt.AddMinutes(5)),
            CreateWork(collectionId, "Book", "Book Two", "2", "/covers/2-s.jpg", createdAt.AddMinutes(2)),
            CreateWork(collectionId, "Book", "Book Four", "4", "/covers/4-s.jpg", createdAt.AddMinutes(4)),
            CreateWork(collectionId, "Book", "Book One", "1", "/covers/1-s.jpg", createdAt.AddMinutes(1)),
            CreateWork(collectionId, "Book", "Book Three", "3", "/covers/3-s.jpg", createdAt.AddMinutes(3)),
        };
        works[3].Description = "The first book in the sequence.";
        works[3].Author = "Example Author";
        works[3].Year = "1951";
        works[3].PageCount = "255";
        works[3].Rating = "4.4";
        foreach (var work in works)
        {
            work.CollectionDescription = "A science-fiction series about the fall and rebuilding of a galactic civilization.";
            work.CollectionType = "Series";
            work.CollectionManifestTotalCount = 7;
        }

        var card = new DisplayCardBuilder()
            .BuildCollectionCards(works, "read", minimumSeriesItems: 2)
            .Single();

        Assert.Equal("bookSeries", card.Presentation);
        Assert.Equal("5 owned titles", card.Subtitle);
        Assert.Equal(["5 owned titles"], card.Facts);
        Assert.Equal(5, card.PreviewTotalCount);
        Assert.Equal(["Book One", "Book Two", "Book Three", "Book Four"], card.PreviewItems.Select(item => item.Title));
        Assert.Equal(["1", "2", "3", "4"], card.PreviewItems.Select(item => item.Position));
        Assert.Equal(["/covers/1-s.jpg", "/covers/2-s.jpg", "/covers/3-s.jpg", "/covers/4-s.jpg"], card.PreviewItems.Select(item => item.ImageUrl));
        Assert.All(card.PreviewItems, item => Assert.Equal("Book", item.MediaType));
        Assert.All(card.PreviewItems, item => Assert.StartsWith("/details/work/", item.WebUrl, StringComparison.Ordinal));
        Assert.Equal("The first book in the sequence.", card.PreviewItems[0].Description);
        Assert.Equal("A science-fiction series about the fall and rebuilding of a galactic civilization.", card.Description);
        Assert.Equal(5, card.GroupSummary?.OwnedCount);
        Assert.Equal(7, card.GroupSummary?.KnownTotalCount);
        Assert.Equal("Books 1\u20135 owned", card.GroupSummary?.SequenceRange);
        Assert.Equal("Ordered series", card.GroupSummary?.RelationshipLabel);
        Assert.Equal("Book", Assert.Single(card.GroupSummary!.MediaCounts).MediaType);
        Assert.Equal(["Example Author", "1951", "255 pages", "★ 4.4"], card.PreviewItems[0].Facts);
    }

    [Fact]
    public void BuildCollectionCards_PrioritizesCurrentNextAndRecentlyCompletedMembers()
    {
        var collectionId = Guid.Parse("12111111-2222-3333-4444-555555555555");
        var createdAt = DateTimeOffset.Parse("2026-06-01T12:00:00Z");
        var works = Enumerable.Range(1, 6)
            .Select(position => CreateWork(
                collectionId,
                "Book",
                $"Book {position}",
                position.ToString(),
                $"/covers/{position}-s.jpg",
                createdAt.AddMinutes(position)))
            .ToArray();
        var progressByWork = new Dictionary<Guid, DisplayJourneyRow>
        {
            [works[2].WorkId] = new() { WorkId = works[2].WorkId, ProgressPct = 42, LastAccessed = createdAt.AddDays(3) },
            [works[1].WorkId] = new() { WorkId = works[1].WorkId, ProgressPct = 100, LastAccessed = createdAt.AddDays(2) },
        };

        var card = new DisplayCardBuilder()
            .BuildCollectionCards(works, "read", progressByWork: progressByWork)
            .Single();

        Assert.Equal(["Book 3", "Book 4", "Book 1", "Book 2"], card.PreviewItems.Select(item => item.Title));
        Assert.Equal(1, card.GroupSummary?.CompletedCount);
        Assert.Equal(1, card.GroupSummary?.InProgressCount);
        Assert.Equal("Books 1\u20136 owned", card.GroupSummary?.SequenceRange);
    }

    [Fact]
    public void BuildCollectionCards_KeepsStructuralRepresentativesWhenArtworkIsMissing()
    {
        var collectionId = Guid.Parse("13111111-2222-3333-4444-555555555555");
        var createdAt = DateTimeOffset.Parse("2026-06-01T12:00:00Z");
        var works = new[]
        {
            CreateWork(collectionId, "Book", "Book One", "1", string.Empty, createdAt),
            CreateWork(collectionId, "Book", "Book Two", "2", string.Empty, createdAt.AddMinutes(1)),
        };

        var card = new DisplayCardBuilder().BuildCollectionCards(works, "read").Single();

        Assert.Equal(["Book One", "Book Two"], card.PreviewItems.Select(item => item.Title));
        Assert.All(card.PreviewItems, item => Assert.Equal(string.Empty, item.ImageUrl));
        Assert.Equal(2, card.PreviewTotalCount);
    }

    [Fact]
    public void BuildCollectionCards_DoesNotPresentComicRunAsCompletionTarget()
    {
        var collectionId = Guid.Parse("44444444-2222-3333-4444-555555555555");
        var createdAt = DateTimeOffset.Parse("2026-06-01T12:00:00Z");
        var works = new[]
        {
            CreateWork(collectionId, "Comic", "The Sandman: Sleep of the Just", "1", "/covers/sandman-1.jpg", createdAt.AddMinutes(1), manifestTotalCount: 75),
            CreateWork(collectionId, "Comic", "The Sandman: Imperfect Hosts", "2", "/covers/sandman-2.jpg", createdAt.AddMinutes(2), manifestTotalCount: 75),
        };

        var card = new DisplayCardBuilder()
            .BuildCollectionCards(works, "read", minimumSeriesItems: 2)
            .Single();

        Assert.Equal("comicSeries", card.Presentation);
        Assert.Equal("2 owned issues", card.Subtitle);
        Assert.Equal(["2 owned issues"], card.Facts);
        Assert.Equal(2, card.PreviewTotalCount);
        Assert.Null(card.GroupSummary?.KnownTotalCount);
        Assert.Equal("Issues 1\u20132 owned", card.GroupSummary?.SequenceRange);
    }

    [Fact]
    public void BuildCollectionCards_UsesOwnedMovieCountInsteadOfManifestTotal()
    {
        var collectionId = Guid.Parse("55555555-2222-3333-4444-555555555555");
        var createdAt = DateTimeOffset.Parse("2026-06-01T12:00:00Z");
        var works = new[]
        {
            CreateWork(collectionId, "Movie", "Harry Potter and the Philosopher's Stone", "1", "/covers/hp-1.jpg", createdAt, manifestTotalCount: 8),
            CreateWork(collectionId, "Movie", "Harry Potter and the Chamber of Secrets", "2", "/covers/hp-2.jpg", createdAt.AddMinutes(1), manifestTotalCount: 8),
        };

        var card = new DisplayCardBuilder()
            .BuildMovieSeriesCards(works, minimumSeriesItems: 2)
            .Single();

        Assert.Equal("2 owned titles", card.Subtitle);
        Assert.Equal(["2 owned titles"], card.Facts);
        Assert.Equal(2, card.PreviewTotalCount);
        Assert.Equal(8, card.GroupSummary?.KnownTotalCount);
    }

    [Fact]
    public void BuildMovieSeriesCards_DoesNotPromoteSingleMovieAsSeries()
    {
        var collectionId = Guid.Parse("22222222-2222-3333-4444-555555555555");
        var works = new[]
        {
            CreateWork(collectionId, "Movie", "The Matrix", "1", "/covers/matrix-s.jpg", DateTimeOffset.Parse("2026-06-01T12:00:00Z")),
        };

        var cards = new DisplayCardBuilder().BuildMovieSeriesCards(works, minimumSeriesItems: 2);

        Assert.Empty(cards);
    }

    [Fact]
    public void BuildCollectionCards_OrdersAlbumPreviewsByTrackNumberAndTargetsTrack()
    {
        var collectionId = Guid.Parse("66666666-2222-3333-4444-555555555555");
        var createdAt = DateTimeOffset.Parse("2026-06-01T12:00:00Z");
        var works = new[]
        {
            CreateMusicTrack(collectionId, "Third Track", "3", "/covers/album.jpg", createdAt.AddMinutes(3)),
            CreateMusicTrack(collectionId, "First Track", "1", "/covers/album.jpg", createdAt.AddMinutes(1)),
            CreateMusicTrack(collectionId, "Second Track", "2", "/covers/album.jpg", createdAt.AddMinutes(2)),
        };
        var albumRootWorkId = Guid.NewGuid();
        foreach (var work in works)
        {
            work.RootWorkId = albumRootWorkId;
        }

        var card = new DisplayCardBuilder()
            .BuildCollectionCards(works, "listen", minimumSeriesItems: 2)
            .Single();

        Assert.Equal("album", card.Presentation);
        Assert.Equal(["First Track", "Second Track", "Third Track"], card.PreviewItems.Select(item => item.Title));
        Assert.Equal(["1", "2", "3"], card.PreviewItems.Select(item => item.Position));
        Assert.Equal(
            works.OrderBy(work => work.TrackNumber).Select(work => $"/listen/music?browse=songs&track={work.WorkId:D}"),
            card.PreviewItems.Select(item => item.WebUrl));
        Assert.Equal($"/details/musicalbum/{albumRootWorkId:D}?context=listen", card.Actions[0].WebUrl);
    }

    [Fact]
    public void BuildTvShowCards_UsesRootShowArtworkWithOwnedEpisodePreview()
    {
        var rootWorkId = Guid.Parse("33333333-2222-3333-4444-555555555555");
        var works = new[]
        {
            CreateTvEpisode(rootWorkId, "Foundation", "1", "1", "/episodes/1.jpg", DateTimeOffset.Parse("2026-06-01T12:00:00Z")),
            CreateTvEpisode(rootWorkId, "Foundation", "1", "2", "/episodes/2.jpg", DateTimeOffset.Parse("2026-06-02T12:00:00Z")),
        };

        var card = new DisplayCardBuilder().BuildTvShowCards(works).Single();

        Assert.Equal("tvSeries", card.Presentation);
        Assert.Equal("Foundation", card.Title);
        Assert.Equal("/shows/foundation-bg-s.jpg", card.Artwork.BackgroundSmallUrl);
        Assert.Equal(2, card.PreviewItems.Count);
        Assert.Equal(["S1 E1", "S1 E2"], card.PreviewItems.Select(item => item.Position));
        Assert.Equal(["wide", "wide"], card.PreviewItems.Select(item => item.Shape));
        Assert.Equal(
            works.Select(work => $"/details/work/{work.WorkId:D}?context=watch"),
            card.PreviewItems.Select(item => item.WebUrl));
        Assert.Equal(2, card.PreviewTotalCount);
    }

    [Fact]
    public void FromJourney_TvEpisodeKeepsEpisodeStillAndSeparatesResumeFromDetails()
    {
        var showId = Guid.Parse("aaaaaaaa-2222-3333-4444-555555555555");
        var episodeId = Guid.Parse("bbbbbbbb-2222-3333-4444-555555555555");
        var assetId = Guid.Parse("cccccccc-2222-3333-4444-555555555555");
        var row = new DisplayJourneyRow
        {
            RootWorkId = showId,
            WorkId = episodeId,
            AssetId = assetId,
            MediaType = "TV",
            ProgressPct = 37,
            LastAccessed = DateTimeOffset.Parse("2026-07-14T12:00:00Z"),
            Title = "The One with the Test",
            ShowName = "Example Show",
            SeasonNumber = "01",
            EpisodeNumber = "03",
            BackgroundSmallUrl = "/episodes/s01e03-s.jpg",
            BackgroundMediumUrl = "/episodes/s01e03-m.jpg",
            BackgroundLargeUrl = "/episodes/s01e03-l.jpg",
        };

        var card = new DisplayCardBuilder().FromJourney(row, "watch");

        Assert.Equal(episodeId, card.Id);
        Assert.Equal(episodeId, card.WorkId);
        Assert.False(card.Flags.IsCollection);
        Assert.Equal("Continue · S1 E3", card.Subtitle);
        Assert.Equal("/episodes/s01e03-s.jpg", card.Artwork.BackgroundSmallUrl);
        Assert.Equal("Resume S1 E3", card.Actions[0].Label);
        Assert.Equal($"/watch/player/{episodeId:D}", card.Actions[0].WebUrl);
        Assert.Equal("Details", card.Actions[1].Label);
        Assert.Equal(MediaEngine.Contracts.Details.TvEpisodeDetailRoute.Build(showId, episodeId), card.Actions[1].WebUrl);
        Assert.Equal(card.Actions[0], card.Progress?.ResumeAction);
    }

    [Fact]
    public void FromWork_MapsCanonicalYearForTimelineGrouping()
    {
        var row = new DisplayWorkRow
        {
            WorkId = Guid.NewGuid(),
            AssetId = Guid.NewGuid(),
            RootWorkId = Guid.NewGuid(),
            MediaType = "Books",
            Title = "Dune",
            Author = "Frank Herbert",
            Year = "1965",
            CreatedAt = DateTimeOffset.Parse("2026-07-14T12:00:00Z"),
        };

        var card = new DisplayCardBuilder().FromWork(row, "read", progress: null);

        Assert.Equal(1965, card.SortYear);
        Assert.Equal($"/stream/{row.AssetId:D}/cover", card.Artwork.CoverUrl);
    }

    [Fact]
    public void FromWork_MusicUsesDirectSongPlaybackRoute()
    {
        var row = new DisplayWorkRow
        {
            WorkId = Guid.NewGuid(),
            AssetId = Guid.NewGuid(),
            RootWorkId = Guid.NewGuid(),
            MediaType = "Music",
            Title = "Listen item",
            CreatedAt = DateTimeOffset.Parse("2026-07-14T12:00:00Z"),
        };

        var card = new DisplayCardBuilder().FromWork(row, "listen", progress: null);

        Assert.Single(card.Actions);
        Assert.Equal($"/listen/music?browse=songs&track={row.WorkId:D}", card.Actions[0].WebUrl);
    }

    [Fact]
    public void FromWork_AudiobookUsesCanonicalWorkDetailRoute()
    {
        var row = new DisplayWorkRow
        {
            WorkId = Guid.NewGuid(),
            AssetId = Guid.NewGuid(),
            RootWorkId = Guid.NewGuid(),
            MediaType = "Audiobook",
            Title = "Listen item",
            CreatedAt = DateTimeOffset.Parse("2026-07-14T12:00:00Z"),
        };

        var card = new DisplayCardBuilder().FromWork(row, "listen", progress: null);

        Assert.All(card.Actions, action =>
            Assert.Equal($"/details/work/{row.WorkId:D}?context=listen", action.WebUrl));
    }

    [Fact]
    public void BuildCollectionCards_UsesCanonicalAudiobookSeriesDetailRoute()
    {
        var collectionId = Guid.NewGuid();
        var createdAt = DateTimeOffset.Parse("2026-07-14T12:00:00Z");
        var works = new[]
        {
            CreateWork(collectionId, "Audiobook", "Volume One", "1", "/covers/one.jpg", createdAt),
            CreateWork(collectionId, "Audiobook", "Volume Two", "2", "/covers/two.jpg", createdAt.AddDays(1)),
        };

        var card = new DisplayCardBuilder().BuildCollectionCards(works, "listen").Single();

        Assert.Equal("audiobookSeries", card.Presentation);
        Assert.Equal($"/details/collection/{collectionId:D}?context=listen", card.Actions[0].WebUrl);
        Assert.All(card.PreviewItems, item =>
            Assert.Equal($"/details/work/{item.WorkId:D}?context=listen", item.WebUrl));
    }

    private static DisplayWorkRow CreateWork(
        Guid collectionId,
        string mediaType,
        string title,
        string position,
        string coverSmallUrl,
        DateTimeOffset createdAt,
        int manifestTotalCount = 0) =>
        new()
        {
            WorkId = Guid.NewGuid(),
            AssetId = Guid.NewGuid(),
            CollectionId = collectionId,
            MediaType = mediaType,
            CreatedAt = createdAt,
            Title = title,
            Series = "Foundation Series",
            SeriesPosition = position,
            CollectionTitle = "Foundation Series",
            CollectionManifestTotalCount = manifestTotalCount,
            CoverSmallUrl = coverSmallUrl,
            CoverWidthPx = "1000",
            CoverHeightPx = "1500",
        };

    private static DisplayWorkRow CreateTvEpisode(
        Guid rootWorkId,
        string showName,
        string season,
        string episode,
        string episodeBackgroundUrl,
        DateTimeOffset createdAt) =>
        new()
        {
            WorkId = Guid.NewGuid(),
            AssetId = Guid.NewGuid(),
            RootWorkId = rootWorkId,
            MediaType = "TV",
            CreatedAt = createdAt,
            Title = $"{showName} S{season}E{episode}",
            ShowName = showName,
            SeasonNumber = season,
            EpisodeNumber = episode,
            BackgroundSmallUrl = episodeBackgroundUrl,
            RootBackgroundUrl = "/shows/foundation-bg.jpg",
            RootBackgroundSmallUrl = "/shows/foundation-bg-s.jpg",
            RootBackgroundWidthPx = "1920",
            RootBackgroundHeightPx = "1080",
        };

    private static DisplayWorkRow CreateMusicTrack(
        Guid collectionId,
        string title,
        string trackNumber,
        string coverSmallUrl,
        DateTimeOffset createdAt) =>
        new()
        {
            WorkId = Guid.NewGuid(),
            AssetId = Guid.NewGuid(),
            CollectionId = collectionId,
            MediaType = "Music",
            CreatedAt = createdAt,
            Title = title,
            Album = "Test Album",
            CollectionTitle = "Test Album",
            TrackNumber = trackNumber,
            SquareSmallUrl = coverSmallUrl,
        };
}
