using System.Data;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services;

public sealed class AssetStoreCleanupService
{
    private readonly AssetPathService _assetPaths;
    private readonly IDatabaseConnection _database;

    public AssetStoreCleanupService(AssetPathService assetPaths, IDatabaseConnection database)
    {
        _assetPaths = assetPaths;
        _database = database;
    }

    public AssetStoreCleanupResult SweepOrphanAssets(CancellationToken cancellationToken)
    {
        var assetsRoot = _assetPaths.AssetsRoot;
        if (!Directory.Exists(assetsRoot))
        {
            return new AssetStoreCleanupResult(0, ".data/assets does not exist; nothing to sweep.");
        }

        var referenced = LoadReferencedAssetFiles();
        var cleaned = 0;

        foreach (var filePath in Directory.EnumerateFiles(assetsRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(filePath);
            if (referenced.Contains(fullPath))
            {
                continue;
            }

            try
            {
                File.Delete(fullPath);
                cleaned++;
                PruneEmptyAssetDirectories(Path.GetDirectoryName(fullPath), assetsRoot);
            }
            catch
            {
                // Best-effort maintenance: keep sweeping other files.
            }
        }

        return new AssetStoreCleanupResult(
            cleaned,
            cleaned == 0
                ? "No orphaned managed asset files found."
                : $"Removed {cleaned} orphaned managed asset file{(cleaned == 1 ? "" : "s")}.");
    }

    private HashSet<string> LoadReferencedAssetFiles()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var conn = _database.CreateConnection();

        // The one shared list of every place a file path is recorded (also used when a title is deleted).
        foreach (var sql in AssetFileReferences.PathQueries)
        {
            AddReferencedPaths(conn, paths, sql);
        }

        return paths;
    }

    private static void AddReferencedPaths(IDbConnection conn, HashSet<string> paths, string sql)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0))
                {
                    continue;
                }

                var value = reader.GetString(0);
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                paths.Add(Path.GetFullPath(value));
            }
        }
        catch
        {
            // Older dev databases can lag the latest asset tables; cleanup stays best-effort.
        }
    }

    private static void PruneEmptyAssetDirectories(string? startDir, string assetsRoot)
    {
        if (string.IsNullOrWhiteSpace(startDir))
        {
            return;
        }

        var root = Path.GetFullPath(assetsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = Path.GetFullPath(startDir);
        while (current.StartsWith(root, StringComparison.OrdinalIgnoreCase)
               && !string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(current) || Directory.EnumerateFileSystemEntries(current).Any())
                {
                    return;
                }

                Directory.Delete(current);
                current = Path.GetDirectoryName(current) ?? string.Empty;
            }
            catch
            {
                return;
            }
        }
    }
}

public sealed record AssetStoreCleanupResult(int Cleaned, string Message);
