using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Backup;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests.Backup;

/// <summary>
/// The backup exporter against a real SQLite data store: what it keeps, how personal state is keyed to the title
/// rather than the file, and what never reaches the file.
/// </summary>
public sealed class PersonalStateBackupRepositoryTests : IDisposable
{
    private const string DescriptionMarker = "DESCRIPTION-MARKER-7741";
    private const string FilePathMarker = "/media/secret-folder/Dune-2021.mkv";
    private const string OtherTitleMarker = "UNTOUCHED-TITLE-MARKER-5512";
    private const string AvatarMarker = "/avatars/AVATAR-PATH-MARKER-3390.png";
    private const string SecretMarker = "ENCRYPTED-SECRET-MARKER-8820";

    private static readonly string Now = "2026-10-21T10:00:00Z";

    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly PersonalStateBackupRepository _repository;

    public PersonalStateBackupRepositoryTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_backup_export_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _repository = new PersonalStateBackupRepository(_database);
    }

    [Fact]
    public void Export_KeepsBookmarksAndPositions_KeyedToTheTitleNotTheFile()
    {
        var profile = AddProfile("Maya");
        var movie = AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: "438631", hash: "aaaa1111", seconds: 9300.5);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO user_states (user_id, asset_id, progress_pct, last_accessed) VALUES (@p, @a, 42.5, @now)",
                new { p = GuidSql.ToBlob(profile), a = movie.Asset, now = Now });
            connection.Execute(
                "INSERT INTO reader_bookmarks (id, user_id, asset_id, chapter_index, label) VALUES (@id, @u, @a, 3, 'Spice')",
                new { id = GuidSql.ToBlob(Guid.NewGuid()), u = profile.ToString("D"), a = movie.Asset });
            connection.Execute(
                """
                INSERT INTO audiobook_bookmarks (id, profile_id, work_id, asset_id, position_seconds, created_at)
                VALUES (@id, @p, @w, @a, 120.0, @now)
                """,
                new { id = GuidSql.ToBlob(Guid.NewGuid()), p = GuidSql.ToBlob(profile), w = movie.Work, a = movie.Asset, now = Now });
        }

        var export = _repository.Export(BackupScope.WholeLibrary);

        var userStates = Assert.Single(export.Tables, table => table.Table == "user_states");
        var row = Assert.Single(userStates.Rows);
        var assetRef = Assert.IsType<BackupValue.TitleRef>(row[userStates.Columns.ToList().IndexOf("asset_id")]);
        var bookmarks = Assert.Single(export.Tables, table => table.Table == "reader_bookmarks");
        var bookmark = Assert.Single(bookmarks.Rows);
        Assert.Equal(assetRef, bookmark[bookmarks.Columns.ToList().IndexOf("asset_id")]);
        Assert.Equal(
            BackupValue.FromText(profile.ToString("D")),
            bookmark[bookmarks.Columns.ToList().IndexOf("user_id")]);
        var audiobook = Assert.Single(export.Tables, table => table.Table == "audiobook_bookmarks");
        Assert.IsType<BackupValue.TitleRef>(Assert.Single(audiobook.Rows)[audiobook.Columns.ToList().IndexOf("work_id")]);

        var fileTitle = Assert.Single(export.Titles, title => title.Key == assetRef.Key);
        Assert.Equal("MediaAsset", fileTitle.Target);
        Assert.Equal(["aaaa1111"], fileTitle.FileHashes);
        Assert.Equal("4K Remux", fileTitle.Edition);
        Assert.Equal(9300.5, fileTitle.RunningTimeSeconds);
        Assert.Equal("tt1160419", fileTitle.Ids["imdb_id"]);
        Assert.Equal("438631", fileTitle.Ids["tmdb_id"]);
        Assert.Equal("Dune", fileTitle.Name);
        Assert.Equal(2021, fileTitle.Year);
        Assert.Equal("Movies", fileTitle.MediaType);
        Assert.Contains(export.Titles, title => title.Target == "Work" && title.Ids["imdb_id"] == "tt1160419");
    }

    [Fact]
    public void Export_LeavesTheCatalogueOut()
    {
        var profile = AddProfile("Maya");
        var movie = AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: null, hash: "aaaa1111", seconds: null, description: DescriptionMarker, path: FilePathMarker);
        AddMovie(OtherTitleMarker, "1999", imdb: "tt0000001", tmdb: null, hash: "bbbb2222", seconds: null);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO user_states (user_id, asset_id, progress_pct, last_accessed) VALUES (@p, @a, 10, @now)",
                new { p = GuidSql.ToBlob(profile), a = movie.Asset, now = Now });
        }

        var text = EntryText(Archive(_repository.Export(BackupScope.WholeLibrary)));

        Assert.Contains("Dune", text);
        Assert.DoesNotContain(DescriptionMarker, text);
        Assert.DoesNotContain(FilePathMarker, text);
        Assert.DoesNotContain("secret-folder", text);
        Assert.DoesNotContain(OtherTitleMarker, text);
        Assert.DoesNotContain("tt0000001", text);
        Assert.DoesNotContain("bbbb2222", text);
    }

    [Fact]
    public void Export_LeavesExcludedSecretColumnsOut()
    {
        var profile = AddProfile("Maya");
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE profiles SET avatar_image_path = @marker WHERE id = @id", new { marker = AvatarMarker, id = GuidSql.ToBlob(profile) });
        }

        var export = _repository.Export(BackupScope.WholeLibrary);

        Assert.DoesNotContain("AVATAR-PATH-MARKER", EntryText(Archive(export)));
        var profiles = Assert.Single(export.Tables, table => table.Table == "profiles");
        var avatar = profiles.Columns.ToList().IndexOf("avatar_image_path");
        Assert.True(avatar >= 0, "A column that may be empty stays in the file, as NULL.");
        Assert.All(profiles.Rows, row => Assert.Equal(BackupValue.Null, row[avatar]));
    }

    [Fact]
    public void Export_RemovesEncryptedDestinationSecrets_ButKeepsTheRestOfTheBackupSettings()
    {
        // Stored the way the Engine stores it: the settings type serialised with the web (camelCase) options.
        var settings = new StoredSettings(
        [
            new StoredDestination("NAS", "WebDav", SecretMarker, new StoredNested(SecretMarker, 1)),
        ]);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT OR REPLACE INTO backup_settings (setting_key, value_json, updated_at) VALUES ('destinations', @json, @now)",
                new { json = JsonSerializer.Serialize(settings, MediaEngineJson.Web), now = Now });
        }

        var export = _repository.Export(BackupScope.WholeLibrary);

        var bytes = Archive(export);
        Assert.DoesNotContain(SecretMarker, Encoding.Latin1.GetString(bytes));
        var text = EntryText(bytes);
        Assert.DoesNotContain(SecretMarker, text);
        Assert.DoesNotContain("EncryptedSecret", text, StringComparison.OrdinalIgnoreCase);
        var settingsTable = Assert.Single(export.Tables, table => table.Table == "backup_settings");
        var kept = settingsTable.Rows
            .Select(row => Assert.IsType<BackupValue.Text>(row[settingsTable.Columns.ToList().IndexOf("value_json")]).Value)
            .Single(value => value.Contains("WebDav", StringComparison.Ordinal));
        using var parsed = JsonDocument.Parse(kept);
        Assert.Equal(1, parsed.RootElement.GetProperty("destinations")[0].GetProperty("nested").GetProperty("keep").GetInt32());
    }

    private sealed record StoredSettings(List<StoredDestination> Destinations);

    private sealed record StoredDestination(string Name, string Kind, string EncryptedSecret, StoredNested Nested);

    private sealed record StoredNested(string EncryptedSecret, int Keep);

    [Fact]
    public void Export_DoesNotBlockAWriterWhileItRuns()
    {
        var profile = AddProfile("Maya");
        var movie = AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: null, hash: "aaaa1111", seconds: null);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO user_states (user_id, asset_id, progress_pct, last_accessed) VALUES (@p, @a, 10, @now)",
                new { p = GuidSql.ToBlob(profile), a = movie.Asset, now = Now });
        }

        int profilesBefore;
        using (var connection = _database.CreateConnection())
        {
            profilesBefore = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM profiles");
        }

        var wroteWhileReading = false;
        var export = _repository.Export(BackupScope.WholeLibrary, () =>
        {
            // A second connection with a one second wait: if the export held a write lock, this would time out.
            using var writer = new SqliteConnection($"Data Source={_databasePath};Default Timeout=1;Pooling=False");
            writer.Open();
            writer.Execute(
                "INSERT INTO profiles (id, display_name, created_at) VALUES (@id, 'Written during export', @now)",
                new { id = GuidSql.ToBlob(Guid.NewGuid()), now = Now });
            wroteWhileReading = true;
        });

        Assert.True(wroteWhileReading);
        Assert.Equal(profilesBefore, export.Contents.Profiles);
    }

    [Fact]
    public void Export_ForOneProfile_HoldsOnlyThatProfilesState()
    {
        var maya = AddProfile("Maya");
        var theo = AddProfile("Theo");
        var movie = AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: null, hash: "aaaa1111", seconds: null);
        using (var connection = _database.CreateConnection())
        {
            foreach (var (profile, progress) in new[] { (maya, 11.0), (theo, 77.0) })
            {
                connection.Execute(
                    "INSERT INTO user_states (user_id, asset_id, progress_pct, last_accessed) VALUES (@p, @a, @pct, @now)",
                    new { p = GuidSql.ToBlob(profile), a = movie.Asset, pct = progress, now = Now });
                connection.Execute(
                    "INSERT INTO reader_bookmarks (id, user_id, asset_id, chapter_index, label) VALUES (@id, @u, @a, 1, @label)",
                    new { id = GuidSql.ToBlob(Guid.NewGuid()), u = profile.ToString("D"), a = movie.Asset, label = profile == maya ? "mine" : "theirs" });
            }
        }

        var export = _repository.Export(BackupScope.ForProfile(maya));

        Assert.DoesNotContain(export.Tables, table => table.Kind == BackupDataKind.Configuration);
        Assert.All(export.Tables, table => Assert.Equal(BackupDataKind.PersonalState, table.Kind));
        var states = Assert.Single(export.Tables, table => table.Table == "user_states");
        Assert.Equal(BackupValue.FromReal(11.0), Assert.Single(states.Rows)[states.Columns.ToList().IndexOf("progress_pct")]);
        var bookmarks = Assert.Single(export.Tables, table => table.Table == "reader_bookmarks");
        Assert.Equal(BackupValue.FromText("mine"), Assert.Single(bookmarks.Rows)[bookmarks.Columns.ToList().IndexOf("label")]);
        Assert.Equal(1, export.Contents.Profiles);

        var text = EntryText(Archive(export));
        Assert.DoesNotContain("Maya", text);
        Assert.DoesNotContain("theirs", text);
        Assert.Contains("\"scope\":\"profile\"", text);
    }

    [Fact]
    public void Export_TitleWithNoIds_IsCountedAsRecognisedByFileOnly()
    {
        var profile = AddProfile("Maya");
        var withIds = AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: null, hash: "aaaa1111", seconds: null);
        var withoutIds = AddMovie("Home Video", "2012", imdb: null, tmdb: null, hash: "cccc3333", seconds: null);
        using (var connection = _database.CreateConnection())
        {
            foreach (var asset in new[] { withIds.Asset, withoutIds.Asset })
            {
                connection.Execute(
                    "INSERT INTO user_states (user_id, asset_id, progress_pct, last_accessed) VALUES (@p, @a, 5, @now)",
                    new { p = GuidSql.ToBlob(profile), a = asset, now = Now });
            }
        }

        var export = _repository.Export(BackupScope.WholeLibrary);

        Assert.Equal(2, export.Titles.Count);
        var fileOnly = Assert.Single(export.Titles, title => title.IsRecognisedByFileOnly);
        Assert.Equal("Home Video", fileOnly.Name);
        Assert.Equal(["cccc3333"], fileOnly.FileHashes);
        Assert.Equal(1, export.Contents.TitlesRecognisedByFileOnly);
        Assert.Equal(1, _repository.CountTitlesRecognisedByFileOnly());
    }

    [Fact]
    public void Export_SavedItemsAndSequenceKeys_ResolveByKindAndKeepExternalKeysAsWritten()
    {
        var profile = AddProfile("Maya");
        var movie = AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: null, hash: "aaaa1111", seconds: null);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO profile_saved_items (profile_id, entity_kind, entity_id, saved_at) VALUES (@p, 'Movie', @w, @now)",
                new { p = GuidSql.ToBlob(profile), w = movie.Work, now = Now });
            connection.Execute(
                "INSERT INTO profile_saved_items (profile_id, entity_kind, entity_id, saved_at) VALUES (@p, 'Movie', @w, @now)",
                new { p = GuidSql.ToBlob(profile), w = GuidSql.ToBlob(Guid.NewGuid()), now = Now });
            connection.Execute(
                "INSERT INTO profile_sequence_preferences (profile_id, media_type, container_key, show_missing, updated_at) VALUES (@p, 'Books', 'Q190192', 1, @now)",
                new { p = GuidSql.ToBlob(profile), now = Now });
            connection.Execute(
                "INSERT INTO profile_sequence_preferences (profile_id, media_type, container_key, show_missing, updated_at) VALUES (@p, 'Movies', @key, 1, @now)",
                new { p = GuidSql.ToBlob(profile), key = ((Guid)new Guid(movie.Work, bigEndian: true)).ToString("D"), now = Now });
        }

        var export = _repository.Export(BackupScope.WholeLibrary);

        var saved = Assert.Single(export.Tables, table => table.Table == "profile_saved_items");
        var sequences = Assert.Single(export.Tables, table => table.Table == "profile_sequence_preferences");
        var keys = sequences.Rows.Select(row => row[sequences.Columns.ToList().IndexOf("container_key")]).ToList();
        Assert.Contains(BackupValue.FromText("Q190192"), keys);
        var reference = Assert.IsType<BackupValue.TitleRef>(Assert.Single(keys, key => key is BackupValue.TitleRef));
        Assert.True(reference.WasText, "The key was stored as TEXT, so a restore must write it back as TEXT.");
        var savedReference = Assert.IsType<BackupValue.TitleRef>(Assert.Single(saved.Rows)[saved.Columns.ToList().IndexOf("entity_id")]);
        Assert.False(savedReference.WasText);
        Assert.Equal(1, export.Contents.DroppedRows);
    }

    [Fact]
    public void Export_KeepsAnActivityEntryWhoseTitleIsGone_WithoutTheTitleLink()
    {
        var profile = AddProfile("Maya");
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                """
                INSERT INTO personal_activity (id, profile_id, kind, started_at, media_type, title, asset_id, artwork_key)
                VALUES (@id, @p, 'watch', @now, 'Movies', 'Gone', @a, 'art-key-marker')
                """,
                new { id = GuidSql.ToBlob(Guid.NewGuid()), p = GuidSql.ToBlob(profile), now = Now, a = GuidSql.ToBlob(Guid.NewGuid()) });
        }

        var export = _repository.Export(BackupScope.WholeLibrary);

        var activity = Assert.Single(export.Tables, table => table.Table == "personal_activity");
        var row = Assert.Single(activity.Rows);
        Assert.Equal(BackupValue.Null, row[activity.Columns.ToList().IndexOf("asset_id")]);
        Assert.Equal(0, export.Contents.DroppedRows);
    }

    [Fact]
    public void Export_KeepsAFavouriteViewItemAsAChoice_AndNothingElseAboutViewItems()
    {
        var favourite = AddLocalItem("Beach day", "ffff0001", favorite: true);
        AddLocalItem("Boring photo", "ffff0002", favorite: false);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE local_items SET captured_at = '2020-01-01T00:00:00Z' WHERE id = @id", new { id = favourite });
        }

        var export = _repository.Export(BackupScope.WholeLibrary);

        var choices = Assert.Single(export.Choices);
        Assert.Equal("local_items", choices.Table);
        Assert.Equal(["ref", "favorite", "hidden", "archived_at", "trashed_at", "captured_at_user_override", "captured_at"], choices.Columns);
        var row = Assert.Single(choices.Rows);
        var reference = Assert.IsType<BackupValue.TitleRef>(row[0]);
        Assert.Equal(BackupValue.FromInteger(1), row[1]);
        Assert.Equal(BackupValue.Null, row[6]);
        var title = Assert.Single(export.Titles, candidate => candidate.Key == reference.Key);
        Assert.Equal("LocalItem", title.Target);
        Assert.Equal(["ffff0001"], title.FileHashes);
        Assert.Equal(1, export.Contents.ChoiceRows);

        var text = EntryText(Archive(export));
        Assert.DoesNotContain("ffff0002", text);
        Assert.DoesNotContain("Boring photo", text);
        Assert.DoesNotContain("2020-01-01", text);
    }

    [Fact]
    public void Export_KeepsACaptureDateOnlyWhenThePersonSetIt()
    {
        var item = AddLocalItem("Scan", "ffff0003", favorite: false);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "UPDATE local_items SET captured_at = '1999-12-31T00:00:00Z', captured_at_user_override = 1 WHERE id = @id",
                new { id = item });
        }

        var row = Assert.Single(Assert.Single(_repository.Export(BackupScope.WholeLibrary).Choices).Rows);

        Assert.Equal(BackupValue.FromInteger(1), row[5]);
        Assert.Equal(BackupValue.FromText("1999-12-31T00:00:00Z"), row[6]);
    }

    [Fact]
    public void ArchiveFormat_RoundTripsEveryValueKind()
    {
        var guid = GuidSql.ToBlob(Guid.NewGuid());
        var title = BackupTitle.Empty("t1", "Work") with
        {
            Ids = new Dictionary<string, string> { ["imdb_id"] = "tt1" },
            FileHashes = ["abc"],
            RunningTimeSeconds = 120.0,
            Year = 2020,
            Name = "Dune",
        };
        var table = new BackupExportTable(
            "user_states",
            BackupDataKind.PersonalState,
            ["a", "b", "c", "d", "e", "f", "g", "h", "i", "j"],
            [[
                BackupValue.Null,
                BackupValue.FromInteger(long.MinValue),
                BackupValue.FromReal(5.0),
                BackupValue.FromReal(0.1 + 0.2),
                BackupValue.FromText("text with \"quotes\""),
                BackupValue.FromBlob([0, 1, 2, 255]),
                BackupValue.FromBlob(guid),
                BackupValue.FromTitleRef("t1"),
                BackupValue.FromReal(double.PositiveInfinity),
                BackupValue.FromTitleRef("t1", wasText: true),
            ]]);
        var choices = new BackupExportTable("local_items", BackupDataKind.Catalogue, ["ref", "favorite"], [[BackupValue.FromTitleRef("t1"), BackupValue.FromInteger(1)]]);
        var contents = new BackupExportContents(BackupScope.ForProfile(Guid.Parse("11111111-2222-3333-4444-555555555555")), 0, 0, 1, 1, 1, 0, 1, 2);
        var export = new BackupExport([table], [choices], [title], contents);

        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            BackupArchiveFormat.Write(archive, export);
        }

        stream.Position = 0;
        using var readable = new ZipArchive(stream, ZipArchiveMode.Read);
        var read = BackupArchiveFormat.Read(readable);

        var row = Assert.Single(Assert.Single(read.Tables).Rows);
        Assert.Equal(table.Rows[0], row);
        Assert.IsType<BackupValue.Real>(row[2]);
        Assert.Equal(0.1 + 0.2, ((BackupValue.Real)row[3]).Value);
        Assert.Equal(guid, ((BackupValue.Blob)row[6]).Value);
        Assert.False(Assert.IsType<BackupValue.TitleRef>(row[7]).WasText);
        Assert.True(Assert.IsType<BackupValue.TitleRef>(row[9]).WasText);
        Assert.Equal(choices.Rows[0], Assert.Single(Assert.Single(read.Choices).Rows));
        var readTitle = Assert.Single(read.Titles);
        Assert.Equal(title.Key, readTitle.Key);
        Assert.Equal(title.Ids, readTitle.Ids);
        Assert.Equal(title.FileHashes, readTitle.FileHashes);
        Assert.Equal(120.0, readTitle.RunningTimeSeconds);
        Assert.Equal(2020, readTitle.Year);
        Assert.Equal(contents, read.Contents);
    }

    [Fact]
    public void ArchiveFormat_RefusesAnUnknownValueShape()
    {
        using var stream = ArchiveWith(
            ("data/personal/user_states.json", """{"table":"user_states","kind":"personal_state","columns":["a"],"rows":[[{"mystery":"x"}]]}"""));
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.Throws<InvalidDataException>(() => BackupArchiveFormat.Read(archive));
    }

    [Fact]
    public void ArchiveFormat_RefusesAnEntryOutsideTheDataFolder()
    {
        using var stream = ArchiveWith(("config/other.json", "{}"));
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.Throws<InvalidDataException>(() => BackupArchiveFormat.Read(archive));
    }

    [Fact]
    public void ArchiveFormat_RefusesARowThatPointsAtAnUnlistedTitle()
    {
        using var stream = ArchiveWith(
            ("data/contents.json", ContentsJson),
            ("data/titles.json", """{"titles":[]}"""),
            ("data/personal/user_states.json", """{"table":"user_states","kind":"personal_state","columns":["a"],"rows":[[{"title":"t9"}]]}"""));
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.Throws<InvalidDataException>(() => BackupArchiveFormat.Read(archive));
    }

    [Fact]
    public void ArchiveFormat_RefusesATableFileForACatalogueTable()
    {
        foreach (var (name, kind) in new[]
        {
            ("data/personal/works.json", "personal_state"),
            ("data/configuration/works.json", "configuration"),
            ("data/choices/works.json", "choices"),
            ("data/personal/playback_inspection_cache.json", "personal_state"),
            ("data/configuration/user_states.json", "configuration"),
            ("data/personal/not_a_table.json", "personal_state"),
        })
        {
            var table = Path.GetFileNameWithoutExtension(name);
            using var stream = ArchiveWith(
                ("data/contents.json", ContentsJson),
                ("data/titles.json", """{"titles":[]}"""),
                (name, $$"""{"table":"{{table}}","kind":"{{kind}}","columns":["id"],"rows":[]}"""));
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            Assert.Throws<InvalidDataException>(() => BackupArchiveFormat.Read(archive));
        }
    }

    [Fact]
    public void ArchiveFormat_RefusesAColumnThatIsNotAPlainName()
    {
        using var stream = ArchiveWith(
            ("data/contents.json", ContentsJson),
            ("data/titles.json", """{"titles":[]}"""),
            ("data/personal/user_states.json", """{"table":"user_states","kind":"personal_state","columns":["progress_pct\" = 1; --"],"rows":[]}"""));
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.Throws<InvalidDataException>(() => BackupArchiveFormat.Read(archive));
    }

    [Fact]
    public void ArchiveFormat_RefusesANumberOutOfRange()
    {
        foreach (var number in new[] { "1e400", "-1e400", "99999999999999999999", "1.5e999" })
        {
            using var inRow = ArchiveWith(
                ("data/contents.json", ContentsJson),
                ("data/titles.json", """{"titles":[]}"""),
                ("data/personal/user_states.json", $$"""{"table":"user_states","kind":"personal_state","columns":["progress_pct"],"rows":[[{{number}}]]}"""));
            using var inRowArchive = new ZipArchive(inRow, ZipArchiveMode.Read);
            Assert.Throws<InvalidDataException>(() => BackupArchiveFormat.Read(inRowArchive));

            if (number.Contains('e', StringComparison.Ordinal))
            {
                // A huge whole number is a valid real for a running time; only an exponent past a double is out of range.
                using var inTitle = ArchiveWith(
                    ("data/contents.json", ContentsJson),
                    ("data/titles.json", $$"""{"titles":[{"key":"t1","target":"Work","running_time_seconds":{{number}}}]}"""));
                using var inTitleArchive = new ZipArchive(inTitle, ZipArchiveMode.Read);
                Assert.Throws<InvalidDataException>(() => BackupArchiveFormat.Read(inTitleArchive));
            }
        }
    }

    [Fact]
    public void ArchiveFormat_RefusesDuplicateEntries()
    {
        var table = """{"table":"user_states","kind":"personal_state","columns":["progress_pct"],"rows":[]}""";
        foreach (var duplicated in new[] { "data/titles.json", "data/contents.json", "data/personal/user_states.json" })
        {
            var entries = new List<(string Name, string Json)>
            {
                ("data/contents.json", ContentsJson),
                ("data/titles.json", """{"titles":[]}"""),
                ("data/personal/user_states.json", table),
            };
            entries.Add(entries.Single(entry => entry.Name == duplicated));
            using var stream = ArchiveWith([.. entries]);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            Assert.Throws<InvalidDataException>(() => BackupArchiveFormat.Read(archive));
        }
    }

    [Fact]
    public void ArchiveFormat_RefusesAnArchiveOverItsSizeBudget()
    {
        var entries = new[]
        {
            ("data/contents.json", ContentsJson),
            ("data/titles.json", """{"titles":[]}"""),
        };

        using (var stream = ArchiveWith(entries))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            Assert.NotNull(BackupArchiveFormat.Read(archive, maxEntries: 2, maxEntryBytes: 4096, maxTotalBytes: 8192));
        }

        using (var stream = ArchiveWith(entries))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            Assert.Throws<InvalidDataException>(() => BackupArchiveFormat.Read(archive, maxEntries: 1, maxEntryBytes: 4096, maxTotalBytes: 8192));
        }

        using (var stream = ArchiveWith(entries))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            Assert.Throws<InvalidDataException>(() => BackupArchiveFormat.Read(archive, maxEntries: 2, maxEntryBytes: 64, maxTotalBytes: 8192));
        }

        using (var stream = ArchiveWith(entries))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            Assert.Throws<InvalidDataException>(() => BackupArchiveFormat.Read(archive, maxEntries: 2, maxEntryBytes: 4096, maxTotalBytes: ContentsJson.Length + 5));
        }
    }

    [Fact]
    public void Export_PicksTheEditionsRunningTimeFromItsEarliestPresentedOriginal_NotByFileHash()
    {
        var movie = AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: null, hash: "ffff0000", seconds: 100.0);
        var earlier = GuidSql.ToBlob(Guid.NewGuid());
        using var connection = _database.CreateConnection();
        connection.Execute("UPDATE media_assets SET presented_at = '2026-02-01T00:00:00Z' WHERE id = @asset", new { asset = movie.Asset });
        connection.Execute(
            "INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at) VALUES (@earlier, @edition, 'aaaa0000', '/media/other.mkv', '2026-01-01T00:00:00Z')",
            new { earlier, edition = movie.Edition });
        connection.Execute(
            "INSERT INTO playback_inspection_cache (asset_id, source_hash, inspected_at, duration_secs) VALUES (@earlier, 'aaaa0000', @now, 200.0)",
            new { earlier, now = Now });

        var title = new TitleRecognitionReader(connection, null).Read(CatalogueTarget.Edition, movie.Edition);

        Assert.NotNull(title);
        Assert.Equal(200.0, title.RunningTimeSeconds);
        Assert.Equal(["aaaa0000", "ffff0000"], title.FileHashes);
    }

    private const string ContentsJson =
        """{"kind":"settings-and-personal-state","scope":"library","configuration_tables":0,"configuration_rows":0,"personal_rows":0,"choice_rows":0,"titles":0,"titles_recognised_by_file_only":0,"profiles":0,"dropped_rows":0}""";

    private static MemoryStream ArchiveWith(params (string Name, string Json)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, json) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(json);
            }
        }

        stream.Position = 0;
        return stream;
    }

    private static byte[] Archive(BackupExport export)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            BackupArchiveFormat.Write(archive, export);
        }

        return stream.ToArray();
    }

    /// <summary>Every entry of the archive, decompressed and joined, so a search is not fooled by compression.</summary>
    private static string EntryText(byte[] archiveBytes)
    {
        using var archive = new ZipArchive(new MemoryStream(archiveBytes), ZipArchiveMode.Read);
        var text = new StringBuilder();
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            text.AppendLine(reader.ReadToEnd());
        }

        return text.ToString();
    }

    private Guid AddProfile(string name)
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO profiles (id, display_name, created_at) VALUES (@id, @name, @now)",
            new { id = GuidSql.ToBlob(id), name, now = Now });
        return id;
    }

    private (byte[] Work, byte[] Edition, byte[] Asset) AddMovie(
        string title,
        string year,
        string? imdb,
        string? tmdb,
        string hash,
        double? seconds,
        string? description = null,
        string? path = null)
    {
        var work = GuidSql.ToBlob(Guid.NewGuid());
        var edition = GuidSql.ToBlob(Guid.NewGuid());
        var asset = GuidSql.ToBlob(Guid.NewGuid());
        using var connection = _database.CreateConnection();
        connection.Execute("INSERT INTO works (id, media_type) VALUES (@work, 'Movies')", new { work });
        connection.Execute("INSERT INTO editions (id, work_id, format_label) VALUES (@edition, @work, '4K Remux')", new { edition, work });
        connection.Execute(
            "INSERT INTO media_assets (id, edition_id, content_hash, file_path_root) VALUES (@asset, @edition, @hash, @path)",
            new { asset, edition, hash, path = path ?? $"/media/{hash}.mkv" });
        foreach (var (key, value) in new[] { ("title", title), ("year", year), ("description", description) })
        {
            if (value is not null)
            {
                connection.Execute(
                    "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@work, @key, @value, @now)",
                    new { work, key, value, now = Now });
            }
        }

        foreach (var (type, value) in new[] { ("imdb_id", imdb), ("tmdb_id", tmdb) })
        {
            if (value is not null)
            {
                connection.Execute(
                    "INSERT INTO bridge_ids (id, entity_id, id_type, id_value) VALUES (@id, @work, @type, @value)",
                    new { id = GuidSql.ToBlob(Guid.NewGuid()), work, type, value });
            }
        }

        if (seconds is { } duration)
        {
            connection.Execute(
                "INSERT INTO playback_inspection_cache (asset_id, source_hash, inspected_at, duration_secs) VALUES (@asset, @hash, @now, @duration)",
                new { asset, hash, now = Now, duration });
        }

        return (work, edition, asset);
    }

    private byte[] AddLocalItem(string title, string hash, bool favorite)
    {
        var item = GuidSql.ToBlob(Guid.NewGuid());
        var file = GuidSql.ToBlob(Guid.NewGuid());
        using var connection = _database.CreateConnection();
        var library = EnsureSharedLibrary(connection);
        connection.Execute(
            """
            INSERT INTO local_files (id, content_hash, byte_size, mime_type, created_at) VALUES (@file, @hash, 10, 'image/jpeg', @now);
            INSERT INTO local_items (id, scope_kind, library_id, media_kind, title, primary_file_name, primary_mime_type, created_at, updated_at, favorite)
            VALUES (@item, 'shared', @library, 'image', @title, 'photo.jpg', 'image/jpeg', @now, @now, @favorite);
            INSERT INTO local_item_files (item_id, file_id, role, position, added_at) VALUES (@item, @file, 'primary', 0, @now);
            """,
            new { item, file, library, hash, title, favorite = favorite ? 1 : 0, now = Now });
        return item;
    }

    private byte[] EnsureSharedLibrary(SqliteConnection connection)
    {
        var existing = connection.QueryFirstOrDefault<byte[]>("SELECT library_id FROM view_shared_library LIMIT 1");
        if (existing is not null)
        {
            return existing;
        }

        var household = GuidSql.ToBlob(Guid.NewGuid());
        var library = GuidSql.ToBlob(Guid.NewGuid());
        connection.Execute(
            """
            INSERT INTO households (id, name, created_at) VALUES (@household, 'Test', @now);
            INSERT INTO view_shared_library (household_id, library_id, created_at, updated_at) VALUES (@household, @library, @now, @now);
            """,
            new { household, library, now = Now });
        return library;
    }

    public void Dispose()
    {
        _database.Dispose();
        using (var pool = new SqliteConnection($"Data Source={_databasePath}"))
        {
            SqliteConnection.ClearPool(pool);
        }

        // Best-effort cleanup of the temporary data store; a locked file must not fail the test run.
        try { File.Delete(_databasePath); } catch (IOException) { }
    }
}
