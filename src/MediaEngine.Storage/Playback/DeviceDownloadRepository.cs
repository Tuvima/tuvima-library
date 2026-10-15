using System.Data;
using System.Globalization;
using Dapper;
using MediaEngine.Contracts.Playback;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Playback;

/// <summary>
/// What each paired app reports it has stored for offline use: one row per device and offline variant in
/// <c>device_downloads</c>, and one row per device, person and kept photo in <c>device_photo_downloads</c>. A row is
/// only visible to its own device and the profile it was reported for, and goes when its variant, asset, photo, device
/// or profile goes. Both kinds count towards the same <see cref="MaxRowsPerDevice"/>.
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
    /// The device's list for one profile (variants and photos together), oldest stored first, with each variant's
    /// current expiry (a photo has none). A variant whose expiry has passed is left out at once, without waiting for
    /// the cleanup pass.
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
              AND (v.expires_at IS NULL OR v.expires_at > @now)
            ORDER BY d.stored_at, d.variant_id
            LIMIT @limit;
            """, new { deviceId, profileId, limit = MaxRowsPerDevice, now = now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) }).AsList();

        var photos = conn.Query<PhotoRow>("""
            SELECT item_id AS ItemId, stored_bytes AS StoredBytes, stored_at AS StoredAt
            FROM device_photo_downloads
            WHERE device_id = @deviceId AND profile_id = @profileId
            ORDER BY stored_at, item_id
            LIMIT @limit;
            """, new { deviceId, profileId, limit = MaxRowsPerDevice }).AsList();

        IReadOnlyList<DeviceDownloadDto> list = rows
            .Where(row => ParseDate(row.ExpiresAt) is not { } expiresAt || expiresAt > now)
            .Select(row => new DeviceDownloadDto
            {
                AssetId = row.AssetId,
                VariantId = row.VariantId,
                Kind = DeviceDownloadKinds.Variant,
                StoredBytes = row.StoredBytes,
                StoredAt = ParseDate(row.StoredAt) ?? DateTimeOffset.MinValue,
                ExpiresAt = ParseDate(row.ExpiresAt),
            })
            .Concat(photos.Select(row => new DeviceDownloadDto
            {
                Kind = DeviceDownloadKinds.Photo,
                PhotoItemId = row.ItemId,
                StoredBytes = row.StoredBytes,
                StoredAt = ParseDate(row.StoredAt) ?? DateTimeOffset.MinValue,
                ExpiresAt = null,
            }))
            .OrderBy(entry => entry.StoredAt)
            .ThenBy(entry => entry.PhotoItemId ?? entry.VariantId)
            .Take(MaxRowsPerDevice)
            .ToList();
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
            if (existing == 0 && CountListed(conn, tx, deviceId, now) >= MaxRowsPerDevice)
            {
                return false;
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

    /// <summary>
    /// Adds or updates the device's entry for a kept photo (one per device, person and View item). Refuses a new entry
    /// once the device holds <see cref="MaxRowsPerDevice"/> entries of any kind.
    /// </summary>
    public Task<bool> UpsertPhotoAsync(
        Guid deviceId,
        Guid profileId,
        Guid itemId,
        long storedBytes,
        DateTimeOffset storedAt,
        DateTimeOffset now,
        CancellationToken ct = default) =>
        _db.ExecuteWriteAsync((conn, tx, _) =>
        {
            var existing = conn.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM device_photo_downloads WHERE device_id = @deviceId AND profile_id = @profileId AND item_id = @itemId;",
                new { deviceId, profileId, itemId }, tx);
            if (existing == 0 && CountListed(conn, tx, deviceId, now) >= MaxRowsPerDevice)
            {
                return false;
            }

            conn.Execute("""
                INSERT INTO device_photo_downloads (device_id, profile_id, item_id, stored_bytes, stored_at, updated_at)
                VALUES (@deviceId, @profileId, @itemId, @storedBytes, @storedAt, @updatedAt)
                ON CONFLICT(device_id, profile_id, item_id) DO UPDATE SET
                    stored_bytes = excluded.stored_bytes,
                    stored_at = excluded.stored_at,
                    updated_at = excluded.updated_at;
                """, new
            {
                deviceId,
                profileId,
                itemId,
                storedBytes,
                storedAt = storedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                updatedAt = now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            }, tx);
            return true;
        }, ct);

    /// <summary>Removes the device's entry for a kept photo, for the profile it was reported under. False when there was none.</summary>
    public Task<bool> DeletePhotoAsync(Guid deviceId, Guid profileId, Guid itemId, CancellationToken ct = default) =>
        _db.ExecuteWriteAsync((conn, tx, _) => conn.Execute(
            "DELETE FROM device_photo_downloads WHERE device_id = @deviceId AND profile_id = @profileId AND item_id = @itemId;",
            new { deviceId, profileId, itemId }, tx) > 0, ct);

    /// <summary>
    /// How many entries of any kind the device holds across all its profiles. Entries whose variant has expired are not
    /// on the list any more, so they do not use up the limit.
    /// </summary>
    private static long CountListed(SqliteConnection conn, IDbTransaction tx, Guid deviceId, DateTimeOffset now) =>
        conn.ExecuteScalar<long>(
            """
            SELECT
                (SELECT COUNT(*) FROM device_downloads d
                 JOIN offline_variants v ON v.id = d.variant_id
                 WHERE d.device_id = @deviceId AND (v.expires_at IS NULL OR v.expires_at > @now))
              + (SELECT COUNT(*) FROM device_photo_downloads WHERE device_id = @deviceId);
            """,
            new { deviceId, now = now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) }, tx);

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

    private sealed class PhotoRow
    {
        public Guid ItemId { get; set; }
        public long StoredBytes { get; set; }
        public string StoredAt { get; set; } = string.Empty;
    }

    private sealed class VariantRow
    {
        public long? FileSizeBytes { get; set; }
        public string? ExpiresAt { get; set; }
    }
}

/// <summary>A variant a device may report as stored: its size on the server and when it expires.</summary>
public sealed record OwnedVariant(long? FileSizeBytes, DateTimeOffset? ExpiresAt);
