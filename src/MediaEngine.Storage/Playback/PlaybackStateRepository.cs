using System.Globalization;
using Dapper;
using MediaEngine.Contracts.Playback;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Playback;

public sealed class PlaybackStateRepository
{
    private readonly IDatabaseConnection _db;

    public PlaybackStateRepository(IDatabaseConnection db)
    {
        _db = db;
    }

    public Task StoreInspectionAsync(
        Guid assetId,
        string sourceHash,
        long? fileSize,
        double? durationSecs,
        string? container,
        string? metadataJson,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO playback_inspection_cache
                (asset_id, source_hash, inspected_at, file_size, duration_secs, container, metadata_json)
            VALUES
                (@assetId, @sourceHash, @inspectedAt, @fileSize, @durationSecs, @container, @metadataJson)
            ON CONFLICT(asset_id, source_hash) DO UPDATE SET
                inspected_at = excluded.inspected_at,
                file_size = excluded.file_size,
                duration_secs = excluded.duration_secs,
                container = excluded.container,
                metadata_json = excluded.metadata_json;
            """,
            new
            {
                assetId,
                sourceHash,
                inspectedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                fileSize,
                durationSecs,
                container,
                metadataJson,
            });

        return Task.CompletedTask;
    }

    public Task<string?> GetInspectionMetadataAsync(
        Guid assetId,
        string sourceHash,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var metadata = conn.QueryFirstOrDefault<string?>("""
            SELECT metadata_json
            FROM playback_inspection_cache
            WHERE asset_id = @assetId
              AND source_hash = @sourceHash
            LIMIT 1;
            """, new { assetId, sourceHash });
        return Task.FromResult(metadata);
    }

    public Task<IReadOnlyList<OfflineVariantDto>> ListOfflineVariantsAsync(
        Guid assetId,
        string sourceHash,
        Guid? ownerProfileId = null,
        Guid? ownerDeviceId = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<OfflineVariantRow>("""
            SELECT id AS Id,
                   asset_id AS AssetId,
                   profile_key AS ProfileKey,
                   source_hash AS SourceHash,
                   display_name AS DisplayName,
                   status AS Status,
                   output_path AS OutputPath,
                   file_size AS FileSizeBytes,
                   container AS Container,
                   video_codec AS VideoCodec,
                   audio_codec AS AudioCodec,
                   width AS Width,
                   height AS Height,
                   bitrate_kbps AS BitrateKbps,
                   created_at AS CreatedAt,
                   expires_at AS ExpiresAt
            FROM offline_variants
            WHERE asset_id = @assetId
              AND source_hash = @sourceHash
              AND (@ownerProfileId IS NULL OR owner_profile_id = @ownerProfileId)
              AND (@ownerDeviceId IS NULL OR owner_device_id = @ownerDeviceId)
            ORDER BY created_at DESC;
            """,
            new { assetId, sourceHash, ownerProfileId, ownerDeviceId }).AsList();

        // An expired variant is gone as far as a client is concerned, whether or not the cleanup pass has removed it yet.
        var now = DateTimeOffset.UtcNow;
        return Task.FromResult<IReadOnlyList<OfflineVariantDto>>(
            rows.Where(row => !IsExpired(row.ExpiresAt, now)).Select(ToDto).ToList());
    }

    public Task<OfflineVariantFile?> GetOfflineVariantFileAsync(
        Guid assetId,
        Guid variantId,
        Guid? ownerProfileId = null,
        Guid? ownerDeviceId = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var row = conn.QueryFirstOrDefault<VariantFileRow>("""
            SELECT output_path AS OutputPath,
                   status AS Status,
                   file_size AS FileSizeBytes,
                   expires_at AS ExpiresAt
            FROM offline_variants
            WHERE id = @variantId
              AND asset_id = @assetId
              AND (@ownerProfileId IS NULL OR owner_profile_id = @ownerProfileId)
              AND (@ownerDeviceId IS NULL OR owner_device_id = @ownerDeviceId)
            LIMIT 1;
            """,
            new { assetId, variantId, ownerProfileId, ownerDeviceId });

        // Expiry is enforced here too, so a download stops at expires_at even when cleanup is switched off or has not run yet.
        return Task.FromResult<OfflineVariantFile?>(row is null || IsExpired(row.ExpiresAt, DateTimeOffset.UtcNow)
            ? null
            : new OfflineVariantFile(row.OutputPath, row.Status, row.FileSizeBytes));
    }

    /// <summary>Every offline variant whose <c>expires_at</c> is at or before <paramref name="now"/>, whatever its status.</summary>
    public Task<IReadOnlyList<ExpiredOfflineVariant>> ListExpiredOfflineVariantsAsync(
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<ExpiredOfflineVariantRow>("""
            SELECT id AS Id, output_path AS OutputPath, file_size AS FileSizeBytes, expires_at AS ExpiresAt
            FROM offline_variants
            WHERE expires_at IS NOT NULL;
            """).AsList();

        // Parsed here rather than compared as text so every stored offset format is judged on the same clock.
        IReadOnlyList<ExpiredOfflineVariant> expired = rows
            .Where(row => ParseDate(row.ExpiresAt) is { } at && at <= now)
            .Select(row => new ExpiredOfflineVariant(row.Id, row.OutputPath, row.FileSizeBytes))
            .ToList();
        return Task.FromResult(expired);
    }

    /// <summary>
    /// Removes the variant row, any device list entry that still marks it as stored, and the completed encode job that
    /// produced it, in one transaction. Without the job a later request for the same variant would be answered with the
    /// finished job (queueing reuses completed jobs) and the variant could never be prepared again.
    /// </summary>
    public Task DeleteOfflineVariantAsync(Guid variantId, CancellationToken ct = default) =>
        _db.ExecuteWriteAsync((conn, tx, _) =>
        {
            conn.Execute("""
                DELETE FROM device_downloads WHERE variant_id = @variantId;
                DELETE FROM encode_jobs
                WHERE status = 'complete'
                  AND output_path IS NOT NULL
                  AND EXISTS (SELECT 1 FROM offline_variants v
                              WHERE v.id = @variantId AND v.asset_id = encode_jobs.asset_id AND v.output_path = encode_jobs.output_path);
                DELETE FROM offline_variants WHERE id = @variantId;
                """, new { variantId }, tx);
        }, ct);

    /// <summary>
    /// True when another offline variant that has not expired (any profile or device) points at the same file, so the
    /// file must stay when this variant is removed. Paths are compared without regard to case: a false "shared" only
    /// keeps a file a little longer, never deletes one that is in use.
    /// </summary>
    public Task<bool> IsFileUsedByAnotherLiveVariantAsync(
        Guid variantId,
        string outputPath,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var others = conn.Query<string?>(
            "SELECT expires_at FROM offline_variants WHERE id <> @variantId AND output_path = @outputPath COLLATE NOCASE;",
            new { variantId, outputPath }).AsList();
        return Task.FromResult(others.Any(expiresAt => !IsExpired(expiresAt, now)));
    }

    private static bool IsExpired(string? expiresAt, DateTimeOffset now) =>
        ParseDate(expiresAt) is { } at && at <= now;

    public Task<EncodeJobDto> QueueEncodeJobAsync(
        Guid assetId,
        string profileKey,
        string sourceHash,
        DateTimeOffset? scheduledFor,
        Guid? ownerProfileId = null,
        Guid? ownerDeviceId = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();

        var existing = conn.QueryFirstOrDefault<EncodeJobRow>("""
            SELECT id AS Id,
                   asset_id AS AssetId,
                   owner_profile_id AS OwnerProfileId,
                   owner_device_id AS OwnerDeviceId,
                   profile_key AS ProfileKey,
                   status AS Status,
                   created_at AS CreatedAt,
                   scheduled_for AS ScheduledFor,
                   started_at AS StartedAt,
                   completed_at AS CompletedAt,
                   progress_pct AS ProgressPct,
                   output_path AS OutputPath,
                   output_bytes AS OutputBytes,
                   last_error AS LastError,
                   retry_count AS RetryCount
            FROM encode_jobs
             WHERE asset_id = @assetId
               AND profile_key = @profileKey
               AND source_hash = @sourceHash
               AND (@ownerProfileId IS NULL OR owner_profile_id = @ownerProfileId)
               AND (@ownerDeviceId IS NULL OR owner_device_id = @ownerDeviceId)
               AND (status IN ('queued', 'scheduled', 'running')
                    OR (status = 'complete' AND EXISTS (
                        SELECT 1 FROM offline_variants v
                        WHERE v.asset_id = encode_jobs.asset_id AND v.output_path = encode_jobs.output_path)))
            ORDER BY created_at DESC
            LIMIT 1;
            """,
            new { assetId, profileKey, sourceHash, ownerProfileId, ownerDeviceId });

        if (existing is not null)
        {
            return Task.FromResult(ToDto(existing));
        }

        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var status = scheduledFor.HasValue && scheduledFor.Value > now
            ? EncodeJobStatuses.Scheduled
            : EncodeJobStatuses.Queued;

        conn.Execute("""
            INSERT INTO encode_jobs
                (id, asset_id, owner_profile_id, owner_device_id, profile_key, source_hash, status, created_at, scheduled_for, progress_pct)
            VALUES
                (@id, @assetId, @ownerProfileId, @ownerDeviceId, @profileKey, @sourceHash, @status, @createdAt, @scheduledFor, 0);
            """,
            new
            {
                id,
                assetId,
                ownerProfileId,
                ownerDeviceId,
                profileKey,
                sourceHash,
                status,
                createdAt = now.ToString("O", CultureInfo.InvariantCulture),
                scheduledFor = scheduledFor?.ToString("O", CultureInfo.InvariantCulture),
            });

        return Task.FromResult(new EncodeJobDto
        {
            Id = id,
            AssetId = assetId,
            ProfileKey = profileKey,
            Status = status,
            CreatedAt = now,
            ScheduledFor = scheduledFor,
        });
    }

    public Task<IReadOnlyList<EncodeJobDto>> ListEncodeJobsAsync(
        Guid? ownerProfileId = null,
        Guid? ownerDeviceId = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<EncodeJobRow>("""
            SELECT id AS Id,
                   asset_id AS AssetId,
                   profile_key AS ProfileKey,
                   status AS Status,
                   created_at AS CreatedAt,
                   scheduled_for AS ScheduledFor,
                   started_at AS StartedAt,
                   completed_at AS CompletedAt,
                   progress_pct AS ProgressPct,
                   output_path AS OutputPath,
                   output_bytes AS OutputBytes,
                   last_error AS LastError,
                   retry_count AS RetryCount,
                   (SELECT v.expires_at FROM offline_variants v
                    WHERE v.asset_id = encode_jobs.asset_id AND v.output_path = encode_jobs.output_path
                    LIMIT 1) AS ExpiresAt
            FROM encode_jobs
            WHERE (@ownerProfileId IS NULL OR owner_profile_id = @ownerProfileId)
              AND (@ownerDeviceId IS NULL OR owner_device_id = @ownerDeviceId)
            ORDER BY created_at DESC
            LIMIT 200;
            """, new { ownerProfileId, ownerDeviceId }).AsList();

        return Task.FromResult<IReadOnlyList<EncodeJobDto>>(rows.Select(ToDto).ToList());
    }

    public Task<IReadOnlyList<EncodeJobDto>> ListActiveEncodeJobsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<EncodeJobRow>("""
            SELECT id AS Id,
                   asset_id AS AssetId,
                   profile_key AS ProfileKey,
                   status AS Status,
                   created_at AS CreatedAt,
                   scheduled_for AS ScheduledFor,
                   started_at AS StartedAt,
                   completed_at AS CompletedAt,
                   progress_pct AS ProgressPct,
                   output_path AS OutputPath,
                   output_bytes AS OutputBytes,
                   last_error AS LastError,
                   retry_count AS RetryCount
            FROM encode_jobs
            WHERE status IN ('queued', 'scheduled', 'running')
            ORDER BY COALESCE(scheduled_for, created_at), created_at
            LIMIT 50;
            """).AsList();

        return Task.FromResult<IReadOnlyList<EncodeJobDto>>(rows.Select(ToDto).ToList());
    }

    public Task<bool> CancelEncodeJobAsync(
        Guid jobId,
        Guid? ownerProfileId = null,
        Guid? ownerDeviceId = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var affected = conn.Execute("""
            UPDATE encode_jobs
            SET status = 'cancelled',
                completed_at = @completedAt
            WHERE id = @jobId
              AND (@ownerProfileId IS NULL OR owner_profile_id = @ownerProfileId)
              AND (@ownerDeviceId IS NULL OR owner_device_id = @ownerDeviceId)
              AND status IN ('queued', 'scheduled', 'running');
            """,
            new
            {
                jobId,
                ownerProfileId,
                ownerDeviceId,
                completedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            });

        return Task.FromResult(affected > 0);
    }

    public Task<LeasedEncodeJob?> LeaseNextEncodeJobAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var row = conn.QueryFirstOrDefault<LeasedEncodeJobRow>("""
            SELECT id AS Id,
                   asset_id AS AssetId,
                   owner_profile_id AS OwnerProfileId,
                   owner_device_id AS OwnerDeviceId,
                   profile_key AS ProfileKey,
                   source_hash AS SourceHash
            FROM encode_jobs
            WHERE status IN ('queued', 'scheduled')
              AND (scheduled_for IS NULL OR scheduled_for <= @now)
            ORDER BY COALESCE(scheduled_for, created_at), created_at
            LIMIT 1;
            """,
            new { now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) });

        if (row is null)
        {
            return Task.FromResult<LeasedEncodeJob?>(null);
        }

        var changed = conn.Execute("""
            UPDATE encode_jobs
            SET status = 'running',
                started_at = @startedAt,
                progress_pct = 1
            WHERE id = @jobId
              AND status IN ('queued', 'scheduled');
            """,
            new
            {
                jobId = row.Id,
                startedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            });

        return Task.FromResult(changed == 0
            ? null
            : new LeasedEncodeJob(row.Id, row.AssetId, row.OwnerProfileId, row.OwnerDeviceId, row.ProfileKey, row.SourceHash));
    }

    public Task CompleteEncodeJobAsync(
        LeasedEncodeJob job,
        string outputPath,
        string displayName,
        string container,
        string? videoCodec,
        string? audioCodec,
        int? width,
        int? height,
        int? bitrateKbps,
        DateTimeOffset? expiresAt = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var info = new FileInfo(outputPath);
        var variantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO offline_variants
                (id, asset_id, owner_profile_id, owner_device_id, profile_key, source_hash, display_name, status, output_path, file_size,
                 container, video_codec, audio_codec, width, height, bitrate_kbps, created_at, expires_at)
            VALUES
                (@id, @assetId, @ownerProfileId, @ownerDeviceId, @profileKey, @sourceHash, @displayName, 'ready', @outputPath, @fileSize,
                 @container, @videoCodec, @audioCodec, @width, @height, @bitrateKbps, @createdAt, @expiresAt);

            UPDATE encode_jobs
            SET status = 'complete',
                completed_at = @createdAt,
                progress_pct = 100,
                output_path = @outputPath,
                output_bytes = @fileSize,
                last_error = NULL
            WHERE id = @jobId;
            """,
            new
            {
                id = variantId,
                assetId = job.AssetId,
                ownerProfileId = job.OwnerProfileId,
                ownerDeviceId = job.OwnerDeviceId,
                profileKey = job.ProfileKey,
                sourceHash = job.SourceHash,
                displayName,
                outputPath,
                fileSize = info.Exists ? info.Length : 0,
                container,
                videoCodec,
                audioCodec,
                width,
                height,
                bitrateKbps,
                createdAt = now,
                expiresAt = expiresAt?.ToString("O", CultureInfo.InvariantCulture),
                jobId = job.Id,
            });

