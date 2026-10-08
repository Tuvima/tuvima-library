using Dapper;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="IMediaAssetRepository"/>.
/// Uses Dapper for type-safe column-to-property mapping.
///
/// Spec: Phase 4 – Hash Dominance (content_hash UNIQUE + INSERT OR IGNORE);
///       Phase 7 – Asset lifecycle status transitions.
/// </summary>
public sealed class MediaAssetRepository : IMediaAssetRepository
{
    private readonly IDatabaseConnection _db;

    /// <summary>
    /// Flat projection used by Dapper to read rows. The <c>Status</c> column is
    /// stored as TEXT in SQLite; we capture it as a string and convert to the
    /// <see cref="AssetStatus"/> enum in <see cref="ToAsset"/>.
    /// </summary>
    private sealed class MediaAssetRow
    {
        public Guid Id { get; set; }
        public Guid EditionId { get; set; }
        public string ContentHash { get; set; } = string.Empty;
        public string FilePathRoot { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string? LibraryId { get; set; }
        public long IsOrphaned { get; set; }
        public string? OrphanedAt { get; set; }
        public string RenditionPurpose { get; set; } = "Original";
        public Guid? DerivedFromAssetId { get; set; }
        public string? EncoderProfileVersion { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public long? BitrateBitsPerSecond { get; set; }
        public string? VideoCodec { get; set; }
        public string? AudioCodec { get; set; }
        public string? DynamicRange { get; set; }
        public string? AudioLayout { get; set; }
        public string? RenditionGeneratedAt { get; set; }
        public string? SourceFingerprint { get; set; }
    }

    private static MediaAsset ToAsset(MediaAssetRow r) => new()
    {
        Id = r.Id,
        EditionId = r.EditionId,
        ContentHash = r.ContentHash,
        FilePathRoot = r.FilePathRoot,
        Status = Enum.Parse<AssetStatus>(r.Status, ignoreCase: true),
        LibraryId = r.LibraryId,
        IsOrphaned = r.IsOrphaned != 0,
        OrphanedAt = string.IsNullOrEmpty(r.OrphanedAt)
            ? null
            : DateTimeOffset.Parse(r.OrphanedAt, System.Globalization.CultureInfo.InvariantCulture),
        RenditionPurpose = Enum.TryParse<RenditionPurpose>(r.RenditionPurpose, true, out var purpose)
            ? purpose : RenditionPurpose.Original,
        DerivedFromAssetId = r.DerivedFromAssetId,
        EncoderProfileVersion = r.EncoderProfileVersion,
        Width = r.Width,
        Height = r.Height,
        BitrateBitsPerSecond = r.BitrateBitsPerSecond,
        VideoCodec = r.VideoCodec,
        AudioCodec = r.AudioCodec,
        DynamicRange = r.DynamicRange,
        AudioLayout = r.AudioLayout,
        RenditionGeneratedAt = string.IsNullOrEmpty(r.RenditionGeneratedAt) ? null
            : DateTimeOffset.Parse(r.RenditionGeneratedAt, System.Globalization.CultureInfo.InvariantCulture),
        SourceFingerprint = r.SourceFingerprint,
    };

    private const string SelectColumns = """
        id             AS Id,
        edition_id     AS EditionId,
        content_hash   AS ContentHash,
        file_path_root AS FilePathRoot,
        status         AS Status,
        library_id     AS LibraryId,
        is_orphaned    AS IsOrphaned,
        orphaned_at    AS OrphanedAt,
        rendition_purpose AS RenditionPurpose,
        derived_from_asset_id AS DerivedFromAssetId,
        encoder_profile_version AS EncoderProfileVersion,
        rendition_width AS Width,
        rendition_height AS Height,
        rendition_bitrate_bps AS BitrateBitsPerSecond,
        rendition_video_codec AS VideoCodec,
        rendition_audio_codec AS AudioCodec,
        rendition_dynamic_range AS DynamicRange,
        rendition_audio_layout AS AudioLayout,
        rendition_generated_at AS RenditionGeneratedAt,
        rendition_source_fingerprint AS SourceFingerprint
        """;

    public MediaAssetRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc/>
    public Task<MediaAsset?> FindByHashAsync(string contentHash, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);

        using var conn = _db.CreateConnection();
        var row = conn.QueryFirstOrDefault<MediaAssetRow>($"""
            SELECT {SelectColumns}
            FROM   media_assets
            WHERE  content_hash = @contentHash
            LIMIT  1;
            """, new { contentHash });

        return Task.FromResult(row is null ? null : (MediaAsset?)ToAsset(row));
    }

