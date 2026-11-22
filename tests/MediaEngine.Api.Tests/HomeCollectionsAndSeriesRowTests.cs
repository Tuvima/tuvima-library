using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Home's "Collections and series" row (Group pages and tiles spec WP0b): Series and folders of videos are group tiles in a row of their
/// own, the one that gained a title most recently first. Recently added keeps the single titles and the per-show, per-album and
/// per-Series roll-up, and a Series leaves the row when nothing is there.
/// </summary>
public sealed class HomeCollectionsAndSeriesRowTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-20T12:00:00Z");

    [Fact]
    public async Task ASeriesOfTwoBooks_IsAGroupTileInTheCollectionsAndSeriesRow_WithItsBookCount()
    {
        var series = Guid.NewGuid();
        var home = await Composer([Book(series, "Dune", 3), Book(series, "Dune Messiah", 2), Book(null, "Standalone", 1)]).BuildHomeAsync(includeCatalog: false);

        var shelf = home.Shelves.Single(item => item.Key == "collections-and-series");
        var tile = Assert.Single(shelf.Items);

        Assert.Equal("Collections and series", shelf.Title);
        Assert.Equal(series, tile.Id);
        Assert.True(tile.Flags.IsCollection);
        Assert.Equal(2, tile.PreviewTotalCount);
    }

    [Fact]
    public async Task ASeriesThatGainsABook_MovesToTheFrontOfTheRow()
    {
        var steady = Guid.NewGuid();
        var growing = Guid.NewGuid();
        var works = new List<DisplayWorkRow>
        {
            Book(steady, "Steady one", 10, "Steady Saga"), Book(steady, "Steady two", 9, "Steady Saga"),
            Book(growing, "Grow one", 20, "Growing Saga"), Book(growing, "Grow two", 19, "Growing Saga"),
        };

        var before = await Composer(works).BuildHomeAsync(includeCatalog: false);
        works.Add(Book(growing, "Grow three", 0, "Growing Saga"));
        var after = await Composer(works).BuildHomeAsync(includeCatalog: false);

        Assert.Equal([steady, growing], before.Shelves.Single(item => item.Key == "collections-and-series").Items.Select(card => card.Id));
        Assert.Equal([growing, steady], after.Shelves.Single(item => item.Key == "collections-and-series").Items.Select(card => card.Id));
    }

    [Fact]
    public async Task AFolderOfVideos_IsAGroupTileInTheRow_AndALooseVideoIsNot()
    {
        var folder = Guid.NewGuid();
        var home = await Composer([Video(folder, "Welcome"), Video(folder, "Loops"), Video(null, "Loose clip")]).BuildHomeAsync(includeCatalog: false);

        var tile = Assert.Single(home.Shelves.Single(item => item.Key == "collections-and-series").Items);

        Assert.Equal(VideoFolderWording.FolderMediaType, tile.MediaType);
    }

    [Fact]
    public async Task AShowAnAlbumAndSingleTitles_AreNotInTheRow_SoTheRowIsLeftOutWhenNothingGroups()
    {
        var home = await Composer([Book(null, "Alone", 1), Book(null, "Also alone", 2), Book(Guid.NewGuid(), "One of a Series", 3)]).BuildHomeAsync(includeCatalog: false);

        Assert.DoesNotContain(home.Shelves, shelf => shelf.Key == "collections-and-series");
    }

    [Fact]
    public void RecentlyAdded_StillRollsASeriesUpIntoOneTile_AndNeverAddsAGroupWithNoNewTitle()
    {
        var series = Guid.NewGuid();
        var rows = new[] { Book(series, "One", 3), Book(series, "Two", 2), Book(null, "Alone", 1) };

        var items = RecentCatalogueReadService.Compose(rows, new DisplayCardBuilder(), "all", null, 20);

        // Every item is a title that was added, or the per-Series roll-up of titles that were added (One tile design D14).
        Assert.Equal(2, items.Count);
        Assert.Contains(items, item => item.Catalogue!.Id == series && item.Catalogue.Flags.IsCollection);
        Assert.Contains(items, item => item.Catalogue!.Title == "Alone" && !item.Catalogue.Flags.IsCollection);
    }

    private static DisplayComposerService Composer(IReadOnlyList<DisplayWorkRow> works)
    {
        var cards = new DisplayCardBuilder();
        return new(new Repository(works), cards, new DisplayShelfBuilder(cards));
    }

    private static DisplayWorkRow Book(Guid? series, string title, int daysAgo, string seriesTitle = "Dune Saga") => new()
    {
        WorkId = Guid.NewGuid(),
        AssetId = Guid.NewGuid(),
        CollectionId = series,
        IdentityQid = "Q" + Math.Abs(title.GetHashCode()),
        RootWorkId = Guid.NewGuid(),
        MediaType = "Book",
        Title = title,
        CollectionTitle = series is null ? null : seriesTitle,
        Series = series is null ? null : seriesTitle,
        CoverUrl = "/cover.jpg",
        CreatedAt = Now.AddDays(-daysAgo),
    };

    // A video with no folder is its own root, the way a loose video file is.
    private static DisplayWorkRow Video(Guid? root, string title) => VideoRow(Guid.NewGuid(), root, title);

    private static DisplayWorkRow VideoRow(Guid workId, Guid? root, string title) => new()
    {
        WorkId = workId,
        AssetId = Guid.NewGuid(),
        RootWorkId = root ?? workId,
        MediaType = "Videos",
        Title = title,
        ShowName = "Python Course",
        CoverUrl = "/frame.jpg",
        BackgroundUrl = "/frame.jpg",
        CreatedAt = Now.AddDays(-2),
    };

    private sealed class Repository(IReadOnlyList<DisplayWorkRow> works) : IDisplayProjectionReadService
    {
        public Task<IReadOnlyList<DisplayWorkRow>> LoadWorksAsync(CancellationToken ct) => Task.FromResult(works);
        public Task<IReadOnlyList<DisplayJourneyRow>> LoadStatesAsync(Guid? profileId, string? lane, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DisplayJourneyRow>>([]);
        public Task<IReadOnlyList<DisplayJourneyRow>> LoadJourneyAsync(Guid? profileId, string? lane, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DisplayJourneyRow>>([]);
        public Task<IReadOnlySet<Guid>> LoadFavoriteWorkIdsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        public Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlyList<DisplayHomeCollectionRow>>([]);
    }
}
