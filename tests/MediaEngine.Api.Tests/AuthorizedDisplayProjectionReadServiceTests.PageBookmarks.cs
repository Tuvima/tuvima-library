using Dapper;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Playback;

namespace MediaEngine.Api.Tests;

// Page bookmarks of comics and PDFs (PDF and comic reader spec D14, Global bookmarks stage 3): private until an administrator promotes
// them, then listed for everyone; and a bookmark file is compared by pages apart using the page block of config/bookmarks.json.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private static BookmarkPackBookmark PageSpot(int page) => new(page, null, null, null, $"incoming {page}", null, PageLocator.Format(page));

    private static EditorBookmarkRow PageRow(Guid assetId, int page, bool global = false) => new()
    {
        Id = Guid.NewGuid(),
        ProfileId = Guid.NewGuid(),
        WorkId = Guid.NewGuid(),
        AssetId = assetId,
        PositionKind = BookmarkPooling.PageKind,
        PositionSeconds = page,
        Locator = PageLocator.Format(page),
        IsGlobal = global,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task PageBookmarks_ArePrivateForever_BecauseOnlyMoviesAndTvHaveAGlobalSet()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE works SET media_type = 'Comics' WHERE id = @workId;", new { workId = shape.Normal.WorkId });
        }

        var kit = EditorBookmarksFor(shape.Account, NewVaultSession(shape.Account));
        var pages = new PageBookmarkRepository(_database);
        var ben = AddOtherProfile();
        var mine = new PageBookmark
        {
            Id = Guid.NewGuid(),
            UserId = VaultProfile.ToString("D"),
            AssetId = shape.Normal.AssetId,
            Page = 14,
            Label = "The reveal",
            CreatedAt = DateTime.UtcNow,
        };
        await pages.InsertAsync(mine);

        Assert.Single(await pages.ListByAssetAsync(VaultProfile.ToString("D"), shape.Normal.AssetId));
        Assert.Empty(await pages.ListByAssetAsync(ben.ToString("D"), shape.Normal.AssetId));

        // A comic has no Global set (global bookmarks v5 D3): the promotion is refused, so nobody else ever sees the page bookmark.
        var change = ChangeOf(EditorOp(EditorBookmarkOpKinds.Add, mine.Id));
        Assert.Equal(EditorBookmarksService.NoGlobal, await kit.Service.CheckAsync(shape.Normal.WorkId, change));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Service.ApplyAsync(shape.Normal.WorkId, change));

        Assert.Empty(await pages.ListByAssetAsync(ben.ToString("D"), shape.Normal.AssetId));
        Assert.Empty((await kit.Service.ReadAsync(shape.Normal.WorkId)).Everyone);
    }

    [Fact]
    public async Task PageBookmarks_AreDeletedOnlyAsPageBookmarks()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var pages = new PageBookmarkRepository(_database);
        var film = await BookmarkOf(VaultProfile, shape.Normal.WorkId, shape.Normal.AssetId, 300);

        await pages.DeleteAsync(film.Id);

        Assert.NotNull(await new AudiobookBookmarkRepository(_database).GetByIdAsync(film.Id));
    }

    [Fact]
    public async Task Import_Pages_Compare_LabelsEachRowByPagesApart_FromTheConfiguredLimits()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var kit = EditorBookmarksFor(shape.Account, NewVaultSession(shape.Account));
        GiveIds(shape.Normal.WorkId, ImdbOfTheFilm);
        var sets = new PageBookmarkSetService(
            kit.Scope, new BookmarkRecognitionRepository(_database), new VaultLibraryRepository(_database),
            new BookmarkClosenessProvider(() => null), new MediaAssetRepository(_database));
        var file = BookFileOf(ImdbOfTheFilm, null, PageSpot(3), PageSpot(5), PageSpot(13), PageSpot(60));
        var everyone = new[] { PageRow(shape.Normal.AssetId, 3), PageRow(shape.Normal.AssetId, 20, global: true) };

        var compare = await sets.CompareWithEveryoneAsync(
            shape.Normal.AssetId, new BookmarkSetRequestDto { AssetId = shape.Normal.AssetId, ContentBase64 = file.ContentBase64 }, everyone);

        Assert.Equal(BookmarkPackMatchResults.Match, compare.Match);
        Assert.True(compare.RowsReady);
        Assert.Equal(
            [BookmarkClosenessLabels.SameSpot, BookmarkClosenessLabels.Close, BookmarkClosenessLabels.Far, BookmarkClosenessLabels.New],
            compare.Rows.Select(row => row.Closeness));
        Assert.Equal([0, 2, 7], compare.Rows.Take(3).Select(row => row.GapPages ?? -1));
        Assert.Null(compare.Rows[3].Yours);
        Assert.Equal(1, compare.GlobalCount);
    }

    [Fact]
    public async Task Import_Pages_AFileForAFilmIsRefused_AndAnotherCopyPlacesNothing()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var kit = EditorBookmarksFor(shape.Account, NewVaultSession(shape.Account));
        GiveIds(shape.Normal.WorkId, ImdbOfTheFilm);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE media_assets SET content_hash = 'aaaa' WHERE id = @id", new { id = shape.Normal.AssetId });
        }

        var sets = new PageBookmarkSetService(
            kit.Scope, new BookmarkRecognitionRepository(_database), new VaultLibraryRepository(_database),
            new BookmarkClosenessProvider(() => null), new MediaAssetRepository(_database));
        var otherCopy = BookFileOf(ImdbOfTheFilm, "file:bbbb", PageSpot(2));
        var timeFile = BookFileOf(ImdbOfTheFilm, null, new BookmarkPackBookmark(120, null, null, null, "a time", null));

        var other = await sets.CompareWithEveryoneAsync(
            shape.Normal.AssetId, new BookmarkSetRequestDto { AssetId = shape.Normal.AssetId, ContentBase64 = otherCopy.ContentBase64 }, []);
        Assert.Equal(BookmarkPackMatchResults.DifferentCut, other.Match);
        Assert.False(other.RowsReady);
        Assert.Empty(other.Rows);

        await Assert.ThrowsAsync<BookmarkPackInvalidException>(() => sets.CompareWithEveryoneAsync(
            shape.Normal.AssetId, new BookmarkSetRequestDto { AssetId = shape.Normal.AssetId, ContentBase64 = timeFile.ContentBase64 }, []));
    }

    [Fact]
    public async Task Export_Pages_WritesOnlyThePageBookmarksOfEveryones()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var kit = EditorBookmarksFor(shape.Account, NewVaultSession(shape.Account));
        GiveIds(shape.Normal.WorkId, ImdbOfTheFilm);
        var sets = new PageBookmarkSetService(
            kit.Scope, new BookmarkRecognitionRepository(_database), new VaultLibraryRepository(_database),
            new BookmarkClosenessProvider(() => null), new MediaAssetRepository(_database));

        var file = await sets.ExportEveryoneAsync(shape.Normal.AssetId, [PageRow(shape.Normal.AssetId, 9, global: true), PageRow(shape.Normal.AssetId, 4, global: true)]);

        Assert.Equal(2, file.BookmarkCount);
        var read = BookmarkPackArchive.Read(Convert.FromBase64String(file.ContentBase64));
        Assert.Equal([4, 9], read.Bookmarks.Select(bookmark => PageLocator.TryParse(bookmark.Locator) ?? -1));
    }
}
