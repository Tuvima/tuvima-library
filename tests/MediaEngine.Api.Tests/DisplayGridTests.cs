using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Display;

namespace MediaEngine.Api.Tests;

// The "View all" grid read: sort, filters, paging and the rules for which filter groups are worth showing.
public sealed class DisplayGridTests
{
    private static readonly DateTimeOffset Day0 = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    [Fact]
    public async Task Grid_SortsByRecentlyAddedWithTitleThenIdBreakingTies()
    {
        var tiedA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
        var tiedB = Guid.Parse("00000000-0000-0000-0000-00000000000b");
        var composer = Composer(
            Movie("Zebra", added: 1),
            Movie("Apple", added: 1, id: tiedB),
            Movie("Apple", added: 1, id: tiedA),
            Movie("Newest", added: 5),
            Movie("Oldest", added: -3));

        var page = await composer.BuildGridAsync("watch", "movies", new DisplayGridQuery());

        Assert.Equal(["Newest", "Apple", "Apple", "Zebra", "Oldest"], page!.Items.Select(card => card.Title));
        Assert.Equal([tiedA, tiedB], page.Items.Where(card => card.Title == "Apple").Select(card => card.Id));
        Assert.Equal(DisplayGridValues.SortAdded, page.Sort);
        Assert.Equal(DisplayGridValues.SortAdded, page.DefaultSort);
    }

    [Fact]
    public async Task Grid_SortsTitlesAToZUsingTheBrowseSortTitle()
    {
        var composer = Composer(
            Movie("The Zoo", added: 1, sortTitle: "Zoo, The"),
            Movie("Beta", added: 2),
            Movie("alpha", added: 3));

        var page = await composer.BuildGridAsync("watch", "movies", new DisplayGridQuery(Sort: "title"));

        Assert.Equal(["alpha", "Beta", "The Zoo"], page!.Items.Select(card => card.Title));
    }

    [Fact]
    public async Task Grid_SortsByYearNewestFirstWithUnknownYearsLast()
    {
        var composer = Composer(
            Movie("Old", added: 1, year: "1980"),
            Movie("Unknown", added: 9),
            Movie("New", added: 2, year: "2024"),
            Movie("Middle", added: 3, year: "2001"));

        var page = await composer.BuildGridAsync("watch", "movies", new DisplayGridQuery(Sort: "year"));

        Assert.Equal(["New", "Middle", "Old", "Unknown"], page!.Items.Select(card => card.Title));
    }

    [Fact]
    public async Task Grid_SortsByRecentlyPlayedWithNeverPlayedItemsLastInAddedOrder()
    {
        var first = Movie("Played first", added: 1);
        var second = Movie("Played second", added: 2);
        var composer = Composer(
            [first, second, Movie("Unplayed old", added: 3), Movie("Unplayed new", added: 8)],
            states:
            [
                State(first, 40, minutesAgo: 5),
                State(second, 100, minutesAgo: 600),
            ]);

        var page = await composer.BuildGridAsync("watch", "movies", new DisplayGridQuery(Sort: "played"));

        Assert.Equal(["Played first", "Played second", "Unplayed new", "Unplayed old"], page!.Items.Select(card => card.Title));
    }

    [Fact]
    public async Task Grid_ContinueGridsDefaultToRecentlyPlayedAndNeverOfferProgressFilters()
    {
        var older = Movie("Older", added: 1);
        var newer = Movie("Newer", added: 2);
        var composer = Composer(
            [older, newer, Movie("Mystery", added: 3, genre: "Drama"), Movie("Other", added: 4, genre: "Comedy")],
            states: [State(older, 30, minutesAgo: 90), State(newer, 60, minutesAgo: 10)]);

        var page = await composer.BuildGridAsync("home", "continue", new DisplayGridQuery());

        Assert.Equal(["Newer", "Older"], page!.Items.Select(card => card.Title));
        Assert.Equal(DisplayGridValues.SortPlayed, page.DefaultSort);
        Assert.Null(page.RememberKey);
        Assert.DoesNotContain(page.Filters, group => group.Key == DisplayGridValues.FilterStatus);
    }

