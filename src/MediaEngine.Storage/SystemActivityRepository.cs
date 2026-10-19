using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="ISystemActivityRepository"/>, <see cref="ISystemActivityRetention"/> and <see cref="ISystemActivityRangeReader"/>.
/// Uses Dapper for type-safe column-to-property mapping.
/// </summary>
public sealed class SystemActivityRepository : ISystemActivityRepository, ISystemActivityRetention, ISystemActivityRangeReader
{
    private readonly IDatabaseConnection _db;

    public SystemActivityRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc/>
    public Task LogAsync(SystemActivityEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        using var conn = _db.CreateConnection();
        Write(conn, transaction: null, entry);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Appends one entry on a caller-owned connection so storage code that already runs inside a
    /// transaction can record what it did atomically with the change itself.
    /// </summary>
    internal static void Write(System.Data.IDbConnection conn, System.Data.IDbTransaction? transaction, SystemActivityEntry entry)
    {
        conn.Execute("""
            INSERT INTO system_activity
                (occurred_at, action_type, collection_name, entity_id, entity_type, profile_id, changes_json, detail, ingestion_run_id)
            VALUES
                (@OccurredAt, @ActionType, @CollectionName, @EntityId, @EntityType, @ProfileId, @ChangesJson, @Detail, @IngestionRunId);
            """,
            new
            {
                OccurredAt = entry.OccurredAt.ToString("O"),
                entry.ActionType,
                entry.CollectionName,
                EntityId = entry.EntityId,
                entry.EntityType,
                ProfileId = entry.ProfileId,
                entry.ChangesJson,
                entry.Detail,
                IngestionRunId = entry.IngestionRunId,
            },
            transaction);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<SystemActivityEntry>> GetRecentAsync(
        int limit = 50,
        CancellationToken ct = default)
    {
        using var conn = _db.CreateConnection();
        var results = conn.Query<SystemActivityEntry>("""
            SELECT id             AS Id,
                   occurred_at    AS OccurredAt,
                   action_type    AS ActionType,
                   collection_name       AS CollectionName,
                   entity_id      AS EntityId,
                   entity_type    AS EntityType,
                   profile_id     AS ProfileId,
                   changes_json   AS ChangesJson,
                   detail         AS Detail,
                   ingestion_run_id AS IngestionRunId
            FROM   system_activity
            ORDER BY id DESC
            LIMIT  @limit;
            """,
            new { limit })
            .AsList();

        return Task.FromResult<IReadOnlyList<SystemActivityEntry>>(results);
    }

    /// <inheritdoc/>
    public Task<int> PruneOlderThanAsync(int retentionDays, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays).ToString("O");

        using var conn = _db.CreateConnection();
        var deleted = conn.Execute("""
            DELETE FROM system_activity
            WHERE occurred_at < @cutoff;
            """,
            new { cutoff });

        return Task.FromResult(deleted);
    }

    /// <inheritdoc/>
    public Task<int> PruneToMaxEventsAsync(long maxEvents, CancellationToken ct = default)
    {
        if (maxEvents < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEvents), "The event cap must be at least 1.");
        }

        using var conn = _db.CreateConnection();
        // The row just past the newest N is the newest one to drop; ids only grow, so everything at or below it is older.
        var deleted = conn.Execute("""
            DELETE FROM system_activity
            WHERE id <= (SELECT id FROM system_activity ORDER BY id DESC LIMIT 1 OFFSET @maxEvents);
            """,
            new { maxEvents });

        return Task.FromResult(deleted);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<SystemActivityEntry>> GetRangeAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        Guid? matchId,
        IReadOnlyCollection<string>? actionTypes,
        string? detailContains,
        int limit,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        // Entries are stored as round-trip UTC text, which sorts in time order. Each filter that is not asked for is switched
        // off by a flag, so there is one fixed query.
        var parameters = new DynamicParameters();
        parameters.Add("from", from.ToUniversalTime().ToString("O"));
        parameters.Add("to", to.ToUniversalTime().ToString("O"));
        parameters.Add("limit", limit);
        parameters.Add("useMatch", matchId is null ? 0 : 1);
        parameters.Add("matchId", matchId ?? Guid.Empty);
        parameters.Add("useTypes", actionTypes is null ? 0 : 1);
        parameters.Add("types", actionTypes is { Count: > 0 } ? actionTypes.ToArray() : [string.Empty]);
        parameters.Add("detail", string.IsNullOrEmpty(detailContains) ? null : "%" + EscapeLike(detailContains) + "%");

        using var conn = _db.CreateConnection();
        var results = conn.Query<SystemActivityEntry>(RangeSql, parameters).AsList();

