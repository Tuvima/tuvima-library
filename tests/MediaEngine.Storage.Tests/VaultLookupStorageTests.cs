using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

// What the Library remembers about a Vault video's match (Videos spec WP12): the matched record, where each field came from, the
// fingerprints computed here, and the quiet activity list. The tables refuse a row for any file that is not in a Vault library.
public sealed class VaultLookupStorageTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_vault_lookup_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly VaultMatchRepository _matches;
    private readonly VaultActivityRepository _activity;
    private readonly DateTimeOffset _now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly string _vaultLibrary = Guid.NewGuid().ToString("D");
    private readonly string _normalLibrary = Guid.NewGuid().ToString("D");
    private readonly Guid _vaultAsset;
    private readonly Guid _normalAsset;

    public VaultLookupStorageTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _matches = new VaultMatchRepository(_database);
        _activity = new VaultActivityRepository(_database);
        var admin = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO profiles(id,display_name,avatar_color,role,created_at) VALUES(@admin,'Admin','#7C4DFF','Administrator',@now);",
                new { admin, now = _now.ToString("O") });
            connection.Execute(
                "INSERT INTO vault_libraries(library_id, lookup_source, lookup_server_id, adults_confirmed_at, created_by_profile_id, created_at, updated_at) VALUES(@id,'none',NULL,@now,@admin,@now,@now);",
                new { id = _vaultLibrary, admin, now = _now.ToString("O") });
        }

        _vaultAsset = InsertAsset(_vaultLibrary, "C:/vault/clip.mkv");
        _normalAsset = InsertAsset(_normalLibrary, "C:/videos/clip.mkv");
    }

    public void Dispose()
    {
        try { _database.Dispose(); } catch (ObjectDisposedException) { }
        using (var pool = new SqliteConnection($"Data Source={_databasePath}"))
        {
            SqliteConnection.ClearPool(pool);
        }

        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private Guid InsertAsset(string libraryId, string path)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@workId, 'Videos', 'standalone', 'accepted');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, library_id)
            VALUES (@assetId, @editionId, @hash, @path, CURRENT_TIMESTAMP, @libraryId);
            """,
            new { workId, editionId, assetId, path, libraryId, hash = "hash-" + assetId.ToString("N") });
        return assetId;
    }

    private VaultFieldSource Source(Guid asset, string field, string record = "rec-1", string by = "fingerprint", string via = "automatic") =>
        new(asset, field, "stashdb", record, by, via, "und-Latn", _now);

    // ---------------------------------------------------------------- the match

    [Fact]
    public async Task AVideoWithNoMatchRow_IsUnmatched()
    {
        Assert.Null(await _matches.GetMatchAsync(_vaultAsset));
        Assert.Empty(await _matches.GetFieldSourcesAsync(_vaultAsset));
    }

    [Fact]
    public async Task SaveMatch_KeepsTheRecordAndWhereEachFieldCameFrom()
    {
        await _matches.SaveMatchAsync(
            new VaultMatch(_vaultAsset, "stashdb", "rec-1", "fingerprint", "automatic", _now),
            [Source(_vaultAsset, "title"), Source(_vaultAsset, "cover")]);

        var match = await _matches.GetMatchAsync(_vaultAsset);
        Assert.Equal(("stashdb", "rec-1", "fingerprint", "automatic"), (match!.ServerId, match.RecordId, match.MatchedBy, match.AppliedVia));
        Assert.Equal(_now, match.MatchedAt);
        var sources = await _matches.GetFieldSourcesAsync(_vaultAsset);
        Assert.Equal(["cover", "title"], sources.Select(s => s.Field).Order());
        Assert.All(sources, s => Assert.Equal("rec-1", s.RecordId));
    }

    [Fact]
    public async Task AnIdentifyReplacesTheTickedFieldsAndLeavesTheOthersTheirOwnSource()
    {
        await _matches.SaveMatchAsync(
            new VaultMatch(_vaultAsset, "stashdb", "rec-1", "fingerprint", "automatic", _now),
            [Source(_vaultAsset, "title"), Source(_vaultAsset, "description")]);

        await _matches.SaveMatchAsync(
            new VaultMatch(_vaultAsset, "stashdb", "rec-2", "identify", "identify", _now.AddHours(1)),
            [Source(_vaultAsset, "title", "rec-2", "identify", "identify")]);

        var sources = (await _matches.GetFieldSourcesAsync(_vaultAsset)).ToDictionary(s => s.Field);
        Assert.Equal("rec-2", sources["title"].RecordId);
        Assert.Equal("identify", sources["title"].AppliedVia);
        Assert.Equal("rec-1", sources["description"].RecordId);
        Assert.Equal("rec-2", (await _matches.GetMatchAsync(_vaultAsset))!.RecordId);
    }

    [Fact]
    public async Task ANormalFile_CannotHaveAVaultMatch_ByTheDataStructureItself()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => _matches.SaveMatchAsync(
            new VaultMatch(_normalAsset, "stashdb", "rec-1", "title", "automatic", _now), []));

        Assert.Contains("Only a Vault file", error.ToString(), StringComparison.Ordinal);
        using var connection = _database.CreateConnection();
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM vault_matches"));
    }

    [Fact]
    public async Task AnUnknownSignalOrField_IsRefusedBeforeAnythingIsWritten()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _matches.SaveMatchAsync(
            new VaultMatch(_vaultAsset, "stashdb", "rec-1", "guess", "automatic", _now), []));
        await Assert.ThrowsAsync<ArgumentException>(() => _matches.SaveMatchAsync(
            new VaultMatch(_vaultAsset, "stashdb", "rec-1", "title", "automatic", _now), [Source(_vaultAsset, "hair_colour")]));

        Assert.Null(await _matches.GetMatchAsync(_vaultAsset));
    }

    [Fact]
    public async Task TheMatchGoesWithTheFile()
    {
        await _matches.SaveMatchAsync(
            new VaultMatch(_vaultAsset, "stashdb", "rec-1", "title", "automatic", _now), [Source(_vaultAsset, "title")]);
        await _matches.SaveFingerprintsAsync(_vaultAsset, [new("OSHASH", "8e245d9679d31e12", 100, _now)]);
        await _activity.AddAsync(_vaultAsset, "added", "clip.mkv", "Added to the Vault.", _now);

        using var connection = _database.CreateConnection();
        connection.Execute("PRAGMA foreign_keys = ON; DELETE FROM media_assets WHERE id = @id", new { id = _vaultAsset });

        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM vault_matches"));
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM vault_field_sources"));
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM vault_file_fingerprints"));
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM vault_activity"));
    }

    // ------------------------------------------------------------ file facts and fingerprints

    [Fact]
    public async Task FileFacts_AreOnlyGivenForAVaultFile_WithTheLengthFromTheInspection()
    {
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO playback_inspection_cache(asset_id, source_hash, inspected_at, file_size, duration_secs) VALUES(@id,@hash,@now,4096,1800.5);",
                new { id = _vaultAsset, hash = "hash-" + _vaultAsset.ToString("N"), now = _now.ToString("O") });
        }

        var facts = await _matches.GetFileFactsAsync(_vaultAsset);

        Assert.Equal(1800.5, facts!.DurationSeconds);
        Assert.Equal(4096, facts.SizeBytes);
        Assert.Equal("C:/vault/clip.mkv", facts.FilePath);
        Assert.Null(await _matches.GetFileFactsAsync(_normalAsset));
    }

    [Fact]
    public async Task FileFacts_WithNoInspectionYet_HaveNoLength()
    {
        Assert.Null((await _matches.GetFileFactsAsync(_vaultAsset))!.DurationSeconds);
    }

    [Fact]
    public async Task Fingerprints_AreKeptAndReplacedAsAWhole()
    {
        await _matches.SaveFingerprintsAsync(_vaultAsset, [new("OSHASH", "aaaaaaaaaaaaaaaa", 100, _now), new("MD5", "bbbb", 100, _now)]);
        await _matches.SaveFingerprintsAsync(_vaultAsset, [new("OSHASH", "cccccccccccccccc", 200, _now)]);

        var stored = await _matches.GetFingerprintsAsync(_vaultAsset);

        var only = Assert.Single(stored);
        Assert.Equal(("OSHASH", "cccccccccccccccc", 200), (only.Algorithm, only.Hash, only.FileSize));
    }

    [Fact]
    public async Task Fingerprints_CannotBeKeptForANormalFile()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => _matches.SaveFingerprintsAsync(_normalAsset, [new("OSHASH", "aaaaaaaaaaaaaaaa", 100, _now)]));

        using var connection = _database.CreateConnection();
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM vault_file_fingerprints"));
    }

    // -------------------------------------------------------------------- activity

    [Fact]
    public async Task Activity_ListsNewestFirst_AndTrimsLongOrControlText()
    {
        await _activity.AddAsync(_vaultAsset, "added", "clip.mkv", "Added to the Vault.", _now);
        await _activity.AddAsync(_vaultAsset, "unmatched", "clip\u0007.mkv" + new string('x', 400), new string('d', 500), _now.AddMinutes(1));

        var items = await _activity.GetRecentAsync(10);

        Assert.Equal(["unmatched", "added"], items.Select(i => i.Kind));
        Assert.DoesNotContain('\u0007', items[0].FileName);
        Assert.True(items[0].FileName.Length <= 260);
        Assert.True(items[0].Detail.Length <= 200);
        Assert.Single(await _activity.GetRecentAsync(1));
    }

    [Fact]
    public async Task Activity_RefusesAnUnknownKind_AndANormalFile()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _activity.AddAsync(_vaultAsset, "party", "clip.mkv", "x", _now));
        await Assert.ThrowsAnyAsync<Exception>(() => _activity.AddAsync(_normalAsset, "added", "clip.mkv", "x", _now));

        Assert.Empty(await _activity.GetRecentAsync(10));
    }

    [Fact]
    public async Task VaultNames_CanBeUnlinkedKindByKind_ForIdentify()
    {
        var names = new VaultNameRepository(_database);
        var performer = await names.EnsurePerformerAsync(_vaultLibrary, "Performer One", null, "stashbox", _now);
        var studio = await names.EnsureStudioAsync(_vaultLibrary, "Example Studio", "stashbox", _now);
        var tag = await names.EnsureTagAsync(_vaultLibrary, "Tag One", _now);
        await names.LinkPerformerAsync(_vaultAsset, performer);
        await names.LinkStudioAsync(_vaultAsset, studio);
        await names.LinkTagAsync(_vaultAsset, tag);

        await names.UnlinkPerformersAsync(_vaultAsset);

        Assert.Empty(await names.GetPerformersOfAsync(_vaultAsset));
        Assert.Single(await names.GetStudiosOfAsync(_vaultAsset));
        Assert.Single(await names.GetTagsOfAsync(_vaultAsset));

        await names.UnlinkStudiosAsync(_vaultAsset);
        await names.UnlinkTagsAsync(_vaultAsset);

        Assert.Empty(await names.GetStudiosOfAsync(_vaultAsset));
        Assert.Empty(await names.GetTagsOfAsync(_vaultAsset));
    }
}