    [Fact]
    public async Task Grid_PagesThroughEveryItemExactlyOnce()
    {
        var composer = Composer(Enumerable.Range(0, 7).Select(index => Movie($"Movie {index}", added: index)).ToArray());

        var first = await composer.BuildGridAsync("watch", "movies", new DisplayGridQuery(Limit: 3));
        var second = await composer.BuildGridAsync("watch", "movies", new DisplayGridQuery(Offset: int.Parse(first!.NextCursor!), Limit: 3));
        var third = await composer.BuildGridAsync("watch", "movies", new DisplayGridQuery(Offset: int.Parse(second!.NextCursor!), Limit: 3));

        Assert.All(new[] { first, second, third }, page => Assert.Equal(7, page!.TotalCount));
        Assert.Equal(new string?[] { "3", "6", null }, new[] { first.NextCursor, second.NextCursor, third!.NextCursor });
        Assert.Equal([true, true, false], new[] { first.HasMore, second.HasMore, third.HasMore });
        Assert.Equal([0, 3, 6], new[] { first.Offset, second.Offset, third.Offset });
        var all = first.Items.Concat(second.Items).Concat(third.Items).Select(card => card.Id).ToList();
        Assert.Equal(7, all.Distinct().Count());
    }

    [Fact]
    public async Task Grid_OffsetPastTheEndReturnsAnEmptyPageAndClampsTheLimit()
    {
        var composer = Composer(Movie("Only", added: 1));

        var past = await composer.BuildGridAsync("watch", "movies", new DisplayGridQuery(Offset: 40, Limit: 10));
        var huge = await composer.BuildGridAsync("watch", "movies", new DisplayGridQuery(Limit: 5000));
        var none = await composer.BuildGridAsync("watch", "movies", new DisplayGridQuery(Limit: 0));

        Assert.Empty(past!.Items);
        Assert.False(past.HasMore);
        Assert.Equal(1, past.TotalCount);
        Assert.Equal(DisplayGridValues.MaxPageSize, huge!.Limit);
        Assert.Equal(DisplayGridValues.DefaultPageSize, none!.Limit);
    }

    [Fact]
    public async Task Grid_ShowsTvEpisodesAsOneShowCardWithTheNewestEpisodeDeterminingRecency()
    {
        var show = Guid.NewGuid();
        var composer = Composer(
            Tv("Pilot", show, "Severance", added: 1, season: "1", episode: "1"),
            Tv("Half Loop", show, "Severance", added: 6, season: "1", episode: "2"),
            Movie("Film", added: 3));

        var page = await composer.BuildGridAsync("watch", "tv-shows", new DisplayGridQuery());
        var watchNext = await composer.BuildGridAsync("home", "watch-next", new DisplayGridQuery());

        var card = Assert.Single(page!.Items);
        Assert.Equal("Severance", card.Title);
        Assert.Equal(show, card.Id);
        Assert.Equal(DisplaySubjectKind.TvShow, card.Subject);
        Assert.Equal(["Severance", "Film"], watchNext!.Items.Select(item => item.Title));
        Assert.Equal(2, watchNext.TotalCount);
    }

    [Fact]
    public async Task Grid_FiltersByMediaType()
    {
        var show = Guid.NewGuid();
        var composer = Composer(
            Movie("Film one", added: 1),
            Movie("Film two", added: 2),
            Tv("Pilot", show, "Show", added: 3, season: "1", episode: "1"));

        var all = await composer.BuildGridAsync("home", "watch-next", new DisplayGridQuery());
        var tvOnly = await composer.BuildGridAsync("home", "watch-next", new DisplayGridQuery(Type: "tv"));

        var group = Assert.Single(all!.Filters, item => item.Key == DisplayGridValues.FilterType);
        Assert.Equal("Media type", group.Label);
        Assert.Equal([("movie", "Movies", 2), ("tv", "TV shows", 1)], group.Options.Select(option => (option.Value, option.Label, option.Count)));
        Assert.Equal(["Show"], tvOnly!.Items.Select(card => card.Title));
        var selected = Assert.Single(tvOnly.Filters, item => item.Key == DisplayGridValues.FilterType);
        Assert.Equal(["tv"], selected.Options.Where(option => option.Selected).Select(option => option.Value));
        // The other options keep counting the items the remaining filters leave, so the choice stays visible.
        Assert.Equal(2, selected.Options.Single(option => option.Value == "movie").Count);
    }