    /// <inheritdoc/>
    public Task<MediaAsset?> FindByPathRootAsync(string pathRoot, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(pathRoot);

        using var conn = _db.CreateConnection();
        var row = conn.QueryFirstOrDefault<MediaAssetRow>($"""
            SELECT {SelectColumns}
            FROM   media_assets
            WHERE  file_path_root = @pathRoot COLLATE NOCASE
            LIMIT  1;
            """, new { pathRoot });

        return Task.FromResult(row is null ? null : (MediaAsset?)ToAsset(row));
    }

    /// <inheritdoc/>
    public Task<MediaAsset?> FindByIdAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var row = conn.QueryFirstOrDefault<MediaAssetRow>($"""
            SELECT {SelectColumns}
            FROM   media_assets
            WHERE  id = @id
            LIMIT  1;
            """, new { id });

        return Task.FromResult(row is null ? null : (MediaAsset?)ToAsset(row));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Uses <c>INSERT OR IGNORE</c> on the <c>content_hash</c> unique constraint.
    /// If the hash already exists the insert is silently skipped and the method
    /// returns <see langword="false"/> — no exception is thrown.
    ///
    /// After the INSERT, <c>SELECT changes()</c> is called on the same connection.
    /// Because SQLite serialises all operations on a single connection,
    /// <c>changes()</c> reliably reflects the row count of the immediately
    /// preceding statement.
    /// </remarks>
    public Task<bool> InsertAsync(MediaAsset asset, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(asset);

        using var conn = _db.CreateConnection();
        if (asset.DerivedFromAssetId == asset.Id)
        {
            return Task.FromResult(false);
        }
        if (asset.DerivedFromAssetId is Guid sourceId)
        {
            var sourceEdition = conn.QuerySingleOrDefault<Guid?>(
                "SELECT edition_id FROM media_assets WHERE id=@sourceId;", new { sourceId });
            if (sourceEdition != asset.EditionId)
            {
                return Task.FromResult(false);
            }
        }
        var parameters = new DynamicParameters();
        parameters.Add("id", asset.Id);
        parameters.Add("editionId", asset.EditionId);
        parameters.Add("contentHash", asset.ContentHash);
        parameters.Add("filePathRoot", asset.FilePathRoot);
        parameters.Add("status", asset.Status.ToString());
        parameters.Add("libraryId", asset.LibraryId);
        parameters.Add("isOrphaned", asset.IsOrphaned ? 1 : 0);
        parameters.Add("orphanedAt", asset.OrphanedAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        parameters.Add("renditionPurpose", asset.RenditionPurpose.ToString());
        parameters.Add("derivedFromAssetId", asset.DerivedFromAssetId);
        parameters.Add("encoderProfileVersion", asset.EncoderProfileVersion);
        parameters.Add("width", asset.Width);
        parameters.Add("height", asset.Height);
        parameters.Add("bitrateBitsPerSecond", asset.BitrateBitsPerSecond);
        parameters.Add("videoCodec", asset.VideoCodec);
        parameters.Add("audioCodec", asset.AudioCodec);
        parameters.Add("dynamicRange", asset.DynamicRange);
        parameters.Add("audioLayout", asset.AudioLayout);
        parameters.Add("renditionGeneratedAt", asset.RenditionGeneratedAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        parameters.Add("sourceFingerprint", asset.SourceFingerprint);

        conn.Execute("""
            INSERT OR IGNORE INTO media_assets
                (id, edition_id, content_hash, file_path_root, status, library_id, is_orphaned, orphaned_at,
                 rendition_purpose, derived_from_asset_id, encoder_profile_version, rendition_width,
                 rendition_height, rendition_bitrate_bps, rendition_video_codec, rendition_audio_codec,
                 rendition_dynamic_range, rendition_audio_layout, rendition_generated_at,
                 rendition_source_fingerprint)
            VALUES
                (@id, @editionId, @contentHash, @filePathRoot, @status, @libraryId, @isOrphaned, @orphanedAt,
                 @renditionPurpose, @derivedFromAssetId, @encoderProfileVersion, @width, @height,
                 @bitrateBitsPerSecond, @videoCodec, @audioCodec, @dynamicRange, @audioLayout,
                 @renditionGeneratedAt, @sourceFingerprint);
            """, parameters);

        // Step 2: changes() returns 1 if a row was inserted, 0 if IGNORE fired.
        var changes = conn.ExecuteScalar<long>("SELECT changes();");
        return Task.FromResult(changes > 0);
    }

    /// <inheritdoc/>
    public Task UpdateStatusAsync(Guid id, AssetStatus status, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE media_assets
            SET    status = @status
            WHERE  id     = @id;
            """,
            new
            {
                status = status.ToString(),
                id,
            });

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task UpdateFilePathAsync(Guid id, string newPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(newPath);

        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE media_assets
            SET    file_path_root = @path
            WHERE  id             = @id;
            """,
            new
            {
                path = newPath,
                id,
            });

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> MarkPresentedAsync(Guid id, DateTimeOffset presentedAt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE media_assets
            SET    presented_at = @presentedAt
            WHERE  id = @id
              AND  presented_at IS NULL;
            """,
            new
            {
                id,
                presentedAt = presentedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            });

        var changes = conn.ExecuteScalar<long>("SELECT changes();");
        return Task.FromResult(changes > 0);
    }

    /// <inheritdoc/>
    public Task<bool> UpdateContentHashAsync(Guid id, string contentHash, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);

        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE OR IGNORE media_assets
            SET    content_hash = @contentHash
            WHERE  id = @id;
            """,
            new
            {
                contentHash,
                id,
            });

        var changes = conn.ExecuteScalar<long>("SELECT changes();");
        return Task.FromResult(changes > 0);
    }

    /// <inheritdoc/>
    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        conn.Execute(
            "DELETE FROM media_assets WHERE id = @id;",
            new { id });

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<MediaAsset>> ListByStatusAsync(AssetStatus status, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var rows = conn.Query<MediaAssetRow>($"""
            SELECT {SelectColumns}
            FROM   media_assets
            WHERE  status = @status;
            """, new { status = status.ToString() }).AsList();

        return Task.FromResult<IReadOnlyList<MediaAsset>>(rows.Select(ToAsset).ToList());
    }

    /// <inheritdoc/>
    public Task<MediaAsset?> FindFirstByWorkIdAsync(Guid workId, CancellationToken ct = default, Guid? profileId = null)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var row = conn.QueryFirstOrDefault<MediaAssetRow>($"""
            SELECT ma.id             AS Id,
                   ma.edition_id     AS EditionId,
                   ma.content_hash   AS ContentHash,
                   ma.file_path_root AS FilePathRoot,
                   ma.status         AS Status,
                   ma.library_id     AS LibraryId,
                   ma.is_orphaned    AS IsOrphaned,
                   ma.orphaned_at    AS OrphanedAt,
                   ma.rendition_purpose AS RenditionPurpose,
                   ma.derived_from_asset_id AS DerivedFromAssetId,
                   ma.encoder_profile_version AS EncoderProfileVersion,
                   ma.rendition_width AS Width,
                   ma.rendition_height AS Height,
                   ma.rendition_bitrate_bps AS BitrateBitsPerSecond,
                   ma.rendition_video_codec AS VideoCodec,
                   ma.rendition_audio_codec AS AudioCodec,
                   ma.rendition_dynamic_range AS DynamicRange,
                   ma.rendition_audio_layout AS AudioLayout,
                   ma.rendition_generated_at AS RenditionGeneratedAt,
                   ma.rendition_source_fingerprint AS SourceFingerprint
            FROM   media_assets ma
            JOIN   editions e ON e.id = ma.edition_id
            LEFT JOIN user_states us ON us.asset_id=ma.id AND us.user_id=@profileId
            WHERE  e.work_id = @workId
              AND  ma.status = 'Normal' AND ma.is_orphaned=0
            ORDER BY us.last_accessed DESC,
                     CASE ma.rendition_purpose WHEN 'Original' THEN 0 ELSE 1 END,
                     ma.id
            LIMIT  1;
            """, new { workId, profileId });

        return Task.FromResult(row is null ? null : (MediaAsset?)ToAsset(row));
    }

