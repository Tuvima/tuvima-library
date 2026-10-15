using System.Text.Json;
using Dapper;
using MediaEngine.Contracts.Playback;
using MediaEngine.Storage;
using MediaEngine.Storage.Playback;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.Tests;

public sealed class PlaybackStateRepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima-playback-state-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;

    public PlaybackStateRepositoryTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
    }

    [Fact]
    public async Task StoreInspectionAsync_UsesGuidBlobAssetForeignKey()
    {
        var assetId = await CreateAssetAsync();
        var repository = new PlaybackStateRepository(_db);

        await repository.StoreInspectionAsync(
            assetId,
            sourceHash: $"hash-{Guid.NewGuid():N}",
            fileSize: 1234,
            durationSecs: 65,
            container: "m4b",
            metadataJson: "{}");

        using var conn = _db.CreateConnection();
        var storageType = await conn.ExecuteScalarAsync<string>(
            "SELECT typeof(asset_id) FROM playback_inspection_cache WHERE asset_id = @assetId",
            new { assetId });

        Assert.Equal("blob", storageType);
    }

    [Fact]
    public async Task NativeEncodeJobs_AreScopedToTheirProfileAndDevice()
    {
        var assetId = await CreateAssetAsync();
        var repository = new PlaybackStateRepository(_db);
        var firstProfile = Guid.NewGuid();
        var firstDevice = Guid.NewGuid();
        var secondProfile = Guid.NewGuid();
        var secondDevice = Guid.NewGuid();

        var first = await repository.QueueEncodeJobAsync(
            assetId,
            "mobile-standard",
            "same-source",
            null,
            firstProfile,
            firstDevice);
        var second = await repository.QueueEncodeJobAsync(
            assetId,
            "mobile-standard",
            "same-source",
            null,
            secondProfile,
            secondDevice);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal([first.Id], (await repository.ListEncodeJobsAsync(firstProfile, firstDevice)).Select(job => job.Id));
        Assert.Equal([second.Id], (await repository.ListEncodeJobsAsync(secondProfile, secondDevice)).Select(job => job.Id));
        Assert.False(await repository.CancelEncodeJobAsync(first.Id, secondProfile, secondDevice));
        Assert.True(await repository.CancelEncodeJobAsync(first.Id, firstProfile, firstDevice));
    }

    [Fact]
    public async Task NativeOfflineVariants_AreScopedToTheJobProfileAndDevice()
    {
        var assetId = await CreateAssetAsync();
        var repository = new PlaybackStateRepository(_db);
        var firstProfile = Guid.NewGuid();
        var firstDevice = Guid.NewGuid();
        var secondProfile = Guid.NewGuid();
        var secondDevice = Guid.NewGuid();

        await repository.QueueEncodeJobAsync(assetId, "mobile-standard", "same-source", null, firstProfile, firstDevice);
        await repository.QueueEncodeJobAsync(assetId, "mobile-standard", "same-source", null, secondProfile, secondDevice);

        var firstLease = Assert.IsType<LeasedEncodeJob>(await repository.LeaseNextEncodeJobAsync());
        await repository.CompleteEncodeJobAsync(firstLease, _dbPath + ".first.media", "First", "mp4", "h264", "aac", 1280, 720, 2500);
        var secondLease = Assert.IsType<LeasedEncodeJob>(await repository.LeaseNextEncodeJobAsync());
        await repository.CompleteEncodeJobAsync(secondLease, _dbPath + ".second.media", "Second", "mp4", "h264", "aac", 1280, 720, 2500);

        var first = Assert.Single(await repository.ListOfflineVariantsAsync(assetId, "same-source", firstProfile, firstDevice));
        var second = Assert.Single(await repository.ListOfflineVariantsAsync(assetId, "same-source", secondProfile, secondDevice));
        Assert.NotEqual(first.Id, second.Id);
        Assert.StartsWith("/api/v1/playback/", first.DownloadUrl, StringComparison.Ordinal);
        Assert.Null(await repository.GetOfflineVariantFileAsync(assetId, first.Id, secondProfile, secondDevice));
        Assert.NotNull(await repository.GetOfflineVariantFileAsync(assetId, first.Id, firstProfile, firstDevice));
    }

    [Fact]
    public async Task CompletedVariant_RecordsItsExpiry_AndTheJobListShowsIt()
    {
        var assetId = await CreateAssetAsync();
        var repository = new PlaybackStateRepository(_db);
        var profile = Guid.NewGuid();
        var device = Guid.NewGuid();
        var expiresAt = DateTimeOffset.UtcNow.AddDays(30);
        await repository.QueueEncodeJobAsync(assetId, "mobile-standard", "source", null, profile, device);
        var lease = Assert.IsType<LeasedEncodeJob>(await repository.LeaseNextEncodeJobAsync());

        await repository.CompleteEncodeJobAsync(lease, _dbPath + ".expiring.media", "Phone", "mp4", "h264", "aac", 1280, 720, 2500, expiresAt);

        var variant = Assert.Single(await repository.ListOfflineVariantsAsync(assetId, "source", profile, device));
        Assert.Equal(expiresAt, variant.ExpiresAt);
        var job = Assert.Single(await repository.ListEncodeJobsAsync(profile, device));
        Assert.Equal(expiresAt, job.ExpiresAt);
    }

    [Fact]
    public async Task ExpiredVariants_AreListedAndRemovedWhileCurrentOnesStay()
    {
        var assetId = await CreateAssetAsync();
        var repository = new PlaybackStateRepository(_db);
        var now = DateTimeOffset.UtcNow;
        var expired = await CompleteVariantAsync(repository, assetId, "expired", now.AddMinutes(-1));
        var current = await CompleteVariantAsync(repository, assetId, "current", now.AddDays(1));
        var forever = await CompleteVariantAsync(repository, assetId, "forever", null);

        var listed = await repository.ListExpiredOfflineVariantsAsync(now);

        Assert.Equal([expired], listed.Select(variant => variant.Id));
        await repository.DeleteOfflineVariantAsync(expired);
        Assert.Empty(await repository.ListExpiredOfflineVariantsAsync(now));
        using var conn = _db.CreateConnection();
        Assert.Equal(
            new[] { current, forever }.Order(),
            (await conn.QueryAsync<Guid>("SELECT id FROM offline_variants;")).Order());
    }

    private async Task<Guid> CompleteVariantAsync(PlaybackStateRepository repository, Guid assetId, string source, DateTimeOffset? expiresAt)
    {
        await repository.QueueEncodeJobAsync(assetId, "mobile-standard", source, null, Guid.NewGuid(), Guid.NewGuid());
        var lease = Assert.IsType<LeasedEncodeJob>(await repository.LeaseNextEncodeJobAsync());
        await repository.CompleteEncodeJobAsync(lease, $"{_dbPath}.{source}.media", source, "mp4", "h264", "aac", 1280, 720, 2500, expiresAt);
        using var conn = _db.CreateConnection();
        return await conn.ExecuteScalarAsync<Guid>("SELECT id FROM offline_variants WHERE source_hash = @source;", new { source });
    }

    [Fact]
    public async Task EncodeJobWireContract_PreservesFieldWithoutExposingServerOutputPath()
    {
        var assetId = await CreateAssetAsync();
        var repository = new PlaybackStateRepository(_db);
        var job = await repository.QueueEncodeJobAsync(assetId, "mobile-standard", "source", null);
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("UPDATE encode_jobs SET output_path = @path WHERE id = @id", new { path = @"C:\server\private\download.mp4", id = job.Id });
        var returned = Assert.Single(await repository.ListEncodeJobsAsync());
        var json = JsonSerializer.Serialize(returned, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"outputPath\":null", json, StringComparison.Ordinal);
        Assert.DoesNotContain("server", json, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<Guid> CreateAssetAsync()
    {
        using var conn = _db.CreateConnection();
        var collectionId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        await conn.ExecuteAsync(
            """
            INSERT INTO collections (id, created_at) VALUES (@collectionId, datetime('now'));
            INSERT INTO works (id, collection_id, media_type) VALUES (@workId, @collectionId, 'Audiobooks');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
            VALUES (@assetId, @editionId, @contentHash, '/library/Audiobooks/test.m4b', 'Normal');
            """,
            new
            {
                collectionId,
                workId,
                editionId,
                assetId,
                contentHash = $"asset-{assetId:N}",
            });
        return assetId;
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task AnExpiredVariant_CanBePreparedAgain_WithANewJobAndANewVariant()
    {
        var assetId = await CreateAssetAsync();
        var repository = new PlaybackStateRepository(_db);
        var profile = Guid.NewGuid();
        var device = Guid.NewGuid();
        var first = await repository.QueueEncodeJobAsync(assetId, "mobile-standard", "source", null, profile, device);
        var lease = Assert.IsType<LeasedEncodeJob>(await repository.LeaseNextEncodeJobAsync());
        await repository.CompleteEncodeJobAsync(lease, _dbPath + ".again.media", "Phone", "mp4", "h264", "aac", 1280, 720, 2500, DateTimeOffset.UtcNow.AddDays(30));
        // While the variant is current, asking again returns the finished job.
        Assert.Equal(first.Id, (await repository.QueueEncodeJobAsync(assetId, "mobile-standard", "source", null, profile, device)).Id);
        var expired = Assert.Single(await repository.ListOfflineVariantsAsync(assetId, "source", profile, device));

        // It expires, and no cleanup pass has run (or cleanup is off): asking again prepares it afresh, not the old job.
        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(
                "UPDATE offline_variants SET expires_at = @past WHERE id = @id;",
                new { id = expired.Id, past = DateTimeOffset.UtcNow.AddSeconds(-1).ToString("O") });
        }

        var second = await repository.QueueEncodeJobAsync(assetId, "mobile-standard", "source", null, profile, device);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(EncodeJobStatuses.Queued, second.Status);
        var newLease = Assert.IsType<LeasedEncodeJob>(await repository.LeaseNextEncodeJobAsync());
        Assert.Equal(second.Id, newLease.Id);
        await repository.CompleteEncodeJobAsync(newLease, _dbPath + ".again2.media", "Phone", "mp4", "h264", "aac", 1280, 720, 2500, DateTimeOffset.UtcNow.AddDays(30));
        var variant = Assert.Single(await repository.ListOfflineVariantsAsync(assetId, "source", profile, device));
        Assert.NotEqual(expired.Id, variant.Id);
    }

    [Fact]
    public async Task AnExpiredVariant_IsNotDownloadableOrListed_EvenBeforeCleanupRuns()
    {
        var assetId = await CreateAssetAsync();
        var repository = new PlaybackStateRepository(_db);
        var profile = Guid.NewGuid();
        var device = Guid.NewGuid();
        await repository.QueueEncodeJobAsync(assetId, "mobile-standard", "source", null, profile, device);
        var lease = Assert.IsType<LeasedEncodeJob>(await repository.LeaseNextEncodeJobAsync());
        await repository.CompleteEncodeJobAsync(lease, _dbPath + ".live.media", "Phone", "mp4", "h264", "aac", 1280, 720, 2500, DateTimeOffset.UtcNow.AddDays(1));
        var variant = Assert.Single(await repository.ListOfflineVariantsAsync(assetId, "source", profile, device));
        Assert.NotNull(await repository.GetOfflineVariantFileAsync(assetId, variant.Id, profile, device));

        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(
                "UPDATE offline_variants SET expires_at = @past WHERE id = @id;",
                new { id = variant.Id, past = DateTimeOffset.UtcNow.AddSeconds(-1).ToString("O") });
        }

        Assert.Null(await repository.GetOfflineVariantFileAsync(assetId, variant.Id, profile, device));
        Assert.Empty(await repository.ListOfflineVariantsAsync(assetId, "source", profile, device));
    }

    [Fact]
    public async Task ASharedFile_IsInUseOnlyWhileAnotherUnexpiredVariantPointsAtIt()
    {
        var assetId = await CreateAssetAsync();
        var repository = new PlaybackStateRepository(_db);
        var shared = _dbPath + ".shared.media";
        var now = DateTimeOffset.UtcNow;
        var a = InsertVariant(assetId, shared, now.AddMinutes(-1));
        var b = InsertVariant(assetId, shared.ToUpperInvariant(), now.AddDays(1));
        var c = InsertVariant(assetId, _dbPath + ".other.media", now.AddDays(1));

        Assert.True(await repository.IsFileUsedByAnotherLiveVariantAsync(a, shared, now));
        Assert.False(await repository.IsFileUsedByAnotherLiveVariantAsync(c, _dbPath + ".other.media", now));
        Assert.False(await repository.IsFileUsedByAnotherLiveVariantAsync(b, shared.ToUpperInvariant(), now.AddDays(2)));
    }

    private Guid InsertVariant(Guid assetId, string outputPath, DateTimeOffset expiresAt)
    {
        var id = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute(
            """
            INSERT INTO offline_variants (id, asset_id, profile_key, source_hash, display_name, status, output_path, created_at, expires_at)
            VALUES (@id, @assetId, 'mobile-small', 'source', 'Phone', 'ready', @outputPath, @now, @expires);
            """,
            new { id, assetId, outputPath, now = DateTimeOffset.UtcNow.ToString("O"), expires = expiresAt.ToString("O") });
        return id;
    }

    [Fact]
    public async Task AfterCleanupRemovesAnExpiredVariant_AskingAgainAlsoPreparesItAfresh()
    {
        var assetId = await CreateAssetAsync();
        var repository = new PlaybackStateRepository(_db);
        var profile = Guid.NewGuid();
        var device = Guid.NewGuid();
        var first = await repository.QueueEncodeJobAsync(assetId, "mobile-standard", "source", null, profile, device);
        var lease = Assert.IsType<LeasedEncodeJob>(await repository.LeaseNextEncodeJobAsync());
        await repository.CompleteEncodeJobAsync(lease, _dbPath + ".removed.media", "Phone", "mp4", "h264", "aac", 1280, 720, 2500, DateTimeOffset.UtcNow.AddMinutes(-1));

        await repository.DeleteOfflineVariantAsync(Assert.Single(await repository.ListExpiredOfflineVariantsAsync(DateTimeOffset.UtcNow)).Id);

        Assert.NotEqual(first.Id, (await repository.QueueEncodeJobAsync(assetId, "mobile-standard", "source", null, profile, device)).Id);
    }

    [Fact]
    public async Task RemovingOneVariant_NeverDeletesAnotherProfilesJobOnTheSameOldLayoutPath()
    {
        var assetId = await CreateAssetAsync();
        var repository = new PlaybackStateRepository(_db);
        var shared = _dbPath + ".old-layout.media";
        var profileA = Guid.NewGuid();
        var profileB = Guid.NewGuid();
        var deviceA = Guid.NewGuid();
        var deviceB = Guid.NewGuid();
        var jobA = await repository.QueueEncodeJobAsync(assetId, "mobile-standard", "source", null, profileA, deviceA);
        var jobB = await repository.QueueEncodeJobAsync(assetId, "mobile-standard", "source", null, profileB, deviceB);
        // The old layout let two people's jobs for the same asset and profile key write one path.
        foreach (var _ in new[] { 0, 1 })
        {
            var lease = Assert.IsType<LeasedEncodeJob>(await repository.LeaseNextEncodeJobAsync());
            await repository.CompleteEncodeJobAsync(lease, shared, "Phone", "mp4", "h264", "aac", 1280, 720, 2500, DateTimeOffset.UtcNow.AddDays(30));
        }

        var variantA = Assert.Single(await repository.ListOfflineVariantsAsync(assetId, "source", profileA, deviceA));

        await repository.DeleteOfflineVariantAsync(variantA.Id);

        using var conn = _db.CreateConnection();
        Assert.Equal(new[] { jobB.Id }, (await conn.QueryAsync<Guid>("SELECT id FROM encode_jobs WHERE status = 'complete';")).ToArray());
        Assert.Equal(jobB.Id, (await repository.QueueEncodeJobAsync(assetId, "mobile-standard", "source", null, profileB, deviceB)).Id);
        Assert.NotEqual(jobA.Id, (await repository.QueueEncodeJobAsync(assetId, "mobile-standard", "source", null, profileA, deviceA)).Id);
    }
}