    [Fact]
    public async Task Grid_FiltersByGenreAndSortsGenreOptionsByCountThenName()
    {
        var composer = Composer(
            Movie("A", added: 1, genre: "Drama; Comedy"),
            Movie("B", added: 2, genre: "Drama"),
            Movie("C", added: 3, genre: "Action"),
            Movie("D", added: 4, genre: "Comedy"),
            Movie("E", added: 5, genre: "Drama"));

        var all = await composer.BuildGridAsync("watch", "movies", new DisplayGridQuery());
        var drama = await composer.BuildGridAsync("watch", "movies", new DisplayGridQuery(Genre: "drama"));

        var group = Assert.Single(all!.Filters, item => item.Key == DisplayGridValues.FilterGenre);
        Assert.Equal([("Drama", 3), ("Comedy", 2), ("Action", 1)], group.Options.Select(option => (option.Value, option.Count)));
        Assert.Equal(["E", "B", "A"], drama!.Items.Select(card => card.Title));
        Assert.Equal(3, drama.TotalCount);
    }

    [Fact]
    public async Task Grid_OffersAtMostFortyGenresPlusTheSelectedOne()
    {
        var works = Enumerable.Range(0, 45).Select(index => Movie($"Film {index}", added: index, genre: $"Genre {index:00}")).ToArray();
        var composer = Composer(works);

        var plain = await composer.BuildGridAsync("watch", "movies", new DisplayGridQuery());
        var selected = await composer.BuildGridAsync("watch", "movies", new DisplayGridQuery(Genre: "Genre 44"));

        Assert.Equal(40, plain!.Filters.Single(group => group.Key == DisplayGridValues.FilterGenre).Options.Count);
        var withSelected = selected!.Filters.Single(group => group.Key == DisplayGridValues.FilterGenre).Options;
        Assert.Equal(41, withSelected.Count);
        Assert.Contains(withSelected, option => option.Value == "Genre 44" && option.Selected);
    }

    [Fact]
    public async Task Grid_FiltersByProgressStatus()
    {
        var started = Book("Started", added: 1);
        var done = Book("Done", added: 2);
        var composer = Composer(
            [started, done, Book("Fresh", added: 3)],
            states: [State(started, 40, minutesAgo: 5), State(done, 100, minutesAgo: 9)]);

        var all = await composer.BuildGridAsync("read", "recently-added", new DisplayGridQuery());
        var inProgress = await composer.BuildGridAsync("read", "recently-added", new DisplayGridQuery(Status: "in-progress"));
        var finished = await composer.BuildGridAsync("read", "recently-added", new DisplayGridQuery(Status: "finished"));
        var notStarted = await composer.BuildGridAsync("read", "recently-added", new DisplayGridQuery(Status: "not-started"));

        var group = Assert.Single(all!.Filters, item => item.Key == DisplayGridValues.FilterStatus);
        Assert.Equal("Progress", group.Label);
        Assert.Equal([("Not started", 1), ("In progress", 1), ("Finished", 1)], group.Options.Select(option => (option.Label, option.Count)));
        Assert.Equal(["Started"], inProgress!.Items.Select(card => card.Title));
        Assert.Equal(["Done"], finished!.Items.Select(card => card.Title));
        Assert.Equal(["Fresh"], notStarted!.Items.Select(card => card.Title));
    }

    [Fact]
    public async Task Grid_ReadsAShowAsFinishedOnlyWhenEveryOwnedEpisodeIs()
    {
        var show = Guid.NewGuid();
        var one = Tv("Pilot", show, "Show", added: 1, season: "1", episode: "1");
        var two = Tv("Second", show, "Show", added: 2, season: "1", episode: "2");
        var composer = Composer(
            [one, two, Movie("Film", added: 3)],
            states: [State(one, 100, minutesAgo: 30)]);

        var started = await composer.BuildGridAsync("watch", "tv-shows", new DisplayGridQuery(Status: "in-progress"));
        var finished = await composer.BuildGridAsync("watch", "tv-shows", new DisplayGridQuery(Status: "finished"));

        Assert.Equal(["Show"], started!.Items.Select(card => card.Title));
        Assert.Empty(finished!.Items);
    }