    /// <inheritdoc/>
    public Task<HashSet<string>> GetAllFilePathsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var paths = conn.Query<string>(
            "SELECT file_path_root FROM media_assets;")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Task.FromResult(paths);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Joins media_assets → editions → works to surface the work's media_type
    /// alongside the asset's writeback state. The WHERE clause keeps the result
    /// set focused on rows the sweep can actually act on:
    ///   • file is in the live library (status = 'Normal')
    ///   • not currently sitting in a retry cooldown
    ///   • not previously marked permanently failed
    ///   • the stored hash differs from the expected per-type hash
    ///
    /// Filtering by media_type happens client-side because expected hashes are
    /// passed in as a dictionary — SQLite cannot bind a per-row lookup directly.
    /// </remarks>
    public Task<IReadOnlyList<StaleRetagAsset>> GetStaleForRetagAsync(
        IReadOnlyDictionary<string, string> expectedHashesByMediaType,
        int batchSize,
        long nowEpochSeconds,
        CancellationToken ct = default,
        Guid? afterAssetId = null)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(expectedHashesByMediaType);

        using var conn = _db.CreateConnection();
        // Read more rows than batchSize so client-side filtering can still
        // produce a full batch when many rows happen to match the expected hash.
        var fetchLimit = Math.Max(batchSize * 4, 200);

