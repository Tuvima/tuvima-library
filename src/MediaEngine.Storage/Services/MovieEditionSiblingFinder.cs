using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Services;

/// <summary>
/// Finds the standalone film that already holds another cut, or another quality, of the same film
/// (Blade Runner Final Cut beside Theatrical Cut) so <see cref="HierarchyResolver"/> can attach the new file as an
/// Edition of that film. Deliberately narrow: same library, same title, and the same year (or, when a year is
/// missing, the same folder and only one film that fits). Other media types are never considered.
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
        string? libraryId,
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

        using var conn = _db.CreateConnection();

        // Step 1: the films of this library whose title reads the same. The comparison is done here, not in SQL,
        // so accents and punctuation ("Amélie", "Spider-Man") never hide a match.
        var titled = conn.Query<FactRow>(
            """
            SELECT w.id AS WorkId, ma.id AS AssetId, ma.file_path_root AS Path, f.value AS Value
            FROM works w
            INNER JOIN editions e ON e.work_id = w.id
            INNER JOIN media_assets ma ON ma.edition_id = e.id
            INNER JOIN (
                SELECT entity_id, claim_value AS value FROM metadata_claims WHERE claim_key = 'title' AND is_current = 1
                UNION ALL
                SELECT entity_id, value FROM canonical_values WHERE key = 'title'
            ) f ON f.entity_id = ma.id
            WHERE w.media_type = 'Movies'
              AND w.work_kind = 'standalone'
              AND w.is_catalog_only = 0
              AND ma.status <> 'Orphaned'
              AND ma.is_orphaned = 0
              AND ma.file_path_root <> @path
              AND ma.library_id IS @library;
            """,
            new { path = sourceFilePath, library = libraryId })
            .Where(row => FilmEditionLabels.IdentityKey(row.Value) == titleKey)
            .ToList();
        if (titled.Count == 0)
        {
            return Task.FromResult<Guid?>(null);
        }

        // Step 2: the years those films carry.
        var assetIds = titled.Select(row => GuidSql.ToBlob(row.AssetId)).Distinct().ToArray();
        var yearsByAsset = conn.Query<(Guid AssetId, string Value)>(
            """
            SELECT entity_id AS AssetId, claim_value AS Value FROM metadata_claims
            WHERE claim_key = 'year' AND is_current = 1 AND entity_id IN @ids
            UNION ALL
            SELECT entity_id, value FROM canonical_values WHERE key = 'year' AND entity_id IN @ids;
            """,
            new { ids = assetIds })
            .GroupBy(row => row.AssetId)
            .ToDictionary(group => group.Key, group => group.Select(row => FilmEditionLabels.IdentityKey(row.Value)).Where(v => v.Length > 0).ToHashSet());

        var sameFolder = new List<Guid>();
        foreach (var work in titled.GroupBy(row => row.WorkId))
        {
            var years = work.SelectMany(row => yearsByAsset.GetValueOrDefault(row.AssetId) ?? []).ToHashSet();
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
                sameFolder.Add(work.Key);
            }
        }

        // Two films fit (a flat folder holding both Dune films): do not guess.
        return Task.FromResult<Guid?>(sameFolder.Count == 1 ? sameFolder[0] : null);
    }

    private static string NormalizeDirectory(string path)
    {
        var index = path.LastIndexOfAny(['\\', '/']);
        var directory = index > 0 ? path[..index] : string.Empty;
        return directory.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
    }

    private sealed class FactRow
    {
        public Guid WorkId { get; set; }
        public Guid AssetId { get; set; }
        public string Path { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }
}
