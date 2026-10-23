using System.IO.Compression;
using System.Text;
using Dapper;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Backups;
using MediaEngine.Api.Services.Vault;
using MediaEngine.Storage;
using MediaEngine.Storage.Backup;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.Tests;

// A backup never holds the Vault catalogue (Videos spec WP17, backup and restore spec D16): the Vault libraries, their settings, Vault mode
// settings and PIN hashes, access grants and each person's bookmarks (as rows on withheld titles) come back, but nothing that names or
// describes a Vault video, image, performer, studio or tag. A restored Vault bookmark waits in the general waiting area and is re-attached
// by the same pass as every other bookmark.
public sealed class VaultBackupTests : IDisposable
{
    private const string VaultTitle = "Zebrawork Vaultcinema";
    private const string VaultFileName = "vaultfilexyz-secret.mkv";
    private const string PerformerName = "Pernamexyz Performer";
    private const string StudioName = "Studioxyz Pictures";
    private const string TagName = "Tagxyz";
    private const string ImageTitle = "Imagexyz Holiday";
    private const string ImageFileName = "imagexyz-holiday.jpg";
    private const string GalleryName = "Galleryxyz";
    private const string VaultFolder = "secretfolderxyz";
    private const string OrdinaryTitle = "Ordinary Moviename";

