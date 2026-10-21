using System.IO.Compression;
using System.Text;
using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;
using MediaEngine.Storage;
using MediaEngine.Storage.Playback;

namespace MediaEngine.Api.Tests;

// Bookmark packs (Videos spec D46). A pack holds only how to recognise the title and each bookmark's time, end, chapter name, note and
// description; it is for one title; its bookmarks are read-only and belong to the profile that imported them; and a Vault title can
// never be exported, whether the session is locked or not.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private const string ImdbOfTheFilm = "tt0120737";

    private (BookmarkPackService Packs, AudiobookBookmarkRepository Bookmarks) PackService(Guid account, Guid? session)
    {
        var (_, scope) = BookmarkPlayer(account, session);
        var bookmarks = new AudiobookBookmarkRepository(_database);
        return (new BookmarkPackService(scope, bookmarks, new BookmarkPackRepository(_database), new VaultLibraryRepository(_database)), bookmarks);
    }

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

    private static BookmarkPackExportRequestDto ExportOf(Guid assetId, params Guid[] ids) => new()
    {
        AssetId = assetId,
        BookmarkIds = ids,
        Name = "Battle of Gondor",
        Author = "A fan",
        RunningTimeSeconds = 7200,
        Chapters = [new BookmarkPackChapterDto { Title = "The Siege", StartSeconds = 3000 }],
    };

    private static BookmarkPackImportRequestDto ImportOf(BookmarkPackFileDto file, Guid assetId, double? runningTime = 7200,
        string mode = BookmarkPackImportModes.AsIs, params BookmarkPackChapterDto[] chapters) => new()
        {
            AssetId = assetId,
            ContentBase64 = file.ContentBase64,
            RunningTimeSeconds = runningTime,
            Mode = mode,
            Chapters = chapters,
        };

    [Fact]
    public async Task BookmarkPacks_ExportThenImport_RoundTripsTimesAndNotes_AsReadOnlyPackBookmarks()
    {
        var shape = await SeedVaultAsync(administrator: true, grantedTheVaultLibrary: false);
        var (packs, bookmarks) = PackService(shape.Account, NewVaultSession(shape.Account));
        GiveIds(shape.Normal.WorkId, ImdbOfTheFilm);
        var copy = await InsertOwnedWorkWithIdAsync(shape.NormalLibrary, "Someone else's copy of the film", "Movie");
        GiveIds(copy.WorkId, ImdbOfTheFilm);
        var first = await OwnBookmarkAsync(bookmarks, shape.Normal.WorkId, shape.Normal.AssetId, 3010, end: 3100, chapter: "The Siege");
        var second = await OwnBookmarkAsync(bookmarks, shape.Normal.WorkId, shape.Normal.AssetId, 5000);
        var shared = await OwnBookmarkAsync(bookmarks, shape.Normal.WorkId, shape.Normal.AssetId, 6000, global: true);

        // The shared bookmark is picked too, but a pack only ever holds the profile's own.
        var file = await packs.ExportAsync(null, shape.Normal.WorkId, ExportOf(shape.Normal.AssetId, first.Id, second.Id, shared.Id));
        Assert.Equal(2, file.BookmarkCount);
        Assert.EndsWith(".zip", file.FileName);

        var preview = await packs.InspectAsync(null, copy.WorkId, ImportOf(file, copy.AssetId));
        Assert.Equal(BookmarkPackMatchResults.Match, preview.Match);
        Assert.Equal("Battle of Gondor", preview.Name);
        Assert.Equal("A fan", preview.Author);
        Assert.False(preview.AlreadyImported);

        var added = await packs.ImportAsync(null, copy.WorkId, ImportOf(file, copy.AssetId));
        Assert.Equal("Battle of Gondor", added.Name);
        Assert.Equal("A fan", added.Author);
        Assert.Equal([3010d, 5000d], added.Bookmarks.Select(item => item.PositionSeconds));
        var scene = added.Bookmarks[0];
        Assert.Equal(3100, scene.EndSeconds);
        Assert.Equal("The Siege", scene.ChapterTitle);
        Assert.Equal("note at 3010", scene.Note);
        Assert.Equal("description at 3010", scene.Description);

        // Read back from the data store, and not mixed into the profile's ordinary bookmarks.
        var listed = Assert.Single(await packs.GetAsync(null, copy.WorkId));
        Assert.Equal(added.Id, listed.Id);
        Assert.Empty(await bookmarks.GetByWorkAsync(VaultProfile, copy.WorkId));

        // The same pack is not added twice, and it can be switched off and removed.
        await Assert.ThrowsAsync<BookmarkPackInvalidException>(() => packs.ImportAsync(null, copy.WorkId, ImportOf(file, copy.AssetId)));
        Assert.True((await packs.InspectAsync(null, copy.WorkId, ImportOf(file, copy.AssetId))).AlreadyImported);
        Assert.True(await packs.SetEnabledAsync(null, added.Id, false));
        Assert.False(Assert.Single(await packs.GetAsync(null, copy.WorkId)).IsEnabled);
        Assert.True(await packs.DeleteAsync(null, added.Id));
        Assert.Empty(await packs.GetAsync(null, copy.WorkId));
    }

    [Fact]
    public async Task BookmarkPacks_APackForAnotherTitle_IsRefused_AndAddsNothing()
    {
        var shape = await SeedVaultAsync(administrator: true, grantedTheVaultLibrary: false);
        var (packs, bookmarks) = PackService(shape.Account, NewVaultSession(shape.Account));
        GiveIds(shape.Normal.WorkId, ImdbOfTheFilm);
        var other = await InsertOwnedWorkWithIdAsync(shape.NormalLibrary, "A different film", "Movie");
        GiveIds(other.WorkId, "tt9999999");
        var bookmark = await OwnBookmarkAsync(bookmarks, shape.Normal.WorkId, shape.Normal.AssetId, 600);
        var file = await packs.ExportAsync(null, shape.Normal.WorkId, ExportOf(shape.Normal.AssetId, bookmark.Id));

        var preview = await packs.InspectAsync(null, other.WorkId, ImportOf(file, other.AssetId));
        Assert.Equal(BookmarkPackMatchResults.WrongTitle, preview.Match);
        await Assert.ThrowsAsync<BookmarkPackInvalidException>(() => packs.ImportAsync(null, other.WorkId, ImportOf(file, other.AssetId)));
        Assert.Empty(await packs.GetAsync(null, other.WorkId));
    }

    [Fact]
    public async Task BookmarkPacks_ADifferentCut_NeedsAlignByChapter_AndMovesBookmarksIntoTheSameNamedChapter()
    {
        var shape = await SeedVaultAsync(administrator: true, grantedTheVaultLibrary: false);
        var (packs, bookmarks) = PackService(shape.Account, NewVaultSession(shape.Account));
        GiveIds(shape.Normal.WorkId, ImdbOfTheFilm);
        var extended = await InsertOwnedWorkWithIdAsync(shape.NormalLibrary, "Extended cut", "Movie");
        GiveIds(extended.WorkId, ImdbOfTheFilm);
        var inChapter = await OwnBookmarkAsync(bookmarks, shape.Normal.WorkId, shape.Normal.AssetId, 3010, chapter: "The Siege");
        var noChapter = await OwnBookmarkAsync(bookmarks, shape.Normal.WorkId, shape.Normal.AssetId, 5000);
        var file = await packs.ExportAsync(null, shape.Normal.WorkId, ExportOf(shape.Normal.AssetId, inChapter.Id, noChapter.Id));

        var chapters = new[] { new BookmarkPackChapterDto { Title = "The Siege", StartSeconds = 3600 } };
        var preview = await packs.InspectAsync(null, extended.WorkId, ImportOf(file, extended.AssetId, 9000, chapters: chapters));
        Assert.Equal(BookmarkPackMatchResults.DifferentCut, preview.Match);
        Assert.True(preview.CanAlignByChapter);
        await Assert.ThrowsAsync<BookmarkPackInvalidException>(() =>
            packs.ImportAsync(null, extended.WorkId, ImportOf(file, extended.AssetId, 9000, chapters: chapters)));

        var added = await packs.ImportAsync(null, extended.WorkId,
            ImportOf(file, extended.AssetId, 9000, BookmarkPackImportModes.AlignByChapter, chapters));
        Assert.True(added.IsAligned);
        // Ten seconds into "The Siege" before, ten seconds into it now; the bookmark without a chapter name cannot be placed.
        Assert.Equal(3610, Assert.Single(added.Bookmarks).PositionSeconds);
    }

    [Fact]
    public async Task BookmarkPacks_AFileThatHoldsNoPathOrProfileName_IsAllThatLeavesTheLibrary()
    {
        var shape = await SeedVaultAsync(administrator: true, grantedTheVaultLibrary: false);
        var (packs, bookmarks) = PackService(shape.Account, NewVaultSession(shape.Account));
        GiveIds(shape.Normal.WorkId, ImdbOfTheFilm);
        var bookmark = await OwnBookmarkAsync(bookmarks, shape.Normal.WorkId, shape.Normal.AssetId, 600, chapter: "Opening");
        var file = await packs.ExportAsync(null, shape.Normal.WorkId, ExportOf(shape.Normal.AssetId, bookmark.Id));

        string path;
        string profileName;
        using (var connection = _database.CreateConnection())
        {
            path = connection.ExecuteScalar<string>("SELECT file_path_root FROM media_assets WHERE id = @asset", new { asset = shape.Normal.AssetId })!;
            profileName = connection.ExecuteScalar<string>("SELECT display_name FROM profiles WHERE id = @profile", new { profile = VaultProfile })!;
        }

        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.False(string.IsNullOrWhiteSpace(profileName));
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
        // Only IDs on the recognition list leave the library.
        Assert.Contains(ImdbOfTheFilm, text);
        Assert.DoesNotContain("internal-only-123", text);
        Assert.Contains("\"includes_secrets\":false", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BookmarkPacks_AVaultTitle_IsNeverExportable_LockedOrUnlocked()
    {
        var shape = await SeedVaultAsync(administrator: true, grantedTheVaultLibrary: true);
        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var (unlocked, bookmarks) = PackService(shape.Account, session);
        GiveIds(shape.Secret.WorkId, "tt7777777");
        var secret = await OwnBookmarkAsync(bookmarks, shape.Secret.WorkId, shape.Secret.AssetId, 120);

        // Even with NSFW mode on, the Engine answers not found.
        await Assert.ThrowsAsync<PlayerResourceDeniedException>(() =>
            unlocked.ExportAsync(null, shape.Secret.WorkId, ExportOf(shape.Secret.AssetId, secret.Id)));

        // A session that has not switched NSFW mode on never even sees the title.
        var (locked, _) = PackService(shape.Account, NewVaultSession(shape.Account));
        await Assert.ThrowsAsync<PlayerResourceDeniedException>(() =>
            locked.ExportAsync(null, shape.Secret.WorkId, ExportOf(shape.Secret.AssetId, secret.Id)));
        await Assert.ThrowsAsync<PlayerResourceDeniedException>(() =>
            locked.InspectAsync(null, shape.Secret.WorkId, new BookmarkPackImportRequestDto { AssetId = shape.Secret.AssetId, ContentBase64 = "AAAA" }));
    }

    [Fact]
    public async Task BookmarkPacks_OnAVaultTitle_AreNotFoundWhileLocked()
    {
        var shape = await SeedVaultAsync(administrator: true, grantedTheVaultLibrary: true);
        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var (unlocked, _) = PackService(shape.Account, session);
        // A pack added on the Vault title while unlocked (a repository write stands in for an earlier import).
        var added = await new BookmarkPackRepository(_database).AddAsync(
            VaultProfile, shape.Secret.WorkId, shape.Secret.AssetId, "A pack", null, "hash-1", 100, null, false,
            [new MediaEngine.Domain.Services.BookmarkPackBookmark(10, null, null, null, "n", null)]);
        Assert.NotNull(added);
        Assert.Single(await unlocked.GetAsync(null, shape.Secret.WorkId));

        var (locked, _) = PackService(shape.Account, NewVaultSession(shape.Account));
        Assert.Empty(await locked.GetAsync(null, shape.Secret.WorkId));
        Assert.False(await locked.SetEnabledAsync(null, added!.Id, false));
        Assert.False(await locked.DeleteAsync(null, added.Id));

        // Nothing was lost by locking.
        var (again, _) = PackService(shape.Account, session);
        Assert.Single(await again.GetAsync(null, shape.Secret.WorkId));
    }
}