    [Fact]
    public async Task Grid_OmitsTheMediaTypeGroupWhenOnlyOneTypeIsPresent()
    {
        var composer = Composer(Movie("One", added: 1, genre: "Drama"), Movie("Two", added: 2, genre: "Comedy"));

        var page = await composer.BuildGridAsync("watch", "movies", new DisplayGridQuery());

        Assert.DoesNotContain(page!.Filters, group => group.Key == DisplayGridValues.FilterType);
        Assert.Contains(page.Filters, group => group.Key == DisplayGridValues.FilterGenre);
    }

    [Fact]
    public async Task Grid_OmitsGroupsWithASingleOptionUnlessAValueIsSelected()
    {
        var composer = Composer(Movie("One", added: 1, genre: "Drama"), Movie("Two", added: 2, genre: "Drama"));

        var plain = await composer.BuildGridAsync("watch", "movies", new DisplayGridQuery());
        var selected = await composer.BuildGridAsync("watch", "movies", new DisplayGridQuery(Genre: "Drama"));

        Assert.DoesNotContain(plain!.Filters, group => group.Key == DisplayGridValues.FilterGenre);
        var kept = Assert.Single(selected!.Filters, group => group.Key == DisplayGridValues.FilterGenre);
        Assert.Single(kept.Options);
        Assert.True(kept.Options[0].Selected);
    }

    [Fact]
    public async Task Grid_OffersNoProgressFilterWhileMusicIsInView()
    {
        var composer = Composer(
            Movie("Film", added: 1),
            Audiobook("Spoken", added: 2),
            Track("Track one", "Album", "Artist", added: 3),
            Track("Track two", "Album", "Artist", added: 4));

        var mixed = await composer.BuildGridAsync("home", "listen-next", new DisplayGridQuery());
        var audiobooksOnly = await composer.BuildGridAsync("home", "listen-next", new DisplayGridQuery(Type: "audiobook"));

        Assert.DoesNotContain(mixed!.Filters, group => group.Key == DisplayGridValues.FilterStatus);
        Assert.Equal(["Album", "Spoken"], mixed.Items.Select(card => card.Title).Order());
        Assert.Equal(["Spoken"], audiobooksOnly!.Items.Select(card => card.Title));
    }

    [Fact]
    public async Task Grid_OmitsTheGenreGroupOnAGenreGridAndNarrowsToThatGenre()
    {
        var show = Guid.NewGuid();
        var composer = Composer(
            Movie("Heist one", added: 1, genre: "Crime"),
            Movie("Heist two", added: 2, genre: "Crime; Drama"),
            Movie("Heist three", added: 3, genre: "Crime"),
            Movie("Love story", added: 4, genre: "Romance"),
            Tv("Pilot", show, "Crime show", added: 5, season: "1", episode: "1", genre: "Crime"));

        var page = await composer.BuildGridAsync("watch", "genre-crime", new DisplayGridQuery());

        Assert.Equal("Crime", page!.Title);
        Assert.Equal(["Crime show", "Heist three", "Heist two", "Heist one"], page.Items.Select(card => card.Title));
        Assert.DoesNotContain(page.Filters, group => group.Key == DisplayGridValues.FilterGenre);
        Assert.Equal(DisplayGridValues.RememberWatch, page.RememberKey);
        Assert.Null(await composer.BuildGridAsync("watch", "genre-western", new DisplayGridQuery()));
    }

    [Fact]
    public async Task Grid_NarrowsAnAuthorGridToThatAuthorsBooks()
    {
        var composer = Composer(
            Book("Dune", added: 1, author: "Frank Herbert"),
            Book("Dune Messiah", added: 2, author: "Frank Herbert"),
            Book("Emma", added: 3, author: "Jane Austen"));

        var page = await composer.BuildGridAsync("read", "author-frank-herbert", new DisplayGridQuery());

        Assert.Equal("Frank Herbert", page!.Title);
        Assert.Equal(["Dune Messiah", "Dune"], page.Items.Select(card => card.Title));
        Assert.Equal(DisplayGridValues.RememberRead, page.RememberKey);
        Assert.Null(await composer.BuildGridAsync("read", "author-nobody", new DisplayGridQuery()));
    }