    // What must never appear in a backup. The Vault folder is not in the list: config/libraries.json keeps it on purpose, because a
    // restore needs it to scan the Vault again (Vault decision D55).
    private static readonly string[] Secrets =
    [
        VaultTitle, VaultFileName, PerformerName, StudioName, TagName, ImageTitle, ImageFileName, GalleryName,
    ];

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-vault-backup-{Guid.NewGuid():N}");
    private readonly string _vaultLibrary = Guid.NewGuid().ToString("D");
    private readonly string _normalLibrary = Guid.NewGuid().ToString("D");
    private readonly Guid _admin = Guid.NewGuid();
    private readonly string _now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero).ToString("O");

    public VaultBackupTests()
    {
        DapperConfiguration.Configure();
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task Backup_ContainsNoVaultTitleFileNamePerformerStudioOrName_InAnyDecompressedEntry()
    {
        var (config, database) = Prepare("secrets");
        using var _ = database;
        Seed(database, config);
        var service = BackupTestKit.Service(database, config);

        var archivePath = await service.CreateAsync(CancellationToken.None);

        using var archive = ZipFile.OpenRead(archivePath);
        // Every entry, decompressed: the manifest, the data and the configuration files.
        var entries = archive.Entries.ToDictionary(entry => entry.FullName, entry => ReadEntryAsText(archive, entry.FullName));
        Assert.Contains(entries.Keys, name => name.StartsWith("config/", StringComparison.Ordinal));
        Assert.Contains(entries.Keys, name => name.StartsWith("data/", StringComparison.Ordinal));
        var everything = string.Concat(entries.Values);
        // The search is not vacuous: an ordinary title is found in the very same entries, and the Vault folder (kept on purpose) is too.
        Assert.Contains(OrdinaryTitle.ToLowerInvariant(), everything, StringComparison.Ordinal);
        Assert.Contains(VaultFolder, entries["config/libraries.json"], StringComparison.Ordinal);
        foreach (var secret in Secrets)
        {
            var needle = secret.ToLowerInvariant();
            foreach (var (name, text) in entries)
            {
                Assert.DoesNotContain(needle, text, StringComparison.Ordinal);
            }
        }

        // The manifest carries no Vault field at all.
        Assert.DoesNotContain("vault", entries["manifest.json"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Backup_KeepsVaultSettings_PinHashes_Grants_AndBookmarks_AsRowsOnWithheldTitles_NeverTheCatalogue()
    {
        var (config, database) = Prepare("keeps");
        using var _ = database;
        Seed(database, config);
        var service = BackupTestKit.Service(database, config);

        var archivePath = await service.CreateAsync(CancellationToken.None);

        using var archive = ZipFile.OpenRead(archivePath);
        Assert.NotNull(archive.GetEntry("config/libraries.json"));
        var export = BackupArchiveFormat.ReadBackupFile(archive);
        string[] Tables() => export.Tables.Select(table => table.Table).ToArray();

        // Settings, PIN hashes and grants are kept.
        Assert.Contains("vault_libraries", Tables());
        Assert.Contains("profile_nsfw_settings", Tables());
        Assert.Contains("profile_library_hidden", Tables());
        var credentials = export.Tables.Single(table => table.Table == "profile_credentials");
        var hashIndex = credentials.Columns.ToList().IndexOf("secret_hash");
        Assert.Contains(credentials.Rows, row => row[hashIndex] is BackupValue.Text { Value: "hash-only-value" });
        Assert.Equal("pin", ((BackupValue.Text)Single(export, "profile_nsfw_settings", "access_mode")).Value);

        // Nothing of the catalogue travels, Vault or not.
        Assert.DoesNotContain(Tables(), name => name.StartsWith("vault_", StringComparison.Ordinal) && name != "vault_libraries");
        foreach (var catalogue in new[] { "media_assets", "works", "editions", "canonical_values", "search_index", "local_items", "vault_matches", "vault_performers", "vault_image_items" })
        {
            Assert.DoesNotContain(catalogue, Tables());
        }

        // Both bookmarks are personal rows. The Vault one is on a withheld title that has a Stash-box id and no name.
        var bookmarks = export.Tables.Single(table => table.Table == "audiobook_bookmarks");
        Assert.Equal(2, bookmarks.Rows.Count);
        var withheld = export.Titles.Where(title => title.Withheld).ToList();
        Assert.NotEmpty(withheld);
        Assert.All(withheld, title =>
        {
            Assert.Equal("server-1|record-1", title.Ids["stash_box_id"]);
            Assert.Null(title.Name);
            Assert.Null(title.Year);
        });
        Assert.Contains(export.Titles, title => !title.Withheld && title.Name == OrdinaryTitle);
    }

    [Fact]
    public async Task Backup_LeavesTheLiveDataStoreUntouched()
    {
        var (config, database) = Prepare("live");
        using var _ = database;
        Seed(database, config);
        int Count(string sql)
        {
            using var live = database.CreateConnection();
            return live.ExecuteScalar<int>(sql);
        }

        var before = (Count("SELECT COUNT(*) FROM media_assets;"), Count("SELECT COUNT(*) FROM vault_matches;"),
            Count("SELECT COUNT(*) FROM audiobook_bookmarks;"), Count("SELECT COUNT(*) FROM local_items;"),
            Count("SELECT COUNT(*) FROM search_index;"), Count("SELECT COUNT(*) FROM waiting_personal_state;"));

        await BackupTestKit.Service(database, config).CreateAsync(CancellationToken.None);

        var after = (Count("SELECT COUNT(*) FROM media_assets;"), Count("SELECT COUNT(*) FROM vault_matches;"),
            Count("SELECT COUNT(*) FROM audiobook_bookmarks;"), Count("SELECT COUNT(*) FROM local_items;"),
            Count("SELECT COUNT(*) FROM search_index;"), Count("SELECT COUNT(*) FROM waiting_personal_state;"));
        Assert.Equal(before, after);
        Assert.Equal(2, before.Item1);
        Assert.Equal(2, Count($"SELECT COUNT(*) FROM canonical_values WHERE value = '{VaultTitle}';"));
        Assert.Equal(0, before.Item6);
    }

    [Fact]
    public async Task Restore_BringsTheVaultLibrariesBackEmpty_AndTheVaultBookmarkWaitsOutOfSight()
    {
        var (config, database) = Prepare("restore");
        string dbPath;
        using (database)
        {
            Seed(database, config);
            var service = BackupTestKit.Service(database, config);
            var archivePath = await service.CreateAsync(CancellationToken.None);
            dbPath = Path.Combine(_root, "restore.db");
            var result = service.ScheduleRestore(Path.GetFileName(archivePath));
            Assert.DoesNotContain("Vault", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(VaultTitle, result.Message, StringComparison.OrdinalIgnoreCase);
        }

        DatabaseBackupService.ApplyPendingRestore(config, dbPath);
        using var restored = BackupTestKit.OpenStore(dbPath);
        Assert.Equal(PendingRestoreImportOutcome.Imported, BackupTestKit.Importer(restored, config).ImportIfPending());

        Assert.Single(await new VaultLibraryRepository(restored).GetAllAsync());
        using (var connection = restored.CreateConnection())
        {
            Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM media_assets;"));
            Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM vault_matches;"));
            Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM vault_performers;"));
            Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM vault_image_items;"));
            Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM profile_credentials WHERE secret_hash = 'hash-only-value';"));
            Assert.Equal("pin", connection.ExecuteScalar<string>("SELECT access_mode FROM profile_nsfw_settings;"));
        }

        // Both bookmarks wait. The ordinary one counts everywhere; the Vault one only for the Vault's own count.
        var waiting = new WaitingPersonalStateRepository(restored);
        Assert.Equal(1, waiting.CountWaiting(includeWithheld: false));
        Assert.Equal(2, waiting.CountWaiting(includeWithheld: true));
        Assert.Equal(1, waiting.CountWithheldWaiting());
    }

    [Fact]
    public async Task ReAttach_BringsTheVaultBookmarkBack_WhenTheSameStashBoxVideoIsAddedWithADifferentFileHash()
    {
        var (_, restored) = await RestoreAsync("reattach", withMatch: true);
        using var __ = restored;
        var waiting = new WaitingPersonalStateRepository(restored);
        var clock = DateTimeOffset.UtcNow;

        // A video that is not the bookmark's: nothing is guessed, the bookmark keeps waiting.
        AddVault(restored, "hash-unrelated", record: "other-record", runningMs: 1_000_000);
        waiting.RunPass(clock.AddMinutes(1), 100, 90);
        Assert.Equal(1, waiting.CountWithheldWaiting());

        // The same Stash-box record, re-encoded, with another file hash.
        var rescanned = AddVault(restored, "hash-reencoded", record: "record-1", runningMs: 3_600_000);
        waiting.RunPass(clock.AddMinutes(2), 100, 90);

        Assert.Equal(0, waiting.CountWithheldWaiting());
        using var connection = restored.CreateConnection();
        var linked = connection.QuerySingle<(Guid AssetId, double Position)>(
            "SELECT asset_id AS AssetId, position_seconds AS Position FROM audiobook_bookmarks WHERE label = 'vault bookmark';");
        Assert.Equal(rescanned, linked.AssetId);
        Assert.Equal(125.5, linked.Position);
    }

    [Fact]
    public async Task ReAttach_FallsBackToTheExactFileHash_WhenTheVideoHadNoStashBoxId()
    {
        var (_, restored) = await RestoreAsync("hash", withMatch: false);
        using var __ = restored;
        var waiting = new WaitingPersonalStateRepository(restored);
        var clock = DateTimeOffset.UtcNow;

        AddVault(restored, "hash-different", record: null, runningMs: 3_600_000);
        waiting.RunPass(clock.AddMinutes(1), 100, 90);
        Assert.Equal(1, waiting.CountWithheldWaiting());

        var same = AddVault(restored, "hash-vault-original", record: null, runningMs: 3_600_000);
        waiting.RunPass(clock.AddMinutes(2), 100, 90);

        Assert.Equal(0, waiting.CountWithheldWaiting());
        using var connection = restored.CreateConnection();
        Assert.Equal(same, connection.ExecuteScalar<Guid>("SELECT asset_id FROM audiobook_bookmarks WHERE label = 'vault bookmark';"));
    }

    [Fact]
    public async Task Backup_OfAStoreWithNoVault_HasNoVaultNoteAnywhere_AndStillRestores()
    {
        var (config, database) = Prepare("novault");
        string dbPath;
        using (database)
        {
            var service = BackupTestKit.Service(database, config);
            var archivePath = await service.CreateAsync(CancellationToken.None);
            using var archive = ZipFile.OpenRead(archivePath);
            Assert.DoesNotContain("vault", ReadEntryAsText(archive, "manifest.json"), StringComparison.Ordinal);
            dbPath = Path.Combine(_root, "novault.db");
            var result = service.ScheduleRestore(Path.GetFileName(archivePath));
            Assert.DoesNotContain("Vault", result.Message, StringComparison.Ordinal);
        }

        DatabaseBackupService.ApplyPendingRestore(config, dbPath);
        using var restored = BackupTestKit.OpenStore(dbPath);
        Assert.Equal(PendingRestoreImportOutcome.Imported, BackupTestKit.Importer(restored, config).ImportIfPending());
        Assert.Empty(await new VaultLibraryRepository(restored).GetAllAsync());
    }

    /// <summary>Backs the seeded store up, restores it into a fresh one and imports it. The caller owns (disposes) the returned store.</summary>
    private async Task<(string Config, DatabaseConnection Restored)> RestoreAsync(string name, bool withMatch)
    {
        var (config, database) = Prepare(name);
        string dbPath;
        using (database)
        {
            Seed(database, config, withMatch);
            var service = BackupTestKit.Service(database, config);
            var archiveName = Path.GetFileName(await service.CreateAsync(CancellationToken.None));
            dbPath = Path.Combine(_root, name + ".db");
            service.ScheduleRestore(archiveName);
        }

        DatabaseBackupService.ApplyPendingRestore(config, dbPath);
        var restored = BackupTestKit.OpenStore(dbPath);
        Assert.Equal(PendingRestoreImportOutcome.Imported, BackupTestKit.Importer(restored, config).ImportIfPending());
        return (config, restored);
    }

    private static BackupValue Single(BackupExport export, string table, string column)
    {
        var data = export.Tables.Single(candidate => candidate.Table == table);
        return Assert.Single(data.Rows)[data.Columns.ToList().IndexOf(column)];
    }

    private (string Config, DatabaseConnection Database) Prepare(string name)
    {
        var config = Path.Combine(_root, name + "-config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "core.json"), "{}");
        File.WriteAllText(Path.Combine(config, "libraries.json"), $$"""
            {"libraries":[{"id":"{{_vaultLibrary}}","name":"Private","sources":[{"id":"s1","path":"/mnt/{{VaultFolder}}"}]},
                          {"id":"{{_normalLibrary}}","name":"Movies","sources":[{"id":"s2","path":"/mnt/movies"}]}]}
            """);
        return (config, BackupTestKit.OpenStore(Path.Combine(_root, name + ".db")));
    }

    /// <summary>Fills the store with one ordinary movie and a Vault full of things that must never reach a backup. Returns the ordinary asset id.</summary>
    private Guid Seed(DatabaseConnection database, string config, bool withMatch = true)
    {
        _ = config;
        using var connection = database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO profiles(id,display_name,avatar_color,role,content_limit,created_at) VALUES(@admin,'Admin','#7C4DFF','Administrator',NULL,@now);
            INSERT INTO vault_libraries(library_id, lookup_source, lookup_server_id, adults_confirmed_at, created_by_profile_id, created_at, updated_at)
                VALUES(@vault,'none',NULL,@now,@admin,@now,@now);
            INSERT INTO profile_credentials(id, profile_id, credential_kind, secret_hash, hash_scheme, hash_version, security_stamp, created_at, updated_at)
                VALUES(@pin,@admin,'ProfilePin','hash-only-value','pbkdf2',1,'stamp',@now,@now);
            INSERT INTO profile_nsfw_settings(profile_id, access_mode, updated_at) VALUES(@admin,'pin',@now);
            """,
            new { admin = _admin, vault = _vaultLibrary, pin = Guid.NewGuid(), now = _now });

        var (ordinaryWork, ordinary) = AddVideo(connection, OrdinaryTitle, "ordinary-hash", _normalLibrary, $"/mnt/movies/{OrdinaryTitle}.mkv");
        var (vaultWork, vaultAsset) = AddVideo(connection, VaultTitle, "hash-vault-original", _vaultLibrary, $"/mnt/{VaultFolder}/{VaultFileName}");
        var vaultEdition = connection.ExecuteScalar<Guid>("SELECT edition_id FROM media_assets WHERE id = @vaultAsset;", new { vaultAsset });

        var performer = Guid.NewGuid();
        var studio = Guid.NewGuid();
        var tag = Guid.NewGuid();
        connection.Execute(
            """
            INSERT INTO search_index(entity_id, title, original_title, alternate_titles, author, description) VALUES(@ordinaryWork, @ordinaryTitle, '', '', '', '');
            INSERT INTO canonical_values(entity_id, key, value, last_scored_at) VALUES(@ordinaryWork,'title',@ordinaryTitle,@now);
            INSERT INTO search_index(entity_id, title, original_title, alternate_titles, author, description) VALUES(@vaultWork, @vaultTitle, '', '', '', 'a description of vaultcinema');
            INSERT INTO media_asset_chapters(asset_id, chapter_index, start_ms, end_ms, title) VALUES(@vaultAsset, 0, 0, 3600000, NULL);
            INSERT INTO vault_file_fingerprints(asset_id, algorithm, hash, file_size, computed_at) VALUES(@vaultAsset,'OSHASH','oshash-vault-1',100,@now);
            INSERT INTO vault_activity(id, asset_id, kind, file_name, detail, created_at) VALUES(@activity,@vaultAsset,'added',@fileName,'added',@now);
            INSERT INTO vault_performers(id, library_id, name, created_at, updated_at) VALUES(@performer,@vault,@performerName,@now,@now);
            INSERT INTO vault_studios(id, library_id, name, created_at, updated_at) VALUES(@studio,@vault,@studioName,@now,@now);
            INSERT INTO vault_tags(id, library_id, name, created_at, updated_at) VALUES(@tag,@vault,@tagName,@now,@now);
            INSERT INTO vault_asset_performers(asset_id, performer_id) VALUES(@vaultAsset,@performer);
            INSERT INTO vault_asset_studios(asset_id, studio_id) VALUES(@vaultAsset,@studio);
            INSERT INTO vault_asset_tags(asset_id, tag_id) VALUES(@vaultAsset,@tag);
            INSERT INTO canonical_values(entity_id, key, value, last_scored_at) VALUES(@vaultWork,'title',@vaultTitle,@now);
            INSERT INTO system_activity(action_type, entity_id, detail) VALUES('Scanned', @vaultAsset, 'Added ' || @fileName);
            INSERT INTO system_activity(action_type, entity_id, detail) VALUES('Scanned', NULL, 'Scanned /mnt/secretfolderxyz/clip.mkv');
            INSERT INTO system_activity(action_type, entity_id, detail) VALUES('Scanned', @ordinary, 'Added the ordinary movie');
            """,
            new
            {
                ordinaryWork,
                ordinary,
                vaultWork,
                vaultAsset,
                vaultEdition,
                performer,
                studio,
                tag,
                activity = Guid.NewGuid(),
                vault = _vaultLibrary,
                now = _now,
                ordinaryTitle = OrdinaryTitle,
                vaultTitle = VaultTitle,
                fileName = VaultFileName,
                performerName = PerformerName,
                studioName = StudioName,
                tagName = TagName,
            });
        if (withMatch)
        {
            connection.Execute(
                "INSERT INTO vault_matches(asset_id, server_id, record_id, matched_by, applied_via, matched_at) VALUES(@vaultAsset,'server-1','record-1','title','automatic',@now);",
                new { vaultAsset, now = _now });
        }

        var account = Guid.NewGuid();
        connection.Execute(
            """
            INSERT INTO profiles(id, display_name, created_at) VALUES(@account,'Library holder',@now);
            INSERT INTO profile_library_hidden(profile_id, library_id, created_at) VALUES(@account,@library,@now);
            INSERT INTO audiobook_bookmarks(id, profile_id, work_id, asset_id, position_seconds, duration_seconds, label, is_global, created_at)
                VALUES(@b1,@admin,@ordinaryWork,@ordinary,10,7200,'ordinary bookmark',0,@now);
            INSERT INTO audiobook_bookmarks(id, profile_id, work_id, asset_id, position_seconds, duration_seconds, label, is_global, created_at)
                VALUES(@b2,@admin,@vaultWork,@vaultAsset,125.5,3600,'vault bookmark',0,@now);
            """,
            new
            {
                account,
                library = Guid.NewGuid(),
                admin = _admin,
                ordinaryWork,
                ordinary,
                vaultWork,
                vaultAsset,
                now = _now,
                b1 = Guid.NewGuid(),
                b2 = Guid.NewGuid(),
            });

        // A Vault image library with an image, a gallery and a tag use.
        var imageLibrary = Guid.NewGuid();
        var item = Guid.NewGuid();
        var gallery = Guid.NewGuid();
        connection.Execute(
            """
            INSERT INTO vault_image_libraries(library_id, vault_library_id, created_at) VALUES(@imageLibrary,@vault,@now);
            INSERT INTO local_items(id, scope_kind, library_id, media_kind, title, primary_file_name, primary_mime_type, created_at, updated_at)
                VALUES(@item,'shared',@imageLibrary,'image',@imageTitle,@imageFile,'image/jpeg',@now,@now);
            INSERT INTO vault_galleries(id, library_id, name, gallery_kind, created_at, updated_at)
                VALUES(@gallery,@imageLibrary,@galleryName,'manual',@now,@now);
            INSERT INTO vault_gallery_items(gallery_id, item_id, position, added_at) VALUES(@gallery,@item,0,@now);
            INSERT INTO vault_image_tags(item_id, tag_id, added_at) VALUES(@item,@tag,@now);
            """,
            new { imageLibrary, item, gallery, vault = _vaultLibrary, tag, now = _now, imageTitle = ImageTitle, imageFile = ImageFileName, galleryName = GalleryName });
        return ordinary;
    }

    private (Guid Work, Guid Asset) AddVideo(SqliteConnection connection, string title, string hash, string library, string path)
    {
        var work = Guid.NewGuid();
        var edition = Guid.NewGuid();
        var asset = Guid.NewGuid();
        connection.Execute(
            """
            INSERT INTO works (id, media_type, work_kind, curator_state, ready_at) VALUES (@work, 'Videos', 'standalone', 'accepted', @now);
            INSERT INTO editions (id, work_id) VALUES (@edition, @work);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, library_id, is_orphaned)
                VALUES (@asset, @edition, @hash, @path, CURRENT_TIMESTAMP, @library, 0);
            INSERT INTO canonical_values(entity_id, key, value, last_scored_at) VALUES(@asset,'title',@title,@now);
            """,
            new { work, edition, asset, hash, path, library, title, now = _now });
        return (work, asset);
    }

    /// <summary>What a rescan of the Vault folder does after a restore: finds a video, optionally matched to a Stash-box record.</summary>
    private Guid AddVault(DatabaseConnection database, string hash, string? record, long runningMs)
    {
        using var connection = database.CreateConnection();
        var (_, asset) = AddVideo(connection, "Rescanned", hash, _vaultLibrary, $"/mnt/{VaultFolder}/{hash}.mkv");
        connection.Execute(
            "INSERT INTO media_asset_chapters(asset_id, chapter_index, start_ms, end_ms, title) VALUES(@asset, 0, 0, @runningMs, NULL);",
            new { asset, runningMs });
        if (record is not null)
        {
            connection.Execute(
                "INSERT INTO vault_matches(asset_id, server_id, record_id, matched_by, applied_via, matched_at) VALUES(@asset,'server-1',@record,'title','automatic',@now);",
                new { asset, record, now = _now });
        }

        return asset;
    }

    private static string ReadEntryAsText(ZipArchive archive, string name)
    {
        using var stream = archive.GetEntry(name)!.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        // Latin-1 keeps one character per byte, so a name is found wherever its bytes sit in the file.
        return Encoding.Latin1.GetString(memory.ToArray()).ToLowerInvariant();
    }

    public void Dispose()
    {
        TestTemp.ReleaseSqlitePoolsIn(_root);
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }
    }
}