        return Task.CompletedTask;
    }

    public Task FailEncodeJobAsync(Guid jobId, string error, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE encode_jobs
            SET status = 'failed',
                completed_at = @completedAt,
                last_error = @error,
                retry_count = retry_count + 1
            WHERE id = @jobId;
            """,
            new
            {
                jobId,
                completedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                error,
            });

        return Task.CompletedTask;
    }

    private static OfflineVariantDto ToDto(OfflineVariantRow row)
    {
        return new OfflineVariantDto
        {
            Id = row.Id,
            AssetId = row.AssetId,
            ProfileKey = row.ProfileKey,
            DisplayName = row.DisplayName,
            Status = row.Status,
            DownloadUrl = string.Equals(row.Status, OfflineVariantStatuses.Ready, StringComparison.OrdinalIgnoreCase)
                ? $"/api/v1/playback/{row.AssetId}/offline/{row.Id}"
                : null,
            FileSizeBytes = row.FileSizeBytes,
            Container = row.Container,
            VideoCodec = row.VideoCodec,
            AudioCodec = row.AudioCodec,
            Width = row.Width,
            Height = row.Height,
            BitrateKbps = row.BitrateKbps,
            CreatedAt = ParseDate(row.CreatedAt) ?? DateTimeOffset.MinValue,
            ExpiresAt = ParseDate(row.ExpiresAt),
            SourceHash = row.SourceHash,
        };
    }

    private static EncodeJobDto ToDto(EncodeJobRow row) => new()
    {
        Id = row.Id,
        AssetId = row.AssetId,
        ProfileKey = row.ProfileKey,
        Status = row.Status,
        CreatedAt = ParseDate(row.CreatedAt) ?? DateTimeOffset.MinValue,
        ScheduledFor = ParseDate(row.ScheduledFor),
        StartedAt = ParseDate(row.StartedAt),
        CompletedAt = ParseDate(row.CompletedAt),
        ProgressPct = row.ProgressPct,
        // Keep the established API v1 field while never exposing the Engine's
        // local filesystem path. Download access is represented by the scoped
        // OfflineVariantDto URL instead.
        OutputPath = null,
        OutputBytes = row.OutputBytes,
        LastError = row.LastError,
        RetryCount = row.RetryCount,
        ExpiresAt = ParseDate(row.ExpiresAt),
    };

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    private sealed class OfflineVariantRow
    {
        public Guid Id { get; set; }
        public Guid AssetId { get; set; }
        public string ProfileKey { get; set; } = string.Empty;
        public string SourceHash { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string? OutputPath { get; set; }
        public long? FileSizeBytes { get; set; }
        public string? Container { get; set; }
        public string? VideoCodec { get; set; }
        public string? AudioCodec { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public int? BitrateKbps { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
        public string? ExpiresAt { get; set; }
    }

    private sealed class VariantFileRow
    {
        public string? OutputPath { get; set; }
        public string Status { get; set; } = string.Empty;
        public long? FileSizeBytes { get; set; }
        public string? ExpiresAt { get; set; }
    }

    private sealed class ExpiredOfflineVariantRow
    {
        public Guid Id { get; set; }
        public string? OutputPath { get; set; }
        public long? FileSizeBytes { get; set; }
        public string? ExpiresAt { get; set; }
    }

    private sealed class EncodeJobRow
    {
        public Guid Id { get; set; }
        public Guid AssetId { get; set; }
        public string ProfileKey { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = string.Empty;
        public string? ScheduledFor { get; set; }
        public string? StartedAt { get; set; }
        public string? CompletedAt { get; set; }
        public double ProgressPct { get; set; }
        public string? OutputPath { get; set; }
        public long? OutputBytes { get; set; }
        public string? LastError { get; set; }
        public int RetryCount { get; set; }
        public string? ExpiresAt { get; set; }
    }

    private sealed class LeasedEncodeJobRow
    {
        public Guid Id { get; set; }
        public Guid AssetId { get; set; }
        public Guid? OwnerProfileId { get; set; }
        public Guid? OwnerDeviceId { get; set; }
        public string ProfileKey { get; set; } = string.Empty;
        public string SourceHash { get; set; } = string.Empty;
    }
}

public sealed record OfflineVariantFile(string? OutputPath, string Status, long? FileSizeBytes);

public sealed record ExpiredOfflineVariant(Guid Id, string? OutputPath, long? FileSizeBytes);

public sealed record LeasedEncodeJob(
    Guid Id,
    Guid AssetId,
    Guid? OwnerProfileId,
    Guid? OwnerDeviceId,
    string ProfileKey,
    string SourceHash);
