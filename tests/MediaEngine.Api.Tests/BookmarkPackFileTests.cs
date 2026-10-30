using System.IO.Compression;
using System.Text;
using Dapper;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Backups;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Backup;
using MediaEngine.Storage.Playback;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.Tests;

// The bookmark pack file and its place in the library backup (Videos spec D46). The pack follows the backup's conventions (a ZIP with
// a manifest, the same safe-entry checks, nothing secret), and a library backup already holds every kind of bookmark (as personal state
// kept against how to recognise the title), so one restore brings personal, shared, scene and pack bookmarks back together once the title is back.
public sealed class BookmarkPackFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"bookmark-pack-file-tests-{Guid.NewGuid():N}");

    public BookmarkPackFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* the temp folder is cleaned by the operating system later */ }
            catch (UnauthorizedAccessException) { /* a file is still open; the operating system cleans the temp folder later */ }
        }
    }

    private static BookmarkPackContent Content(string name = "Gondor", string? author = "A fan") => new(
        name,
        author,
        new BookmarkPackTitle(new Dictionary<string, string> { ["imdb_id"] = "tt0120737" }, 7200, "Extended"),
        [new BookmarkPackBookmark(300, 360, "Opening", 0, "note", "description")],
        BookmarkPackArchive.Fingerprint(name, author, [new BookmarkPackBookmark(300, 360, "Opening", 0, "note", "description")]));

    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
        }

        return buffer.ToArray();
    }

    [Fact]
    public void APack_IsABackupStyleZip_WithAManifestThatSaysItHoldsNoSecrets()
    {
        var bytes = BookmarkPackArchive.Write(Content(), new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));

        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.NotNull(archive.GetEntry("manifest.json"));
        using var manifest = System.Text.Json.JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open());
        Assert.Equal("1.0", manifest.RootElement.GetProperty("schema_version").GetString());
        Assert.Equal("bookmark-pack", manifest.RootElement.GetProperty("kind").GetString());
        Assert.False(manifest.RootElement.GetProperty("includes_secrets").GetBoolean());
    }

    [Fact]
    public void APack_ReadsBackExactlyWhatWasWritten()
    {
        var original = Content();

        var read = BookmarkPackArchive.Read(BookmarkPackArchive.Write(original, DateTimeOffset.UtcNow));

        Assert.Equal(original.Name, read.Name);
        Assert.Equal(original.Author, read.Author);
        Assert.Equal(original.Title.Ids, read.Title.Ids);
        Assert.Equal(7200, read.Title.RunningTimeSeconds);
        Assert.Equal("Extended", read.Title.Edition);
        Assert.Equal(original.Bookmarks, read.Bookmarks);
        Assert.Equal(original.ContentHash, read.ContentHash);
    }

    [Theory]
    [InlineData("not a zip")]
    [InlineData("")]
    public void ReadingSomethingThatIsNotAPack_IsRefusedWithAPlainMessage(string text)
    {
        Assert.Throws<BookmarkPackInvalidException>(() => BookmarkPackArchive.Read(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void AnArchiveWithAnExtraEntryOrAnUnsafePath_IsRefused()
    {
        var good = BookmarkPackArchive.Write(Content(), DateTimeOffset.UtcNow);
        using var source = new ZipArchive(new MemoryStream(good), ZipArchiveMode.Read);
        var manifest = new StreamReader(source.GetEntry("manifest.json")!.Open()).ReadToEnd();
        var payload = new StreamReader(source.GetEntry("pack/bookmarks.json")!.Open()).ReadToEnd();

        Assert.Throws<BookmarkPackInvalidException>(() => BookmarkPackArchive.Read(
            Zip(("manifest.json", manifest), ("pack/bookmarks.json", payload), ("config/secrets/key.json", "{}"))));
        Assert.Throws<BookmarkPackInvalidException>(() => BookmarkPackArchive.Read(
            Zip(("manifest.json", manifest), ("../bookmarks.json", payload))));
        Assert.Throws<BookmarkPackInvalidException>(() => BookmarkPackArchive.Read(
            Zip(("manifest.json", manifest), ("manifest.json", manifest))));
    }

    [Fact]
    public void AManifestThatClaimsSecrets_OrAnotherKind_IsRefused()
    {
        var good = BookmarkPackArchive.Write(Content(), DateTimeOffset.UtcNow);
        using var source = new ZipArchive(new MemoryStream(good), ZipArchiveMode.Read);
        var payload = new StreamReader(source.GetEntry("pack/bookmarks.json")!.Open()).ReadToEnd();

        Assert.Throws<BookmarkPackInvalidException>(() => BookmarkPackArchive.Read(Zip(
            ("manifest.json", "{\"schema_version\":\"1.0\",\"kind\":\"bookmark-pack\",\"includes_secrets\":true}"),
            ("pack/bookmarks.json", payload))));
        Assert.Throws<BookmarkPackInvalidException>(() => BookmarkPackArchive.Read(Zip(
            ("manifest.json", "{\"schema_version\":\"1.0\",\"kind\":\"library-backup\"}"),
            ("pack/bookmarks.json", payload))));
    }

    [Fact]
    public void ABookmarkWithABadTimeOrATooLongNote_IsRefused()
    {
        static string Pack(string bookmark) =>
            "{\"format\":\"bookmark-pack\",\"version\":1,\"name\":\"x\",\"title\":{\"ids\":{\"imdb_id\":\"tt1\"}},\"bookmarks\":[" + bookmark + "]}";
        const string manifest = "{\"schema_version\":\"1.0\",\"kind\":\"bookmark-pack\",\"includes_secrets\":false}";

        Assert.Throws<BookmarkPackInvalidException>(() => BookmarkPackArchive.Read(Zip(("manifest.json", manifest), ("pack/bookmarks.json", Pack("{\"time\":-1}")))));
        Assert.Throws<BookmarkPackInvalidException>(() => BookmarkPackArchive.Read(Zip(("manifest.json", manifest), ("pack/bookmarks.json", Pack("{\"time\":10,\"end_time\":5}")))));
        Assert.Throws<BookmarkPackInvalidException>(() => BookmarkPackArchive.Read(Zip(("manifest.json", manifest),
            ("pack/bookmarks.json", Pack("{\"time\":10,\"note\":\"" + new string('a', 201) + "\"}")))));
        Assert.NotEmpty(BookmarkPackArchive.Read(Zip(("manifest.json", manifest), ("pack/bookmarks.json", Pack("{\"time\":10}")))).Bookmarks);
    }

    [Fact]
    public async Task ARestoredLibraryBackup_BringsBackPersonalSharedSceneAndPackBookmarksTogether_WhenTheTitleIsInTheLibraryAgain()
    {
        var config = Path.Combine(_root, "config");
        var dbPath = Path.Combine(_root, "library.db");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "core.json"), "{}");
        var profile = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        string archiveName;

        using (var database = CreateDatabase(dbPath))
        {
            using (var connection = database.CreateConnection())
            {
                var editionId = Guid.NewGuid();
                connection.Execute(
                    """
                    INSERT INTO profiles(id,display_name,avatar_color,role,created_at) VALUES(@profile,'Someone','#7C4DFF','Administrator','2026-10-21T12:00:00.0000000+00:00');
                    INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@workId, 'Videos', 'standalone', 'accepted');
                    INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
                    INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, library_id)
                    VALUES (@assetId, @editionId, 'hash-backup', 'C:/videos/film.mkv', CURRENT_TIMESTAMP, 'lib');
                    """,
                    new { profile, workId, editionId, assetId });
            }

            var bookmarks = new AudiobookBookmarkRepository(database);
            var packs = new BookmarkPackRepository(database);
            await bookmarks.CreateAsync(profile, workId, new CreateAudiobookBookmarkRequestDto
            {
                AssetId = assetId,
                PositionSeconds = 100,
                DurationSeconds = 7200,
                Note = "personal",
                Description = "long text",
            });
            await bookmarks.CreateAsync(profile, workId, new CreateAudiobookBookmarkRequestDto
            {
                AssetId = assetId,
                PositionSeconds = 200,
                EndSeconds = 260,
                DurationSeconds = 7200,
                Note = "scene",
                IsGlobal = true,
            });
            await packs.AddAsync(profile, workId, assetId, "Gondor", "A fan", "hash-1", 7200, null, false,
                [new BookmarkPackBookmark(300, 360, "Opening", 0, "pack note", "pack description")]);

            var service = BackupTestKit.Service(database, config);
            var archivePath = await service.CreateAsync(CancellationToken.None);
            archiveName = Path.GetFileName(archivePath);

            // After the backup everything is lost, as if the library had been damaged.
            using (var damage = database.CreateConnection())
            {
                damage.Execute("DELETE FROM audiobook_bookmarks; DELETE FROM bookmark_packs;");
            }

            Assert.True(service.ScheduleRestore(archiveName).Scheduled);
        }

        DatabaseBackupService.ApplyPendingRestore(config, dbPath);
        using var fresh = CreateDatabase(dbPath);
        Assert.Equal(PendingRestoreImportOutcome.Imported, BackupTestKit.Importer(fresh, config).ImportIfPending());

        // Nothing is guessed: until the title is in the Library the bookmarks wait. A rescan finds the same file again (new ids, same hash).
        using var restored = fresh.CreateConnection();
        Assert.Equal(0, restored.ExecuteScalar<int>("SELECT COUNT(*) FROM audiobook_bookmarks;"));
        restored.Execute(
            """
            INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@workId, 'Videos', 'standalone', 'accepted');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, library_id)
            VALUES (@assetId, @editionId, 'hash-backup', 'D:/rescanned/film.mkv', CURRENT_TIMESTAMP, 'lib');
            """,
            new { workId = Guid.NewGuid(), editionId = Guid.NewGuid(), assetId = Guid.NewGuid() });
        Assert.True(new WaitingPersonalStateRepository(fresh).RunPass(DateTimeOffset.UtcNow.AddMinutes(1), 100, 90).Attached >= 1);

        var rows = restored.Query<(double Position, double? End, string? Note, string? Description, long IsGlobal)>(
            "SELECT position_seconds, end_seconds, note, description, is_global FROM audiobook_bookmarks ORDER BY position_seconds;").ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal((100d, (double?)null, "personal", "long text", 0L), rows[0]);
        Assert.Equal((200d, (double?)260, "scene", (string?)null, 1L), rows[1]);
        Assert.Equal("Gondor", restored.ExecuteScalar<string>("SELECT name FROM bookmark_packs;"));
        var item = restored.QuerySingle<(double Position, double End, string Note, string Description)>(
            "SELECT position_seconds, end_seconds, note, description FROM bookmark_pack_items;");
        Assert.Equal((300d, 360d, "pack note", "pack description"), item);
    }

    private static DatabaseConnection CreateDatabase(string path)
    {
        var database = new DatabaseConnection(path);
        database.InitializeSchema();
        database.RunStartupChecks();
        return database;
    }
}
