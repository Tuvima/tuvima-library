using Dapper;
using MediaEngine.Api.Services.Details.Internals;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The TV show details page used to read every episode with about nine statements each. These tests pin that the
/// batched readers return exactly what the per-episode readers return, and that the statement count stays flat.
/// </summary>
public sealed class EpisodeBatchLoadingTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"episode_batch_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly Guid _provider = Guid.NewGuid();

    public EpisodeBatchLoadingTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_path);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        using var connection = _db.CreateConnection();
        connection.Execute(
            "INSERT INTO metadata_providers (id, name, version, is_enabled) VALUES (@provider, 'batch-test', '1', 1);",
            new { provider = _provider });
    }

    [Fact]
    public async Task BatchedCanonicalMapsMatchThePerEpisodeReader()
    {
        var episodes = SeedEpisodes(50);
        var composer = new DetailCompositionOrchestrator(_db, null!, null!, null!, null!, null!, null!, null!);

        var batched = await composer.LoadWorkAndAssetCanonicalMapsAsync(episodes, CancellationToken.None);

        Assert.Equal(episodes.Count, batched.Count);
        foreach (var episode in episodes)
        {
            var expected = await composer.LoadWorkAndAssetCanonicalMapAsync(episode, CancellationToken.None);
            Assert.Equal(Normalize(expected), Normalize(batched[episode]));
        }

        // The seed must actually reach the interesting branches, or the comparison above proves little.
        Assert.Contains(batched.Values, map => map.ContainsKey("edition_subtitle"));
        Assert.Contains(batched.Values, map => map.TryGetValue("genre", out var genre) && genre.Contains('|'));
        Assert.Contains(batched.Values, map => map.ContainsKey("duration_sec"));
        Assert.DoesNotContain(batched.Values, map => map.ContainsKey("orphaned_only"));
    }

    [Fact]
    public async Task BatchedCanonicalMapsGiveUnknownAndEmptyRequestsAnEmptyResult()
    {
        var composer = new DetailCompositionOrchestrator(_db, null!, null!, null!, null!, null!, null!, null!);
        Assert.Empty(await composer.LoadWorkAndAssetCanonicalMapsAsync([], CancellationToken.None));

        var unknown = Guid.NewGuid();
        var result = await composer.LoadWorkAndAssetCanonicalMapsAsync([unknown, unknown], CancellationToken.None);
        Assert.Empty(Assert.Single(result).Value);
    }

    [Fact]
    public async Task BatchedDisplayOverridesMatchThePerEpisodeReader()
    {
        var episodes = SeedEpisodes(50);
        var composer = new DetailCompositionOrchestrator(_db, null!, null!, null!, null!, null!, null!, null!);

        var batched = await composer.LoadWorkDisplayOverridesBatchAsync(episodes, CancellationToken.None);

        Assert.Equal(episodes.Count, batched.Count);
        foreach (var episode in episodes)
        {
            var expected = await composer.LoadWorkDisplayOverridesAsync(episode, CancellationToken.None);
            Assert.Equal(Normalize(expected), Normalize(batched[episode]));
        }

        Assert.Contains(batched.Values, overrides => overrides.ContainsKey("description"));
    }

    [Fact]
    public async Task ReadingAThousandEpisodesUsesAFlatNumberOfStatements()
    {
        var episodes = SeedEpisodes(1000);
        var counting = new StatementCountingDatabase(_db);
        var composer = new DetailCompositionOrchestrator(counting, null!, null!, null!, null!, null!, null!, null!);

        counting.Reset();
        await composer.LoadWorkAndAssetCanonicalMapsAsync(episodes, CancellationToken.None);
        await composer.LoadWorkDisplayOverridesBatchAsync(episodes, CancellationToken.None);

        // Was about 9,000 (nine per episode). Chunked IN lists keep it to a couple of dozen.
        Assert.InRange(counting.Statements, 1, 40);
    }

    /// <summary>
    /// Seeds standalone TV episodes with deliberately uneven data: extra editions and assets, orphaned assets,
    /// conflicting keys between asset and work, packed and multi-valued genres, blank subtitles, bad override JSON.
    /// </summary>
    private List<Guid> SeedEpisodes(int count)
    {
        var ids = new List<Guid>(count);
        using var connection = _db.CreateConnection();
        using var transaction = connection.BeginTransaction();
        for (var i = 0; i < count; i++)
        {
            var work = Guid.NewGuid();
            ids.Add(work);
            var overrides = i % 5 == 0 ? $"{{\"description\":\"Override {i}\"}}" : i % 7 == 0 ? "{broken" : null;
            connection.Execute(
                "INSERT INTO works(id,media_type,work_kind,curator_state,display_overrides_json) VALUES (@work,'TV','standalone','accepted',@overrides);",
                new { work, overrides }, transaction);

            var firstEdition = Guid.NewGuid();
            var firstAsset = Guid.NewGuid();
            connection.Execute("INSERT INTO editions(id,work_id) VALUES (@firstEdition,@work);", new { firstEdition, work }, transaction);
            InsertAsset(connection, transaction, firstAsset, firstEdition, "Normal", $"first-{work:N}");
            InsertValue(connection, transaction, firstAsset, "runtime", $"{40 + i % 20}");
            InsertValue(connection, transaction, firstAsset, "title", $"Asset title {i}");
            InsertValue(connection, transaction, firstAsset, "quality", i % 2 == 0 ? "1080p" : "720p");

            InsertValue(connection, transaction, work, "title", $"Work title {i}");
            InsertValue(connection, transaction, work, "episode_number", $"{i + 1}");
            InsertValue(connection, transaction, work, "episode_description", $"Episode {i}");
            if (i % 2 == 0)
            {
                InsertValue(connection, transaction, work, "air_date", $"2020-01-{i % 28 + 1:00}");
            }

            if (i % 5 == 0)
            {
                InsertValue(connection, transaction, work, "episode_still_url", $"https://img/{i}.jpg");
            }

            if (i % 4 == 0)
            {
                // A second edition with its own assets, one of which shares a key with the first asset.
                var secondEdition = Guid.NewGuid();
                var secondAsset = Guid.NewGuid();
                connection.Execute("INSERT INTO editions(id,work_id) VALUES (@secondEdition,@work);", new { secondEdition, work }, transaction);
                InsertAsset(connection, transaction, secondAsset, secondEdition, "Normal", $"second-{work:N}");
                InsertValue(connection, transaction, secondAsset, "title", $"Other asset title {i}");
                InsertValue(connection, transaction, secondAsset, "resolution", "1920x1080");
                if (i % 12 == 0)
                {
                    InsertValue(connection, transaction, firstEdition, "subtitle", $"First edition subtitle {i}");
                    InsertValue(connection, transaction, secondEdition, "subtitle", $"Second edition subtitle {i}");
                }
            }
            else if (i % 3 == 0)
            {
                InsertValue(connection, transaction, firstEdition, "subtitle", i % 6 == 0 ? string.Empty : $"Subtitle {i}");
            }

            if (i % 9 == 0)
            {
                var orphaned = Guid.NewGuid();
                InsertAsset(connection, transaction, orphaned, firstEdition, "Orphaned", $"orphan-{work:N}");
                InsertValue(connection, transaction, orphaned, "orphaned_only", "should never appear");
            }

            if (i % 3 == 0)
            {
                InsertArray(connection, transaction, work, "genre", 0, "Drama");
                InsertArray(connection, transaction, work, "genre", 1, "Comedy");
                InsertArray(connection, transaction, firstAsset, "director", 0, "Someone");
                InsertArray(connection, transaction, firstAsset, "director", 1, string.Empty);
            }

            if (i % 2 == 1)
            {
                InsertClaim(connection, transaction, firstAsset, "duration_sec", $"{2400 + i}", 0.9, "2026-01-02");
                InsertClaim(connection, transaction, firstAsset, "duration_sec", $"{1200 + i}", 0.5, "2026-01-03");
                InsertClaim(connection, transaction, firstAsset, "genre", "Drama|Thriller", 0.8, "2026-01-02");
            }

            if (i % 4 == 1)
            {
                InsertClaim(connection, transaction, work, "genre", "Comedy|Action", 0.7, "2026-01-04");
                InsertClaim(connection, transaction, work, "duration_seconds", $"{3000 + i}", 0.6, "2026-01-05");
            }
        }

        transaction.Commit();
        return ids;
    }

    private static void InsertAsset(SqliteConnection connection, SqliteTransaction transaction, Guid asset, Guid edition, string status, string hash) =>
        connection.Execute(
            "INSERT INTO media_assets(id,edition_id,content_hash,file_path_root,status) VALUES (@asset,@edition,@hash,@path,@status);",
            new { asset, edition, hash, path = $"C:/qa/{hash}.mkv", status }, transaction);

    private static void InsertValue(SqliteConnection connection, SqliteTransaction transaction, Guid entity, string key, string value) =>
        connection.Execute(
            "INSERT INTO canonical_values(entity_id,key,value,last_scored_at) VALUES (@entity,@key,@value,'2026-01-01');",
            new { entity, key, value }, transaction);

    private static void InsertArray(SqliteConnection connection, SqliteTransaction transaction, Guid entity, string key, int ordinal, string value) =>
        connection.Execute(
            "INSERT INTO canonical_value_arrays(entity_id,key,ordinal,value) VALUES (@entity,@key,@ordinal,@value);",
            new { entity, key, ordinal, value }, transaction);

    private void InsertClaim(SqliteConnection connection, SqliteTransaction transaction, Guid entity, string key, string value, double confidence, string claimedAt) =>
        connection.Execute(
            "INSERT INTO metadata_claims(id,entity_id,provider_id,claim_key,claim_value,confidence,claimed_at) VALUES (@id,@entity,@provider,@key,@value,@confidence,@claimedAt);",
            new { id = Guid.NewGuid(), entity, provider = _provider, key, value, confidence, claimedAt }, transaction);

    private static string[] Normalize(IReadOnlyDictionary<string, string> map) =>
        map.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}").ToArray();

    public void Dispose()
    {
        _db.Dispose();
        // Only this fixture's pool is released; other concurrently running
        // SQLite tests retain their own connections and pools.
        using var fixturePool = new SqliteConnection($"Data Source={_path}");
        SqliteConnection.ClearPool(fixturePool);
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    /// <summary>Counts every statement SQLite starts on connections handed out after construction.</summary>
    private sealed class StatementCountingDatabase(IDatabaseConnection inner) : IDatabaseConnection
    {
        private int _statements;

        public int Statements => Volatile.Read(ref _statements);

        public void Reset() => Volatile.Write(ref _statements, 0);

        public SqliteConnection CreateConnection()
        {
            var connection = inner.CreateConnection();
            raw.sqlite3_trace(connection.Handle!, (_, _) => Interlocked.Increment(ref _statements), null);
            return connection;
        }

        public SqliteConnection Open() => inner.Open();

        public Task<T> ExecuteReadAsync<T>(Func<SqliteConnection, SqliteTransaction, CancellationToken, T> body, CancellationToken ct = default) =>
            inner.ExecuteReadAsync(body, ct);

        public void InitializeSchema() => inner.InitializeSchema();

        public void RunStartupChecks() => inner.RunStartupChecks();

        public Task AcquireWriteLockAsync(CancellationToken ct = default) => inner.AcquireWriteLockAsync(ct);

        public void ReleaseWriteLock() => inner.ReleaseWriteLock();

        public Task<T> ExecuteWriteAsync<T>(Func<SqliteConnection, SqliteTransaction, CancellationToken, T> body, CancellationToken ct = default) =>
            inner.ExecuteWriteAsync(body, ct);

        public Task ExecuteWriteAsync(Action<SqliteConnection, SqliteTransaction, CancellationToken> body, CancellationToken ct = default) =>
            inner.ExecuteWriteAsync(body, ct);

        public void Dispose()
        {
        }
    }
}
