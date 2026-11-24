using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Services;

/// <summary>
/// Finds the standalone film that already holds another cut, or another quality, of the same film
/// (Blade Runner Final Cut beside Theatrical Cut) so <see cref="HierarchyResolver"/> can attach the new file as an
/// Edition of that film. Deliberately narrow: same title, and the same year (or, when a year is missing,
/// the same folder). Other media types are never considered.
/// </summary>
public sealed class MovieEditionSiblingFinder : IMovieEditionSiblingFinder
{
    private readonly IDatabaseConnection _db;

    public MovieEditionSiblingFinder(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc/>
    public Task<Guid?> FindSiblingWorkAsync(
        string sourceFilePath,
        string? title,
        string? year,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var titleKey = FilmEditionLabels.IdentityKey(title);
        if (titleKey.Length == 0 || string.IsNullOrWhiteSpace(sourceFilePath))
        {
            return Task.FromResult<Guid?>(null);
        }

        var yearKey = FilmEditionLabels.IdentityKey(year);
        var directory = NormalizeDirectory(sourceFilePath);

        // Narrow the scan to files whose claimed title holds the title's longest word.
        var word = LongestWord(title!);
        var likePattern = "%" + EscapeLike(word) + "%";

        using var conn = _db.CreateConnection();
        var rows = conn.Query<FactRow>(
            """
            SELECT w.id AS WorkId, ma.id AS AssetId, ma.file_path_root AS Path, f.key AS Key, f.value AS Value
            FROM works w
            INNER JOIN editions e ON e.work_id = w.id
            INNER JOIN media_assets ma ON ma.edition_id = e.id
            INNER JOIN (
                SELECT entity_id, claim_key AS key, claim_value AS value
                FROM metadata_claims
                WHERE claim_key IN ('title', 'year')
                UNION ALL
                SELECT entity_id, key, value
                FROM canonical_values
                WHERE key IN ('title', 'year')
            ) f ON f.entity_id = ma.id
            WHERE w.media_type = 'Movies'
              AND w.work_kind = 'standalone'
              AND w.is_catalog_only = 0
              AND ma.status <> 'Orphaned'
              AND ma.is_orphaned = 0
              AND ma.file_path_root <> @path
              AND ma.id IN (
                  SELECT entity_id FROM metadata_claims WHERE claim_key = 'title' AND lower(claim_value) LIKE @like ESCAPE '\'
                  UNION
                  SELECT entity_id FROM canonical_values WHERE key = 'title' AND lower(value) LIKE @like ESCAPE '\')
            ORDER BY w.id, ma.file_path_root;
            """,
            new { path = sourceFilePath, like = likePattern }).AsList();

        foreach (var work in rows.GroupBy(row => row.WorkId))
        {
            if (!work.Any(row => row.Key == "title" && FilmEditionLabels.IdentityKey(row.Value) == titleKey))
            {
                continue;
            }

            var years = work.Where(row => row.Key == "year")
                .Select(row => FilmEditionLabels.IdentityKey(row.Value))
                .Where(value => value.Length > 0)
                .ToHashSet(StringComparer.Ordinal);

            if (yearKey.Length > 0 && years.Count > 0)
            {
                if (years.Contains(yearKey))
                {
                    return Task.FromResult<Guid?>(work.Key);
                }

                continue;
            }

            // A year is missing on one side: only a file in the same folder counts as the same film.
            if (work.Any(row => NormalizeDirectory(row.Path) == directory))
            {
                return Task.FromResult<Guid?>(work.Key);
            }
        }

        return Task.FromResult<Guid?>(null);
    }

    private static string NormalizeDirectory(string path)
    {
        var index = path.LastIndexOfAny(['\\', '/']);
        var directory = index > 0 ? path[..index] : string.Empty;
        return directory.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
    }

    private static string LongestWord(string title)
    {
        var words = System.Text.RegularExpressions.Regex.Split(title.ToLowerInvariant(), @"[^\p{L}\p{N}]+")
            .Where(word => word.Length > 0);
        return words.OrderByDescending(word => word.Length).FirstOrDefault() ?? title.ToLowerInvariant();
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private sealed class FactRow
    {
        public Guid WorkId { get; set; }
        public Guid AssetId { get; set; }
        public string Path { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }
}
