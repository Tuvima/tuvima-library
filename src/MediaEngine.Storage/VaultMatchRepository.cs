using System.Globalization;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="IVaultMatchRepository"/>: how each Vault video was matched, where each of its fields came
/// from, and the fingerprints computed for it. The tables refuse a row for a file that is not in a Vault library.
/// </summary>
public sealed class VaultMatchRepository(IDatabaseConnection database) : IVaultMatchRepository
{
    public Task<VaultFileFacts?> GetFileFactsAsync(Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var row = connection.QueryFirstOrDefault<FactsRow>(
            """
            SELECT ma.library_id AS LibraryId, ma.file_path_root AS FilePath,
                   (SELECT c.duration_secs FROM playback_inspection_cache c
                     WHERE c.asset_id = ma.id AND c.source_hash = ma.content_hash LIMIT 1) AS DurationSeconds,
                   (SELECT c.file_size FROM playback_inspection_cache c
                     WHERE c.asset_id = ma.id AND c.source_hash = ma.content_hash LIMIT 1) AS SizeBytes
            FROM media_assets ma
            JOIN vault_libraries vl ON vl.library_id = ma.library_id COLLATE NOCASE
            WHERE ma.id = @assetId;
            """,
            new { assetId });
        return Task.FromResult<VaultFileFacts?>(row is null ? null : new VaultFileFacts(assetId, row.LibraryId, row.FilePath, row.DurationSeconds, row.SizeBytes));
    }

    public Task<VaultMatch?> GetMatchAsync(Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var row = connection.QueryFirstOrDefault<MatchRow>(
            """
            SELECT server_id AS ServerId, record_id AS RecordId, matched_by AS MatchedBy, applied_via AS AppliedVia, matched_at AS MatchedAt
            FROM vault_matches WHERE asset_id = @assetId;
            """,
            new { assetId });
        return Task.FromResult<VaultMatch?>(row is null
            ? null
            : new VaultMatch(assetId, row.ServerId, row.RecordId, row.MatchedBy, row.AppliedVia, ParseTime(row.MatchedAt)));
    }

    public Task SaveMatchAsync(VaultMatch match, IReadOnlyList<VaultFieldSource> fields, CancellationToken ct = default)
    {
        if (!AdultMatchSignals.IsValid(match.MatchedBy) || !VaultAppliedVia.IsValid(match.AppliedVia))
        {
            throw new ArgumentException("Unknown match signal.", nameof(match));
        }

        if (fields.Any(f => !VaultFieldNames.IsValid(f.Field) || f.AssetId != match.AssetId))
        {
            throw new ArgumentException("Unknown Vault field.", nameof(fields));
        }

        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute(
                """
                INSERT INTO vault_matches (asset_id, server_id, record_id, matched_by, applied_via, matched_at)
                VALUES (@AssetId, @ServerId, @RecordId, @MatchedBy, @AppliedVia, @matchedAt)
                ON CONFLICT(asset_id) DO UPDATE SET server_id = excluded.server_id, record_id = excluded.record_id,
                    matched_by = excluded.matched_by, applied_via = excluded.applied_via, matched_at = excluded.matched_at;
                """,
                new { match.AssetId, match.ServerId, match.RecordId, match.MatchedBy, match.AppliedVia, matchedAt = Format(match.MatchedAt) },
                transaction);

            foreach (var field in fields)
            {
                connection.Execute(
                    """
                    INSERT INTO vault_field_sources (asset_id, field, server_id, record_id, matched_by, applied_via, language, applied_at)
                    VALUES (@AssetId, @Field, @ServerId, @RecordId, @MatchedBy, @AppliedVia, @Language, @appliedAt)
                    ON CONFLICT(asset_id, field) DO UPDATE SET server_id = excluded.server_id, record_id = excluded.record_id,
                        matched_by = excluded.matched_by, applied_via = excluded.applied_via, language = excluded.language,
                        applied_at = excluded.applied_at;
                    """,
                    new { field.AssetId, field.Field, field.ServerId, field.RecordId, field.MatchedBy, field.AppliedVia, field.Language, appliedAt = Format(field.AppliedAt) },
                    transaction);
            }
        }, ct);
    }

    public Task<IReadOnlyList<VaultFieldSource>> GetFieldSourcesAsync(Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<SourceRow>(
            """
            SELECT field AS Field, server_id AS ServerId, record_id AS RecordId, matched_by AS MatchedBy, applied_via AS AppliedVia,
                   language AS Language, applied_at AS AppliedAt
            FROM vault_field_sources WHERE asset_id = @assetId ORDER BY field;
            """,
            new { assetId });
        return Task.FromResult<IReadOnlyList<VaultFieldSource>>(rows
            .Select(r => new VaultFieldSource(assetId, r.Field, r.ServerId, r.RecordId, r.MatchedBy, r.AppliedVia, r.Language, ParseTime(r.AppliedAt)))
            .ToList());
    }

    public Task<IReadOnlyList<VaultStoredFingerprint>> GetFingerprintsAsync(Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<FingerprintRow>(
            "SELECT algorithm AS Algorithm, hash AS Hash, file_size AS FileSize, computed_at AS ComputedAt FROM vault_file_fingerprints WHERE asset_id = @assetId ORDER BY algorithm;",
            new { assetId });
        return Task.FromResult<IReadOnlyList<VaultStoredFingerprint>>(rows
            .Select(r => new VaultStoredFingerprint(r.Algorithm, r.Hash, r.FileSize, ParseTime(r.ComputedAt)))
            .ToList());
    }

    public Task SaveFingerprintsAsync(Guid assetId, IReadOnlyList<VaultStoredFingerprint> fingerprints, CancellationToken ct = default)
    {
        if (fingerprints.Any(f => !AdultFingerprintAlgorithms.IsValid(f.Algorithm)))
        {
            throw new ArgumentException("Unknown fingerprint kind.", nameof(fingerprints));
        }

        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute("DELETE FROM vault_file_fingerprints WHERE asset_id = @assetId;", new { assetId }, transaction);
            foreach (var fingerprint in fingerprints)
            {
                connection.Execute(
                    """
                    INSERT INTO vault_file_fingerprints (asset_id, algorithm, hash, file_size, computed_at)
                    VALUES (@assetId, @Algorithm, @Hash, @FileSize, @computedAt);
                    """,
                    new { assetId, fingerprint.Algorithm, fingerprint.Hash, fingerprint.FileSize, computedAt = Format(fingerprint.ComputedAt) },
                    transaction);
            }
        }, ct);
    }

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private sealed class FactsRow
    {
        public string LibraryId { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public double? DurationSeconds { get; set; }
        public long? SizeBytes { get; set; }
    }

    private sealed class MatchRow
    {
        public string ServerId { get; set; } = string.Empty;
        public string RecordId { get; set; } = string.Empty;
        public string MatchedBy { get; set; } = string.Empty;
        public string AppliedVia { get; set; } = string.Empty;
        public string MatchedAt { get; set; } = string.Empty;
    }

    private sealed class SourceRow
    {
        public string Field { get; set; } = string.Empty;
        public string ServerId { get; set; } = string.Empty;
        public string RecordId { get; set; } = string.Empty;
        public string MatchedBy { get; set; } = string.Empty;
        public string AppliedVia { get; set; } = string.Empty;
        public string? Language { get; set; }
        public string AppliedAt { get; set; } = string.Empty;
    }

    private sealed class FingerprintRow
    {
        public string Algorithm { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public string ComputedAt { get; set; } = string.Empty;
    }
}
