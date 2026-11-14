using System.Globalization;
using System.Text.Json;
using Dapper;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// One media file's stored facts for a details page's "Your copy" row: where it sits, how big it is, when it was added, its edition, and what
/// the last inspection found (<see cref="Probe"/>, null when the file has not been scanned yet). Nothing here is read from the file itself.
/// </summary>
public sealed record YourCopyAsset(
    Guid AssetId,
    Guid EditionId,
    string? EditionLabel,
    string FilePath,
    long? FileSizeBytes,
    DateTimeOffset? AddedAt,
    string? Container,
    double? DurationSeconds,
    long? RenditionBitrateBps,
    MediaProbeResult? Probe);

/// <summary>
/// Reads the stored file facts behind a details page's "Your copy" row (details-page-bottom spec D4, D5): for a list of media assets, each file's
/// edition, size, added date, container and cached playback inspection. Files in a Vault library are never returned, so the row never names them.
/// </summary>
public sealed class YourCopyReadRepository(IDatabaseConnection db)
{
    // A page lists at most this many files: one batched read per slice keeps the query parameters well under SQLite's limit.
    private const int SliceSize = 400;

    public Task<IReadOnlyList<YourCopyAsset>> GetAsync(IReadOnlyCollection<Guid> assetIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(assetIds);
        if (assetIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<YourCopyAsset>>([]);
        }

        var distinct = assetIds.Distinct().ToList();
        return db.ExecuteReadAsync<IReadOnlyList<YourCopyAsset>>((connection, transaction, token) =>
        {
            var found = new List<YourCopyAsset>(distinct.Count);
            foreach (var slice in distinct.Chunk(SliceSize))
            {
                token.ThrowIfCancellationRequested();
                var rows = connection.Query<Row>(new CommandDefinition($"""
                    SELECT ma.id AS AssetId, e.id AS EditionId, NULLIF(e.format_label, '') AS EditionLabel,
                           ma.file_path_root AS FilePath, ma.presented_at AS PresentedAt,
                           ma.rendition_bitrate_bps AS RenditionBitrateBps,
                           pic.file_size AS InspectedFileSize, pic.duration_secs AS InspectedDurationSeconds,
                           pic.metadata_json AS InspectionJson,
                           (SELECT fhc.size_bytes FROM file_hash_cache fhc WHERE fhc.sha256 = ma.content_hash LIMIT 1) AS HashedFileSize,
                           (SELECT MIN(il.created_at) FROM ingestion_log il WHERE il.media_asset_id = ma.id) AS FirstLoggedAt,
                           (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = ma.id AND cv.key = 'container' LIMIT 1) AS Container
                    FROM media_assets ma
                    JOIN editions e ON e.id = ma.edition_id
                    LEFT JOIN playback_inspection_cache pic ON pic.asset_id = ma.id AND pic.source_hash = ma.content_hash
                    WHERE ma.id IN @ids AND ma.status = 'Normal' AND ma.is_orphaned = 0
                      AND {VaultSql.NotVaultAsset("ma.id")}
                    ORDER BY lower(COALESCE(e.format_label, '')), e.id, lower(ma.file_path_root), ma.id;
                    """, new { ids = slice.Select(GuidSql.ToBlob).ToArray() }, transaction, cancellationToken: token));
                found.AddRange(rows.Select(ToAsset));
            }

            return found;
        }, ct);
    }

    private static YourCopyAsset ToAsset(Row row) => new(
        row.AssetId,
        row.EditionId,
        row.EditionLabel,
        row.FilePath,
        row.InspectedFileSize is > 0 ? row.InspectedFileSize : row.HashedFileSize is > 0 ? row.HashedFileSize : null,
        ParseTimestamp(row.PresentedAt) ?? ParseTimestamp(row.FirstLoggedAt),
        string.IsNullOrWhiteSpace(row.Container) ? null : row.Container.Trim(),
        row.InspectedDurationSeconds is > 0 ? row.InspectedDurationSeconds : null,
        row.RenditionBitrateBps is > 0 ? row.RenditionBitrateBps : null,
        ReadCachedProbe(row.InspectionJson));

    private static MediaProbeResult? ReadCachedProbe(string? inspectionJson)
    {
        if (string.IsNullOrWhiteSpace(inspectionJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<MediaProbeResult>(inspectionJson);
        }
        catch (JsonException)
        {
            // A damaged cache row only hides these optional facts (the row reads "Reading file facts"); the next inspection rewrites it.
            return null;
        }
    }

    private static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;

    private sealed class Row
    {
        public Guid AssetId { get; init; }
        public Guid EditionId { get; init; }
        public string? EditionLabel { get; init; }
        public string FilePath { get; init; } = string.Empty;
        public string? PresentedAt { get; init; }
        public long? RenditionBitrateBps { get; init; }
        public long? InspectedFileSize { get; init; }
        public double? InspectedDurationSeconds { get; init; }
        public string? InspectionJson { get; init; }
        public long? HashedFileSize { get; init; }
        public string? FirstLoggedAt { get; init; }
        public string? Container { get; init; }
    }
}