        var stale = new List<StaleRetagAsset>(batchSize);
        var cursor = afterAssetId;
        while (stale.Count < batchSize)
        {
            var rows = conn.Query<(Guid Id, string FilePathRoot, string MediaType, string? Hash, string? Status, int Attempts)>("""
                SELECT ma.id             AS Id,
                       ma.file_path_root AS FilePathRoot,
                       w.media_type      AS MediaType,
                       ma.writeback_fields_hash AS Hash,
                       ma.writeback_status AS Status,
                       ma.writeback_attempts    AS Attempts
                FROM   media_assets ma
                JOIN   editions e ON e.id = ma.edition_id
                JOIN   works    w ON w.id = e.work_id
                WHERE  ma.status = 'Normal'
                  AND  COALESCE(ma.writeback_status, '') <> 'failed'
                  AND  COALESCE(ma.writeback_next_retry_at, 0) <= @now
                  AND  (@cursor IS NULL OR ma.id > @cursor)
                ORDER BY ma.id
                LIMIT @limit;
                """, new { now = nowEpochSeconds, cursor, limit = fetchLimit }).AsList();
            if (rows.Count == 0)
            {
                break;
            }
            foreach (var r in rows)
            {
                cursor = r.Id;
                if (!expectedHashesByMediaType.TryGetValue(r.MediaType, out var expected))
                {
                    continue;
                }
                // NULL means no prior write; newly ingested files are not swept.
                if (r.Hash is null || string.Equals(r.Hash, expected, StringComparison.Ordinal)
                    || (string.Equals(r.Status, "unverified", StringComparison.Ordinal)
                        && string.Equals(r.Hash, "unverified:" + expected, StringComparison.Ordinal))
                    || (string.Equals(r.Status, "unsupported", StringComparison.Ordinal)
                        && string.Equals(r.Hash, "unsupported:" + expected, StringComparison.Ordinal)))
                {
                    continue;
                }

                stale.Add(new StaleRetagAsset(r.Id, r.FilePathRoot, r.MediaType, r.Hash, r.Attempts));

                if (stale.Count >= batchSize)
                {
                    break;
                }
            }
            if (rows.Count < fetchLimit)
            {
                break;
            }
        }

