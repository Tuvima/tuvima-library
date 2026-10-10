using System.Globalization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

public sealed class MediaAssetCoverageRepository : IMediaAssetCoverageRepository
{
    private const string SelectColumns =
        "asset_id, work_id, position, start_seconds, end_seconds, source";

    private readonly IDatabaseConnection _db;

    public MediaAssetCoverageRepository(IDatabaseConnection db)
    {
        _db = db;
    }

    public Task ReplaceForAssetAsync(
        Guid assetId,
        IReadOnlyList<MediaAssetCoverage> coverage,
        CancellationToken ct = default)
    {
        Validate(assetId, coverage);

        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            using (var delete = conn.CreateCommand())
            {
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM media_asset_coverage WHERE asset_id = @assetId;";
                delete.Parameters.Add("@assetId", SqliteType.Blob).Value = GuidSql.ToBlob(assetId);
                delete.ExecuteNonQuery();
            }

            foreach (var row in coverage)
            {
                innerCt.ThrowIfCancellationRequested();
                using var insert = conn.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO media_asset_coverage
                        (asset_id, work_id, position, start_seconds, end_seconds, source)
                    VALUES
                        (@assetId, @workId, @position, @start, @end, @source);
                    """;
                insert.Parameters.Add("@assetId", SqliteType.Blob).Value = GuidSql.ToBlob(assetId);
                insert.Parameters.Add("@workId", SqliteType.Blob).Value = GuidSql.ToBlob(row.WorkId);
                insert.Parameters.AddWithValue("@position", row.Position);
                insert.Parameters.AddWithValue("@start", (object?)row.StartSeconds ?? DBNull.Value);
                insert.Parameters.AddWithValue("@end", (object?)row.EndSeconds ?? DBNull.Value);
                insert.Parameters.AddWithValue("@source", row.Source);
                insert.ExecuteNonQuery();
            }
        }, ct);
    }

    public Task<IReadOnlyList<MediaAssetCoverage>> ListByAssetAsync(Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {SelectColumns}
            FROM media_asset_coverage
            WHERE asset_id = @assetId
            ORDER BY position;
            """;
        cmd.Parameters.Add("@assetId", SqliteType.Blob).Value = GuidSql.ToBlob(assetId);
        return Task.FromResult(ReadAll(cmd));
    }

    public Task<IReadOnlyList<MediaAssetCoverage>> ListByWorkAsync(Guid workId, CancellationToken ct = default)
        => ListByWorksAsync([workId], ct);

    public Task<IReadOnlyList<MediaAssetCoverage>> ListByWorksAsync(
        IReadOnlyCollection<Guid> workIds,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var distinct = workIds.Distinct().ToList();
        if (distinct.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<MediaAssetCoverage>>([]);
        }

        // SQLite's default bound-parameter limit is far above any realistic page of
        // episodes, but chunk anyway so a very large season rail can never hit it.
        const int chunkSize = 500;
        var results = new List<MediaAssetCoverage>();
        using var conn = _db.CreateConnection();
        foreach (var chunk in distinct.Chunk(chunkSize))
        {
            ct.ThrowIfCancellationRequested();
            using var cmd = conn.CreateCommand();
            var names = new string[chunk.Length];
            for (var i = 0; i < chunk.Length; i++)
            {
                names[i] = "@w" + i.ToString(CultureInfo.InvariantCulture);
                cmd.Parameters.Add(names[i], SqliteType.Blob).Value = GuidSql.ToBlob(chunk[i]);
            }

            cmd.CommandText = $"""
                SELECT {SelectColumns}
                FROM media_asset_coverage
                WHERE work_id IN ({string.Join(",", names)})
                ORDER BY asset_id, position;
                """;
            results.AddRange(ReadAll(cmd));
        }

        return Task.FromResult<IReadOnlyList<MediaAssetCoverage>>(results);
    }

    public Task DeleteForAssetAsync(Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM media_asset_coverage WHERE asset_id = @assetId;";
        cmd.Parameters.Add("@assetId", SqliteType.Blob).Value = GuidSql.ToBlob(assetId);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    private static void Validate(Guid assetId, IReadOnlyList<MediaAssetCoverage> coverage)
    {
        if (assetId == Guid.Empty)
        {
            throw new ArgumentException("Asset id is required.", nameof(assetId));
        }

        var works = new HashSet<Guid>();
        var positions = new HashSet<int>();
        foreach (var row in coverage)
        {
            if (row.AssetId != assetId)
            {
                throw new ArgumentException("Every coverage row must belong to the given asset.", nameof(coverage));
            }

            if (row.WorkId == Guid.Empty || !works.Add(row.WorkId))
            {
                throw new ArgumentException("Coverage must list each episode once.", nameof(coverage));
            }

            if (row.Position < 1 || !positions.Add(row.Position))
            {
                throw new ArgumentException("Coverage positions must be unique and start at 1.", nameof(coverage));
            }

            if (!MediaAssetCoverage.IsKnownSource(row.Source))
            {
                throw new ArgumentException($"Unknown coverage source '{row.Source}'.", nameof(coverage));
            }

            if (row.StartSeconds is < 0 || row.EndSeconds is < 0
                || (row.StartSeconds is { } start && row.EndSeconds is { } end && end < start))
            {
                throw new ArgumentException("Coverage times must be non-negative and end after start.", nameof(coverage));
            }
        }
    }

    private static IReadOnlyList<MediaAssetCoverage> ReadAll(SqliteCommand cmd)
    {
        var results = new List<MediaAssetCoverage>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new MediaAssetCoverage(
                GuidSql.FromDb(reader.GetValue(0)),
                GuidSql.FromDb(reader.GetValue(1)),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetDouble(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4),
                reader.GetString(5)));
        }

        return results;
    }
}
