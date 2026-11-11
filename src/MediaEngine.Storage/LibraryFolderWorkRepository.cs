using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>Titles beneath one folder, matched with the same rule as the Folders tab's title counts. Short-lived connection per call.</summary>
public sealed class LibraryFolderWorkRepository(IDatabaseConnection db) : ILibraryFolderWorkRepository
{
    public Task<IReadOnlySet<Guid>> ListWorkIdsInFolderAsync(string folderPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        }

        using var connection = db.CreateConnection();
        var ids = connection.Query<Guid>(
            $"""
            SELECT DISTINCT e.work_id
            FROM media_assets ma
            INNER JOIN editions e ON e.id = ma.edition_id
            INNER JOIN works w ON w.id = e.work_id
            WHERE ma.file_path_root LIKE @pattern ESCAPE '\'
              AND ma.is_orphaned = 0
              AND ma.status <> 'Orphaned'
              AND w.is_catalog_only = 0
              AND {WorkReadinessSql.ReadyWorkPredicate("w")};
            """,
            new { pattern = PatternFor(folderPath) });
        return Task.FromResult<IReadOnlySet<Guid>>(ids.ToHashSet());
    }

    // Everything beneath the folder, never a sibling that merely starts with the same letters ("Movies" and "Movies 4K").
    private static string PatternFor(string folderPath)
    {
        var trimmed = folderPath.TrimEnd('\\', '/');
        var separator = folderPath.Contains('\\') ? '\\' : '/';
        return Escape(trimmed + separator) + "%";
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
