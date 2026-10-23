using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// Finds the files inside the "Suitable for limited profiles" folders in one read (simpler access D7). A folder is matched with the first
/// check's path rule (<see cref="FolderPathSql"/>, <see cref="FolderFirstCheckPaths"/>), so SQL and the in-memory
/// <see cref="FolderFirstCheckPaths.IsInFolder(string, string?)"/> agree. Short-lived connection per call.
/// </summary>
public sealed class SuitableFolderAssetRepository(IDatabaseConnection db) : ISuitableFolderAssetRepository
{
    public Task<IReadOnlySet<Guid>> FindAssetsInFoldersAsync(
        IReadOnlyCollection<Guid> assetIds, IReadOnlyList<string> folderPaths, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assetIds);
        ArgumentNullException.ThrowIfNull(folderPaths);
        ct.ThrowIfCancellationRequested();

        var found = new HashSet<Guid>();
        var folders = folderPaths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(FolderFirstCheckPaths.Prefix).Distinct().ToList();
        if (assetIds.Count == 0 || folders.Count == 0)
        {
            return Task.FromResult<IReadOnlySet<Guid>>(found);
        }

        // One bound set of values per folder (three each), so the number of values grows with the few ticked folders, not with the files.
        var arguments = new DynamicParameters();
        arguments.Add("assetIds", GuidSql.ToJsonHexArray(assetIds.Distinct()));
        var tests = new List<string>(folders.Count);
        for (var i = 0; i < folders.Count; i++)
        {
            var folded = FolderFirstCheckPaths.Folded(folders[i]);
            arguments.Add($"low{i}", folded);
            arguments.Add($"high{i}", FolderFirstCheckPaths.RangeEnd(folded));
            arguments.Add($"prefix{i}", folders[i]);
            tests.Add(FolderPathSql.InFolder("a.file_path_root", $"@low{i}", $"@high{i}", $"@prefix{i}"));
        }

        using var connection = db.CreateConnection();
        var ids = connection.Query<string>($"""
            SELECT hex(a.id)
            FROM media_assets a
            WHERE a.id IN (SELECT unhex(value) FROM json_each(@assetIds))
              AND ({string.Join(" OR ", tests)});
            """, arguments);
        foreach (var id in ids)
        {
            found.Add(Guid.ParseExact(id, "N"));
        }

        return Task.FromResult<IReadOnlySet<Guid>>(found);
    }
}
