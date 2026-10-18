using System.Globalization;
using Dapper;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// The latest upgrade step recorded in the data store: its number, its name and when it ran.
/// </summary>
public sealed record DataStoreVersion(int Number, string Name, DateTimeOffset AppliedAt);

/// <summary>
/// Reads the applied-steps record (<c>data_store_upgrades</c>) that the upgrade runner writes at startup.
/// Read-only; the runner is the only writer.
/// </summary>
public sealed class DataStoreVersionRepository
{
    private readonly IDatabaseConnection _db;

    public DataStoreVersionRepository(IDatabaseConnection db)
    {
        _db = db;
    }

    /// <summary>
    /// The recorded step with the highest number, or <c>null</c> when no step has been recorded yet.
    /// </summary>
    public DataStoreVersion? GetCurrent()
    {
        using var conn = _db.CreateConnection();
        var row = conn.QueryFirstOrDefault<VersionRow>("""
            SELECT number AS Number,
                   name AS Name,
                   applied_at AS AppliedAt
            FROM data_store_upgrades
            ORDER BY number DESC
            LIMIT 1;
            """);

        return row is null
            ? null
            : new DataStoreVersion(
                row.Number,
                row.Name,
                DateTimeOffset.Parse(
                    row.AppliedAt,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.RoundtripKind));
    }

    private sealed class VersionRow
    {
        public int Number { get; set; }
        public string Name { get; set; } = string.Empty;
        public string AppliedAt { get; set; } = string.Empty;
    }
}
