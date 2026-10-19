using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Paging;
using MediaEngine.Storage;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.Tests;

public sealed class LibraryReadServicesTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;

    public LibraryReadServicesTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima-library-reads-{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task WorkFeed_ComposesRootFallbacksAndManagedArtwork()
    {
        var rootWorkId = Guid.NewGuid();
        var leafWorkId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        SeedOwnedWork(rootWorkId, leafWorkId, editionId, assetId);

        var service = new LibraryWorkFeedReadService(_db);
        var response = await service.GetWorksAsync(new PagedRequest(0, 20));

        var item = Assert.Single(response.Items);
        Assert.Equal(leafWorkId, item.Id);
        Assert.Equal(rootWorkId, item.RootWorkId);
        Assert.Equal(assetId, item.AssetId);
        Assert.Equal("Pilot", item.CanonicalValues["title"]);
        Assert.Equal("Jane Creator", item.CanonicalValues["author"]);
        Assert.Equal($"/stream/{assetId}/cover", item.CoverUrl);
        Assert.Equal(item.CoverUrl, item.CanonicalValues["cover"]);
    }

    [Fact]
    public async Task WorkFeed_AppliesAllowedWorkScopeBeforePaging()
    {
        var hidden = SeedStandaloneWork("Hidden first", "QHIDDEN");
        var allowed = SeedStandaloneWork("Allowed second", "QALLOWED");
        var service = new LibraryWorkFeedReadService(_db);

        var response = await service.GetWorksAsync(
            new PagedRequest(0, 1),
            CancellationToken.None,
            new HashSet<Guid> { allowed.WorkId });

        var item = Assert.Single(response.Items);
        Assert.Equal(allowed.WorkId, item.Id);
        Assert.DoesNotContain(response.Items, candidate => candidate.Id == hidden.WorkId);
        Assert.False(response.HasMore);
    }

    [Fact]
    public async Task BatchTargets_RouteParentAndAssetFieldsAcrossLargeSelections()
    {
        var rootWorkId = Guid.NewGuid();
        var seasonWorkId = Guid.NewGuid();
        var episodeWorkId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        SeedTvHierarchy(rootWorkId, seasonWorkId, episodeWorkId, editionId, assetId);

        var selected = Enumerable.Range(0, 405)
            .Select(_ => Guid.NewGuid())
            .Append(rootWorkId)
            .Append(assetId)
            .ToArray();
        var service = new LibraryCurationReadService(_db);

        var targets = await service.ResolveBatchEditTargetsAsync(
            selected,
            ["show_name", "episode_title"]);

        Assert.Equal(rootWorkId, targets[rootWorkId]["show_name"]);
        Assert.Equal(assetId, targets[rootWorkId]["episode_title"]);
        Assert.Equal(rootWorkId, targets[assetId]["show_name"]);
        Assert.Equal(assetId, targets[assetId]["episode_title"]);
    }

    private void SeedOwnedWork(Guid rootWorkId, Guid leafWorkId, Guid editionId, Guid assetId)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO works (id, media_type, work_kind) VALUES ($rootWorkId, 'TV', 'parent');
            INSERT INTO works (id, parent_work_id, media_type, work_kind, ordinal)
                VALUES ($leafWorkId, $rootWorkId, 'TV', 'child', 1);
            INSERT INTO editions (id, work_id) VALUES ($editionId, $leafWorkId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at)
                VALUES ($assetId, $editionId, $hash, $path, $now);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES ($assetId, 'title', 'Pilot', $now),
                       ($assetId, 'cover_state', 'present', $now),
                       ($rootWorkId, 'show_name', 'Sample Show', $now);
            INSERT INTO canonical_value_arrays (entity_id, key, value, ordinal)
                VALUES ($rootWorkId, 'author', 'Jane Creator', 0);
            """;
        AddGuid(cmd, "$rootWorkId", rootWorkId);
        AddGuid(cmd, "$leafWorkId", leafWorkId);
        AddGuid(cmd, "$editionId", editionId);
        AddGuid(cmd, "$assetId", assetId);
        cmd.Parameters.AddWithValue("$hash", $"hash-{assetId:N}");
        cmd.Parameters.AddWithValue("$path", $"C:/library/{assetId:N}.mkv");
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private void SeedTvHierarchy(
        Guid rootWorkId,
        Guid seasonWorkId,
        Guid episodeWorkId,
        Guid editionId,
        Guid assetId)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO works (id, media_type, work_kind) VALUES ($rootWorkId, 'TV', 'parent');
            INSERT INTO works (id, parent_work_id, media_type, work_kind, ordinal)
                VALUES ($seasonWorkId, $rootWorkId, 'TV', 'parent', 1);
            INSERT INTO works (id, parent_work_id, media_type, work_kind, ordinal)
                VALUES ($episodeWorkId, $seasonWorkId, 'TV', 'child', 1);
            INSERT INTO editions (id, work_id) VALUES ($editionId, $episodeWorkId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                VALUES ($assetId, $editionId, $hash, $path);
            """;
        AddGuid(cmd, "$rootWorkId", rootWorkId);
        AddGuid(cmd, "$seasonWorkId", seasonWorkId);
        AddGuid(cmd, "$episodeWorkId", episodeWorkId);
        AddGuid(cmd, "$editionId", editionId);
        AddGuid(cmd, "$assetId", assetId);
        cmd.Parameters.AddWithValue("$hash", $"hash-{assetId:N}");
        cmd.Parameters.AddWithValue("$path", $"C:/library/{assetId:N}.mkv");
        cmd.ExecuteNonQuery();
    }

    private (Guid WorkId, Guid AssetId) SeedStandaloneWork(
        string title,
        string qid,
        string? universeKey = null,
        string? universeValue = null)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO works (id, media_type, work_kind)
                VALUES ($workId, 'Books', 'standalone');
            INSERT INTO editions (id, work_id) VALUES ($editionId, $workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                VALUES ($assetId, $editionId, $hash, $path);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES ($assetId, 'title', $title, $now),
                       ($assetId, 'media_type', 'Books', $now),
                       ($assetId, 'wikidata_qid', $qid, $now);
            """;
        if (!string.IsNullOrWhiteSpace(universeKey) && !string.IsNullOrWhiteSpace(universeValue))
        {
            cmd.CommandText += """
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                    VALUES ($assetId, $universeKey, $universeValue, $now);
                """;
        }

        AddGuid(cmd, "$workId", workId);
        AddGuid(cmd, "$editionId", editionId);
        AddGuid(cmd, "$assetId", assetId);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$qid", qid);
        cmd.Parameters.AddWithValue("$hash", $"hash-{assetId:N}");
        cmd.Parameters.AddWithValue("$path", $"C:/library/{assetId:N}.epub");
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        if (!string.IsNullOrWhiteSpace(universeKey))
        {
            cmd.Parameters.AddWithValue("$universeKey", universeKey);
        }

        if (!string.IsNullOrWhiteSpace(universeValue))
        {
            cmd.Parameters.AddWithValue("$universeValue", universeValue);
        }

        cmd.ExecuteNonQuery();
        return (workId, assetId);
    }

    private static void AddGuid(SqliteCommand command, string name, Guid value) =>
        command.Parameters.Add(name, SqliteType.Blob).Value = GuidSql.ToBlob(value);
}
