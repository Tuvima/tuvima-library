using MediaEngine.Api.Services.Display;

namespace MediaEngine.Api.Tests;

public sealed class DisplayComposerServiceTests
{
    [Fact]
    public async Task Timeline_IndexCoversFilteredResultsBeyondPageAndSupportsPeriodOffsets()
    {
        var works = Enumerable.Range(0, 180).Select(i => Work(Guid.NewGuid(), "Book", $"Book {i}", year: i < 90 ? "2024" : "1995", genre: "History")).ToList();
        works.Add(Work(Guid.NewGuid(), "Book", "Unknown", genre: "History"));
        works.Add(Work(Guid.NewGuid(), "Book", "Excluded", year: "1800", genre: "Fantasy"));
        var service = CreateComposer(new StubDisplayProjectionRepository(works, []));
        var page = await service.BuildBrowseAsync("read", "Books", "timeline", null, 0, 72, genres: "History", sort: "newest");
        Assert.Equal(181, page.TotalCount);
        Assert.Equal(72, page.Catalog.Count);
        Assert.Equal(new[] { 2024, 1995, 0 }, page.Timeline!.Select(p => p.Year));
        Assert.Equal(new[] { 0, 90, 180 }, page.Timeline!.Select(p => p.Offset));
        var older = await service.BuildBrowseAsync("read", "Books", "timeline", null, page.Timeline![1].Offset, 72, genres: "History", sort: "newest");
        Assert.All(older.Catalog, card => Assert.Equal(1995, card.SortYear));
        var ascending = await service.BuildBrowseAsync("read", "Books", "timeline", null, 0, 72, genres: "History", sort: "oldest");
        Assert.Equal(new[] { 1995, 2024, 0 }, ascending.Timeline!.Select(p => p.Year));
    }

    [Fact]
    public async Task Timeline_CountsAlbumsAndShowsRatherThanTracksAndEpisodes()
    {
        var album = Guid.NewGuid(); var show = Guid.NewGuid();
        var service = CreateComposer(new StubDisplayProjectionRepository([
            Work(Guid.NewGuid(), "Music", "Track one", artist: "Artist", album: "Album", year: "1998", rootWorkId: album),
            Work(Guid.NewGuid(), "Music", "Track two", artist: "Artist", album: "Album", year: "1998", rootWorkId: album),
            Work(Guid.NewGuid(), "TV", "Pilot", showName: "Show", year: "2001", rootWorkId: show, season: "1", episode: "1"),
            Work(Guid.NewGuid(), "TV", "Episode two", showName: "Show", year: "2001", rootWorkId: show, season: "1", episode: "2")], []));
        var music = await service.BuildBrowseAsync("listen", "Music", "timeline", null, 0, 72);
        Assert.Equal(1, music.TotalCount);
        Assert.Equal(1998, Assert.Single(music.Timeline!).Year);
        var tv = await service.BuildBrowseAsync("watch", "TV", "timeline", null, 0, 72);
        Assert.Equal(1, tv.TotalCount);
        Assert.Equal(2001, Assert.Single(tv.Timeline!).Year);
    }
    [Fact]
    public async Task MusicBrowse_ReturnsStructuredSongMetadataArtworkAndServerSort()
    {
        var second = Work(
            Guid.NewGuid(),
            "Music",
            "Zenith",
            artist: "Example Artist",
            album: "Example Album",
            year: "2025",
            genre: "Ambient",
            track: "2",
            duration: "4:12");
        second.SquareUrl = "/stream/example/square";
        var first = Work(
            Guid.NewGuid(),
            "Music",
            "Arrival",
            artist: "Example Artist",
            album: "Example Album",
            year: "2025",
            genre: "Ambient",
            track: "1",
            duration: "3:48");
        first.SquareUrl = "/stream/example/square";
        var composer = CreateComposer(new StubDisplayProjectionRepository([second, first], []));

        var page = await composer.BuildBrowseAsync(
            "listen",
            "Music",
            "songs",
            null,
            0,
            48,
            sort: "title");

        Assert.Equal(["Arrival", "Zenith"], page.Catalog.Select(card => card.Title));
        var card = page.Catalog[0];
        Assert.Equal("/stream/example/square", card.Artwork.SquareUrl);
        Assert.NotNull(card.ListMetadata);
        Assert.Equal("Example Artist", card.ListMetadata.Artist);
        Assert.Equal("Example Album", card.ListMetadata.Album);
        Assert.Equal("Ambient", card.ListMetadata.Genre);
        Assert.Equal("3:48", card.ListMetadata.Duration);
        Assert.Equal("1", card.ListMetadata.TrackNumber);
        Assert.Equal("2025", card.ListMetadata.Year);
    }

    [Fact]
    public async Task Browse_AppliesUrlFiltersBeforePagingAndReturnsFullScopeFacets()
    {
        var matchingId = Guid.NewGuid();
        var repository = new StubDisplayProjectionRepository(
            [
                Work(matchingId, "Book", "The Lantern", author: "A. Writer", year: "2024", genre: "Fantasy; Mystery"),
                Work(Guid.NewGuid(), "Book", "A History", author: "B. Writer", year: "2022", genre: "History"),
                Work(Guid.NewGuid(), "Book", "Another Fantasy", author: "C. Writer", year: "2024", genre: "Fantasy"),
            ],
            [Journey(matchingId, "Book", "The Lantern", progressPct: 40, author: "A. Writer", year: "2024", genre: "Fantasy; Mystery")]);
        var composer = CreateComposer(repository);

        var page = await composer.BuildBrowseAsync(
            "read",
            "Books",
            "all",
            null,
            0,
            48,
            ct: default,
            genres: "Fantasy",
            creator: "A. Writer",
            status: "in-progress",
            year: "2024");

        var card = Assert.Single(page.Catalog);
        Assert.Equal(matchingId, card.WorkId);
        Assert.Equal(1, page.TotalCount);
        Assert.NotNull(page.Facets);
        Assert.Contains("Fantasy", page.Facets.Genres);
        Assert.Contains("History", page.Facets.Genres);
        Assert.Contains("A. Writer", page.Facets.Creators);
        Assert.Contains("2022", page.Facets.Years);
    }

    [Fact]
    public async Task Browse_MatchesAnySelectedCreatorAndYear()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var repository = new StubDisplayProjectionRepository(
            [
                Work(first, "Book", "First", author: "A. Writer", year: "2024"),
                Work(second, "Book", "Second", author: "B. Writer", year: "2022"),
                Work(Guid.NewGuid(), "Book", "Third", author: "C. Writer", year: "2020"),
            ],
            []);
        var composer = CreateComposer(repository);

        var page = await composer.BuildBrowseAsync(
            "read",
            "Books",
            "all",
            null,
            0,
            48,
            ct: default,
            creator: "A. Writer,B. Writer",
            year: "2024,2022");

