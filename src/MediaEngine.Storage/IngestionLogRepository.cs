using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="IIngestionLogRepository"/>.
/// Tracks each file through the ingestion pipeline from detection to completion.
/// Uses Dapper for type-safe column-to-property mapping.
/// </summary>
public sealed class IngestionLogRepository : IIngestionLogRepository
{
    private readonly IDatabaseConnection _db;

    // Reusable SELECT list with column aliases for Dapper mapping.
    private const string SelectColumns = """
        id               AS Id,
        file_path        AS FilePath,
        media_asset_id   AS MediaAssetId,
        content_hash     AS ContentHash,
        status           AS Status,
        media_type       AS MediaType,
        confidence_score AS ConfidenceScore,
        detected_title   AS DetectedTitle,
        normalized_title AS NormalizedTitle,
        wikidata_qid     AS WikidataQid,
        error_detail     AS ErrorDetail,
        created_at       AS CreatedAt,
        updated_at       AS UpdatedAt
        """;

    public IngestionLogRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc/>
    public Task InsertAsync(IngestionLogEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT OR IGNORE INTO ingestion_log
                (id, file_path, media_asset_id, content_hash, status, media_type, confidence_score,
                 detected_title, normalized_title, wikidata_qid, error_detail,
                 created_at, updated_at)
            VALUES
                (@id, @path, @mediaAssetId, @hash, @status, @mediaType, @confidence,
                 @title, @normalized, @qid, @error,
                 @created, @updated);
            """,
            new
            {
                id = entry.Id,
                path = entry.FilePath,
                mediaAssetId = entry.MediaAssetId,
                hash = entry.ContentHash,
                status = entry.Status,
                mediaType = entry.MediaType,
                confidence = entry.ConfidenceScore,
                title = entry.DetectedTitle,
                normalized = entry.NormalizedTitle,
                qid = entry.WikidataQid,
                error = entry.ErrorDetail,
                created = entry.CreatedAt.ToString("O"),
                updated = entry.UpdatedAt.ToString("O"),
            });

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task UpdateStatusAsync(
        Guid id,
        string status,
        string? contentHash = null,
        string? mediaType = null,
        double? confidenceScore = null,
        string? detectedTitle = null,
        string? normalizedTitle = null,
        string? wikidataQid = null,
        Guid? mediaAssetId = null,
        string? errorDetail = null,
        CancellationToken ct = default)
    {
        // Build SET clause dynamically for non-null optional fields.
        var setClauses = new List<string> { "status = @status", "updated_at = @updated" };
        var dp = new DynamicParameters();
        dp.Add("id", id);
        dp.Add("status", status);
        dp.Add("updated", DateTimeOffset.UtcNow.ToString("O"));

        if (contentHash is not null)
        {
            setClauses.Add("content_hash = @hash");
            dp.Add("hash", contentHash);
        }
        if (mediaType is not null)
        {
            setClauses.Add("media_type = @mediaType");
            dp.Add("mediaType", mediaType);
        }
        if (confidenceScore.HasValue)
        {
            setClauses.Add("confidence_score = @confidence");
            dp.Add("confidence", confidenceScore.Value);
        }
        if (detectedTitle is not null)
        {
            setClauses.Add("detected_title = @title");
            dp.Add("title", detectedTitle);
        }
        if (normalizedTitle is not null)
        {
            setClauses.Add("normalized_title = @normalized");
            dp.Add("normalized", normalizedTitle);
        }
        if (wikidataQid is not null)
        {
            setClauses.Add("wikidata_qid = @qid");
            dp.Add("qid", wikidataQid);
        }
        if (mediaAssetId.HasValue)
        {
            setClauses.Add("media_asset_id = @mediaAssetId");
            dp.Add("mediaAssetId", mediaAssetId.Value);
        }
        if (errorDetail is not null)
        {
            setClauses.Add("error_detail = @error");
            dp.Add("error", errorDetail);
        }

        using var conn = _db.CreateConnection();
        conn.Execute(
            $"UPDATE ingestion_log SET {string.Join(", ", setClauses)} WHERE id = @id;",
            dp);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<IngestionLogEntry>> GetRecentAsync(
        int limit = 50,
        CancellationToken ct = default)
    {
        using var conn = _db.CreateConnection();
        var results = conn.Query<IngestionLogEntry>($"""
            SELECT {SelectColumns}
            FROM   ingestion_log
            ORDER BY created_at DESC
            LIMIT  @limit;
            """, new { limit }).AsList();

        return Task.FromResult<IReadOnlyList<IngestionLogEntry>>(results);
    }

    /// <inheritdoc/>
    public Task<IngestionLogEntry?> FindByIdAsync(Guid id, CancellationToken ct = default)
    {
        using var conn = _db.CreateConnection();
        var result = conn.QueryFirstOrDefault<IngestionLogEntry>($"""
            SELECT {SelectColumns}
            FROM   ingestion_log
            WHERE  id = @id;
            """, new { id });

        return Task.FromResult(result);
    }
}
