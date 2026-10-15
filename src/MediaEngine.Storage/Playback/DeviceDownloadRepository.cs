using System.Globalization;
using Dapper;
using MediaEngine.Contracts.Playback;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Playback;

/// <summary>
/// What each paired app reports it has stored for offline use: one row per device and offline variant in
/// <c>device_downloads</c>. A row is only visible to its own device and the profile it was reported for, and goes
/// when its variant, asset, device or profile goes.
/// </summary>
public sealed class DeviceDownloadRepository
{
    /// <summary>The most downloads one device can hold on its list.</summary>
    public const int MaxRowsPerDevice = 500;

    private readonly IDatabaseConnection _db;

    public DeviceDownloadRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <summary>
    /// The device's list for one profile, oldest stored first, with each variant's current expiry. A variant whose
    /// expiry has passed is left out at once, without waiting for the cleanup pass.
    /// </summary>
    public Task<IReadOnlyList<DeviceDownloadDto>> ListAsync(Guid deviceId, Guid profileId, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<DownloadRow>("""
            SELECT d.asset_id AS AssetId,
                   d.variant_id AS VariantId,
                   d.stored_bytes AS StoredBytes,
                   d.stored_at AS StoredAt,
                   v.expires_at AS ExpiresAt
            FROM device_downloads d
            JOIN offline_variants v ON v.id = d.variant_id
            WHERE d.device_id = @deviceId
              AND d.profile_id = @profileId
            ORDER BY d.stored_at, d.variant_id
            LIMIT @limit;
            """, new { deviceId, profileId, limit = MaxRowsPerDevice }).AsList();

        IReadOnlyList<DeviceDownloadDto> list = rows
            .Where(row => ParseDate(row.ExpiresAt) is not { } expiresAt || expiresAt > now)
            .Select(row => new DeviceDownloadDto
            {
                AssetId = row.AssetId,
                VariantId = row.VariantId,
                StoredBytes = row.StoredBytes,
                StoredAt = ParseDate(row.StoredAt) ?? DateTimeOffset.MinValue,
                ExpiresAt = ParseDate(row.ExpiresAt),
            }).ToList();
        return Task.FromResult(list);
    }

    /// <summary>
    /// The size and expiry of a ready variant that this exact profile and device prepared for this asset, or null when
    /// there is no such variant (unknown, someone else's, not ready, or already expired).
    /// </summary>
    public Task<OwnedVariant?> FindOwnedReadyVariantAsync(
        Guid assetId,
        Guid variantId,
        Guid profileId,
        Guid deviceId,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var row = conn.QueryFirstOrDefault<VariantRow>("""
            SELECT file_size AS FileSizeBytes, expires_at AS ExpiresAt
            FROM offline_variants
            WHERE id = @variantId
              AND asset_id = @assetId
              AND owner_profile_id = @profileId
              AND owner_device_id = @deviceId
              AND status = @ready
            LIMIT 1;
            """, new { variantId, assetId, profileId, deviceId, ready = OfflineVariantStatuses.Ready });

        if (row is null)
        {
            return Task.FromResult<OwnedVariant?>(null);
        }

        var expiresAt = ParseDate(row.ExpiresAt);
        return Task.FromResult<OwnedVariant?>(expiresAt is { } at && at <= now
            ? null
            : new OwnedVariant(row.FileSizeBytes, expiresAt));
    }

    /// <summary>Adds or updates the device's entry for a variant. Refuses a new entry once the device holds <see cref="MaxRowsPerDevice"/>.</summary>
    public Task<bool> UpsertAsync(
        Guid deviceId,
        Guid profileId,
        Guid assetId,
        Guid variantId,
        long storedBytes,
        DateTimeOffset storedAt,
        DateTimeOffset now,
        CancellationToken ct = default) =>
        _db.ExecuteWriteAsync((conn, tx, _) =>
        {
            var existing = conn.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM device_downloads WHERE device_id = @deviceId AND variant_id = @variantId;",
                new { deviceId, variantId }, tx);
            if (existing == 0)
            {
                var total = conn.ExecuteScalar<long>(
                    "SELECT COUNT(*) FROM device_downloads WHERE device_id = @deviceId;",
                    new { deviceId }, tx);
                if (total >= MaxRowsPerDevice)
                {
                    return false;
                }
            }

            conn.Execute("""
                INSERT INTO device_downloads (device_id, profile_id, asset_id, variant_id, stored_bytes, stored_at, updated_at)
                VALUES (@deviceId, @profileId, @assetId, @variantId, @storedBytes, @storedAt, @updatedAt)
                ON CONFLICT(device_id, variant_id) DO UPDATE SET
                    profile_id = excluded.profile_id,
                    asset_id = excluded.asset_id,
                    stored_bytes = excluded.stored_bytes,
                    stored_at = excluded.stored_at,
                    updated_at = excluded.updated_at;
                """, new
            {
                deviceId,
                profileId,
                assetId,
                variantId,
                storedBytes,
                storedAt = storedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                updatedAt = now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            }, tx);
            return true;
        }, ct);

    /// <summary>Removes the device's entry for a variant, for the profile it was reported under. False when there was none.</summary>
    public Task<bool> DeleteAsync(Guid deviceId, Guid profileId, Guid variantId, CancellationToken ct = default) =>
        _db.ExecuteWriteAsync((conn, tx, _) => conn.Execute(
            "DELETE FROM device_downloads WHERE device_id = @deviceId AND profile_id = @profileId AND variant_id = @variantId;",
            new { deviceId, profileId, variantId }, tx) > 0, ct);

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    private sealed class DownloadRow
    {
        public Guid AssetId { get; set; }
        public Guid VariantId { get; set; }
        public long StoredBytes { get; set; }
        public string StoredAt { get; set; } = string.Empty;
        public string? ExpiresAt { get; set; }
    }

    private sealed class VariantRow
    {
        public long? FileSizeBytes { get; set; }
        public string? ExpiresAt { get; set; }
    }
}

/// <summary>A variant a device may report as stored: its size on the server and when it expires.</summary>
public sealed record OwnedVariant(long? FileSizeBytes, DateTimeOffset? ExpiresAt);
