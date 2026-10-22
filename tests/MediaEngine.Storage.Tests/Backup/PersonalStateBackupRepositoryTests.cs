using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Entities;
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
        var contents = new BackupExportContents(BackupScope.ForProfile(Guid.Parse("11111111-2222-3333-4444-555555555555")), 0, 0, 1, 1, 1, 0, 1, 2, 3, 4, 5);
        var export = new BackupExport([table], [choices], [title], contents, []);

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
            ("data/choices/playback_inspection_cache.json", "choices"),
            ("data/corrections/playback_inspection_cache.json", "corrections"),
            ("data/corrections/user_states.json", "corrections"),
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

    [Fact]
    public async Task Export_KeepsATitleFieldAPersonLocked_KeyedToTheTitle()
    {
        var movie = AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: null, hash: "aaaa1111", seconds: null);
        await AddClaimAsync(movie.Work, WellKnownProviders.UserManual, "title", "PERSON-TITLE-MARKER", locked: true);
        await AddClaimAsync(movie.Work, WellKnownProviders.Tmdb, "title", "PROVIDER-CLAIM-MARKER-4471");
        await AddClaimAsync(movie.Work, WellKnownProviders.UserManual, "subtitle", "SUPERSEDED-PERSON-MARKER", current: false);

        var export = _repository.Export(BackupScope.WholeLibrary);

        var claims = Assert.Single(export.Corrections);
        Assert.Equal("metadata_claims", claims.Table);
        var row = Assert.Single(claims.Rows);
        var entity = Assert.IsType<BackupValue.TitleRef>(row[claims.Columns.ToList().IndexOf("entity_id")]);
        Assert.Equal(BackupValue.FromText("PERSON-TITLE-MARKER"), row[claims.Columns.ToList().IndexOf("claim_value")]);
        var title = Assert.Single(export.Titles, candidate => candidate.Key == entity.Key);
        Assert.Equal("Work", title.Target);
        Assert.Equal("tt1160419", title.Ids["imdb_id"]);
        Assert.Equal(1, export.Contents.CorrectionRows);

        var text = EntryText(Archive(export));
        Assert.Contains("PERSON-TITLE-MARKER", text);
        Assert.DoesNotContain("PROVIDER-CLAIM-MARKER-4471", text);
        Assert.DoesNotContain("SUPERSEDED-PERSON-MARKER", text);
        Assert.Contains("data/corrections/metadata_claims.json", ArchiveEntryNames(Archive(export)));
    }

    [Fact]
    public async Task Export_KeepsAProviderClaimAPersonLocked()
    {
        var movie = AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: null, hash: "aaaa1111", seconds: null);
        await AddClaimAsync(movie.Work, WellKnownProviders.Tmdb, "genre", "LOCKED-PROVIDER-VALUE", locked: true);

        var claims = Assert.Single(_repository.Export(BackupScope.WholeLibrary).Corrections);

        Assert.Equal(BackupValue.FromText("LOCKED-PROVIDER-VALUE"), Assert.Single(claims.Rows)[claims.Columns.ToList().IndexOf("claim_value")]);
    }

    [Fact]
    public async Task Export_ForOneProfile_HoldsNoCorrectionsOrLibraryChoices()
    {
        var maya = AddProfile("Maya");
        var movie = AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: null, hash: "aaaa1111", seconds: null);
        await AddClaimAsync(movie.Work, WellKnownProviders.UserManual, "title", "PERSON-TITLE-MARKER", locked: true);

        var export = _repository.Export(BackupScope.ForProfile(maya));

        Assert.Empty(export.Corrections);
        Assert.Empty(export.Choices);
        Assert.DoesNotContain("PERSON-TITLE-MARKER", EntryText(Archive(export)));
    }

    [Fact]
    public void Export_KeepsARenamedAudiobookChapter_ButNotAnEmbeddedOne()
    {
        var book = AddMovie("Dune", "1965", imdb: null, tmdb: null, hash: "bbbb0001", seconds: null);
        using (var connection = _database.CreateConnection())
        {
            foreach (var (index, title, source) in new[] { (0, "The Spice Must Flow", "Override"), (1, "EMBEDDED-CHAPTER-MARKER", "Embedded") })
            {
                connection.Execute(
                    "INSERT INTO audiobook_chapter_title_overrides (work_id, asset_id, chapter_index, title, title_source, updated_at) VALUES (@w, @a, @i, @t, @s, @now)",
                    new { w = book.Work, a = book.Asset, i = index, t = title, s = source, now = Now });
            }
        }

        var export = _repository.Export(BackupScope.WholeLibrary);

        var chapters = Assert.Single(export.Corrections);
        Assert.Equal("audiobook_chapter_title_overrides", chapters.Table);
        var row = Assert.Single(chapters.Rows);
        Assert.Equal(BackupValue.FromText("The Spice Must Flow"), row[chapters.Columns.ToList().IndexOf("title")]);
        Assert.IsType<BackupValue.TitleRef>(row[chapters.Columns.ToList().IndexOf("asset_id")]);
        Assert.IsType<BackupValue.TitleRef>(row[chapters.Columns.ToList().IndexOf("work_id")]);
        Assert.DoesNotContain("EMBEDDED-CHAPTER-MARKER", EntryText(Archive(export)));
    }

    [Fact]
    public void Export_KeepsTheSwitchesOnALibraryBuiltCollection()
    {
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                """
                INSERT INTO collections (id, collection_type, display_name, description, is_enabled, wikidata_qid)
                VALUES (@id, 'Universe', 'Dune Universe', 'PROVIDER-DESCRIPTION-MARKER', 0, 'Q190192');
                INSERT INTO collections (id, collection_type, display_name, description, is_enabled, wikidata_qid)
                VALUES (@other, 'Universe', 'Untouched Universe', 'OTHER-DESCRIPTION-MARKER', 1, 'Q999');
                """,
                new { id = GuidSql.ToBlob(Guid.NewGuid()), other = GuidSql.ToBlob(Guid.NewGuid()) });
        }

        var export = _repository.Export(BackupScope.WholeLibrary);

        var choices = Assert.Single(export.Choices);
        Assert.Equal("collections", choices.Table);
        Assert.Equal(["ref", "is_enabled", "is_featured", "display_overrides_json"], choices.Columns);
        var row = Assert.Single(choices.Rows);
        Assert.Equal(BackupValue.FromInteger(0), row[1]);
        var shelf = Assert.Single(export.Titles, title => title.Key == Assert.IsType<BackupValue.TitleRef>(row[0]).Key);
        Assert.Equal("Q190192", shelf.Ids["wikidata_qid"]);
        Assert.Empty(Assert.Single(export.Tables, table => table.Table == "collections").Rows);

        var text = EntryText(Archive(export));
        Assert.DoesNotContain("PROVIDER-DESCRIPTION-MARKER", text);
        Assert.DoesNotContain("OTHER-DESCRIPTION-MARKER", text);
        Assert.DoesNotContain("Q999", text);
    }

    [Fact]
    public void Export_KeepsAWorksLockedSeriesPlacement_AndDropsAnUnlockedOne()
    {
        var shelf = Guid.NewGuid();
        var locked = AddMovie("Locked Book", "2001", imdb: "tt0000011", tmdb: null, hash: "cccc0001", seconds: null);
        var unlocked = AddMovie("UNLOCKED-WORK-MARKER", "2002", imdb: "tt0000022", tmdb: null, hash: "cccc0002", seconds: null);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO collections (id, collection_type, display_name, wikidata_qid) VALUES (@id, 'Series', 'Placed Series', 'Q7')",
                new { id = GuidSql.ToBlob(shelf) });
            connection.Execute(
                "UPDATE works SET series_placement_locked = 1, collection_id = @c, wikidata_match_source = 'WRONG-SOURCE-MARKER' WHERE id = @w",
                new { c = GuidSql.ToBlob(shelf), w = locked.Work });
            connection.Execute(
                "UPDATE works SET series_placement_locked = 0, collection_id = @c, wikidata_match_source = 'WRONG-SOURCE-MARKER-2' WHERE id = @w",
                new { c = GuidSql.ToBlob(shelf), w = unlocked.Work });
        }

        var export = _repository.Export(BackupScope.WholeLibrary);

        var works = Assert.Single(export.Choices, table => table.Table == "works");
        var row = Assert.Single(works.Rows);
        Assert.Equal(BackupValue.FromInteger(1), row[works.Columns.ToList().IndexOf("series_placement_locked")]);
        var placed = Assert.IsType<BackupValue.TitleRef>(row[works.Columns.ToList().IndexOf("collection_id")]);
        Assert.Equal("Q7", Assert.Single(export.Titles, title => title.Key == placed.Key).Ids["wikidata_qid"]);
        Assert.Equal(BackupValue.Null, row[works.Columns.ToList().IndexOf("wikidata_match_source")]);

        var text = EntryText(Archive(export));
        Assert.DoesNotContain("UNLOCKED-WORK-MARKER", text);
        Assert.DoesNotContain("WRONG-SOURCE-MARKER", text);
        Assert.DoesNotContain("tt0000022", text);
    }

    [Fact]
    public void Export_KeepsAFaceNameAPersonGave_ButNotAMachineDetection()
    {
        var item = AddLocalItem("Beach day", "ffff0010", favorite: false);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                """
                INSERT INTO local_item_annotations (id, item_id, annotation_kind, annotation_value, source, created_at, reviewed_at)
                VALUES (@a, @item, 'person_name', 'Maya Rao', 'user_assignment', @now, @now);
                INSERT INTO local_item_annotations (id, item_id, annotation_kind, annotation_value, confidence, source, model_name, model_version, provenance_json, created_at)
                VALUES (@b, @item, 'face_identity', 'MACHINE-DETECTION-MARKER', 0.91, 'face_model', 'MODEL-NAME-MARKER', '3', 'PROVENANCE-MARKER', @now);
                """,
                new { a = GuidSql.ToBlob(Guid.NewGuid()), b = GuidSql.ToBlob(Guid.NewGuid()), item, now = Now });
        }

        var export = _repository.Export(BackupScope.WholeLibrary);

        var annotations = Assert.Single(export.Corrections, table => table.Table == "local_item_annotations");
        var row = Assert.Single(annotations.Rows);
        Assert.Equal(BackupValue.FromText("Maya Rao"), row[annotations.Columns.ToList().IndexOf("annotation_value")]);
        Assert.IsType<BackupValue.TitleRef>(row[annotations.Columns.ToList().IndexOf("item_id")]);
        Assert.Equal(BackupValue.Null, row[annotations.Columns.ToList().IndexOf("model_name")]);
        var text = EntryText(Archive(export));
        Assert.DoesNotContain("MACHINE-DETECTION-MARKER", text);
        Assert.DoesNotContain("MODEL-NAME-MARKER", text);
        Assert.DoesNotContain("PROVENANCE-MARKER", text);
    }

    [Fact]
    public void Export_KeepsACoverAPersonChoseFromAProvider_ButNotAnUpload()
    {
        var movie = AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: null, hash: "aaaa1111", seconds: null);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                """
                INSERT INTO entity_assets (id, entity_id, entity_type, asset_type, image_url, local_image_path, source_provider, is_user_override)
                VALUES (@a, @work, 'Work', 'CoverArt', 'https://image.tmdb.org/t/p/original/chosen.jpg', '/art/LOCAL-PATH-MARKER.jpg', 'tmdb', 1);
                INSERT INTO entity_assets (id, entity_id, entity_type, asset_type, image_url, source_provider, is_user_override)
                VALUES (@b, @work, 'Work', 'CoverArt', 'https://example.test/UPLOAD-MARKER.jpg', 'user_upload', 1);
                INSERT INTO entity_assets (id, entity_id, entity_type, asset_type, image_url, source_provider, is_user_override)
                VALUES (@c, @work, 'Work', 'CoverArt', 'https://example.test/NOT-CHOSEN-MARKER.jpg', 'tmdb', 0);
                """,
                new { a = GuidSql.ToBlob(Guid.NewGuid()), b = GuidSql.ToBlob(Guid.NewGuid()), c = GuidSql.ToBlob(Guid.NewGuid()), work = movie.Work });
        }

        var export = _repository.Export(BackupScope.WholeLibrary);

        var artwork = Assert.Single(export.Corrections, table => table.Table == "entity_assets");
        var row = Assert.Single(artwork.Rows);
        Assert.Equal(BackupValue.FromText("https://image.tmdb.org/t/p/original/chosen.jpg"), row[artwork.Columns.ToList().IndexOf("image_url")]);
        Assert.Equal(BackupValue.FromText("tmdb"), row[artwork.Columns.ToList().IndexOf("source_provider")]);
        Assert.IsType<BackupValue.TitleRef>(row[artwork.Columns.ToList().IndexOf("entity_id")]);
        Assert.Equal(BackupValue.Null, row[artwork.Columns.ToList().IndexOf("id")]);
        var text = EntryText(Archive(export));
        Assert.DoesNotContain("LOCAL-PATH-MARKER", text);
        Assert.DoesNotContain("UPLOAD-MARKER", text);
        Assert.DoesNotContain("NOT-CHOSEN-MARKER", text);
    }

    [Fact]
    public void Export_KeepsACoverAPersonPickedFromAProvider_KeyedToItsSourceAddress()
    {
        var movie = AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: null, hash: "aaaa1111", seconds: null);
        var asset = AddArtworkAsset("tmdb", "https://image.tmdb.org/t/p/original/picked.jpg", "picked-ref", "ARTWORK-PATH-MARKER", "arthash1");
        var notPicked = AddArtworkAsset("tmdb", "https://example.test/NOT-PICKED-MARKER.jpg", null, "OTHER-PATH-MARKER", "arthash2");
        AddArtworkLink(movie.Work, asset, userOverride: true);
        AddArtworkLink(movie.Work, notPicked, userOverride: false);

        var export = _repository.Export(BackupScope.WholeLibrary);

        var links = Assert.Single(export.Corrections, table => table.Table == "entity_artwork_links");
        var row = Assert.Single(links.Rows);
        Assert.IsType<BackupValue.TitleRef>(row[links.Columns.ToList().IndexOf("entity_id")]);
        Assert.Equal(BackupValue.FromText("Primary"), row[links.Columns.ToList().IndexOf("role")]);
        Assert.Equal(BackupValue.FromInteger(1), row[links.Columns.ToList().IndexOf("is_preferred")]);
        var reference = Assert.IsType<BackupValue.TitleRef>(row[links.Columns.ToList().IndexOf("artwork_asset_id")]);
        var artwork = Assert.Single(export.Titles, title => title.Key == reference.Key);
        Assert.Equal("ArtworkAsset", artwork.Target);
        Assert.Equal("https://image.tmdb.org/t/p/original/picked.jpg", artwork.Ids["source_url"]);
        Assert.Equal("tmdb", artwork.Ids["source_provider"]);
        Assert.Equal("picked-ref", artwork.Ids["provider_reference"]);
        Assert.Empty(artwork.FileHashes);
        Assert.Null(artwork.Name);

        var bytes = Archive(export);
        var text = EntryText(bytes);
        Assert.DoesNotContain("ARTWORK-PATH-MARKER", text);
        Assert.DoesNotContain("arthash1", text);
        Assert.DoesNotContain("NOT-PICKED-MARKER", text);
        Assert.DoesNotContain("OTHER-PATH-MARKER", text);
    }

    [Fact]
    public void Export_LeavesOutACoverAPersonUploaded()
    {
        var movie = AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: null, hash: "aaaa1111", seconds: null);
        var upload = AddArtworkAsset("user_upload", "https://example.test/UPLOAD-SOURCE-MARKER.jpg", null, "UPLOAD-PATH-MARKER", "arthash3");
        var anonymous = AddArtworkAsset("tmdb", null, null, "ANON-PATH-MARKER", "arthash4");
        AddArtworkLink(movie.Work, upload, userOverride: true);
        AddArtworkLink(movie.Work, anonymous, userOverride: true);

        var export = _repository.Export(BackupScope.WholeLibrary);

        Assert.DoesNotContain(export.Corrections, table => table.Table == "entity_artwork_links");
        Assert.Equal(0, export.Contents.DroppedRows);
        Assert.Equal(0, export.Contents.WithheldRows);
        var text = EntryText(Archive(export));
        Assert.DoesNotContain("UPLOAD-SOURCE-MARKER", text);
        Assert.DoesNotContain("UPLOAD-PATH-MARKER", text);
        Assert.DoesNotContain("ANON-PATH-MARKER", text);
    }

    [Fact]
    public void Export_KeepsAPhotosLocationAndDescriptionAPersonSet_ButNotItsEmbeddedData()
    {
        var edited = AddLocalItem("Honeymoon", "ffff0020", favorite: false);
        var untouched = AddLocalItem("Plain", "ffff0021", favorite: false);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                """
                INSERT INTO local_item_metadata (item_id, latitude, longitude, location_name, location_city, location_source, location_user_override,
                    embedded_latitude, embedded_longitude, description, device_make, lens_model, document_text, updated_at)
                VALUES (@edited, 48.8566, 2.3522, 'PERSON-PLACE-NAME', 'Paris', 'user', 1, 12.3456, 65.4321, 'Our honeymoon', 'DEVICE-MARKER', 'LENS-MARKER', 'DOCTEXT-MARKER', @now);
                INSERT INTO local_item_metadata (item_id, latitude, longitude, location_name, location_source, location_user_override,
                    embedded_latitude, embedded_longitude, device_make, document_text, updated_at)
                VALUES (@untouched, 10.5, 20.5, 'EMBEDDED-PLACE-MARKER', 'embedded', 0, 10.5, 20.5, 'OTHER-DEVICE-MARKER', 'OTHER-DOCTEXT-MARKER', @now);
                """,
                new { edited, untouched, now = Now });
        }

        var export = _repository.Export(BackupScope.WholeLibrary);

        var metadata = Assert.Single(export.Choices, table => table.Table == "local_item_metadata");
        var row = Assert.Single(metadata.Rows);
        var columns = metadata.Columns.ToList();
        var reference = Assert.IsType<BackupValue.TitleRef>(row[0]);
        Assert.Equal("LocalItem", Assert.Single(export.Titles, title => title.Key == reference.Key).Target);
        Assert.Equal(BackupValue.FromText("Our honeymoon"), row[columns.IndexOf("description")]);
        Assert.Equal(BackupValue.FromInteger(1), row[columns.IndexOf("location_user_override")]);
        Assert.Equal(BackupValue.FromText("PERSON-PLACE-NAME"), row[columns.IndexOf("location_name")]);
        Assert.Equal(BackupValue.FromReal(48.8566), row[columns.IndexOf("latitude")]);
        Assert.DoesNotContain("embedded_latitude", columns);
        Assert.DoesNotContain("document_text", columns);

        var text = EntryText(Archive(export));
        foreach (var marker in new[] { "DEVICE-MARKER", "LENS-MARKER", "DOCTEXT-MARKER", "EMBEDDED-PLACE-MARKER", "12.3456", "65.4321", "ffff0021" })
        {
            Assert.DoesNotContain(marker, text);
        }
    }

    [Fact]
    public void Export_KeepsWhichEpisodesAPersonSaidAFileCovers()
    {
        var first = AddMovie("Episode One", "2020", imdb: "tt0000101", tmdb: null, hash: "dddd0001", seconds: null);
        var second = AddMovie("Episode Two", "2020", imdb: "tt0000102", tmdb: null, hash: "dddd0002", seconds: null);
        var other = AddMovie("Other Show", "2021", imdb: "tt0000103", tmdb: null, hash: "dddd0003", seconds: null);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                """
                INSERT INTO media_asset_coverage (asset_id, work_id, position, source) VALUES (@a1, @w1, 1, 'manual');
                INSERT INTO media_asset_coverage (asset_id, work_id, position, source) VALUES (@a1, @w2, 2, 'manual');
                INSERT INTO media_asset_coverage (asset_id, work_id, position, source) VALUES (@a3, @w3, 1, 'filename');
                """,
                new { a1 = first.Asset, w1 = first.Work, w2 = second.Work, a3 = other.Asset, w3 = other.Work });
        }

        var export = _repository.Export(BackupScope.WholeLibrary);

        var coverage = Assert.Single(export.Corrections, table => table.Table == "media_asset_coverage");
        Assert.Equal(2, coverage.Rows.Count);
        var columns = coverage.Columns.ToList();
        Assert.All(coverage.Rows, row =>
        {
            Assert.IsType<BackupValue.TitleRef>(row[columns.IndexOf("asset_id")]);
            Assert.IsType<BackupValue.TitleRef>(row[columns.IndexOf("work_id")]);
            Assert.Equal(BackupValue.FromText("manual"), row[columns.IndexOf("source")]);
        });
        Assert.Equal([1L, 2L], coverage.Rows.Select(row => ((BackupValue.Integer)row[columns.IndexOf("position")]).Value).Order().ToArray());
        Assert.DoesNotContain("tt0000103", EntryText(Archive(export)));
    }

    [Fact]
    public async Task Export_DropsACorrectionOnAWithheldTitle()
    {
        var hidden = AddMovie("Hidden", "2022", imdb: "tt0000201", tmdb: null, hash: "eeee0001", seconds: null);
        var shown = AddMovie("Shown", "2022", imdb: "tt0000202", tmdb: null, hash: "eeee0002", seconds: null);
        await AddClaimAsync(hidden.Work, WellKnownProviders.UserManual, "title", "HIDDEN-CLAIM-MARKER", locked: true);
        await AddClaimAsync(shown.Work, WellKnownProviders.UserManual, "title", "SHOWN-CLAIM-MARKER", locked: true);
        var repository = new PersonalStateBackupRepository(
            _database,
            null,
            (target, id) => target == CatalogueTarget.Work && id.AsSpan().SequenceEqual(hidden.Work));

        var export = repository.Export(BackupScope.WholeLibrary);

        Assert.Equal(1, export.Contents.WithheldRows);
        Assert.Equal(0, export.Contents.DroppedRows);
        var claims = Assert.Single(export.Corrections);
        Assert.Equal(BackupValue.FromText("SHOWN-CLAIM-MARKER"), Assert.Single(claims.Rows)[claims.Columns.ToList().IndexOf("claim_value")]);
        var title = Assert.Single(export.Titles);
        Assert.Equal("tt0000202", title.Ids["imdb_id"]);
        var text = EntryText(Archive(export));
        Assert.DoesNotContain("HIDDEN-CLAIM-MARKER", text);
        Assert.DoesNotContain("tt0000201", text);
        Assert.DoesNotContain("eeee0001", text);
    }

    [Fact]
    public void Export_KeepsAFictionalThingCorrection_KeyedToItsWikidataId()
    {
        var entity = GuidSql.ToBlob(Guid.NewGuid());
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                """
                INSERT INTO fictional_entities (id, wikidata_qid, label, entity_sub_type, created_at) VALUES (@e, 'Q5', 'Paul Atreides', 'Character', @now);
                INSERT INTO fictional_entity_user_overrides (entity_id, label, description) VALUES (@e, 'Muad''Dib', 'Own words');
                """,
                new { e = entity, now = Now });
        }

        var export = _repository.Export(BackupScope.WholeLibrary);

        var overrides = Assert.Single(export.Tables, table => table.Table == "fictional_entity_user_overrides");
        var row = Assert.Single(overrides.Rows);
        var reference = Assert.IsType<BackupValue.TitleRef>(row[overrides.Columns.ToList().IndexOf("entity_id")]);
        var title = Assert.Single(export.Titles, candidate => candidate.Key == reference.Key);
        Assert.Equal("FictionalEntity", title.Target);
        Assert.Equal("Q5", title.Ids["wikidata_qid"]);
        Assert.Equal(BackupValue.FromText("Muad'Dib"), row[overrides.Columns.ToList().IndexOf("label")]);
    }

    [Fact]
    public void Export_WritesTheTwoStepSecretReadable_WhenTheCodecCanReadIt()
    {
        AddTwoStepSecret("PROTECTED-BLOB-MARKER");
        var repository = new PersonalStateBackupRepository(_database, new FakePortableCodec(canRead: true));

        var export = repository.Export(BackupScope.WholeLibrary);

        var table = Assert.Single(export.Tables, candidate => candidate.Table == "account_two_step");
        var value = Assert.Single(table.Rows)[table.Columns.ToList().IndexOf("secret_protected")];
        Assert.Equal(BackupValue.FromPortable("readable:PROTECTED-BLOB-MARKER"), value);
        Assert.Equal(0, export.Contents.PortableValuesKeptMachineBound);
        var text = EntryText(Archive(export));
        Assert.Contains("{\"portable\":\"readable:PROTECTED-BLOB-MARKER\"}", text);
    }

    [Fact]
    public void Export_KeepsTheTwoStepSecretMachineBound_WhenTheCodecCannotReadIt()
    {
        AddTwoStepSecret("PROTECTED-BLOB-MARKER");
        var repository = new PersonalStateBackupRepository(_database, new FakePortableCodec(canRead: false));

        var export = repository.Export(BackupScope.WholeLibrary);

        var table = Assert.Single(export.Tables, candidate => candidate.Table == "account_two_step");
        Assert.Equal(BackupValue.FromText("PROTECTED-BLOB-MARKER"), Assert.Single(table.Rows)[table.Columns.ToList().IndexOf("secret_protected")]);
        Assert.Equal(1, export.Contents.PortableValuesKeptMachineBound);
    }

    [Fact]
    public void Export_LeavesThePortableColumnUnchanged_WhenNoCodecIsRegistered()
    {
        AddTwoStepSecret("PROTECTED-BLOB-MARKER");

        var export = _repository.Export(BackupScope.WholeLibrary);

        var table = Assert.Single(export.Tables, candidate => candidate.Table == "account_two_step");
        Assert.Equal(BackupValue.FromText("PROTECTED-BLOB-MARKER"), Assert.Single(table.Rows)[table.Columns.ToList().IndexOf("secret_protected")]);
        Assert.Equal(0, export.Contents.PortableValuesKeptMachineBound);
    }

    [Fact]
    public void ArchiveFormat_RoundTripsCorrectionsPortableValuesAndGatedChoices()
    {
        var title = BackupTitle.Empty("t1", "Work") with { Ids = new Dictionary<string, string> { ["imdb_id"] = "tt1" } };
        var corrections = new BackupExportTable(
            "metadata_claims",
            BackupDataKind.Catalogue,
            ["id", "entity_id", "claim_value"],
            [[BackupValue.FromBlob([1, 2, 3, 4]), BackupValue.FromTitleRef("t1"), BackupValue.FromText("Own title")]]);
        var secret = new BackupExportTable(
            "account_two_step",
            BackupDataKind.Configuration,
            ["account_id", "secret_protected"],
            [[BackupValue.FromBlob([9]), BackupValue.FromPortable("JBSWY3DPEHPK3PXP")]]);
        var works = new BackupExportTable(
            "works",
            BackupDataKind.Catalogue,
            ["ref", "series_placement_locked", "collection_id", "wikidata_match_locked", "wikidata_match_source"],
            [[BackupValue.FromTitleRef("t1"), BackupValue.FromInteger(1), BackupValue.FromTitleRef("t1"), BackupValue.FromInteger(0), BackupValue.Null]]);
        var contents = new BackupExportContents(BackupScope.WholeLibrary, 1, 1, 0, 1, 1, 0, 1, 0, 1, 2, 3);
        var export = new BackupExport([secret], [works], [title], contents, [corrections]);

        var read = RoundTrip(export);

        Assert.Equal(secret.Rows[0], Assert.Single(Assert.Single(read.Tables).Rows));
        Assert.IsType<BackupValue.Portable>(Assert.Single(read.Tables).Rows[0][1]);
        Assert.Equal(works.Rows[0], Assert.Single(Assert.Single(read.Choices).Rows));
        var readCorrections = Assert.Single(read.Corrections);
        Assert.Equal(corrections.Rows[0], Assert.Single(readCorrections.Rows));
        Assert.Equal(contents, read.Contents);
    }

    [Fact]
    public void ArchiveFormat_RefusesACorrectionsFileForATableWithNoCorrectionCondition()
    {
        using var stream = ArchiveWith(
            ("data/contents.json", ContentsJson),
            ("data/titles.json", """{"titles":[]}"""),
            ("data/corrections/works.json", """{"table":"works","kind":"corrections","columns":["id"],"rows":[]}"""));
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.Throws<InvalidDataException>(() => BackupArchiveFormat.Read(archive));
    }

    [Fact]
    public void ArchiveFormat_RefusesAPortableValueInAColumnThatIsNotPortable()
    {
        foreach (var column in new[] { "created_at", "account_id" })
        {
            using var stream = ArchiveWith(
                ("data/contents.json", ContentsJson),
                ("data/titles.json", """{"titles":[]}"""),
                ("data/configuration/account_two_step.json", $$"""{"table":"account_two_step","kind":"configuration","columns":["{{column}}"],"rows":[[{"portable":"x"}]]}"""));
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            Assert.Throws<InvalidDataException>(() => BackupArchiveFormat.Read(archive));
        }

        using var inPersonal = ArchiveWith(
            ("data/contents.json", ContentsJson),
            ("data/titles.json", """{"titles":[]}"""),
            ("data/personal/user_states.json", """{"table":"user_states","kind":"personal_state","columns":["progress_pct"],"rows":[[{"portable":"x"}]]}"""));
        using var personalArchive = new ZipArchive(inPersonal, ZipArchiveMode.Read);
        Assert.Throws<InvalidDataException>(() => BackupArchiveFormat.Read(personalArchive));
    }

    [Fact]
    public void ArchiveFormat_RefusesAChoicesFileForAConfigurationTableThatKeepsAllItsRows()
    {
        using var stream = ArchiveWith(
            ("data/contents.json", ContentsJson),
            ("data/titles.json", """{"titles":[]}"""),
            ("data/choices/profiles.json", """{"table":"profiles","kind":"choices","columns":["ref"],"rows":[]}"""));
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.Throws<InvalidDataException>(() => BackupArchiveFormat.Read(archive));
    }

    private sealed class FakePortableCodec(bool canRead) : IBackupPortableValueCodec
    {
        public string? Export(string table, string column, string protectedValue) => canRead ? "readable:" + protectedValue : null;

        public string Import(string table, string column, string portableValue) => portableValue;
    }

    private byte[] AddArtworkAsset(string provider, string? url, string? providerReference, string path, string hash)
    {
        var id = GuidSql.ToBlob(Guid.NewGuid());
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO artwork_assets (id, content_hash, original_path, source_provider, source_url, provider_reference)
            VALUES (@id, @hash, @path, @provider, @url, @providerReference)
            """,
            new { id, hash, path = "/art/" + path + ".jpg", provider, url, providerReference });
        return id;
    }

    private void AddArtworkLink(byte[] entity, byte[] asset, bool userOverride)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO entity_artwork_links (id, entity_id, entity_type, artwork_asset_id, role, is_preferred, is_user_override)
            VALUES (@id, @entity, 'Work', @asset, 'Primary', 1, @userOverride)
            """,
            new { id = GuidSql.ToBlob(Guid.NewGuid()), entity, asset, userOverride = userOverride ? 1 : 0 });
    }

    private void AddTwoStepSecret(string protectedValue)
    {
        var account = GuidSql.ToBlob(Guid.NewGuid());
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO accounts (id, created_at, updated_at) VALUES (@account, @now, @now);
            INSERT INTO account_two_step (account_id, secret_protected, created_at) VALUES (@account, @secret, @now);
            """,
            new { account, secret = protectedValue, now = Now });
    }

    private async Task AddClaimAsync(byte[] work, Guid provider, string key, string value, bool locked = false, bool current = true)
    {
        var claims = new MetadataClaimRepository(_database);
        await claims.InsertBatchAsync(
        [
            new MetadataClaim
            {
                Id = Guid.NewGuid(),
                EntityId = new Guid(work, bigEndian: true),
                ProviderId = provider,
                ClaimKey = key,
                ClaimValue = value,
                Confidence = 1.0,
                ClaimedAt = DateTimeOffset.UtcNow,
                IsUserLocked = locked,
                IsCurrent = current,
            },
        ]);
    }

    private static BackupExport RoundTrip(BackupExport export)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            BackupArchiveFormat.Write(archive, export);
        }

        stream.Position = 0;
        using var readable = new ZipArchive(stream, ZipArchiveMode.Read);
        return BackupArchiveFormat.Read(readable);
    }

    private static List<string> ArchiveEntryNames(byte[] archiveBytes)
    {
        using var archive = new ZipArchive(new MemoryStream(archiveBytes), ZipArchiveMode.Read);
        return archive.Entries.Select(entry => entry.FullName).ToList();
    }

    private const string ContentsJson =
        """{"kind":"settings-and-personal-state","scope":"library","configuration_tables":0,"configuration_rows":0,"personal_rows":0,"choice_rows":0,"titles":0,"titles_recognised_by_file_only":0,"profiles":0,"dropped_rows":0,"correction_rows":0,"portable_values_kept_machine_bound":0,"withheld_rows":0}""";

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
