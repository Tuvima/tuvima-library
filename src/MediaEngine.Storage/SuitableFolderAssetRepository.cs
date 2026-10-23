using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// Finds the files inside the "Suitable for limited profiles" folders (simpler access D7). A folder is matched with the first
/// check's path rule (<see cref="FolderPathSql"/>, <see cref="FolderFirstCheckPaths"/>), so SQL and the in-memory
/// <see cref="FolderFirstCheckPaths.IsInFolder(string, string?)"/> agree. Short-lived connection per call.
/// </summary>
public sealed class SuitableFolderAssetRepository(IDatabaseConnection db) : ISuitableFolderAssetRepository
{
    public Task<IReadOnlySet<Guid>> FindAssetsInFoldersAsync(IReadOnlyList<string> folderPaths, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folderPaths);
        ct.ThrowIfCancellationRequested();

        var found = new HashSet<Guid>();
        var folders = folderPaths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(FolderFirstCheckPaths.Prefix).Distinct().ToList();
        if (folders.Count == 0)
        {
            return Task.FromResult<IReadOnlySet<Guid>>(found);
        }

        // One range read per folder, so each can use the folded-path index; three bound values per folder, and the folders are the few ticked ones.
        var arguments = new DynamicParameters();
        var reads = new List<string>(folders.Count);
        for (var i = 0; i < folders.Count; i++)
        {
            var folded = FolderFirstCheckPaths.Folded(folders[i]);
            arguments.Add($"low{i}", folded);
            arguments.Add($"high{i}", FolderFirstCheckPaths.RangeEnd(folded));
            arguments.Add($"prefix{i}", folders[i]);
            reads.Add($"""
                SELECT hex(a.id) FROM media_assets a
                WHERE a.is_orphaned = 0
                  AND {FolderPathSql.InFolder("a.file_path_root", $"@low{i}", $"@high{i}", $"@prefix{i}")}
                  AND EXISTS (SELECT 1 FROM editions e WHERE e.id = a.edition_id)
                """);
        }

        // Microsoft.Data.Sqlite reads synchronously, so the read is too (the storage guardrail forbids the misleading async calls); the
        // Task is only the shape of the interface, like the other repositories.
        using var connection = db.CreateConnection();
        var ids = connection.Query<string>(string.Join("\nUNION\n", reads) + ";", arguments);
        foreach (var id in ids)
        {
            found.Add(Guid.ParseExact(id, "N"));
        }

        return Task.FromResult<IReadOnlySet<Guid>>(found);
    }
}