    [Fact]
    public async Task Grid_ReturnsNothingForAnUnknownGridAndRejectsValuesItDoesNotOffer()
    {
        var composer = Composer(Movie("Film", added: 1));

        Assert.Null(await composer.BuildGridAsync("watch", "no-such-shelf", new DisplayGridQuery()));
        Assert.Null(await composer.BuildGridAsync("nowhere", "movies", new DisplayGridQuery()));
        await Assert.ThrowsAsync<ArgumentException>(() => composer.BuildGridAsync("watch", "movies", new DisplayGridQuery(Sort: "random")));
        await Assert.ThrowsAsync<ArgumentException>(() => composer.BuildGridAsync("watch", "movies", new DisplayGridQuery(Type: "podcast")));
        await Assert.ThrowsAsync<ArgumentException>(() => composer.BuildGridAsync("watch", "movies", new DisplayGridQuery(Format: "vhs")));
        await Assert.ThrowsAsync<ArgumentException>(() => composer.BuildGridAsync("watch", "movies", new DisplayGridQuery(Status: "abandoned")));
    }

    [Fact]
    public async Task Grid_ShelfPageAnswersFromTheFullListPastTheHomeWindow()
    {
        var works = Enumerable.Range(0, 1_200).Select(index => Movie($"Movie {index:0000}", added: index)).ToArray();
        var composer = Composer(works, homeWindow: 1_000);

        var page = await composer.BuildShelfPageAsync("watch-next", null, null, null, null, "1100", null, 24);

        Assert.NotNull(page);
        Assert.Equal(24, page.Shelf.Items.Count);
        Assert.Equal(1_100, page.Offset);
        Assert.True(page.HasMore);
        Assert.Equal("1124", page.NextCursor);
        Assert.Equal("/all/home/watch-next", page.Shelf.SeeAllRoute);
        var last = await composer.BuildShelfPageAsync("watch-next", null, null, null, null, "1180", null, 24);
        Assert.Equal(20, last!.Shelf.Items.Count);
        Assert.False(last.HasMore);
    }

    [Fact]
    public async Task Grid_ShelfPageStillAnswersShelvesThatHaveNoGrid()
    {
        var composer = Composer(Movie("Film", added: 1));

        var page = await composer.BuildShelfPageAsync("home-collections", null, null, null, null, null, null, 24);

        Assert.Null(page);
    }

    [Fact]
    public async Task Shelves_OfferTwentyItemsAndLinkEveryGridShelfToItsViewAll()
    {
        var works = Enumerable.Range(0, 30)
            .Select(index => Book($"Book {index:00}", added: index, author: index < 25 ? "Prolific Author" : "Other"))
            .Concat(Enumerable.Range(0, 30).Select(index => Movie($"Movie {index:00}", added: index, genre: "Crime")))
            .ToArray();
        var composer = Composer(works);

        var home = await composer.BuildHomeAsync(includeCatalog: false);
        var read = await composer.BuildBrowseAsync("read", null, null, null, 0, 24, includeCatalog: false);
        var watch = await composer.BuildBrowseAsync("watch", null, null, null, 0, 24, includeCatalog: false);

        Assert.Equal(20, home.Shelves.Single(shelf => shelf.Key == "watch-next").Items.Count);
        Assert.Equal("/all/home/watch-next", home.Shelves.Single(shelf => shelf.Key == "watch-next").SeeAllRoute);
        Assert.Equal("/all/home/read-next", home.Shelves.Single(shelf => shelf.Key == "read-next").SeeAllRoute);
        var author = read.Shelves.Single(shelf => shelf.Key == "author-prolific-author");
        Assert.Equal(20, author.Items.Count);
        Assert.Equal("/all/read/author-prolific-author", author.SeeAllRoute);
        var genre = watch.Shelves.Single(shelf => shelf.Key == "genre-crime");
        Assert.Equal(20, genre.Items.Count);
        Assert.Equal("/all/watch/genre-crime", genre.SeeAllRoute);
        Assert.Equal("/all/watch/movies", watch.Shelves.Single(shelf => shelf.Key == "movies").SeeAllRoute);
    }

    // -- helpers ----------------------------------------------------------------------------------------------

    private static DisplayComposerService Composer(params DisplayWorkRow[] works) => Composer(works, []);

    private static DisplayComposerService Composer(
        IReadOnlyList<DisplayWorkRow> works,
        IReadOnlyList<DisplayJourneyRow>? states = null,
        int? homeWindow = null)
    {
        var cards = new DisplayCardBuilder();
        return new DisplayComposerService(
            new GridProjection(works, states ?? [], homeWindow),
            cards,
            new DisplayShelfBuilder(cards));
    }

