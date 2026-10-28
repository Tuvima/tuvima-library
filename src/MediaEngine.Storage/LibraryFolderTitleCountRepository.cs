using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite count of titles per library folder. A title counts when it is Ready (the same rule every display list uses) and one of its live media assets sits beneath the folder's path;
/// the path index on <c>media_assets.file_path_root</c> serves the prefix match. Every call uses a short-lived connection.
/// </summary>
public sealed class LibraryFolderTitleCountRepository(IDatabaseConnection db) : ILibraryFolderTitleCountRepository
{
    public Task<IReadOnlyDictionary<string, int>> CountByFolderAsync(IReadOnlyCollection<string> folderPaths, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folderPaths);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var connection = db.CreateConnection();
        foreach (var path in folderPaths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            counts[path] = connection.ExecuteScalar<int>(
                $"""
                SELECT COUNT(DISTINCT e.work_id)
                FROM media_assets ma
                INNER JOIN editions e ON e.id = ma.edition_id
                INNER JOIN works w ON w.id = e.work_id
                WHERE ma.file_path_root LIKE @pattern ESCAPE '\'
                  AND ma.is_orphaned = 0
                  AND ma.status <> 'Orphaned'
                  AND w.is_catalog_only = 0
                  AND {WorkReadinessSql.ReadyMarkPredicate("w")};
                """,
                new { pattern = PatternFor(path) });
        }

        return Task.FromResult<IReadOnlyDictionary<string, int>>(counts);
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
