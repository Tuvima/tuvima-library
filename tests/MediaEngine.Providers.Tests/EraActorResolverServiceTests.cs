using Dapper;
using MediaEngine.Domain.Constants;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

public sealed class EraActorResolverServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _innerDatabase;
    private readonly CountingDatabaseConnection _database;

    public EraActorResolverServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_era_actor_{Guid.NewGuid():N}.db");
        _innerDatabase = new DatabaseConnection(_dbPath);
        _innerDatabase.InitializeSchema();
        _database = new CountingDatabaseConnection(_innerDatabase);
    }

    [Fact]
    public async Task BatchResolution_PreservesEraSelectionWithTwoRepositoryConnectionScopes()
    {
        const int characterCount = 25;
        var characterQids = Enumerable.Range(0, characterCount)
            .Select(index => $"Q-character-{index}")
            .ToArray();
        SeedPeopleAndPerformerEdges(characterQids);
        _database.ResetCount();

        var resolver = new EraActorResolverService(
            new EntityRelationshipRepository(_database),
            new PersonRepository(_database),
            NullLogger<EraActorResolverService>.Instance);

        var results = await resolver.ResolveActorsForEraAsync(characterQids, 2005);

        Assert.Equal(characterCount, results.Count);
        Assert.Equal("P-historic", results[characterQids[0]].ActorPersonQid);
        Assert.NotNull(results[characterQids[0]].ActorPersonId);
        Assert.Equal("Historic Actor", results[characterQids[0]].ActorLabel);
        Assert.Equal("https://example.test/historic.jpg", results[characterQids[0]].HeadshotUrl);
        Assert.Equal(2, _database.CreateConnectionCount);
    }

    [Fact]
    public async Task SingleResolution_UsesTheSameBatchSelectionBehavior()
    {
        var characterQids = new[] { "Q-character-0" };
        SeedPeopleAndPerformerEdges(characterQids);
        _database.ResetCount();

        var resolver = new EraActorResolverService(
            new EntityRelationshipRepository(_database),
            new PersonRepository(_database),
            NullLogger<EraActorResolverService>.Instance);

        var result = await resolver.ResolveActorForEraAsync(characterQids[0], 2015);

        Assert.NotNull(result);
        Assert.Equal("P-current", result.ActorPersonQid);
        Assert.NotNull(result.ActorPersonId);
        Assert.Equal("Current Actor", result.ActorLabel);
        Assert.Equal(2, _database.CreateConnectionCount);
    }

    public void Dispose()
    {
        _database.Dispose();
        TryDelete(_dbPath);
        TryDelete($"{_dbPath}-wal");
        TryDelete($"{_dbPath}-shm");
    }

    private void SeedPeopleAndPerformerEdges(IReadOnlyList<string> characterQids)
    {
        var createdAt = DateTimeOffset.UtcNow;
        using var conn = _database.CreateConnection();
        using var tx = conn.BeginTransaction();

        InsertPerson(conn, tx, "P-historic", "Historic Actor", "historic", createdAt);
        InsertPerson(conn, tx, "P-current", "Current Actor", "current", createdAt);
        for (var index = 1; index < characterQids.Count; index++)
        {
            InsertPerson(conn, tx, $"P-{index}", $"Actor {index}", index.ToString(), createdAt);
        }

        InsertEdge(conn, tx, "P-historic", characterQids[0], "2000", "2009", createdAt.AddMinutes(-2));
        InsertEdge(conn, tx, "P-current", characterQids[0], "2010", null, createdAt.AddMinutes(-1));
        for (var index = 1; index < characterQids.Count; index++)
        {
            InsertEdge(conn, tx, $"P-{index}", characterQids[index], null, null, createdAt);
        }

        tx.Commit();
    }

    private static void InsertPerson(
        SqliteConnection conn,
        SqliteTransaction tx,
        string qid,
        string name,
        string imageKey,
        DateTimeOffset createdAt)
    {
        conn.Execute("""
            INSERT INTO persons (id, name, wikidata_qid, headshot_url, created_at)
            VALUES (@Id, @Name, @Qid, @HeadshotUrl, @CreatedAt);
            """, new
        {
            Id = GuidSql.ToBlob(Guid.NewGuid()),
            Name = name,
            Qid = qid,
            HeadshotUrl = $"https://example.test/{imageKey}.jpg",
            CreatedAt = createdAt.ToString("O"),
        }, tx);
    }

    private static void InsertEdge(
        SqliteConnection conn,
        SqliteTransaction tx,
        string actorQid,
        string characterQid,
        string? startTime,
        string? endTime,
        DateTimeOffset discoveredAt)
    {
        conn.Execute("""
            INSERT INTO entity_relationships
                (id, statement_key, subject_qid, relationship_type, object_qid,
                 confidence, discovered_at, start_time, end_time)
            VALUES
                (@Id, @StatementKey, @SubjectQid, @RelationshipType, @ObjectQid,
                 1.0, @DiscoveredAt, @StartTime, @EndTime);
            """, new
        {
            Id = GuidSql.ToBlob(Guid.NewGuid()),
            StatementKey = $"{actorQid}:{characterQid}:{startTime}:{endTime}",
            SubjectQid = actorQid,
            RelationshipType = RelationshipType.Performer,
            ObjectQid = characterQid,
            DiscoveredAt = discoveredAt.ToString("O"),
            StartTime = startTime,
            EndTime = endTime,
        }, tx);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // SQLite pool cleanup can briefly retain a test file on Windows.
        }
    }

    private sealed class CountingDatabaseConnection(DatabaseConnection inner) : IDatabaseConnection
    {
        private int _createConnectionCount;

        public int CreateConnectionCount => Volatile.Read(ref _createConnectionCount);

        public SqliteConnection Open() => inner.Open();

        public SqliteConnection CreateConnection()
        {
            Interlocked.Increment(ref _createConnectionCount);
            return inner.CreateConnection();
        }

        public void InitializeSchema() => inner.InitializeSchema();
        public void RunStartupChecks() => inner.RunStartupChecks();
        public Task AcquireWriteLockAsync(CancellationToken ct = default) => inner.AcquireWriteLockAsync(ct);
        public void ReleaseWriteLock() => inner.ReleaseWriteLock();
        public Task<T> ExecuteWriteAsync<T>(
            Func<SqliteConnection, SqliteTransaction, CancellationToken, T> body,
            CancellationToken ct = default) => inner.ExecuteWriteAsync(body, ct);
        public Task<T> ExecuteReadAsync<T>(
            Func<SqliteConnection, SqliteTransaction, CancellationToken, T> body,
            CancellationToken ct = default) => inner.ExecuteReadAsync(body, ct);
        public Task ExecuteWriteAsync(
            Action<SqliteConnection, SqliteTransaction, CancellationToken> body,
            CancellationToken ct = default) => inner.ExecuteWriteAsync(body, ct);

        public void ResetCount() => Interlocked.Exchange(ref _createConnectionCount, 0);
        public void Dispose() => inner.Dispose();
    }
}
