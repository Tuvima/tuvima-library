using System.Globalization;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite list of the files the Engine could not match (<c>unmatched_assets</c>, Library setup spec D34): one row per media asset.
/// Every call uses a short-lived connection. Times are stored as UTC ISO-8601 text with a fixed width, so they compare as text.
/// </summary>
public sealed class UnmatchedAssetRepository(IDatabaseConnection db) : IUnmatchedAssetRepository
{
    private const string SelectSql = """
        SELECT asset_id AS AssetId,
               library_id AS LibraryId,
               reason AS Reason,
               first_unmatched_at AS FirstUnmatchedAt,
               last_tried_at AS LastTriedAt,
               next_retry_at AS NextRetryAt,
               kept_as_is AS KeptAsIs,
               kept_at AS KeptAt
        FROM unmatched_assets
        """;

    public Task MarkUnmatchedAsync(
        Guid assetId, string libraryId, string reason, DateTimeOffset now, DateTimeOffset nextRetryAt, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ct.ThrowIfCancellationRequested();

        using var connection = db.CreateConnection();
        // A row that already exists keeps when it first became Unmatched and any kept mark; a kept item has no next retry.
        connection.Execute("""
            INSERT INTO unmatched_assets
                (asset_id, library_id, reason, first_unmatched_at, last_tried_at, next_retry_at, kept_as_is, kept_at)
            VALUES
                (@assetId, @libraryId, @reason, @now, @now, @nextRetryAt, 0, NULL)
            ON CONFLICT(asset_id) DO UPDATE SET
                library_id = excluded.library_id,
                reason = excluded.reason,
                last_tried_at = excluded.last_tried_at,
                next_retry_at = CASE WHEN unmatched_assets.kept_as_is = 1 THEN NULL ELSE excluded.next_retry_at END;
            """,
            new { assetId, libraryId, reason, now = Format(now), nextRetryAt = Format(nextRetryAt) });
        return Task.CompletedTask;
    }

    public Task<UnmatchedAsset?> GetAsync(Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var row = connection.QuerySingleOrDefault<Row>($"{SelectSql} WHERE asset_id = @assetId;", new { assetId });
        return Task.FromResult(row?.ToModel());
    }

    public Task<bool> ClearAsync(Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        return Task.FromResult(connection.Execute("DELETE FROM unmatched_assets WHERE asset_id = @assetId;", new { assetId }) > 0);
    }

    public Task<bool> KeepAsIsAsync(Guid assetId, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        return Task.FromResult(connection.Execute("""
            UPDATE unmatched_assets
            SET kept_as_is = 1,
                kept_at = COALESCE(kept_at, @now),
                next_retry_at = NULL
            WHERE asset_id = @assetId;
            """,
            new { assetId, now = Format(now) }) > 0);
    }

