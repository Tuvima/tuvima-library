using System.Globalization;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="IActivityRunStore"/>, backed by <c>activity_runs</c> and <c>activity_run_items</c> (spec library-activity
/// WP1). Short-lived connection per call. Times are stored as ISO 8601 UTC text, so comparing the text compares the times.
/// </summary>
public sealed class ActivityRunRepository(IDatabaseConnection db) : IActivityRunStore
{
    private const int MaxLimit = 500;

    public Task<ActivityRunRecord?> FindOpenRunAsync(string libraryId, string kind, DateTimeOffset notBefore, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var row = connection.QueryFirstOrDefault<RunRow>("""
            SELECT id AS Id, library_id AS LibraryId, kind AS Kind, status AS Status, is_first_run AS IsFirstRun,
                   started_at AS StartedAt, last_activity_at AS LastActivityAt, ended_at AS EndedAt, item_count AS ItemCount, scan_id AS ScanId
            FROM activity_runs
            WHERE library_id = @libraryId AND kind = @kind AND status = 'active' AND last_activity_at >= @notBefore
            ORDER BY started_at DESC
            LIMIT 1;
            """, new { libraryId, kind, notBefore = Iso(notBefore) });
        return Task.FromResult(row?.ToRecord());
    }

    public Task<Guid?> FindOpenScanIdAsync(string kind, DateTimeOffset notBefore, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var id = connection.QueryFirstOrDefault<Guid?>("""
            SELECT COALESCE(scan_id, id)
            FROM activity_runs
            WHERE kind = @kind AND status = 'active' AND last_activity_at >= @notBefore
            ORDER BY started_at DESC
            LIMIT 1;
            """, new { kind, notBefore = Iso(notBefore) });
        return Task.FromResult(id);
    }

    public Task<DateTimeOffset?> GetInitialImportStartAsync(string libraryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var text = connection.ExecuteScalar<string?>("""
            SELECT MIN(started_at)
            FROM activity_runs
            WHERE library_id = @libraryId AND kind = 'added'
              AND NOT EXISTS (SELECT 1 FROM activity_runs later WHERE later.library_id = @libraryId AND later.kind = 'added' AND later.is_first_run = 0);
            """, new { libraryId });
        return Task.FromResult<DateTimeOffset?>(text is null ? null : ParseTime(text));
    }

    public Task<bool> LibraryHasRunsAsync(string libraryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var any = connection.ExecuteScalar<long>(
            "SELECT EXISTS (SELECT 1 FROM activity_runs WHERE library_id = @libraryId);", new { libraryId });
        return Task.FromResult(any != 0);
    }

    public Task InsertRunAsync(ActivityRunRecord run, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        connection.Execute("""
            INSERT INTO activity_runs (id, library_id, kind, status, is_first_run, started_at, last_activity_at, ended_at, item_count, scan_id)
            VALUES (@Id, @LibraryId, @Kind, @Status, @first, @started, @last, @ended, @ItemCount, @ScanId);
            """, new
        {
            run.Id,
            run.LibraryId,
            run.Kind,
            run.Status,
            first = run.IsFirstRun ? 1 : 0,
            started = Iso(run.StartedAt),
            last = Iso(run.LastActivityAt),
            ended = run.EndedAt is { } ended ? Iso(ended) : null,
            run.ItemCount,
            run.ScanId,
        });
        return Task.CompletedTask;
    }

    public Task AddItemAsync(ActivityRunItemRecord item, DateTimeOffset at, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute("""
            INSERT INTO activity_run_items (id, run_id, work_id, asset_id, media_type, title, group_key, group_title, group_work_id, created_at)
            VALUES (@Id, @RunId, @WorkId, @AssetId, @MediaType, @Title, @GroupKey, @GroupTitle, @GroupWorkId, @created);
            """, new
            {
                item.Id,
                item.RunId,
                item.WorkId,
                item.AssetId,
                item.MediaType,
                item.Title,
                item.GroupKey,
                item.GroupTitle,
                item.GroupWorkId,
                created = Iso(item.CreatedAt),
            }, transaction);
            connection.Execute("""
            UPDATE activity_runs SET item_count = item_count + 1, last_activity_at = @at WHERE id = @runId;
            """, new { runId = item.RunId, at = Iso(at) }, transaction);
        }, ct);
    }

    public Task<int> CompleteQuietRunsAsync(DateTimeOffset quietBefore, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var closed = connection.Execute("""
            UPDATE activity_runs SET status = 'completed', ended_at = last_activity_at
            WHERE status = 'active' AND last_activity_at < @quietBefore;
            """, new { quietBefore = Iso(quietBefore) });
        return Task.FromResult(closed);
    }

    public Task<int> CompleteInterruptedRunsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var closed = connection.Execute("""
            UPDATE activity_runs SET status = 'completed', ended_at = last_activity_at, interrupted = 1
            WHERE status = 'active';
            """);
        return Task.FromResult(closed);
    }

    public Task<int> ReopenInterruptedRunsAsync(string kind, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var reopened = connection.Execute("""
            UPDATE activity_runs SET status = 'active', ended_at = NULL, interrupted = 0
            WHERE interrupted = 1 AND kind = @kind;
            """, new { kind });
        return Task.FromResult(reopened);
    }

    public Task<int> ClearInterruptedAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var cleared = connection.Execute("UPDATE activity_runs SET interrupted = 0 WHERE interrupted = 1;");
        return Task.FromResult(cleared);
    }

    public Task<ActivityAssetContext?> GetAssetContextAsync(Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var row = connection.QueryFirstOrDefault<AssetContextRow>(
            "SELECT library_id AS LibraryId, file_path_root AS FilePath FROM media_assets WHERE id = @assetId;", new { assetId });
        return Task.FromResult(row is null ? null : new ActivityAssetContext(row.LibraryId, row.FilePath));
    }

    public Task<IReadOnlyList<ActivityRunSummary>> ListRunsAsync(ActivityRunQuery query, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var parameters = new DynamicParameters();
        var where = BuildWhere(query, parameters);
        parameters.Add("limit", Math.Clamp(query.Limit, 1, MaxLimit));
        parameters.Add("offset", Math.Max(0, query.Offset));
        var order = query.NewestFirst ? "DESC" : "ASC";

        using var connection = db.CreateConnection();
        var rows = connection.Query<RunRow>($"""
            SELECT r.id AS Id, r.library_id AS LibraryId, r.kind AS Kind, r.status AS Status, r.is_first_run AS IsFirstRun,
                   r.started_at AS StartedAt, r.last_activity_at AS LastActivityAt, r.ended_at AS EndedAt, r.item_count AS ItemCount, r.scan_id AS ScanId
            FROM activity_runs r
            WHERE {where}
            ORDER BY r.started_at {order}, r.id {order}
            LIMIT @limit OFFSET @offset;
            """, parameters).ToList();

        var counts = new Dictionary<Guid, Dictionary<string, int>>();
        foreach (var batch in rows.Select(row => row.Id).Chunk(400))
        {
            var perType = connection.Query<CountRow>("""
                SELECT run_id AS RunId, media_type AS MediaType, COUNT(*) AS Total
                FROM activity_run_items WHERE run_id IN @ids GROUP BY run_id, media_type;
                """, new { ids = batch.Select(GuidSql.ToBlob).ToArray() });
            foreach (var entry in perType)
            {
                if (!counts.TryGetValue(entry.RunId, out var bucket))
                {
                    counts[entry.RunId] = bucket = new Dictionary<string, int>(StringComparer.Ordinal);
                }

                bucket[entry.MediaType] = entry.Total;
            }
        }

        IReadOnlyList<ActivityRunSummary> summaries = rows
            .Select(row => new ActivityRunSummary(
                row.ToRecord(),
                counts.TryGetValue(row.Id, out var bucket) ? bucket : new Dictionary<string, int>()))
            .ToList();
        return Task.FromResult(summaries);
    }

    public Task<ActivityRunRecord?> GetRunAsync(Guid entryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var row = connection.QueryFirstOrDefault<RunRow>("""
            SELECT id AS Id, library_id AS LibraryId, kind AS Kind, status AS Status, is_first_run AS IsFirstRun,
                   started_at AS StartedAt, last_activity_at AS LastActivityAt, ended_at AS EndedAt, item_count AS ItemCount, scan_id AS ScanId
            FROM activity_runs WHERE COALESCE(scan_id, id) = @entryId
            ORDER BY started_at
            LIMIT 1;
            """, new { entryId });
        return Task.FromResult(row?.ToRecord());
    }

    public Task<IReadOnlyList<ActivityRunGroup>> ListGroupsAsync(Guid entryId, string? mediaType, int offset, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var rows = connection.Query<GroupRow>("""
            SELECT group_key AS GroupKey,
                   MIN(group_title) AS GroupTitle,
                   MIN(media_type) AS MediaType,
                   MAX(group_work_id) AS GroupWorkId,
                   MIN(work_id) AS FirstWorkId,
                   MIN(asset_id) AS FirstAssetId,
                   COUNT(*) AS ItemCount
            FROM activity_run_items
            WHERE run_id IN (SELECT id FROM activity_runs WHERE COALESCE(scan_id, id) = @entryId) AND (@mediaType IS NULL OR media_type = @mediaType)
            GROUP BY group_key
            ORDER BY MIN(created_at), group_key
            LIMIT @limit OFFSET @offset;
            """, new { entryId, mediaType, limit = Math.Clamp(limit, 1, MaxLimit), offset = Math.Max(0, offset) });
        IReadOnlyList<ActivityRunGroup> groups = rows
            .Select(row => new ActivityRunGroup(row.GroupKey, row.GroupTitle, row.MediaType, row.GroupWorkId, row.FirstWorkId, row.FirstAssetId, row.ItemCount))
            .ToList();
        return Task.FromResult(groups);
    }

    public Task<IReadOnlyList<ActivityTitleHit>> SearchTitlesAsync(ActivityRunQuery query, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(query.Search))
        {
            return Task.FromResult<IReadOnlyList<ActivityTitleHit>>([]);
        }

        // The run filters (library, kind, dates) apply; the title match is done per item here, so a hit says how much of that title one run added.
        var parameters = new DynamicParameters();
        var where = BuildWhere(query with { Search = null }, parameters);
        parameters.Add("like", Like(query.Search));
        parameters.Add("limit", Math.Clamp(query.Limit, 1, MaxLimit));
        var order = query.NewestFirst ? "DESC" : "ASC";

        using var connection = db.CreateConnection();
        var rows = connection.Query<HitRow>($"""
            SELECT i.group_key AS GroupKey, MIN(i.group_title) AS GroupTitle, MIN(i.media_type) AS MediaType,
                   MAX(i.group_work_id) AS GroupWorkId, MIN(i.work_id) AS FirstWorkId,
                   COALESCE(r.scan_id, r.id) AS RunId, MIN(r.library_id) AS LibraryId, r.kind AS Kind, MIN(r.started_at) AS StartedAt, COUNT(*) AS ItemCount
            FROM activity_run_items i
            INNER JOIN activity_runs r ON r.id = i.run_id
            WHERE {where}
              AND (i.title LIKE @like ESCAPE '\' OR i.group_title LIKE @like ESCAPE '\')
            GROUP BY i.group_key, COALESCE(r.scan_id, r.id), r.kind
            ORDER BY MIN(r.started_at) {order}
            LIMIT @limit;
            """, parameters).ToList();
        IReadOnlyList<ActivityTitleHit> hits = rows
            .Select(row => new ActivityTitleHit(
                row.GroupKey, row.GroupTitle, row.MediaType, row.GroupWorkId, row.FirstWorkId, row.RunId, row.LibraryId, row.Kind,
                ParseTime(row.StartedAt), row.ItemCount))
            .ToList();
        return Task.FromResult(hits);
    }

    public Task<DateTimeOffset?> GetLastCompletedAsync(IReadOnlyCollection<string>? libraryIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var parameters = new DynamicParameters();
        var filter = libraryIds is { Count: > 0 } ? "AND library_id IN @libraries" : string.Empty;
        if (libraryIds is { Count: > 0 })
        {
            parameters.Add("libraries", libraryIds.Select(id => id.ToLowerInvariant()).ToArray());
        }

        var text = connection.ExecuteScalar<string?>(
            $"SELECT MAX(ended_at) FROM activity_runs WHERE status = 'completed' {filter};", parameters);
        return Task.FromResult<DateTimeOffset?>(text is null ? null : ParseTime(text));
    }

    public Task<int> PurgeAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        // Items go with their run (ON DELETE CASCADE). An active run is never purged.
        var removed = connection.Execute(
            "DELETE FROM activity_runs WHERE started_at < @olderThan AND status <> 'active';", new { olderThan = Iso(olderThan) });
        return Task.FromResult(removed);
    }

    public Task<IReadOnlySet<Guid>> GetNewWorkIdsAsync(IReadOnlyCollection<Guid> workIds, DateTimeOffset newSince, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var result = new HashSet<Guid>();
        if (workIds.Count == 0)
        {
            return Task.FromResult<IReadOnlySet<Guid>>(result);
        }

        using var connection = db.CreateConnection();
        foreach (var batch in workIds.Distinct().Chunk(400))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var id in connection.Query<Guid>($"""
                SELECT DISTINCT i.work_id
                FROM activity_run_items i
                INNER JOIN activity_runs r ON r.id = i.run_id
                WHERE i.work_id IN @ids AND {NewRunPredicate};
                """, new { ids = batch.Select(GuidSql.ToBlob).ToArray(), newSince = Iso(newSince) }))
            {
                result.Add(id);
            }
        }

        return Task.FromResult<IReadOnlySet<Guid>>(result);
    }

    public Task<IReadOnlySet<Guid>> ListNewWorkIdsAsync(DateTimeOffset newSince, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var ids = connection.Query<Guid>($"""
            SELECT i.work_id
            FROM activity_run_items i
            INNER JOIN activity_runs r ON r.id = i.run_id
            WHERE i.work_id IS NOT NULL AND {NewRunPredicate}
            ORDER BY r.started_at DESC
            LIMIT @limit;
            """, new { newSince = Iso(newSince), limit = Math.Clamp(limit, 1, 5000) });
        return Task.FromResult<IReadOnlySet<Guid>>(ids.ToHashSet());
    }

    // D14: a later run (never a library's first) that added the item and finished, or is still going, within the New window.
    private const string NewRunPredicate =
        "r.kind = 'added' AND r.is_first_run = 0 AND COALESCE(r.ended_at, r.last_activity_at) >= @newSince";

    private static string BuildWhere(ActivityRunQuery query, DynamicParameters parameters)
    {
        var clauses = new List<string> { "1 = 1" };
        if (query.LibraryIds is { Count: > 0 } libraries)
        {
            parameters.Add("libraries", libraries.Select(id => id.ToLowerInvariant()).ToArray());
            clauses.Add("r.library_id IN @libraries");
        }

        if (query.Kinds is { Count: > 0 } kinds)
        {
            parameters.Add("kinds", kinds.ToArray());
            clauses.Add("r.kind IN @kinds");
        }

        if (query.From is { } from)
        {
            parameters.Add("from", Iso(from));
            clauses.Add("r.started_at >= @from");
        }

        if (query.To is { } to)
        {
            parameters.Add("to", Iso(to));
            clauses.Add("r.started_at < @to");
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            parameters.Add("search", Like(query.Search));
            clauses.Add("""
                EXISTS (SELECT 1 FROM activity_run_items m WHERE m.run_id = r.id
                        AND (m.title LIKE @search ESCAPE '\' OR m.group_title LIKE @search ESCAPE '\'))
                """);
        }

        return string.Join(" AND ", clauses);
    }

    private static string Like(string text)
        => "%" + text.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string text)
        => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal);

    private sealed class RunRow
    {
        public Guid Id { get; set; }
        public string LibraryId { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public long IsFirstRun { get; set; }
        public string StartedAt { get; set; } = string.Empty;
        public string LastActivityAt { get; set; } = string.Empty;
        public string? EndedAt { get; set; }
        public long ItemCount { get; set; }
        public Guid? ScanId { get; set; }

        public ActivityRunRecord ToRecord() => new(
            Id, LibraryId, Kind, Status, IsFirstRun != 0, ParseTime(StartedAt), ParseTime(LastActivityAt),
            EndedAt is null ? null : ParseTime(EndedAt), (int)ItemCount, ScanId);
    }

    private sealed class CountRow
    {
        public Guid RunId { get; set; }
        public string MediaType { get; set; } = string.Empty;
        public int Total { get; set; }
    }

    private sealed class GroupRow
    {
        public string GroupKey { get; set; } = string.Empty;
        public string GroupTitle { get; set; } = string.Empty;
        public string MediaType { get; set; } = string.Empty;
        public Guid? GroupWorkId { get; set; }
        public Guid? FirstWorkId { get; set; }
        public Guid? FirstAssetId { get; set; }
        public int ItemCount { get; set; }
    }

    private sealed class HitRow
    {
        public string GroupKey { get; set; } = string.Empty;
        public string GroupTitle { get; set; } = string.Empty;
        public string MediaType { get; set; } = string.Empty;
        public Guid? GroupWorkId { get; set; }
        public Guid? FirstWorkId { get; set; }
        public Guid RunId { get; set; }
        public string LibraryId { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string StartedAt { get; set; } = string.Empty;
        public int ItemCount { get; set; }
    }

    private sealed class AssetContextRow
    {
        public string? LibraryId { get; set; }
        public string? FilePath { get; set; }
    }
}