        return Task.FromResult<IReadOnlyList<SystemActivityEntry>>(results);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<string>> GetActionTypesAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        using var conn = _db.CreateConnection();
        var types = conn.Query<string>(
            "SELECT DISTINCT action_type FROM system_activity WHERE occurred_at >= @from AND occurred_at <= @to;",
            new { from = from.ToUniversalTime().ToString("O"), to = to.ToUniversalTime().ToString("O") })
            .AsList();

        return Task.FromResult<IReadOnlyList<string>>(types);
    }

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    // The newest `limit` matches, then put back in time order.
    private const string RangeSql = """
            SELECT * FROM (
                SELECT id             AS Id,
                       occurred_at    AS OccurredAt,
                       action_type    AS ActionType,
                       collection_name       AS CollectionName,
                       entity_id      AS EntityId,
                       entity_type    AS EntityType,
                       profile_id     AS ProfileId,
                       changes_json   AS ChangesJson,
                       detail         AS Detail,
                       ingestion_run_id AS IngestionRunId
                FROM   system_activity
                WHERE  occurred_at >= @from AND occurred_at <= @to
                  AND  (@useMatch = 0 OR ingestion_run_id = @matchId OR entity_id = @matchId)
                  AND  (@useTypes = 0 OR action_type IN @types)
                  AND  (@detail IS NULL OR detail LIKE @detail ESCAPE '\')
                ORDER BY id DESC
                LIMIT  @limit
            )
            ORDER BY Id ASC;
            """;

    /// <inheritdoc/>
    public Task<long> CountAsync(CancellationToken ct = default)
    {
        using var conn = _db.CreateConnection();
        var count = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM system_activity;");
        return Task.FromResult(count);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<SystemActivityEntry>> GetByRunIdAsync(
        Guid runId,
        CancellationToken ct = default)
    {
        using var conn = _db.CreateConnection();
        var results = conn.Query<SystemActivityEntry>("""
            SELECT id             AS Id,
                   occurred_at    AS OccurredAt,
                   action_type    AS ActionType,
                   collection_name       AS CollectionName,
                   entity_id      AS EntityId,
                   entity_type    AS EntityType,
                   profile_id     AS ProfileId,
                   changes_json   AS ChangesJson,
                   detail         AS Detail,
                   ingestion_run_id AS IngestionRunId
            FROM   system_activity
            WHERE  ingestion_run_id = @runId
            ORDER BY id ASC;
            """,
            new { runId })
            .AsList();

        return Task.FromResult<IReadOnlyList<SystemActivityEntry>>(results);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<SystemActivityEntry>> GetRecentByTypesAsync(
        IReadOnlyList<string> actionTypes,
        int limit = 50,
        CancellationToken ct = default)
    {
        if (actionTypes.Count == 0)
        {
            return GetRecentAsync(limit, ct);
        }

        using var conn = _db.CreateConnection();

        // Build parameterized IN clause: @t0, @t1, @t2, ...
        var paramNames = new List<string>();
        var parameters = new DynamicParameters();
        for (var i = 0; i < actionTypes.Count; i++)
        {
            var paramName = $"t{i}";
            paramNames.Add($"@{paramName}");
            parameters.Add(paramName, actionTypes[i]);
        }
        parameters.Add("limit", limit);

        var inClause = string.Join(", ", paramNames);
        var results = conn.Query<SystemActivityEntry>($"""
            SELECT id             AS Id,
                   occurred_at    AS OccurredAt,
                   action_type    AS ActionType,
                   collection_name       AS CollectionName,
                   entity_id      AS EntityId,
                   entity_type    AS EntityType,
                   profile_id     AS ProfileId,
                   changes_json   AS ChangesJson,
                   detail         AS Detail,
                   ingestion_run_id AS IngestionRunId
            FROM   system_activity
            WHERE  action_type IN ({inClause})
            ORDER BY id DESC
            LIMIT  @limit;
            """,
            parameters)
            .AsList();

        return Task.FromResult<IReadOnlyList<SystemActivityEntry>>(results);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<SystemActivityEntry>> GetRecentByProfileAsync(
        Guid profileId,
        int limit = 50,
        CancellationToken ct = default)
    {
        using var conn = _db.CreateConnection();
        var results = conn.Query<SystemActivityEntry>("""
            SELECT id             AS Id,
                   occurred_at    AS OccurredAt,
                   action_type    AS ActionType,
                   collection_name       AS CollectionName,
                   entity_id      AS EntityId,
                   entity_type    AS EntityType,
                   profile_id     AS ProfileId,
                   changes_json   AS ChangesJson,
                   detail         AS Detail,
                   ingestion_run_id AS IngestionRunId
            FROM   system_activity
            WHERE  profile_id = @profileId
            ORDER BY id DESC
            LIMIT  @limit;
            """,
            new { profileId, limit })
            .AsList();

        return Task.FromResult<IReadOnlyList<SystemActivityEntry>>(results);
    }
}