        return Task.FromResult<IReadOnlyList<StaleRetagAsset>>(stale);
    }

    /// <inheritdoc/>
    public Task UpdateWritebackHashAsync(Guid assetId, string newHash, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(newHash);

        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE media_assets
            SET    writeback_fields_hash   = @hash,
                   writeback_status        = 'ok',
                   writeback_last_error    = NULL,
                   writeback_attempts      = 0,
                   writeback_next_retry_at = NULL
            WHERE  id = @id;
            """,
            new { hash = newHash, id = assetId });

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MediaAsset>> ListByEditionAsync(Guid editionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<MediaAssetRow>($"SELECT {SelectColumns} FROM media_assets WHERE edition_id=@editionId ORDER BY id;", new { editionId });
        return Task.FromResult<IReadOnlyList<MediaAsset>>(rows.Select(ToAsset).ToArray());
    }

    public async Task<bool> UpdateRenditionAsync(MediaAsset asset, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Width is <= 0 || asset.Height is <= 0 || asset.BitrateBitsPerSecond is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(asset), "Rendition dimensions and bitrate must be positive when supplied.");
        }
        if (asset.DerivedFromAssetId == asset.Id)
        {
            return false;
        }

        var result = await _db.ExecuteWriteAsync((conn, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var current = conn.QuerySingleOrDefault<(Guid EditionId, Guid? ParentId)>(
                "SELECT edition_id AS EditionId, derived_from_asset_id AS ParentId FROM media_assets WHERE id=@id;",
                new { id = asset.Id }, transaction);
            if (current.EditionId == Guid.Empty)
            {
                return (Changed: false, EditionId: asset.EditionId);
            }
            var targetEditionId = current.EditionId;
            if (asset.DerivedFromAssetId is Guid sourceId)
            {
                var sourceEdition = conn.QuerySingleOrDefault<Guid?>(
                    "SELECT edition_id FROM media_assets WHERE id=@sourceId;", new { sourceId }, transaction);
                if (sourceEdition is null)
                {
                    return (Changed: false, EditionId: asset.EditionId);
                }
                targetEditionId = sourceEdition.Value;
                var cycle = conn.ExecuteScalar<long>("""
                    WITH RECURSIVE ancestors(id) AS (
                        SELECT @sourceId
                        UNION ALL
                        SELECT ma.derived_from_asset_id FROM media_assets ma JOIN ancestors a ON ma.id=a.id
                        WHERE ma.derived_from_asset_id IS NOT NULL
                    )
                    SELECT EXISTS(SELECT 1 FROM ancestors WHERE id=@assetId);
                    """, new { sourceId, assetId = asset.Id }, transaction);
                if (cycle != 0)
                {
                    return (Changed: false, EditionId: asset.EditionId);
                }
            }

            var parameters = new DynamicParameters();
            parameters.Add("id", asset.Id);
            parameters.Add("editionId", targetEditionId);
            parameters.Add("purpose", asset.RenditionPurpose.ToString());
            parameters.Add("derivedFromAssetId", asset.DerivedFromAssetId);
            parameters.Add("encoderProfileVersion", asset.EncoderProfileVersion);
            parameters.Add("width", asset.Width);
            parameters.Add("height", asset.Height);
            parameters.Add("bitrate", asset.BitrateBitsPerSecond);
            parameters.Add("videoCodec", asset.VideoCodec);
            parameters.Add("audioCodec", asset.AudioCodec);
            parameters.Add("dynamicRange", asset.DynamicRange);
            parameters.Add("audioLayout", asset.AudioLayout);
            parameters.Add("generatedAt", asset.RenditionGeneratedAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            parameters.Add("sourceFingerprint", asset.SourceFingerprint);
            var changed = conn.Execute("""
                UPDATE media_assets SET
                    edition_id=@editionId, rendition_purpose=@purpose, derived_from_asset_id=@derivedFromAssetId,
                    encoder_profile_version=@encoderProfileVersion, rendition_width=@width,
                    rendition_height=@height, rendition_bitrate_bps=@bitrate,
                    rendition_video_codec=@videoCodec, rendition_audio_codec=@audioCodec,
                    rendition_dynamic_range=@dynamicRange, rendition_audio_layout=@audioLayout,
                    rendition_generated_at=@generatedAt, rendition_source_fingerprint=@sourceFingerprint
                WHERE id=@id;
                """, parameters, transaction);
            return (Changed: changed == 1, EditionId: targetEditionId);
        }, ct).ConfigureAwait(false);
        if (result.Changed)
        {
            asset.EditionId = result.EditionId;
        }
        return result.Changed;
    }

    /// <inheritdoc/>
    public Task MarkWritebackUnverifiedAsync(Guid assetId, string expectedHash, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHash);
        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE media_assets
            SET writeback_fields_hash = @marker,
                writeback_status = 'unverified',
                writeback_last_error = NULL,
                writeback_attempts = 0,
                writeback_next_retry_at = NULL
            WHERE id = @id;
            """, new { marker = "unverified:" + expectedHash, id = assetId });
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task MarkWritebackUnsupportedAsync(Guid assetId, string expectedHash, string reason, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHash);
        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE media_assets
            SET writeback_fields_hash = @marker,
                writeback_status = 'unsupported',
                writeback_last_error = @reason,
                writeback_attempts = 0,
                writeback_next_retry_at = NULL
            WHERE id = @id;
            """, new { marker = "unsupported:" + expectedHash, reason, id = assetId });
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task ScheduleRetagRetryAsync(
        Guid assetId,
        long nextRetryAtEpochSeconds,
        string error,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE media_assets
            SET    writeback_status        = 'retry',
                   writeback_attempts      = COALESCE(writeback_attempts, 0) + 1,
                   writeback_last_error    = @error,
                   writeback_next_retry_at = @next
            WHERE  id = @id;
            """,
            new
            {
                error = error ?? string.Empty,
                next = nextRetryAtEpochSeconds,
                id = assetId,
            });

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task MarkRetagFailedAsync(Guid assetId, string error, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE media_assets
            SET    writeback_status     = 'failed',
                   writeback_last_error = @error
            WHERE  id = @id;
            """,
            new { error = error ?? string.Empty, id = assetId });

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task SetLibraryIdAsync(Guid id, string? libraryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE media_assets
            SET    library_id = @libraryId
            WHERE  id         = @id;
            """,
            new { libraryId, id });

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task MarkOrphanedAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE media_assets
            SET    is_orphaned = 1,
                   orphaned_at = @now
            WHERE  id          = @id;
            """,
            new
            {
                now = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                id,
            });

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task ClearOrphanedAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE media_assets
            SET    is_orphaned = 0,
                   orphaned_at = NULL
            WHERE  id          = @id;
            """,
            new { id });

        return Task.CompletedTask;
    }
}