    public Task<int> KeepAllAsIsAsync(string libraryId, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryId);
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        return Task.FromResult(connection.Execute("""
            UPDATE unmatched_assets
            SET kept_as_is = 1,
                kept_at = @now,
                next_retry_at = NULL
            WHERE library_id = @libraryId AND kept_as_is = 0;
            """,
            new { libraryId, now = Format(now) }));
    }

    public Task<bool> LookUpAgainAsync(Guid assetId, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        return Task.FromResult(connection.Execute("""
            UPDATE unmatched_assets
            SET kept_as_is = 0,
                kept_at = NULL,
                next_retry_at = @now
            WHERE asset_id = @assetId;
            """,
            new { assetId, now = Format(now) }) > 0);
    }

    public Task<bool> MakeDueAsync(Guid assetId, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        return Task.FromResult(connection.Execute(
            "UPDATE unmatched_assets SET next_retry_at = @now WHERE asset_id = @assetId AND kept_as_is = 0;",
            new { assetId, now = Format(now) }) > 0);
    }

    public Task<int> MakeAllDueAsync(string? libraryId, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        return Task.FromResult(connection.Execute("""
            UPDATE unmatched_assets
            SET next_retry_at = @now
            WHERE kept_as_is = 0 AND (@libraryId IS NULL OR library_id = @libraryId);
            """,
            new { libraryId, now = Format(now) }));
    }

    public Task<IReadOnlyList<UnmatchedAsset>> ListDueAsync(DateTimeOffset now, int limit, string? libraryId = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var rows = connection.Query<Row>(
            $"{SelectSql} WHERE kept_as_is = 0 AND next_retry_at IS NOT NULL AND next_retry_at <= @now AND (@libraryId IS NULL OR library_id = @libraryId) ORDER BY next_retry_at, asset_id LIMIT @limit;",
            new { now = Format(now), limit = Math.Max(1, limit), libraryId });
        return Task.FromResult<IReadOnlyList<UnmatchedAsset>>(rows.Select(row => row.ToModel()).ToList());
    }

    public Task RecordTriedAsync(Guid assetId, DateTimeOffset now, DateTimeOffset nextRetryAt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        connection.Execute("""
            UPDATE unmatched_assets
            SET last_tried_at = @now,
                next_retry_at = CASE WHEN kept_as_is = 1 THEN NULL ELSE @nextRetryAt END
            WHERE asset_id = @assetId;
            """,
            new { assetId, now = Format(now), nextRetryAt = Format(nextRetryAt) });
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, int>> CountByLibraryAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var counts = connection.Query<CountRow>(
            "SELECT library_id AS LibraryId, COUNT(*) AS Total FROM unmatched_assets WHERE kept_as_is = 0 GROUP BY library_id;");
        return Task.FromResult<IReadOnlyDictionary<string, int>>(
            counts.ToDictionary(count => count.LibraryId, count => count.Total, StringComparer.OrdinalIgnoreCase));
    }

    public Task<UnmatchedAssetPage> ListByLibraryAsync(string libraryId, bool includeKept, int skip, int take, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryId);
        ct.ThrowIfCancellationRequested();

        using var connection = db.CreateConnection();
        var keptFilter = includeKept ? 1 : 0;
        var total = connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM unmatched_assets WHERE library_id = @libraryId AND (@includeKept = 1 OR kept_as_is = 0);",
            new { libraryId, includeKept = keptFilter });
        var rows = connection.Query<ListRow>("""
            SELECT u.asset_id AS AssetId,
                   u.library_id AS LibraryId,
                   u.reason AS Reason,
                   u.first_unmatched_at AS FirstUnmatchedAt,
                   u.last_tried_at AS LastTriedAt,
                   u.next_retry_at AS NextRetryAt,
                   u.kept_as_is AS KeptAsIs,
                   u.kept_at AS KeptAt,
                   a.file_path_root AS FilePath,
                   (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = u.asset_id AND cv.key = 'title') AS Title
            FROM unmatched_assets u
            JOIN media_assets a ON a.id = u.asset_id
            WHERE u.library_id = @libraryId AND (@includeKept = 1 OR u.kept_as_is = 0)
            ORDER BY u.first_unmatched_at DESC, u.asset_id
            LIMIT @take OFFSET @skip;
            """,
            new { libraryId, includeKept = keptFilter, take = Math.Max(1, take), skip = Math.Max(0, skip) });
        return Task.FromResult(new UnmatchedAssetPage(
            rows.Select(row => new UnmatchedAssetListItem(row.ToModel(), row.FilePath, row.Title)).ToList(),
            total));
    }

    // Fixed width (seven fraction digits, Z), so the text compares in time order.
    private static string Format(DateTimeOffset value) => value.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private sealed class CountRow
    {
        public string LibraryId { get; set; } = string.Empty;
        public int Total { get; set; }
    }

    private class Row
    {
        public Guid AssetId { get; set; }
        public string LibraryId { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string FirstUnmatchedAt { get; set; } = string.Empty;
        public string LastTriedAt { get; set; } = string.Empty;
        public string? NextRetryAt { get; set; }
        public long KeptAsIs { get; set; }
        public string? KeptAt { get; set; }

        public UnmatchedAsset ToModel() => new(
            AssetId,
            LibraryId,
            Reason,
            Parse(FirstUnmatchedAt),
            Parse(LastTriedAt),
            NextRetryAt is null ? null : Parse(NextRetryAt),
            KeptAsIs != 0,
            KeptAt is null ? null : Parse(KeptAt));
    }

    private sealed class ListRow : Row
    {
        public string FilePath { get; set; } = string.Empty;
        public string? Title { get; set; }
    }
}
