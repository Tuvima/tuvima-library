using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Playback;

namespace MediaEngine.Api.Tests;

// Find bookmarks for books (Global bookmarks spec D6 to D10, D14). A bookmark file holds reader locations; two places are compared only
// inside one file (same paragraph is the same spot, same chapter is close, up to two chapters away is far); a file made on another
// edition is placed by chapter name; an administrator's apply changes Global and anyone else's changes only their own bookmarks.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private (ReaderBookmarkSetService Sets, ReaderBookmarkRepository Bookmarks) ReaderSetService(Guid account, Guid? session)
    {
        var (_, scope) = BookmarkPlayer(account, session);
        var bookmarks = new ReaderBookmarkRepository(_database);
        return (new ReaderBookmarkSetService(scope, bookmarks, new BookmarkRecognitionRepository(_database),
            new VaultLibraryRepository(_database), new BookmarkClosenessProvider(() => null)), bookmarks);
    }

    private static string Spot(int chapter, int paragraph, int offset = 4) => $"epubcfi(/6/{(chapter + 1) * 2}!/4/{paragraph * 2}/1:{offset})";

    private Task OwnPlaceAsync(ReaderBookmarkRepository bookmarks, Guid assetId, string cfi, int chapter, bool global = false) =>
        bookmarks.InsertAsync(new ReaderBookmark
        {
            Id = Guid.NewGuid(),
            UserId = VaultProfile.ToString("D"),
            AssetId = assetId,
            ChapterIndex = chapter,
            CfiPosition = cfi,
            Label = $"place {cfi}",
            IsGlobal = global,
            CreatedAt = DateTime.UtcNow,
        });

    private static BookmarkPackFileDto BookFileOf(string imdb, string? edition, params BookmarkPackBookmark[] items)
    {
        var content = new BookmarkPackContent("A reading", "A fan",
            new BookmarkPackTitle(new Dictionary<string, string> { ["imdb_id"] = imdb }, null, edition),
            items, BookmarkPackArchive.Fingerprint("A reading", "A fan", items));
        return new BookmarkPackFileDto
        {
            FileName = "set.zip",
            ContentBase64 = Convert.ToBase64String(BookmarkPackArchive.Write(content, DateTimeOffset.UtcNow)),
            BookmarkCount = items.Length,
        };
    }

    private static BookmarkPackBookmark Place(string cfi, int chapter, string? chapterTitle = null) =>
        new(chapter, null, chapterTitle, chapter, $"incoming {cfi}", null, cfi);

    [Fact]
    public async Task FindBookmarks_Books_Compare_LabelsEachRowByChaptersApart()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var (sets, bookmarks) = ReaderSetService(shape.Account, NewVaultSession(shape.Account));
        GiveIds(shape.Normal.WorkId, ImdbOfTheFilm);
        await OwnPlaceAsync(bookmarks, shape.Normal.AssetId, Spot(3, 5), 3);
        await OwnPlaceAsync(bookmarks, shape.Normal.AssetId, Spot(10, 2), 10);
        var file = BookFileOf(ImdbOfTheFilm, null, Place(Spot(3, 5, 40), 3), Place(Spot(3, 9), 3), Place(Spot(12, 1), 12), Place(Spot(30, 1), 30));

        var compare = await sets.CompareAsync(VaultProfile, shape.Normal.AssetId,
            new BookmarkSetRequestDto { AssetId = shape.Normal.AssetId, ContentBase64 = file.ContentBase64 });

        Assert.Equal(BookmarkPackMatchResults.Match, compare.Match);
        Assert.True(compare.RowsReady);
        Assert.Equal(
            [BookmarkClosenessLabels.SameSpot, BookmarkClosenessLabels.Close, BookmarkClosenessLabels.Far, BookmarkClosenessLabels.New],
            compare.Rows.Select(row => row.Closeness));
        Assert.Equal([0, 0, 2], compare.Rows.Take(3).Select(row => row.GapChapters ?? -1));
        Assert.Null(compare.Rows[3].Yours);
    }

    [Fact]
    public async Task FindBookmarks_Books_Apply_AsAnAdministrator_AddsGlobal_AndReplacesTheBookmarkAtTheSameSpot()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var (sets, bookmarks) = ReaderSetService(shape.Account, NewVaultSession(shape.Account));
        GiveIds(shape.Normal.WorkId, ImdbOfTheFilm);
        await OwnPlaceAsync(bookmarks, shape.Normal.AssetId, Spot(3, 5), 3);
        await OwnPlaceAsync(bookmarks, shape.Normal.AssetId, Spot(7, 2), 7);
        var file = BookFileOf(ImdbOfTheFilm, null, Place(Spot(3, 5, 40), 3), Place(Spot(7, 8), 7), Place(Spot(20, 1), 20));

        var result = await sets.ApplyAsync(VaultProfile, shape.Normal.AssetId, new BookmarkSetApplyRequestDto
        {
            Set = new BookmarkSetRequestDto { AssetId = shape.Normal.AssetId, ContentBase64 = file.ContentBase64 },
        });

        // Defaults: same spot takes the incoming one, close keeps yours, new is added.
        Assert.True(result.AddedToGlobal);
        Assert.Equal(2, result.Added);
        Assert.Equal(1, result.Kept);
        var after = await bookmarks.ListByAssetAsync(VaultProfile.ToString("D"), shape.Normal.AssetId);
        Assert.Equivalent(new[] { Spot(3, 5, 40), Spot(20, 1) }, after.Where(item => item.IsGlobal).Select(item => item.CfiPosition).ToArray());
        Assert.Contains(after, item => !item.IsGlobal && item.CfiPosition == Spot(7, 2));
        Assert.DoesNotContain(after, item => item.CfiPosition == Spot(3, 5));
    }

    [Fact]
    public async Task FindBookmarks_Books_AnotherEdition_IsPlacedByChapterName_AtTheStartOfThatChapter()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var (sets, bookmarks) = ReaderSetService(shape.Account, NewVaultSession(shape.Account));
        GiveIds(shape.Normal.WorkId, ImdbOfTheFilm);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE media_assets SET content_hash = 'aaaa' WHERE id = @id", new { id = shape.Normal.AssetId });
        }

        var file = BookFileOf(ImdbOfTheFilm, "file:bbbb", Place(Spot(2, 4), 2, "Part Two"), Place(Spot(9, 4), 9, "Missing chapter"));
        var request = new BookmarkSetRequestDto
        {
            AssetId = shape.Normal.AssetId,
            ContentBase64 = file.ContentBase64,
            Chapters = [new BookmarkPackChapterDto { Title = "part  two", StartSeconds = 5 }],
        };

        var asIs = await sets.CompareAsync(VaultProfile, shape.Normal.AssetId, request);
        Assert.Equal(BookmarkPackMatchResults.DifferentCut, asIs.Match);
        Assert.False(asIs.RowsReady);
        Assert.True(asIs.CanAlignByChapter);

        var aligned = await sets.CompareAsync(VaultProfile, shape.Normal.AssetId, request with { Mode = BookmarkPackImportModes.AlignByChapter });
        Assert.True(aligned.RowsReady);
        Assert.Equal(1, aligned.SkippedCount);
        Assert.Equal(ReaderBookmarkSetService.ChapterStartLocator(5), Assert.Single(aligned.Rows).Incoming.Locator);
    }

    [Fact]
    public async Task FindBookmarks_Books_RefusesAFileMadeForAFilm_AndExportNeedsGlobalBookmarks()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var (sets, bookmarks) = ReaderSetService(shape.Account, NewVaultSession(shape.Account));
        GiveIds(shape.Normal.WorkId, ImdbOfTheFilm);
        var film = FileOf(ImdbOfTheFilm, 7200, Item(100));

        await Assert.ThrowsAsync<BookmarkPackInvalidException>(() => sets.CompareAsync(VaultProfile, shape.Normal.AssetId,
            new BookmarkSetRequestDto { AssetId = shape.Normal.AssetId, ContentBase64 = film.ContentBase64 }));
        await Assert.ThrowsAsync<BookmarkPackInvalidException>(() => sets.ExportGlobalAsync(VaultProfile, shape.Normal.AssetId, new ExportGlobalBookmarksRequestDto()));

        await OwnPlaceAsync(bookmarks, shape.Normal.AssetId, Spot(4, 3), 4, global: true);
        await OwnPlaceAsync(bookmarks, shape.Normal.AssetId, Spot(8, 3), 8);
        var exported = await sets.ExportGlobalAsync(VaultProfile, shape.Normal.AssetId, new ExportGlobalBookmarksRequestDto());
        Assert.Equal(1, exported.BookmarkCount);
    }

    [Fact]
    public async Task FindBookmarks_Books_ExportGlobal_IsForAdministratorsOnly_AndHoldsNoPathOrProfile()
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var (sets, bookmarks) = ReaderSetService(shape.Account, NewVaultSession(shape.Account));
        GiveIds(shape.Normal.WorkId, ImdbOfTheFilm);
        await OwnPlaceAsync(bookmarks, shape.Normal.AssetId, Spot(4, 3), 4, global: true);

        await Assert.ThrowsAsync<PlayerBookmarkAdminDeniedException>(() =>
            sets.ExportGlobalAsync(VaultProfile, shape.Normal.AssetId, new ExportGlobalBookmarksRequestDto()));
    }

    [Fact]
    public async Task FindBookmarks_Books_AVaultBook_NeverExportsOrImports()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: true);
        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var (sets, bookmarks) = ReaderSetService(shape.Account, session);
        GiveIds(shape.Secret.WorkId, "tt7777777");
        await OwnPlaceAsync(bookmarks, shape.Secret.AssetId, Spot(4, 3), 4, global: true);
        var file = BookFileOf("tt7777777", null, Place(Spot(4, 3), 4));

        await Assert.ThrowsAsync<PlayerResourceDeniedException>(() =>
            sets.ExportGlobalAsync(VaultProfile, shape.Secret.AssetId, new ExportGlobalBookmarksRequestDto()));
        await Assert.ThrowsAsync<PlayerResourceDeniedException>(() => sets.CompareAsync(VaultProfile, shape.Secret.AssetId,
            new BookmarkSetRequestDto { AssetId = shape.Secret.AssetId, ContentBase64 = file.ContentBase64 }));
    }
}