        Assert.Equal(2, page.TotalCount);
        Assert.Contains(page.Catalog, card => card.WorkId == first);
        Assert.Contains(page.Catalog, card => card.WorkId == second);
        Assert.DoesNotContain(page.Catalog, card => card.Title == "Third");
    }

    [Fact]
    public async Task WatchLane_ComposesContinueMovieAndTvShelvesWithProgress()
    {
        var movieId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var tvId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var tvRootId = Guid.Parse("22222222-bbbb-2222-2222-222222222222");
        var tvCollectionId = Guid.Parse("22222222-aaaa-2222-2222-222222222222");
        var movieSeriesId = Guid.Parse("11111111-aaaa-1111-1111-111111111111");
        var repository = new StubDisplayProjectionRepository(
            [
                Work(movieId, "Movie", "Arrival", year: "2016", genre: "Science Fiction; Drama"),
                Work(Guid.Parse("11111111-bbbb-1111-1111-111111111111"), "Movie", "Dune", year: "2021", collectionId: movieSeriesId, series: "Dune", seriesPosition: "1"),
                Work(Guid.Parse("11111111-cccc-1111-1111-111111111111"), "Movie", "Dune: Part Two", year: "2024", collectionId: movieSeriesId, series: "Dune", seriesPosition: "2"),
                Work(tvId, "TV", "Pilot", year: "2008", genre: "Crime", season: "1", episode: "1", collectionId: tvCollectionId, showName: "Breaking Bad", rootWorkId: tvRootId),
            ],
            [
                Journey(movieId, "Movie", "Arrival", progressPct: 42, year: "2016", genre: "Science Fiction; Drama"),
            ]);
        var composer = CreateComposer(repository);

        var page = await composer.BuildBrowseAsync("watch", null, "all", null, 0, 48);

        Assert.Equal("watch", page.Key);
        Assert.Contains(page.Shelves, shelf => shelf.Key == "continue-watching");
        Assert.Contains(page.Shelves, shelf => shelf.Key == "tv-shows");
        Assert.Contains(page.Shelves, shelf => shelf.Key == "series");
        Assert.Contains(page.Shelves, shelf => shelf.Key == "movies");
        Assert.Equal("/watch/movies", page.Shelves.Single(shelf => shelf.Key == "movies").SeeAllRoute);
        Assert.Equal("/watch/tv", page.Shelves.Single(shelf => shelf.Key == "tv-shows").SeeAllRoute);
        Assert.Equal("/watch/movies?grouping=series", page.Shelves.Single(shelf => shelf.Key == "series").SeeAllRoute);

        var continueCard = page.Shelves.Single(shelf => shelf.Key == "continue-watching").Items.Single();
        Assert.Equal(42, continueCard.Progress?.Percent);
        Assert.Equal("Continue Watching", continueCard.Actions[0].Label);
        Assert.Equal(["2016"], continueCard.Facts);

        var showCard = page.Shelves.Single(shelf => shelf.Key == "tv-shows").Items.Single();
        Assert.Equal("Breaking Bad", showCard.Title);
        Assert.Equal("1 season", showCard.Subtitle);
        Assert.Equal("Open Show", showCard.Actions[0].Label);
        Assert.Equal($"/details/tvshow/{tvRootId:D}?context=watch", showCard.Actions[0].WebUrl);

        var seriesCard = page.Shelves.Single(shelf => shelf.Key == "series").Items.Single();
        Assert.Equal("Dune", seriesCard.Title);
        Assert.Equal("movieSeries", seriesCard.Presentation);
        Assert.Equal(2021, seriesCard.GroupSummary?.EarliestYear);
        Assert.Equal(2024, seriesCard.GroupSummary?.LatestYear);
        Assert.Equal("Movie", Assert.Single(seriesCard.GroupSummary!.MediaCounts).MediaType);
        Assert.Equal(2, seriesCard.GroupSummary.MediaCounts[0].Count);
        Assert.DoesNotContain(page.Shelves.Single(shelf => shelf.Key == "series").Items, card => card.MediaType == "TV");
    }

    [Fact]
    public async Task ReadLane_ComposesBooksAndComicsWithoutAudiobooks()
    {
        var bookId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var comicId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var audiobookId = Guid.Parse("44444444-5555-5555-5555-444444444444");
        var repository = new StubDisplayProjectionRepository(
            [
                Work(bookId, "Book", "Dune", author: "Frank Herbert", genre: "Science Fiction;Adventure"),
                Work(comicId, "Comic", "Saga", author: "Brian K. Vaughan", genre: "Space Opera"),
                Work(audiobookId, "Audiobook", "Project Hail Mary", author: "Andy Weir", narrator: "Ray Porter", genre: "Science Fiction"),
            ],
            [
                Journey(bookId, "Book", "Dune", progressPct: 31, author: "Frank Herbert", genre: "Science Fiction;Adventure"),
            ]);
        var composer = CreateComposer(repository);

        var page = await composer.BuildBrowseAsync("read", null, "all", null, 0, 48);

        Assert.Contains(page.Shelves, shelf => shelf.Key == "continue-reading");
        Assert.Contains(page.Shelves, shelf => shelf.Key == "recently-added");
        Assert.Equal("/read/books", page.Shelves.Single(shelf => shelf.Key == "continue-reading").SeeAllRoute);
        Assert.Equal("/read/books", page.Shelves.Single(shelf => shelf.Key == "recently-added").SeeAllRoute);

        var continueCard = page.Shelves.Single(shelf => shelf.Key == "continue-reading").Items.Single();
        Assert.Equal("smart", continueCard.PreviewPlacement);
        Assert.Equal("Continue Reading", continueCard.Actions[0].Label);
        Assert.Equal(["Frank Herbert"], continueCard.Facts);

        var catalogCard = page.Catalog.Single(card => card.Title == "Dune");
        Assert.Equal("smart", catalogCard.PreviewPlacement);
        Assert.Equal(31, catalogCard.Progress?.Percent);

        Assert.Equal("smart", page.Catalog.Single(card => card.Title == "Saga").PreviewPlacement);
        Assert.DoesNotContain(page.Catalog, card => card.Title == "Project Hail Mary");
    }

    [Fact]
    public async Task ReadLane_ExcludesAudiobookVariantsWithSameQid()
    {
        var bookId = Guid.Parse("33333333-aaaa-3333-3333-333333333333");
        var audiobookId = Guid.Parse("33333333-bbbb-3333-3333-333333333333");
        var repository = new StubDisplayProjectionRepository(
            [
                Work(bookId, "Book", "Harry Potter and the Philosopher's Stone", author: "J. K. Rowling", identityQid: "Q43361"),
                Work(audiobookId, "Audiobook", "Harry Potter and the Philosopher's Stone", author: "J. K. Rowling", narrator: "Jim Dale", identityQid: "Q43361"),
            ],
            []);
        var composer = CreateComposer(repository);

        var page = await composer.BuildBrowseAsync("read", null, "all", null, 0, 48);

        var catalogCard = Assert.Single(page.Catalog);
        Assert.Equal(bookId, catalogCard.WorkId);
        Assert.Equal("Harry Potter and the Philosopher's Stone", catalogCard.Title);

        var listenPage = await composer.BuildBrowseAsync("listen", null, "all", null, 0, 48);
        Assert.Contains(listenPage.Catalog, card => card.WorkId == audiobookId && card.MediaType == "Audiobook");
    }

    [Fact]
    public async Task ListenLane_ComposesMusicAudiobookAndContinueShelves()
    {
        var musicId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var audiobookId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var repository = new StubDisplayProjectionRepository(
            [
                Work(musicId, "Music", "Not Strong Enough", artist: "boygenius", album: "The Record", genre: "Indie Rock", track: "6"),
                Work(audiobookId, "Audiobook", "Project Hail Mary", author: "Andy Weir", narrator: "Ray Porter", genre: "Science Fiction"),
            ],
            [
                Journey(audiobookId, "Audiobook", "Project Hail Mary", progressPct: 58, author: "Andy Weir", narrator: "Ray Porter", genre: "Science Fiction"),
            ]);
        var composer = CreateComposer(repository);

        var page = await composer.BuildBrowseAsync("listen", null, "all", null, 0, 48);

        Assert.Contains(page.Shelves, shelf => shelf.Key == "continue-listening");
        Assert.Contains(page.Shelves, shelf => shelf.Key == "recently-updated-albums");
        Assert.DoesNotContain(page.Shelves, shelf => shelf.Key == "albums");
        Assert.DoesNotContain(page.Shelves, shelf => shelf.Key == "audiobooks");
        Assert.DoesNotContain(page.Shelves, shelf => shelf.Key == "listen-collections");

        var albumCard = page.Shelves.Single(shelf => shelf.Key == "recently-updated-albums").Items.Single();
        Assert.Equal("The Record", albumCard.Title);
        Assert.Equal("album", albumCard.Presentation);
        Assert.Equal("square", albumCard.PreferredShape);
        Assert.Single(albumCard.PreviewItems);
        Assert.StartsWith("/details/musicalbum/", albumCard.Actions[0].WebUrl, StringComparison.Ordinal);
        Assert.EndsWith("?context=listen", albumCard.Actions[0].WebUrl, StringComparison.Ordinal);
        Assert.All(albumCard.PreviewItems, item =>
        {
            Assert.StartsWith("/listen/music?browse=songs&track=", item.WebUrl, StringComparison.Ordinal);
        });
        Assert.DoesNotContain(page.Shelves, shelf => shelf.Items.Any(card => card.GroupingType == "item" && card.MediaType == "Music"));

        var audiobookCard = page.Shelves.Single(shelf => shelf.Key == "continue-listening").Items.Single();
        Assert.Equal(58, audiobookCard.Progress?.Percent);
        Assert.Equal("Continue Listening", audiobookCard.Actions[0].Label);
        Assert.All(audiobookCard.Actions, action =>
            Assert.Equal($"/details/work/{audiobookId:D}?context=listen", action.WebUrl));
        Assert.DoesNotContain(
            page.Shelves.Where(shelf => shelf.Key == "recently-added-audiobooks").SelectMany(shelf => shelf.Items),
            card => card.WorkId == audiobookId);
    }

    [Fact]
    public async Task ListenLane_DeduplicatesRecentlyPlayedAlbumsFromRecentlyUpdatedAlbums()
    {
        var playedWorkId = Guid.Parse("56565656-5656-5656-5656-565656565656");
        var updatedWorkId = Guid.Parse("57575757-5757-5757-5757-575757575757");
        var playedAlbumId = Guid.Parse("58585858-5858-5858-5858-585858585858");
        var updatedAlbumId = Guid.Parse("59595959-5959-5959-5959-595959595959");
        var repository = new StubDisplayProjectionRepository(
            [
                Work(playedWorkId, "Music", "Played Track", artist: "Played Artist", album: "Played Album", track: "1", collectionId: playedAlbumId),
                Work(updatedWorkId, "Music", "Updated Track", artist: "Updated Artist", album: "Updated Album", track: "1", collectionId: updatedAlbumId),
            ],
            [
                Journey(playedWorkId, "Music", "Played Track", progressPct: 100, artist: "Played Artist", album: "Played Album", track: "1", collectionId: playedAlbumId),
            ]);
        var composer = CreateComposer(repository);

        var page = await composer.BuildBrowseAsync("listen", null, "all", null, 0, 48);

        var recentlyPlayed = page.Shelves.Single(shelf => shelf.Key == "recently-played");
        var recentlyUpdated = page.Shelves.Single(shelf => shelf.Key == "recently-updated-albums");
        Assert.Contains(recentlyPlayed.Items, card => card.CollectionId == playedAlbumId);
        Assert.DoesNotContain(recentlyUpdated.Items, card => card.CollectionId == playedAlbumId);
        Assert.Contains(recentlyUpdated.Items, card => card.CollectionId == updatedAlbumId);
    }

    [Fact]
    public async Task BrowseMusic_FavoritesFilterReturnsOnlyProfileFavorites()
    {
        var favoriteId = Guid.Parse("60606060-6060-6060-6060-606060606060");
        var otherId = Guid.Parse("61616161-6161-6161-6161-616161616161");
        var profileId = Guid.Parse("62626262-6262-6262-6262-626262626262");
        var repository = new StubDisplayProjectionRepository(
            [
                Work(favoriteId, "Music", "Favorite Track", artist: "Artist", album: "Album"),
                Work(otherId, "Music", "Other Track", artist: "Artist", album: "Album"),
            ],
            [],
            new HashSet<Guid> { favoriteId });
        var composer = CreateComposer(repository);

        var page = await composer.BuildBrowseAsync(
            "listen",
            "Music",
            "songs",
            null,
            0,
            48,
            profileId: profileId,
            status: "favorite");

        var card = Assert.Single(page.Catalog);
        Assert.Equal(favoriteId, card.WorkId);
        Assert.True(card.Flags.IsFavorite);
    }

    [Fact]
    public async Task BrowseLane_CanOmitCatalogToAvoidDuplicatingShelfCards()
    {
        var movieId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var repository = new StubDisplayProjectionRepository(
            [Work(movieId, "Movie", "Arrival", year: "2016", genre: "Science Fiction")],
            []);
        var composer = CreateComposer(repository);

        var page = await composer.BuildBrowseAsync("watch", null, "all", null, 0, 48, includeCatalog: false);

        Assert.Empty(page.Catalog);
        Assert.NotEmpty(page.Shelves);
        Assert.Contains(page.Shelves, shelf => shelf.Items.Any(card => card.Title == "Arrival"));
    }

    [Fact]
    public async Task BrowseResults_UseCatalogOnlyPayloadByDefault()
    {
        var movieId = Guid.Parse("77777777-8888-8888-8888-777777777777");
        var repository = new StubDisplayProjectionRepository(
            [Work(movieId, "Movie", "Arrival", year: "2016", genre: "Science Fiction")],
            []);
        var composer = CreateComposer(repository);

        var page = await composer.BuildBrowseAsync("watch", "Movie", "all", null, 0, 48, includeCatalog: true);

        Assert.Empty(page.Shelves);
        Assert.Single(page.Catalog);
        Assert.Equal("Arrival", page.Catalog[0].Title);
    }

    [Fact]
    public async Task TvShowsBrowse_ReturnsShowCardsInsteadOfEpisodeCards()
    {
        var collectionId = Guid.Parse("77777777-9999-9999-9999-777777777777");
        var showRootId = Guid.Parse("77777777-cccc-9999-9999-777777777777");
        var firstEpisode = Guid.Parse("77777777-aaaa-9999-9999-777777777777");
        var secondEpisode = Guid.Parse("77777777-bbbb-9999-9999-777777777777");
        var repository = new StubDisplayProjectionRepository(
            [
                Work(firstEpisode, "TV", "Pilot", year: "2008", seriesEndYear: "2013", genre: "Crime", season: "1", episode: "1", collectionId: collectionId, showName: "Breaking Bad", rootWorkId: showRootId),
                Work(secondEpisode, "TV", "Cat's in the Bag...", year: "2008", seriesEndYear: "2013", genre: "Crime", season: "1", episode: "2", collectionId: collectionId, showName: "Breaking Bad", rootWorkId: showRootId),
            ],
            []);
        var composer = CreateComposer(repository);

        var page = await composer.BuildBrowseAsync("watch", "TV", "shows", null, 0, 48, includeCatalog: true);

        var card = Assert.Single(page.Catalog);
        Assert.Equal(showRootId, card.Id);
        Assert.Null(card.WorkId);
        Assert.Null(card.CollectionId);
        Assert.Equal("Breaking Bad", card.Title);
        Assert.Equal("1 season", card.Subtitle);
        Assert.Equal("tvSeries", card.Presentation);
        Assert.Contains("2008\u20132013", card.Facts);
        Assert.Contains("2 episodes", card.Facts);
        Assert.Contains("Crime", card.Facts);
        Assert.Equal(2008, card.GroupSummary?.EarliestYear);
        Assert.Equal(2013, card.GroupSummary?.LatestYear);
        Assert.Equal(2008, card.SortYear);
        Assert.Equal("Open Show", card.Actions[0].Label);
        Assert.Equal($"/details/tvshow/{showRootId:D}?context=watch", card.Actions[0].WebUrl);
        Assert.DoesNotContain(page.Catalog, item => item.Title == "Pilot");
    }

    [Fact]
    public async Task TvShowCards_UseShowArtworkInsteadOfEpisodeStill()
    {
        var collectionId = Guid.Parse("77777777-9999-9999-9999-888888888888");
        var showRootId = Guid.Parse("77777777-cccc-9999-9999-888888888888");
        var firstEpisode = Guid.Parse("77777777-aaaa-9999-9999-888888888888");
        var secondEpisode = Guid.Parse("77777777-bbbb-9999-9999-888888888888");
        var repository = new StubDisplayProjectionRepository(
            [
                Work(firstEpisode, "TV", "Pilot", season: "1", episode: "1", collectionId: collectionId, showName: "Severance", backgroundUrl: "/art/episode-pilot.jpg", rootBackgroundUrl: "/art/severance-show.jpg", rootWorkId: showRootId),
                Work(secondEpisode, "TV", "Half Loop", season: "1", episode: "2", collectionId: collectionId, showName: "Severance", backgroundUrl: "/art/episode-half-loop.jpg", rootBackgroundUrl: "/art/severance-show.jpg", rootWorkId: showRootId),
            ],
            []);
        var composer = CreateComposer(repository);

        var page = await composer.BuildBrowseAsync("watch", "TV", "shows", null, 0, 48, includeCatalog: true);

        var card = Assert.Single(page.Catalog);
        Assert.Equal("Severance", card.Title);
        Assert.Equal("/art/severance-show.jpg", card.Artwork.BackgroundUrl);
    }

    [Fact]
    public async Task TvShowCards_DoNotFallbackToEpisodeArtworkWhenShowArtworkIsMissing()
    {
        var showRootId = Guid.Parse("77777777-dddd-9999-9999-888888888888");
        var episodeId = Guid.Parse("77777777-eeee-9999-9999-888888888888");
        var repository = new StubDisplayProjectionRepository(
            [Work(episodeId, "TV", "Pilot", season: "1", episode: "1", showName: "Severance", coverUrl: "/art/episode-cover.jpg", backgroundUrl: "/art/episode-still.jpg", rootWorkId: showRootId)],
            []);
        var composer = CreateComposer(repository);

        var page = await composer.BuildBrowseAsync("watch", "TV", "shows", null, 0, 48, includeCatalog: true);

        var card = Assert.Single(page.Catalog);
        Assert.Equal("Severance", card.Title);
        Assert.Null(card.Artwork.CoverUrl);
        Assert.Null(card.Artwork.BackgroundUrl);
        Assert.Null(card.Artwork.BannerUrl);
    }

    [Fact]
    public async Task WatchLandingHero_UsesShowBackgroundWhileResumingOwnedEpisode()
    {
        var showRootId = Guid.Parse("77777777-ffff-9999-9999-888888888888");
        var episodeId = Guid.Parse("77777777-aaaa-8888-9999-888888888888");
        var repository = new StubDisplayProjectionRepository(
            [Work(episodeId, "TV", "Pilot", season: "1", episode: "1", showName: "Severance", backgroundUrl: "/art/episode-still.jpg", rootBackgroundUrl: "/art/severance-show.jpg", rootWorkId: showRootId)],
            [Journey(episodeId, "TV", "Pilot", 38, season: "1", episode: "1", showName: "Severance", rootWorkId: showRootId, backgroundUrl: "/art/episode-still.jpg")]);
        var composer = CreateComposer(repository);

        var page = await composer.BuildBrowseAsync("watch", null, "all", null, 0, 48, includeCatalog: false);

        Assert.NotNull(page.Hero);
        Assert.Equal("Severance", page.Hero.Title);
        Assert.Equal("tvSeries", page.Hero.Presentation);
        Assert.Equal("/art/severance-show.jpg", page.Hero.Artwork.BackgroundUrl);
        Assert.DoesNotContain("/art/episode-still.jpg", new[] { page.Hero.Artwork.BackgroundUrl, page.Hero.Artwork.BannerUrl });
        Assert.Equal("Resume S1 E1", page.Hero.Progress?.ResumeAction?.Label);
        Assert.Equal($"/watch/player/{episodeId:D}", page.Hero.Progress?.ResumeAction?.WebUrl);
    }

    [Fact]
    public async Task WatchLandingHero_UsesShowBackgroundWhenNothingIsInProgress()
    {
        var showRootId = Guid.Parse("77777777-bbbb-8888-9999-888888888888");
        var episodeId = Guid.Parse("77777777-cccc-8888-9999-888888888888");
        var repository = new StubDisplayProjectionRepository(
            [Work(episodeId, "TV", "Pilot", season: "1", episode: "1", showName: "Severance", backgroundUrl: "/art/episode-still.jpg", rootBackgroundUrl: "/art/severance-show.jpg", rootWorkId: showRootId)],
            []);
        var composer = CreateComposer(repository);

        var page = await composer.BuildBrowseAsync("watch", null, "all", null, 0, 48, includeCatalog: false);

        Assert.NotNull(page.Hero);
        Assert.Equal("Severance", page.Hero.Title);
        Assert.Equal("/art/severance-show.jpg", page.Hero.Artwork.BackgroundUrl);
    }

    [Fact]
    public async Task WatchShelves_KeepTvShowsSeparateAndHideSingleMovieSeries()
    {
        var movieCollectionId = Guid.Parse("77777777-9999-aaaa-9999-777777777777");
        var showCollectionId = Guid.Parse("77777777-9999-bbbb-9999-777777777777");
        var showRootId = Guid.Parse("77777777-9999-eeee-9999-777777777777");
        var movieId = Guid.Parse("77777777-1111-aaaa-9999-777777777777");
        var episodeId = Guid.Parse("77777777-2222-bbbb-9999-777777777777");
        var repository = new StubDisplayProjectionRepository(
            [
                Work(movieId, "Movie", "The Matrix", collectionId: movieCollectionId, series: "The Matrix series", collectionTitle: "The Matrix series"),
                Work(episodeId, "TV", "Pilot", season: "1", episode: "1", collectionId: showCollectionId, showName: "Severance", collectionTitle: "Severance", rootWorkId: showRootId),
            ],
            []);
        var composer = CreateComposer(repository);

        var page = await composer.BuildBrowseAsync("watch", null, "all", null, 0, 48, includeCatalog: false);

        var shows = page.Shelves.Single(shelf => shelf.Key == "tv-shows").Items;
        Assert.Contains(shows, card => card.Title == "Severance" && card.Presentation == "tvSeries");
        Assert.DoesNotContain(page.Shelves, shelf => shelf.Key == "series");
    }

    [Fact]
    public async Task WatchSeriesShelf_RemovesRedundantStructuralCollectionSuffix()
    {
        var collectionId = Guid.Parse("77777777-9999-abab-9999-777777777777");
        var repository = new StubDisplayProjectionRepository(
            [
                Work(Guid.Parse("77777777-1111-abab-9999-777777777777"), "Movie", "Dune", collectionId: collectionId, series: "Dune", seriesPosition: "1", collectionTitle: "Dune Collection"),
                Work(Guid.Parse("77777777-2222-abab-9999-777777777777"), "Movie", "Dune: Part Two", collectionId: collectionId, series: "Dune", seriesPosition: "2", collectionTitle: "Dune Collection"),
            ],
            []);
        var composer = CreateComposer(repository);

        var page = await composer.BuildBrowseAsync("watch", null, "all", null, 0, 48, includeCatalog: false);

        var seriesShelf = page.Shelves.Single(shelf => shelf.Key == "series");
        Assert.Equal("Series", seriesShelf.Title);
        Assert.Equal("Movies dynamically aligned into series from your library metadata", seriesShelf.Subtitle);
        Assert.Equal("/watch/movies?grouping=series", seriesShelf.SeeAllRoute);
        var card = Assert.Single(seriesShelf.Items);
        Assert.Equal("Dune", card.Title);
        Assert.Equal("movieSeries", card.Presentation);
    }

    [Fact]
    public void HomeCollections_PreserveCuratedCollectionNames()
    {
        var card = DisplayCardBuilder.FromHomeCollection(new DisplayHomeCollectionRow
        {
            CollectionId = Guid.NewGuid(),
            Title = "The Criterion Collection",
            ItemCount = 12,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        Assert.Equal("The Criterion Collection", card.Title);
    }

    [Fact]
    public void HomeCollections_ExposeMemberArtworkAndOwnedCount()
    {
        var workId = Guid.NewGuid();
        var card = DisplayCardBuilder.FromHomeCollection(new DisplayHomeCollectionRow
        {
            CollectionId = Guid.NewGuid(),
            Title = "Weekend Watchlist",
            CollectionType = "Playlist",
            ItemCount = 7,
            WatchCount = 4,
            ReadCount = 2,
            ListenCount = 1,
            EarliestYear = 1982,
            LatestYear = 2024,
            PreviewItems =
            [
                new MediaEngine.Contracts.Display.DisplayCardPreviewItemDto(workId, null, "Arrival", "/art/arrival.jpg", "portrait", null),
            ],
            CreatedAt = DateTimeOffset.UtcNow,
        });

        Assert.Equal(7, card.PreviewTotalCount);
        Assert.Equal(workId, Assert.Single(card.PreviewItems).WorkId);
        Assert.Equal("Curated list", card.GroupSummary?.RelationshipLabel);
        Assert.Equal(["Watch", "Read", "Listen"], card.GroupSummary?.MediaCounts.Select(count => count.MediaType));
        Assert.Equal(1982, card.GroupSummary?.EarliestYear);
        Assert.Equal(2024, card.GroupSummary?.LatestYear);
        Assert.Equal(2024, card.SortYear);
    }

    [Fact]
    public async Task WatchTvShowShelf_UsesTvRootInsteadOfSharedBookCollection()
    {
        var sharedCollectionId = Guid.Parse("77777777-9999-fafa-9999-777777777777");
        var showRootId = Guid.Parse("77777777-9999-fbfb-9999-777777777777");
        var repository = new StubDisplayProjectionRepository(
            [
                Work(Guid.Parse("77777777-1111-fafa-9999-777777777777"), "Book", "Leviathan Wakes", collectionId: sharedCollectionId, series: "The Expanse", collectionTitle: "The Expanse"),
                Work(Guid.Parse("77777777-2222-fafa-9999-777777777777"), "Book", "Caliban's War", collectionId: sharedCollectionId, series: "The Expanse", collectionTitle: "The Expanse"),
                Work(Guid.Parse("77777777-3333-fafa-9999-777777777777"), "TV", "Dulcinea", season: "1", episode: "1", collectionId: sharedCollectionId, showName: "The Expanse", collectionTitle: "The Expanse", rootBackgroundUrl: "/art/expanse-show.jpg", rootWorkId: showRootId),
            ],
            []);
        var composer = CreateComposer(repository);

        var page = await composer.BuildBrowseAsync("watch", null, "all", null, 0, 48, includeCatalog: false);

        var showShelf = page.Shelves.Single(shelf => shelf.Key == "tv-shows");
        Assert.Equal("TV Shows", showShelf.Title);
        Assert.Equal("Shows built from the episodes you own", showShelf.Subtitle);
        var card = Assert.Single(showShelf.Items, item => item.Title == "The Expanse");
        Assert.Equal("TV", card.MediaType);
        Assert.Equal("tvSeries", card.Presentation);
        Assert.Null(card.CollectionId);
        Assert.Equal("/art/expanse-show.jpg", card.Artwork.BackgroundUrl);
        Assert.Equal($"/details/tvshow/{showRootId:D}?context=watch", card.Actions[0].WebUrl);
        Assert.DoesNotContain("/details/bookseries", card.Actions[0].WebUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadLane_DoesNotExposeSeriesShelfOnMainHub()
    {
        var singleCollectionId = Guid.Parse("77777777-9999-cccc-9999-777777777777");
        var multiCollectionId = Guid.Parse("77777777-9999-dddd-9999-777777777777");
        var repository = new StubDisplayProjectionRepository(
            [
                Work(Guid.Parse("77777777-1111-cccc-9999-777777777777"), "Book", "Spirited Away", collectionId: singleCollectionId, series: "Studio Ghibli Feature Films", collectionTitle: "Studio Ghibli Feature Films"),
                Work(Guid.Parse("77777777-1111-dddd-9999-777777777777"), "Book", "Leviathan Wakes", author: "James S. A. Corey", collectionId: multiCollectionId, series: "The Expanse", seriesPosition: "1", collectionTitle: "The Expanse"),
                Work(Guid.Parse("77777777-2222-dddd-9999-777777777777"), "Book", "Caliban's War", author: "James S. A. Corey", collectionId: multiCollectionId, series: "The Expanse", collectionTitle: "The Expanse"),
            ],
            []);
        var composer = CreateComposer(repository);

        var page = await composer.BuildBrowseAsync("read", null, "all", null, 0, 48, includeCatalog: false);

        Assert.DoesNotContain(page.Shelves, shelf => shelf.Key == "series-and-reading-lists");
        var recentlyAdded = page.Shelves.Single(shelf => shelf.Key == "recently-added").Items;
        var leviathanWakes = Assert.Single(recentlyAdded, card => card.Title == "Leviathan Wakes");
        Assert.Equal("James S. A. Corey", leviathanWakes.Subtitle);
        Assert.Contains(recentlyAdded, card => card.Title == "Caliban's War");
        Assert.Contains(recentlyAdded, card => card.Title == "Spirited Away");
    }

    [Fact]
    public async Task WatchCards_PopulateQualityAndSourceBadgesOnlyFromRealData()
    {
        var movieId = Guid.Parse("99999999-1111-1111-1111-999999999999");
        var tvId = Guid.Parse("99999999-2222-2222-2222-999999999999");
        var bookId = Guid.Parse("99999999-3333-3333-3333-999999999999");
        var repository = new StubDisplayProjectionRepository(
            [
                Work(movieId, "Movie", "Arrival", year: "2016", genre: "Science Fiction", quality: "2160p", source: "HBO"),
                Work(tvId, "TV", "Pilot", year: "2008", genre: "Crime"),
                Work(bookId, "Book", "Dune", author: "Frank Herbert", quality: "2160p", source: "HBO"),
            ],
            []);
        var composer = CreateComposer(repository);

        var watchPage = await composer.BuildBrowseAsync("watch", null, "all", null, 0, 48);
        var arrival = watchPage.Catalog.Single(card => card.Title == "Arrival");
        var pilot = watchPage.Catalog.Single(card => card.Title == "Pilot");

        Assert.Equal(["quality:4K", "source:HBO"], arrival.Badges.Select(badge => $"{badge.Kind}:{badge.Label}"));
        Assert.Empty(pilot.Badges);

        var readPage = await composer.BuildBrowseAsync("read", null, "all", null, 0, 48);
        Assert.Empty(readPage.Catalog.Single(card => card.Title == "Dune").Badges);
    }

    [Fact]
    public async Task Home_ComposesCinematicShelfOrderAndProfileAwareCollections()
    {
        var profileId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var movieId = Guid.Parse("aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa");
        var bookId = Guid.Parse("aaaaaaaa-2222-2222-2222-aaaaaaaaaaaa");
        var trackId = Guid.Parse("aaaaaaaa-3333-3333-3333-aaaaaaaaaaaa");
        var collectionId = Guid.Parse("aaaaaaaa-4444-4444-4444-aaaaaaaaaaaa");
        var repository = new StubDisplayProjectionRepository(
            [
                Work(movieId, "Movie", "Arrival", year: "2016", genre: "Science Fiction", quality: "Ultra HD", source: "Max"),
                Work(bookId, "Book", "Dune", author: "Frank Herbert", genre: "Science Fiction"),
                Work(trackId, "Music", "Static", artist: "Among The Outcasts", album: "Static On The Line"),
            ],
            [
                Journey(movieId, "Movie", "Arrival", progressPct: 42, year: "2016", genre: "Science Fiction", quality: "Ultra HD", source: "Max"),
            ],
            homeCollections:
            [
                new DisplayHomeCollectionRow
                {
                    CollectionId = collectionId,
                    Title = "Sci-Fi Favorites",
                    CollectionType = "Smart",
                    PrimaryLane = "CrossMedia",
                    ItemCount = 12,
                    WatchCount = 7,
                    ReadCount = 5,
                    CreatedAt = DateTimeOffset.Parse("2026-04-24T12:00:00Z"),
                },
            ]);
        var composer = CreateComposer(repository);

        var page = await composer.BuildHomeAsync(includeCatalog: false, profileId: profileId);

        Assert.Equal(profileId, repository.LastHomeCollectionsProfileId);
        Assert.Equal(
            ["continue", "watch-next", "read-next", "listen-next", "home-collections"],
            page.Shelves.Select(shelf => shelf.Key));
        Assert.Equal("Jump Back In", page.Shelves[0].Title);
        Assert.Equal("Watch", page.Shelves[1].Title);
        Assert.Equal("/watch", page.Shelves[1].SeeAllRoute);
        Assert.Equal("Read", page.Shelves[2].Title);
        Assert.Equal("/read", page.Shelves[2].SeeAllRoute);
        Assert.Equal("Listen", page.Shelves[3].Title);
        Assert.Equal("/listen", page.Shelves[3].SeeAllRoute);
        Assert.Equal("Collections & Lists", page.Shelves[4].Title);
        Assert.Equal("/collections", page.Shelves[4].SeeAllRoute);
        Assert.Equal(page.Shelves[0].Items[0].Facts, page.Hero?.Facts);
    }

    [Fact]
    public async Task Home_HidesCollectionsShelfWhenNoPlacedCollectionsAreEligible()
    {
        var repository = new StubDisplayProjectionRepository(
            [Work(Guid.Parse("aaaaaaaa-5555-5555-5555-aaaaaaaaaaaa"), "Movie", "Arrival")],
            []);
        var composer = CreateComposer(repository);

        var page = await composer.BuildHomeAsync(includeCatalog: false);

        Assert.DoesNotContain(page.Shelves, shelf => shelf.Key == "home-collections");
    }

    [Fact]
    public async Task Home_UsesEpisodeStillForContinueAndShowCoverForWatchShelf()
    {
        var showRootId = Guid.Parse("aaaaaaaa-6666-6666-6666-aaaaaaaaaaaa");
        var firstEpisode = Guid.Parse("aaaaaaaa-7777-7777-7777-aaaaaaaaaaaa");
        var secondEpisode = Guid.Parse("aaaaaaaa-8888-8888-8888-aaaaaaaaaaaa");
        var repository = new StubDisplayProjectionRepository(
            [
                Work(firstEpisode, "TV", "Pilot", showName: "Severance", season: "1", episode: "1", coverUrl: "/episodes/pilot.jpg", rootCoverUrl: "/shows/severance-cover.jpg", rootBackgroundUrl: "/shows/severance-bg.jpg", rootWorkId: showRootId),
                Work(secondEpisode, "TV", "Half Loop", showName: "Severance", season: "1", episode: "2", coverUrl: "/episodes/half-loop.jpg", rootCoverUrl: "/shows/severance-cover.jpg", rootBackgroundUrl: "/shows/severance-bg.jpg", rootWorkId: showRootId),
            ],
            [Journey(firstEpisode, "TV", "Pilot", 38, season: "5", episode: "1", showName: "Severance", rootWorkId: showRootId, backgroundUrl: "/episodes/pilot-still.jpg")]);
        var composer = CreateComposer(repository);

        var page = await composer.BuildHomeAsync(includeCatalog: true);

        var watchNext = page.Shelves.Single(shelf => shelf.Key == "watch-next");
        var showCard = Assert.Single(watchNext.Items, card => card.Title == "Severance");
        Assert.Equal("/shows/severance-cover.jpg", showCard.Artwork.CoverUrl);
        Assert.DoesNotContain(watchNext.Items, card => card.Title is "Pilot" or "Half Loop");

        Assert.DoesNotContain(page.Shelves, shelf => shelf.Key == "fresh");

        var continueCard = Assert.Single(page.Shelves.Single(shelf => shelf.Key == "continue").Items);
        Assert.Equal(firstEpisode, continueCard.WorkId);
        Assert.Equal("Pilot", continueCard.Title);
        Assert.False(continueCard.Flags.IsCollection);
        Assert.Equal("/episodes/pilot-still.jpg", continueCard.Artwork.BackgroundUrl);
        Assert.Equal("Continue · S5 E1", continueCard.Subtitle);
        Assert.Equal("Resume S5 E1", continueCard.Actions[0].Label);
        Assert.Equal($"/watch/player/{firstEpisode:D}", continueCard.Actions[0].WebUrl);
        Assert.Equal($"/details/work/{firstEpisode:D}?context=watch", continueCard.Actions[1].WebUrl);

        Assert.Single(page.Catalog, card => card.Title == "Severance");
        Assert.DoesNotContain(page.Catalog, card => card.Title is "Pilot" or "Half Loop");
    }

    [Fact]
    public async Task Home_FillsFreshWithStructurallyUnplacedWorks()
    {
        var sharedCollectionId = Guid.Parse("bbbbbbbb-9999-9999-9999-bbbbbbbbbbbb");
        var works = Enumerable.Range(1, 20)
            .Select(index => Work(
                Guid.Parse($"aaaaaaaa-9999-9999-9999-{index:D12}"),
                "Movie",
                $"Movie {index:D2}",
                collectionId: sharedCollectionId))
            .ToList();
        var composer = CreateComposer(new StubDisplayProjectionRepository(works, []));

        var page = await composer.BuildHomeAsync(includeCatalog: false, shelfLimit: 18);

        var watch = page.Shelves.Single(shelf => shelf.Key == "watch-next");
        var fresh = page.Shelves.Single(shelf => shelf.Key == "fresh");
        Assert.Equal(18, watch.Items.Count);
        Assert.Equal(2, fresh.Items.Count);
        Assert.Empty(watch.Items.Select(card => card.WorkId).Intersect(fresh.Items.Select(card => card.WorkId)));
    }

    [Fact]
    public async Task ShelfPage_ReturnsCursorPagedShelfItems()
    {
        var firstId = Guid.Parse("77777777-1111-1111-1111-777777777777");
        var secondId = Guid.Parse("77777777-2222-2222-2222-777777777777");
        var repository = new StubDisplayProjectionRepository(
            [
                Work(firstId, "Movie", "Arrival", year: "2016", genre: "Science Fiction"),
                Work(secondId, "Movie", "Heat", year: "1995", genre: "Crime"),
            ],
            []);
        var composer = CreateComposer(repository);

        var page = await composer.BuildShelfPageAsync(
            "movies",
            lane: "watch",
            mediaType: null,
            grouping: "all",
            search: null,
            cursor: null,
            offset: 0,
            limit: 1);

        Assert.NotNull(page);
        Assert.Equal("movies", page.Shelf.Key);
        Assert.Single(page.Shelf.Items);
        Assert.Equal("1", page.NextCursor);
        Assert.Equal(0, page.Offset);
        Assert.Equal(1, page.Limit);
        Assert.True(page.HasMore);
    }

    [Fact]
    public async Task MusicHome_ComposesStreamingStyleShelvesFromDisplayApi()
    {
        var albumId = Guid.Parse("88888888-8888-8888-8888-888888888888");
        var firstTrack = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var secondTrack = Guid.Parse("aaaaaaaa-9999-9999-9999-aaaaaaaaaaaa");
        var profileId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var repository = new StubDisplayProjectionRepository(
            [
                Work(firstTrack, "Music", "Static", artist: "Among The Outcasts", album: "Static On The Line", genre: "Rock", track: "1", collectionId: albumId, duration: "180000"),
                Work(secondTrack, "Music", "Signals", artist: "Among The Outcasts", album: "Static On The Line", genre: "Rock", track: "2", collectionId: albumId, duration: "240000"),
            ],
            [
                Journey(secondTrack, "Music", "Signals", progressPct: 44, artist: "Among The Outcasts", album: "Static On The Line", genre: "Rock", track: "2", collectionId: albumId),
            ],
            new HashSet<Guid> { firstTrack });
        var composer = CreateComposer(repository);

        var page = await composer.BuildBrowseAsync("listen", "Music", "home", null, 0, 48, includeCatalog: true, profileId: profileId);

        Assert.Equal("listen-music", page.Key);
        Assert.Contains(page.Shelves, shelf => shelf.Key == "recently-played");
        Assert.Contains(page.Shelves, shelf => shelf.Key == "favorite-songs");
        Assert.Contains(page.Shelves, shelf => shelf.Key == "new-tracks-added");
        Assert.Contains(page.Shelves, shelf => shelf.Key == "albums");
        Assert.Contains(page.Shelves, shelf => shelf.Key == "artists");

        var favoriteCard = page.Shelves.Single(shelf => shelf.Key == "favorite-songs").Items.Single();
        Assert.Equal(firstTrack, favoriteCard.WorkId);
        Assert.True(favoriteCard.Flags.IsFavorite);

        var albumCard = page.Shelves.Single(shelf => shelf.Key == "albums").Items.Single();
        Assert.Equal("Static On The Line", albumCard.Title);
        Assert.Equal("album", albumCard.GroupingType);
        Assert.Equal("album", albumCard.Presentation);
        Assert.Equal("square", albumCard.PreferredShape);
        Assert.Equal(2, albumCard.PreviewItems.Count);
        Assert.Contains("7 min", albumCard.Facts);
        Assert.StartsWith("/stream/", albumCard.Artwork.CoverUrl, StringComparison.Ordinal);
        Assert.EndsWith("/cover", albumCard.Artwork.CoverUrl, StringComparison.Ordinal);
        Assert.Equal($"/details/musicalbum/{firstTrack:D}?context=listen", albumCard.Actions[0].WebUrl);
        Assert.All(albumCard.PreviewItems, item =>
            Assert.Equal($"/listen/music?browse=songs&track={item.WorkId:D}", item.WebUrl));

        Assert.NotNull(page.Hero);
        Assert.Equal("album", page.Hero.Presentation);
        Assert.Equal(2, page.Hero.PreviewItems.Count);

        var artistCard = page.Shelves.Single(shelf => shelf.Key == "artists").Items.Single();
        Assert.Equal("Among The Outcasts", artistCard.Title);
        Assert.Equal("artist", artistCard.GroupingType);
        Assert.Equal("artist", artistCard.Presentation);
    }

    private static DisplayWorkRow Work(
        Guid workId,
        string mediaType,
        string title,
        string? author = null,
        string? artist = null,
        string? album = null,
        string? year = null,
        string? seriesEndYear = null,
        string? genre = null,
        string? narrator = null,
        string? season = null,
        string? episode = null,
        string? track = null,
        Guid? collectionId = null,
        string? identityQid = null,
        string? network = null,
        string? source = null,
        string? quality = null,
        string? showName = null,
        string? series = null,
        string? seriesPosition = null,
        string? collectionTitle = null,
        string? backgroundUrl = null,
        string? rootBackgroundUrl = null,
        string? collectionBackgroundUrl = null,
        string? coverUrl = null,
        string? rootCoverUrl = null,
        Guid? rootWorkId = null,
        string? duration = null)
    {
        return new DisplayWorkRow
        {
            IsIdentityReady = true,
            WorkId = workId,
            AssetId = Guid.NewGuid(),
            CollectionId = collectionId,
            IdentityQid = identityQid,
            MediaType = mediaType,
            RootWorkId = rootWorkId ?? workId,
            CreatedAt = DateTimeOffset.Parse("2026-04-24T12:00:00Z"),
            Title = title,
            Author = author,
            Artist = artist,
            Album = album,
            Year = year,
            SeriesEndYear = seriesEndYear,
            Genre = genre,
            Duration = duration,
            Series = series,
            SeriesPosition = seriesPosition,
            CollectionTitle = collectionTitle,
            ShowName = showName,
            Narrator = narrator,
            SeasonNumber = season,
            EpisodeNumber = episode,
            TrackNumber = track,
            Network = network,
            Source = source,
            Quality = quality,
            BackgroundUrl = backgroundUrl,
            RootBackgroundUrl = rootBackgroundUrl,
            CollectionBackgroundUrl = collectionBackgroundUrl,
            CoverUrl = coverUrl,
            RootCoverUrl = rootCoverUrl,
        };
    }

    private static DisplayJourneyRow Journey(
        Guid workId,
        string mediaType,
        string title,
        double progressPct,
        string? author = null,
        string? artist = null,
        string? album = null,
        string? year = null,
        string? genre = null,
        string? narrator = null,
        string? season = null,
        string? episode = null,
        string? track = null,
        Guid? collectionId = null,
        string? network = null,
        string? source = null,
        string? quality = null,
        string? showName = null,
        Guid? rootWorkId = null,
        string? backgroundUrl = null)
    {
        return new DisplayJourneyRow
        {
            WorkId = workId,
            AssetId = Guid.NewGuid(),
            CollectionId = collectionId,
            MediaType = mediaType,
            ProgressPct = progressPct,
            LastAccessed = DateTimeOffset.Parse("2026-04-24T13:00:00Z"),
            Title = title,
            Author = author,
            Artist = artist,
            Album = album,
            Year = year,
            Genre = genre,
            Narrator = narrator,
            SeasonNumber = season,
            EpisodeNumber = episode,
            TrackNumber = track,
            Network = network,
            Source = source,
            Quality = quality,
            ShowName = showName,
            RootWorkId = rootWorkId ?? Guid.Empty,
            BackgroundUrl = backgroundUrl,
        };
    }

    private static DisplayComposerService CreateComposer(IDisplayProjectionReadService repository)
    {
        var cards = new DisplayCardBuilder();
        return new DisplayComposerService(repository, cards, new DisplayShelfBuilder(cards));
    }

    private sealed class StubDisplayProjectionRepository : IDisplayProjectionReadService
    {
        private readonly IReadOnlyList<DisplayWorkRow> _works;
        private readonly IReadOnlyList<DisplayJourneyRow> _journey;
        private readonly IReadOnlySet<Guid> _favoriteWorkIds;
        private readonly IReadOnlyList<DisplayHomeCollectionRow> _homeCollections;

        public Guid? LastHomeCollectionsProfileId { get; private set; }

        public StubDisplayProjectionRepository(
            IReadOnlyList<DisplayWorkRow> works,
            IReadOnlyList<DisplayJourneyRow> journey,
            IReadOnlySet<Guid>? favoriteWorkIds = null,
            IReadOnlyList<DisplayHomeCollectionRow>? homeCollections = null)
        {
            _works = works;
            _journey = journey;
            _favoriteWorkIds = favoriteWorkIds ?? new HashSet<Guid>();
            _homeCollections = homeCollections ?? [];
        }

        public Task<IReadOnlyList<DisplayWorkRow>> LoadWorksAsync(CancellationToken ct) =>
            Task.FromResult(_works);

        public Task<IReadOnlyList<DisplayJourneyRow>> LoadJourneyAsync(string? lane, CancellationToken ct)
        {
            var filtered = DisplayMediaRules.NormalizeLane(lane) switch
            {
                "watch" => _journey.Where(item => DisplayMediaRules.IsWatchKind(item.MediaType)),
                "read" => _journey.Where(item => DisplayMediaRules.IsReadKind(item.MediaType)),
                "listen" => _journey.Where(item => DisplayMediaRules.IsListenKind(item.MediaType)),
                _ => _journey,
            };

            return Task.FromResult<IReadOnlyList<DisplayJourneyRow>>(filtered.ToList());
        }

        public Task<IReadOnlySet<Guid>> LoadFavoriteWorkIdsAsync(Guid? profileId, CancellationToken ct) =>
            Task.FromResult(profileId.HasValue ? _favoriteWorkIds : new HashSet<Guid>());

        public Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(Guid? profileId, CancellationToken ct)
        {
            LastHomeCollectionsProfileId = profileId;
            return Task.FromResult(_homeCollections);
        }
    }
}
