using Dapper;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Identity.Contracts;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

public sealed class ProfileOverviewReadServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly Guid _profileId = Guid.NewGuid();
    private readonly FakeProfileService _profiles;
    private readonly FakeActivityRepository _activity = new();
    private readonly FakeTasteProfiler _taste = new();

    public ProfileOverviewReadServiceTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_profile_overview_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        ReadyWorks.TreatEveryWorkAsReady(_db);
        _profiles = new FakeProfileService(new Profile
        {
            Id = _profileId,
            DisplayName = "Owner",
            Role = ProfileRole.Administrator,
            CreatedAt = DateTimeOffset.UtcNow,
        });
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task GetOverviewAsync_ReturnsNullWhenProfileIsMissing()
    {
        var service = new ProfileOverviewReadService(new FakeProfileService(null), _db, _activity, _taste, new TasteOverrideRepository(_db));

        var result = await service.GetOverviewAsync(_profileId, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetOverviewAsync_ComposesProgressStatsActivityAndRecentlyAddedItems()
    {
        var firstAsset = SeedOwnedAsset("Books", "Book One.epub", "Novel", DateTimeOffset.UtcNow.AddDays(-1), progress: 50, duration: 200, position: null);
        var secondAsset = SeedOwnedAsset("Movies", "Movie One.mkv", "Film", DateTimeOffset.UtcNow.AddHours(-2), progress: 100, duration: 500, position: 480);
        SeedUserState(firstAsset, progress: 50, DateTimeOffset.UtcNow.AddMinutes(-10), """{"duration_seconds":"200"}""");
        SeedUserState(secondAsset, progress: 100, DateTimeOffset.UtcNow.AddMinutes(-5), """{"position_seconds":"480","duration_seconds":"500"}""");
        _activity.Entries.Add(new SystemActivityEntry { Id = 1, ActionType = "Playback", ProfileId = _profileId });
        _activity.Entries.Add(new SystemActivityEntry { Id = 2, ActionType = "Playback", ProfileId = _profileId });

        var service = new ProfileOverviewReadService(_profiles, _db, _activity, _taste, new TasteOverrideRepository(_db));

        var result = await service.GetOverviewAsync(_profileId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2, result.Stats.TotalItems);
        Assert.Equal(1, result.Stats.InProgress);
        Assert.Equal(1, result.Stats.Completed);
        Assert.Equal(2, result.Stats.RecentActivity);
        Assert.Equal(1, result.Stats.MediaTypeMix["Books"]);
        Assert.Equal(1, result.Stats.LibraryCounts["Movies"]);
        Assert.Equal(580, result.Stats.ConsumedSeconds);
        Assert.Equal("Movie One", result.RecentItems[0].Title);
        Assert.Equal("Movie One", result.RecentlyAddedItems[0].Title);
        Assert.Equal("Playback", Assert.Single(result.Stats.ActivityBuckets).Key);
    }

    [Fact]
    public async Task GetOverviewAsync_ACoverIsTheEnginesOwnCopyAndNeverTheProvidersLink()
    {
        var managedCover = $"/stream/artwork/{Guid.NewGuid():D}";
        var bothCovers = SeedOwnedAsset("Movies", "Both Covers.mkv", "Film", DateTimeOffset.UtcNow.AddDays(-1), progress: 10, duration: null, position: null);
        var providerOnly = SeedOwnedAsset("Movies", "Provider Only.mkv", "Film", DateTimeOffset.UtcNow.AddDays(-2), progress: 10, duration: null, position: null);
        // Alphabetically 'cover' comes first, but the Engine's copy (cover_url) is the one written first.
        SetValue(WorkIdOf(bothCovers), "cover", "https://image.tmdb.org/t/p/original/both.jpg");
        SetValue(WorkIdOf(bothCovers), "cover_url", managedCover);
        SetValue(WorkIdOf(providerOnly), "cover", "https://image.tmdb.org/t/p/original/only.jpg");
        SeedUserState(bothCovers, progress: 10, DateTimeOffset.UtcNow.AddMinutes(-5), "{}");
        SeedUserState(providerOnly, progress: 10, DateTimeOffset.UtcNow.AddMinutes(-10), "{}");
        var service = new ProfileOverviewReadService(_profiles, _db, _activity, _taste, new TasteOverrideRepository(_db));

        var result = await service.GetOverviewAsync(_profileId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(managedCover, result.RecentItems.Single(item => item.AssetId == bothCovers).CoverUrl);
        Assert.Equal(managedCover, result.RecentlyAddedItems.Single(item => item.AssetId == bothCovers).CoverUrl);
        Assert.Equal($"/stream/{providerOnly:D}/cover", result.RecentItems.Single(item => item.AssetId == providerOnly).CoverUrl);
        Assert.Equal($"/stream/{providerOnly:D}/cover", result.RecentlyAddedItems.Single(item => item.AssetId == providerOnly).CoverUrl);
    }

    [Fact]
    public async Task GetOverviewAsync_ATvShowSubtitleUsesTheShowNameBeforeTheFolderName()
    {
        var episode = SeedOwnedAsset("TV", "Doctor Who 2005 S01E01.mkv", "Drama", DateTimeOffset.UtcNow.AddDays(-1), progress: 10, duration: null, position: null);
        SetValue(WorkIdOf(episode), "show_name", "Doctor Who");
        SetValue(WorkIdOf(episode), "series", "Doctor Who 2005");
        SeedUserState(episode, progress: 10, DateTimeOffset.UtcNow.AddMinutes(-5), "{}");
        var service = new ProfileOverviewReadService(_profiles, _db, _activity, _taste, new TasteOverrideRepository(_db));

        var result = await service.GetOverviewAsync(_profileId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Doctor Who", Assert.Single(result.RecentItems).Subtitle);
        Assert.Equal("Doctor Who", Assert.Single(result.RecentlyAddedItems).Subtitle);
    }

    private Guid WorkIdOf(Guid assetId)
    {
        using var conn = _db.CreateConnection();
        return conn.QuerySingle<Guid>(
            "SELECT e.work_id FROM media_assets ma INNER JOIN editions e ON e.id = ma.edition_id WHERE ma.id = @assetId;",
            new { assetId });
    }

    private void SetValue(Guid entityId, string key, string value)
    {
        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT OR REPLACE INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@entityId, @key, @value, @now);",
            new { entityId, key, value, now = DateTimeOffset.UtcNow.ToString("O") });
    }

    private Guid SeedOwnedAsset(string mediaType, string title, string genre, DateTimeOffset claimedAt, double progress, double? duration, double? position)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        cmd.CommandText = """
            INSERT OR IGNORE INTO metadata_providers (id, name, version, is_enabled)
            VALUES ($providerId, $providerName, '1.0', 1);
            INSERT INTO works (id, media_type, work_kind, ownership, is_catalog_only)
            VALUES ($workId, $mediaType, 'standalone', 'Owned', 0);
            INSERT INTO editions (id, work_id)
            VALUES ($editionId, $workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
            VALUES ($assetId, $editionId, $hash, $title);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES ($assetId, 'title', $title, $now);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES ($assetId, 'media_type', $mediaType, $now);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES ($workId, 'genre', $genre, $now);
            INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at)
            VALUES ($claimId, $assetId, $providerId, 'title', $title, 1.0, $claimedAt);
            """;
        cmd.Parameters.AddWithValue("$providerId", GuidSql.ToBlob(providerId));
        cmd.Parameters.AddWithValue("$providerName", $"test-{providerId:N}");
        cmd.Parameters.AddWithValue("$workId", GuidSql.ToBlob(workId));
        cmd.Parameters.AddWithValue("$mediaType", mediaType);
        cmd.Parameters.AddWithValue("$editionId", GuidSql.ToBlob(editionId));
        cmd.Parameters.AddWithValue("$assetId", GuidSql.ToBlob(assetId));
        cmd.Parameters.AddWithValue("$hash", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$genre", genre);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$claimId", GuidSql.ToBlob(Guid.NewGuid()));
        cmd.Parameters.AddWithValue("$claimedAt", claimedAt.ToString("O"));
        cmd.ExecuteNonQuery();
        return assetId;
    }

    [Fact]
    public async Task GetOverviewAsync_ShowsTheLearnedTasteWithThePersonsOwnRemovalsAndAdditionsApplied()
    {
        using (var connection = _db.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO profiles (id, display_name, created_at) VALUES (@id, 'Owner', @now);",
                new { id = _profileId, now = DateTimeOffset.UtcNow.ToString("O") });
        }

        _taste.Genres = new Dictionary<string, double> { ["sci-fi"] = 0.6, ["drama"] = 0.4 };
        var overrides = new TasteOverrideRepository(_db);
        await overrides.SetAsync(_profileId, TasteGroups.Genre, "sci-fi", TasteOverrideActions.Removed, DateTimeOffset.UtcNow);
        await overrides.SetAsync(_profileId, TasteGroups.Genre, "western", TasteOverrideActions.Added, DateTimeOffset.UtcNow);
        var service = new ProfileOverviewReadService(_profiles, _db, _activity, _taste, overrides);

        var result = await service.GetOverviewAsync(_profileId, CancellationToken.None);

        Assert.NotNull(result?.Taste);
        Assert.Equal(["drama", "western"], result.Taste.GenreDistribution.Keys.Order());
        Assert.Equal(0.6, _taste.Genres["sci-fi"]);
    }

    private void SeedUserState(Guid assetId, double progress, DateTimeOffset lastAccessed, string extendedProperties)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO user_states (user_id, asset_id, progress_pct, last_accessed, extended_properties)
            VALUES ($profileId, $assetId, $progress, $lastAccessed, $extendedProperties);
            """;
        cmd.Parameters.AddWithValue("$profileId", GuidSql.ToBlob(_profileId));
        cmd.Parameters.AddWithValue("$assetId", GuidSql.ToBlob(assetId));
        cmd.Parameters.AddWithValue("$progress", progress);
        cmd.Parameters.AddWithValue("$lastAccessed", lastAccessed.ToString("O"));
        cmd.Parameters.AddWithValue("$extendedProperties", extendedProperties);
        cmd.ExecuteNonQuery();
    }

    private sealed class FakeProfileService(Profile? profile) : IProfileService
    {
        public Task<IReadOnlyList<Profile>> GetAllProfilesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Profile>>(profile is null ? [] : [profile]);

        public Task<Profile?> GetProfileAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(profile?.Id == id ? profile : null);

        public Task<bool> UpdateProfileAsync(Profile profile, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeActivityRepository : ISystemActivityRepository
    {
        public List<SystemActivityEntry> Entries { get; } = [];

        public Task LogAsync(SystemActivityEntry entry, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SystemActivityEntry>> GetRecentAsync(int limit = 50, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> PruneOlderThanAsync(int retentionDays, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long> CountAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SystemActivityEntry>> GetRecentByTypesAsync(IReadOnlyList<string> actionTypes, int limit = 50, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SystemActivityEntry>> GetRecentByProfileAsync(Guid profileId, int limit = 50, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SystemActivityEntry>>(Entries.Where(entry => entry.ProfileId == profileId).Take(limit).ToList());

        public Task<(IReadOnlyList<SystemActivityEntry> Items, int Total)> QueryAsync(IReadOnlyList<string> actionTypes, string? search, int offset, int limit, string? mediaType, CancellationToken ct = default)
            => Task.FromResult<(IReadOnlyList<SystemActivityEntry>, int)>(([], 0));
    }

    private sealed class FakeTasteProfiler : ITasteProfiler
    {
        public Dictionary<string, double> Genres { get; set; } = [];

        public Task<TasteProfileBuildResult> GetProfileAsync(Guid userId, CancellationToken ct = default) =>
            Task.FromResult(new TasteProfileBuildResult(
                TasteProfileBuildStatus.Generated,
                userId,
                new TasteProfile { UserId = userId, GenreDistribution = Genres, LastUpdatedAt = DateTimeOffset.UtcNow },
                SignalCount: 3,
                InputFingerprint: "test"));
    }
}
