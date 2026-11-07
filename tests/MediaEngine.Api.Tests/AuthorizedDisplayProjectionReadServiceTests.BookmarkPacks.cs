using System.IO.Compression;
using System.Text;
using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Playback;

namespace MediaEngine.Api.Tests;

// The editor's bookmark files (universal editor v5 D37). A bookmark file holds only how to recognise the title and each bookmark's time, end,
// chapter name, note and description; it is for one title; each row is compared with the nearest bookmark in the title's Everyone's list and
// labelled Same spot, Close, Far or New from the gaps in config/bookmarks.json; comparing changes nothing (the editor stages what is taken and
// writes it on Save changes); and a Vault title never exports or imports.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private const string ImdbOfTheFilm = "tt0120737";

    private (BookmarkSetService Sets, AudiobookBookmarkRepository Bookmarks) SetService(Guid account, Guid? session)
    {
        var (_, scope) = BookmarkPlayer(account, session);
        var bookmarks = new AudiobookBookmarkRepository(_database);
        return (new BookmarkSetService(scope, new BookmarkRecognitionRepository(_database),
            new VaultLibraryRepository(_database), new BookmarkClosenessProvider(() => null)), bookmarks);
    }

    /// <summary>The title's bookmarks as the editor's Everyone's list hands them to the compare.</summary>
    private static async Task<IReadOnlyList<AudiobookBookmarkDto>> EveryoneOfAsync(AudiobookBookmarkRepository bookmarks, Guid workId) =>
        await bookmarks.GetByWorkAsync(VaultProfile, workId);

    private void GiveIds(Guid workId, string imdb)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO bridge_ids(id, entity_id, id_type, id_value) VALUES (@id, @workId, 'imdb_id', @imdb);" +
            "INSERT INTO bridge_ids(id, entity_id, id_type, id_value) VALUES (@other, @workId, 'goodreads_id', 'internal-only-123');",
            new { id = Guid.NewGuid(), other = Guid.NewGuid(), workId, imdb });
    }

    private Task<AudiobookBookmarkDto> OwnBookmarkAsync(AudiobookBookmarkRepository bookmarks, Guid workId, Guid assetId, double at,
        double? end = null, string? chapter = null, bool global = false) =>
        bookmarks.CreateAsync(VaultProfile, workId, new CreateAudiobookBookmarkRequestDto
        {
            AssetId = assetId,
            PositionSeconds = at,
            EndSeconds = end,
            DurationSeconds = 7200,
            ChapterTitle = chapter,
            Note = $"note at {at}",
            Description = $"description at {at}",
            IsGlobal = global,
        });

    /// <summary>A bookmark file as another library would have made it.</summary>
    private static BookmarkPackFileDto FileOf(string imdb, double runningTime, params BookmarkPackBookmark[] items)
    {
        var content = new BookmarkPackContent("A set", "A fan",
            new BookmarkPackTitle(new Dictionary<string, string> { ["imdb_id"] = imdb }, runningTime, null),
            items, BookmarkPackArchive.Fingerprint("A set", "A fan", items));
        return new BookmarkPackFileDto
        {
            FileName = "set.zip",
            ContentBase64 = Convert.ToBase64String(BookmarkPackArchive.Write(content, DateTimeOffset.UtcNow)),
            BookmarkCount = items.Length,
        };
    }

    private static BookmarkSetRequestDto SetOf(BookmarkPackFileDto file, Guid assetId, double? runningTime = 7200,
        string mode = BookmarkPackImportModes.AsIs, params BookmarkPackChapterDto[] chapters) => new()
        {
            AssetId = assetId,
            ContentBase64 = file.ContentBase64,
            RunningTimeSeconds = runningTime,
            Mode = mode,
            Chapters = chapters,
        };

    private static BookmarkPackBookmark Item(double at, string? note = null, string? chapter = null, double? chapterStart = null) =>
        new(at, null, chapter, chapterStart, note ?? $"incoming {at}", null);

    [Fact]
    public async Task Import_Compare_LabelsEachRowByItsGap_AndTheDefaultsDoTheSensibleThing()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var (sets, bookmarks) = SetService(shape.Account, NewVaultSession(shape.Account));
        GiveIds(shape.Normal.WorkId, ImdbOfTheFilm);
        var mine = await OwnBookmarkAsync(bookmarks, shape.Normal.WorkId, shape.Normal.AssetId, 761);
        await OwnBookmarkAsync(bookmarks, shape.Normal.WorkId, shape.Normal.AssetId, 1865);
        await OwnBookmarkAsync(bookmarks, shape.Normal.WorkId, shape.Normal.AssetId, 4690);
        var file = FileOf(ImdbOfTheFilm, 7200, Item(763), Item(1880), Item(4800), Item(6330));

        var compare = await sets.CompareWithEveryoneAsync(
            null, shape.Normal.WorkId, SetOf(file, shape.Normal.AssetId), await EveryoneOfAsync(bookmarks, shape.Normal.WorkId));

        Assert.Equal(BookmarkPackMatchResults.Match, compare.Match);
        Assert.True(compare.RowsReady);
        Assert.Equal(
            [BookmarkClosenessLabels.SameSpot, BookmarkClosenessLabels.Close, BookmarkClosenessLabels.Far, BookmarkClosenessLabels.New],
            compare.Rows.Select(row => row.Closeness));
        Assert.Equal(
            [BookmarkRowChoices.Incoming, BookmarkRowChoices.Yours, BookmarkRowChoices.Yours, BookmarkRowChoices.Incoming],
            compare.Rows.Select(row => row.DefaultChoice));
        Assert.Equal(2, compare.Rows[0].GapSeconds);
        Assert.Equal(mine.Id, compare.Rows[0].Yours!.Id);
        Assert.Null(compare.Rows[3].Yours);
        Assert.Null(compare.Rows[3].GapSeconds);
        // Comparing changes nothing.
        Assert.Equal(3, (await bookmarks.GetByWorkAsync(VaultProfile, shape.Normal.WorkId)).Count);
    }

    [Fact]
    public async Task Import_AFileForAnotherTitle_IsRefused_AndAddsNothing()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var (sets, bookmarks) = SetService(shape.Account, NewVaultSession(shape.Account));
        var other = await InsertOwnedWorkWithIdAsync(shape.NormalLibrary, "A different film", "Movie");
        GiveIds(other.WorkId, "tt9999999");
        var file = FileOf(ImdbOfTheFilm, 7200, Item(600));

        var compare = await sets.CompareWithEveryoneAsync(null, other.WorkId, SetOf(file, other.AssetId), []);
        Assert.Equal(BookmarkPackMatchResults.WrongTitle, compare.Match);
        Assert.False(compare.RowsReady);
        Assert.Empty(compare.Rows);
        Assert.Empty(await bookmarks.GetByWorkAsync(VaultProfile, other.WorkId));
    }

    [Fact]
    public async Task Import_ADifferentCut_NeedsAlignByChapter_AndMovesBookmarksIntoTheSameNamedChapter()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var (sets, bookmarks) = SetService(shape.Account, NewVaultSession(shape.Account));
        var extended = await InsertOwnedWorkWithIdAsync(shape.NormalLibrary, "Extended cut", "Movie");
        GiveIds(extended.WorkId, ImdbOfTheFilm);
        var file = FileOf(ImdbOfTheFilm, 7200, Item(3010, chapter: "The Siege", chapterStart: 3000), Item(5000));
        var chapters = new[] { new BookmarkPackChapterDto { Title = "The Siege", StartSeconds = 3600 } };

        var plain = await sets.CompareWithEveryoneAsync(null, extended.WorkId, SetOf(file, extended.AssetId, 9000, chapters: chapters), []);
        Assert.Equal(BookmarkPackMatchResults.DifferentCut, plain.Match);
        Assert.True(plain.CanAlignByChapter);
        Assert.False(plain.RowsReady);

        var aligned = await sets.CompareWithEveryoneAsync(null, extended.WorkId,
            SetOf(file, extended.AssetId, 9000, BookmarkPackImportModes.AlignByChapter, chapters), []);
        Assert.True(aligned.RowsReady);
        // Ten seconds into "The Siege" before, ten seconds into it now; the bookmark without a chapter name cannot be placed.
        Assert.Equal(3610, Assert.Single(aligned.Rows).Incoming.PositionSeconds);
        Assert.Equal(1, aligned.SkippedCount);
    }

    [Fact]
    public async Task Import_ExportEveryone_HoldsOnlyTheListGiven_AndNoPathOrProfileName()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var (sets, bookmarks) = SetService(shape.Account, NewVaultSession(shape.Account));
        GiveIds(shape.Normal.WorkId, ImdbOfTheFilm);
        var listed = await OwnBookmarkAsync(bookmarks, shape.Normal.WorkId, shape.Normal.AssetId, 600, chapter: "Opening", global: true);
        await OwnBookmarkAsync(bookmarks, shape.Normal.WorkId, shape.Normal.AssetId, 900);

        var file = await sets.ExportEveryoneAsync(shape.Normal.AssetId, [listed], 7200);

        Assert.Equal(1, file.BookmarkCount);
        string path;
        string profileName;
        using (var connection = _database.CreateConnection())
        {
            path = connection.ExecuteScalar<string>("SELECT file_path_root FROM media_assets WHERE id = @asset", new { asset = shape.Normal.AssetId })!;
            profileName = connection.ExecuteScalar<string>("SELECT display_name FROM profiles WHERE id = @profile", new { profile = VaultProfile })!;
        }

        using var archive = new ZipArchive(new MemoryStream(Convert.FromBase64String(file.ContentBase64)), ZipArchiveMode.Read);
        Assert.Equal(["manifest.json", "pack/bookmarks.json"], archive.Entries.Select(entry => entry.FullName).OrderBy(name => name));
        var everything = new StringBuilder();
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            everything.Append(await reader.ReadToEndAsync());
        }

        var text = everything.ToString();
        Assert.DoesNotContain(path, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(profileName, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(VaultProfile.ToString("D"), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(shape.Normal.AssetId.ToString("D"), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(shape.Normal.WorkId.ToString("D"), text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ImdbOfTheFilm, text);
        Assert.DoesNotContain("internal-only-123", text);
        Assert.DoesNotContain("note at 900", text);
        Assert.Contains("\"includes_secrets\":false", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_AVaultTitle_NeverImports_LockedOrUnlocked()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: true);
        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var (unlocked, bookmarks) = SetService(shape.Account, session);
        GiveIds(shape.Secret.WorkId, "tt7777777");
        await OwnBookmarkAsync(bookmarks, shape.Secret.WorkId, shape.Secret.AssetId, 120, global: true);
        var file = FileOf("tt7777777", 7200, Item(120));

        // Even with Vault mode on, the Engine answers not found.
        await Assert.ThrowsAsync<PlayerResourceDeniedException>(() =>
            unlocked.CompareWithEveryoneAsync(null, shape.Secret.WorkId, SetOf(file, shape.Secret.AssetId), []));

        // A session that has not switched Vault mode on never even sees the title.
        var (locked, _) = SetService(shape.Account, NewVaultSession(shape.Account));
        await Assert.ThrowsAsync<PlayerResourceDeniedException>(() =>
            locked.CompareWithEveryoneAsync(null, shape.Secret.WorkId, SetOf(file, shape.Secret.AssetId), []));
    }
}