    private static DisplayWorkRow Movie(string title, int added, string? year = null, string? genre = null, string? sortTitle = null, Guid? id = null) =>
        Row("Movie", title, added, year, genre, id, sortTitle: sortTitle);

    private static DisplayWorkRow Book(string title, int added, string? author = null, string? genre = null) =>
        Row("Book", title, added, null, genre, null, author: author);

    private static DisplayWorkRow Audiobook(string title, int added) => Row("Audiobook", title, added, null, null, null);

    private static DisplayWorkRow Track(string title, string album, string artist, int added)
    {
        var track = Row("Music", title, added, null, null, null, artist: artist);
        track.Album = album;
        track.RootWorkId = Guid.Empty;
        return track;
    }

    private static DisplayWorkRow Tv(string title, Guid show, string showName, int added, string season, string episode, string? genre = null)
    {
        var row = Row("TV", title, added, "2022", genre, null);
        row.RootWorkId = show;
        row.ShowName = showName;
        row.SeasonNumber = season;
        row.EpisodeNumber = episode;
        return row;
    }

    private static DisplayWorkRow Row(
        string mediaType,
        string title,
        int added,
        string? year,
        string? genre,
        Guid? id,
        string? author = null,
        string? artist = null,
        string? sortTitle = null)
    {
        var workId = id ?? Guid.NewGuid();
        return new DisplayWorkRow
        {
            WorkId = workId,
            AssetId = Guid.NewGuid(),
            RootWorkId = workId,
            MediaType = mediaType,
            Title = title,
            SortTitle = sortTitle,
            Year = year,
            Genre = genre,
            Author = author,
            Artist = artist,
            CreatedAt = Day0.AddDays(added),
        };
    }

    private static DisplayJourneyRow State(DisplayWorkRow work, double progress, int minutesAgo) => new()
    {
        WorkId = work.WorkId,
        AssetId = work.AssetId,
        RootWorkId = work.RootWorkId,
        MediaType = work.MediaType,
        Title = work.Title,
        ShowName = work.ShowName,
        SeasonNumber = work.SeasonNumber,
        EpisodeNumber = work.EpisodeNumber,
        ProgressPct = progress,
        LastAccessed = DateTimeOffset.Parse("2026-06-01T12:00:00Z").AddMinutes(-minutesAgo),
    };

    // The shelf pages' view of the library: Home reads a capped window, everything else reads the full list.
    private sealed class GridProjection(
        IReadOnlyList<DisplayWorkRow> works,
        IReadOnlyList<DisplayJourneyRow> states,
        int? homeWindow) : IDisplayProjectionReadService
    {
        public Task<IReadOnlyList<DisplayWorkRow>> LoadWorksAsync(CancellationToken ct) => Task.FromResult(works);

        public Task<IReadOnlyList<DisplayWorkRow>> LoadHomeWorksAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DisplayWorkRow>>(homeWindow is { } window ? works.OrderByDescending(work => work.CreatedAt).Take(window).ToList() : works);

        public Task<IReadOnlyList<DisplayJourneyRow>> LoadStatesAsync(Guid? profileId, string? lane, CancellationToken ct) =>
            Task.FromResult(states);

        public Task<IReadOnlyList<DisplayJourneyRow>> LoadJourneyAsync(Guid? profileId, string? lane, CancellationToken ct)
        {
            var started = states.Where(state => state.ProgressPct is > 0 and < 99.5);
            var inLane = DisplayMediaRules.NormalizeLane(lane) switch
            {
                "watch" => started.Where(state => DisplayMediaRules.IsWatchKind(state.MediaType)),
                "read" => started.Where(state => DisplayMediaRules.IsReadKind(state.MediaType)),
                "listen" => started.Where(state => DisplayMediaRules.IsListenKind(state.MediaType)),
                _ => started,
            };
            return Task.FromResult<IReadOnlyList<DisplayJourneyRow>>(inLane.ToList());
        }

        public Task<IReadOnlySet<Guid>> LoadFavoriteWorkIdsAsync(Guid? profileId, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

        public Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(Guid? profileId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DisplayHomeCollectionRow>>([]);
    }
}
