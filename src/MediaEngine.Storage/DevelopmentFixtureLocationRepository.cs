using Dapper;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="IDevelopmentFixtureLocationRepository"/>. <c>ingestion_log.file_path</c> keeps
/// the path a file was first found at, while <c>media_assets.file_path_root</c> follows it when it is filed. Used only
/// by the development reset.
/// </summary>
public sealed class DevelopmentFixtureLocationRepository : IDevelopmentFixtureLocationRepository
{
    private readonly IDatabaseConnection _db;

    public DevelopmentFixtureLocationRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc/>
    public IReadOnlyList<DevelopmentFixtureLocation> FindCurrentLocations(IReadOnlyCollection<string> detectedPaths)
    {
        ArgumentNullException.ThrowIfNull(detectedPaths);
        var wanted = new HashSet<string>(
            detectedPaths.Select(FullPathOrNull).Where(path => path is not null).Cast<string>(),
            StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
        {
            return [];
        }

        // A file found to be a copy of one already in the library is logged against that other file's asset, which may
        // be someone's own; such rows say nothing about where the detected file went, so they are left out.
        using var conn = _db.CreateConnection();
        var rows = conn.Query<LocationRow>("""
            SELECT il.file_path        AS DetectedPath,
                   ma.file_path_root   AS CurrentPath,
                   tt.local_path       AS TrackPath,
                   tt.sidecar_path     AS SidecarPath
            FROM ingestion_log il
            JOIN media_assets ma ON ma.id = il.media_asset_id
            LEFT JOIN text_tracks tt ON tt.asset_id = ma.id AND tt.is_user_owned = 0
            WHERE il.status <> 'duplicate';
            """);

        return rows
            .Select(row => (
                Detected: FullPathOrNull(row.DetectedPath),
                Current: FullPathOrNull(row.CurrentPath),
                Track: FullPathOrNull(row.TrackPath),
                Sidecar: FullPathOrNull(row.SidecarPath)))
            .Where(row => row.Detected is not null && row.Current is not null && wanted.Contains(row.Detected))
            .GroupBy(row => (Detected: row.Detected!, Current: row.Current!))
            .Select(group => new DevelopmentFixtureLocation(
                group.Key.Detected,
                group.Key.Current,
                group.SelectMany(row => new[] { row.Track, row.Sidecar })
                    .Where(path => path is not null)
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .ToList();
    }

    // A stored path that cannot be read as a full path cannot be a listed fixture or where one went, so it is left out
    // rather than stopping every development reset.
    private static string? FullPathOrNull(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private sealed class LocationRow
    {
        public string? DetectedPath { get; set; }
        public string? CurrentPath { get; set; }
        public string? TrackPath { get; set; }
        public string? SidecarPath { get; set; }
    }
}
