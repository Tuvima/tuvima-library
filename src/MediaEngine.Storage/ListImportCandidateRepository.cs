using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// Reads the owned titles an imported list is matched against (Collections and lists spec D6): one candidate per display root,
/// with its title, year, first creator and every identifier Library stores for it (bridge ids, canonical values and the work's
/// own external identifiers). Only Ready, owned, non-Vault titles count, the same visibility rule every other read uses.
/// </summary>
public sealed class ListImportCandidateRepository : IListImportCandidateRepository
{
    // The canonical value keys that hold an identifier, besides the bridge id table. A key is written into the identifier map
    // under the same name.
    private static readonly string[] IdentifierKeys =
    [
        "imdb_id", "tmdb_id", "tvdb_id", "isbn", "isbn_13", "isbn_10", "asin", "musicbrainz_id", "musicbrainz_release_id",
        "musicbrainz_release_group_id", "musicbrainz_recording_id", "comic_vine_id", "comic_vine_volume_id",
    ];

    private readonly IDatabaseConnection _db;

    public ListImportCandidateRepository(IDatabaseConnection db) => _db = db;

    public Task<IReadOnlyList<ListImportCandidate>> GetCandidatesAsync(
        IReadOnlySet<Guid>? allowedWorkIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();

        var allowedFilter = allowedWorkIds is null
            ? string.Empty
            : "AND hex(w.id) IN (SELECT UPPER(CAST(value AS TEXT)) FROM json_each(@AllowedJson))";
        var readyLeaf = WorkReadinessSql.ReadyWorkPredicate("w");
        var notVault = VaultSql.NotVaultEntity("w.id");

        var roots = conn.Query<RootRow>(
            $"""
            -- readiness: owned titles a list import is matched against; every leaf carries the Ready rule
            WITH leaf AS (
                SELECT w.id AS leaf_id, COALESCE(gp.id, p.id, w.id) AS root_id
                FROM works w
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                WHERE {readyLeaf}
                  AND {notVault}
                  {allowedFilter}
            ),
            roots AS (SELECT DISTINCT root_id FROM leaf)
            SELECT r.root_id AS WorkId,
                   rw.media_type AS MediaType,
                   CAST(COALESCE(
                       (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = r.root_id AND cv.key = 'title' LIMIT 1),
                       (SELECT cv.value FROM editions e
                        JOIN media_assets ma ON ma.edition_id = e.id
                        JOIN canonical_values cv ON cv.entity_id = ma.id AND cv.key = 'title'
                        WHERE e.work_id = r.root_id LIMIT 1)) AS TEXT) AS Title,
                   CAST(COALESCE(
                       (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = r.root_id AND cv.key IN ('release_year', 'year')
                        ORDER BY CASE cv.key WHEN 'release_year' THEN 0 ELSE 1 END LIMIT 1),
                       (SELECT cv.value FROM editions e
                        JOIN media_assets ma ON ma.edition_id = e.id
                        JOIN canonical_values cv ON cv.entity_id = ma.id AND cv.key IN ('release_year', 'year')
                        WHERE e.work_id = r.root_id
                        ORDER BY CASE cv.key WHEN 'release_year' THEN 0 ELSE 1 END LIMIT 1)) AS TEXT) AS Year,
                   CAST(COALESCE(
                       (SELECT cva.value FROM canonical_value_arrays cva
                        WHERE cva.entity_id = r.root_id AND cva.key IN ('album_artist', 'artist', 'author', 'director')
                        ORDER BY CASE cva.key WHEN 'album_artist' THEN 1 WHEN 'artist' THEN 2 WHEN 'author' THEN 3 ELSE 4 END, cva.ordinal
                        LIMIT 1),
                       (SELECT cva.value FROM editions e
                        JOIN media_assets ma ON ma.edition_id = e.id
                        JOIN canonical_value_arrays cva ON cva.entity_id = ma.id
                            AND cva.key IN ('album_artist', 'artist', 'author', 'director')
                        WHERE e.work_id = r.root_id
                        ORDER BY CASE cva.key WHEN 'album_artist' THEN 1 WHEN 'artist' THEN 2 WHEN 'author' THEN 3 ELSE 4 END, cva.ordinal
                        LIMIT 1)) AS TEXT) AS Creator,
                   rw.external_identifiers AS ExternalIdentifiers
            FROM roots r
            JOIN works rw ON rw.id = r.root_id
            """,
            new { AllowedJson = allowedWorkIds is null ? "[]" : GuidSql.ToJsonHexArray(allowedWorkIds) }).ToList();

        var identifiers = ReadIdentifiers(conn);
        var candidates = new List<ListImportCandidate>(roots.Count);
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root.Title))
            {
                continue;
            }

            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            if (identifiers.TryGetValue(root.WorkId, out var stored))
            {
                foreach (var (key, value) in stored)
                {
                    ids[key] = value;
                }
            }

            AddExternalIdentifiers(ids, root.ExternalIdentifiers);
            candidates.Add(new ListImportCandidate(
                root.WorkId,
                root.MediaType ?? string.Empty,
                root.Title.Trim(),
                int.TryParse(root.Year?.Trim().Length >= 4 ? root.Year.Trim()[..4] : null, out var year) ? year : null,
                string.IsNullOrWhiteSpace(root.Creator) ? null : root.Creator.Trim(),
                ids));
        }

        return Task.FromResult<IReadOnlyList<ListImportCandidate>>(candidates);
    }

    // Identifiers by display root: the root, its editions and its files all carry them.
    private static Dictionary<Guid, List<KeyValuePair<string, string>>> ReadIdentifiers(System.Data.IDbConnection conn)
    {
        var keyList = string.Join(", ", IdentifierKeys.Select(key => $"'{key}'"));
        var rows = conn.Query<IdentifierRow>(
            $"""
            WITH owners(entity_id, root_id) AS (
                SELECT w.id, COALESCE(gp.id, p.id, w.id)
                FROM works w
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                UNION ALL
                SELECT e.id, COALESCE(gp.id, p.id, w.id)
                FROM editions e
                JOIN works w ON w.id = e.work_id
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                UNION ALL
                SELECT ma.id, COALESCE(gp.id, p.id, w.id)
                FROM media_assets ma
                JOIN editions e ON e.id = ma.edition_id
                JOIN works w ON w.id = e.work_id
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                WHERE ma.is_orphaned = 0
            )
            SELECT o.root_id AS WorkId, b.id_type AS Name, b.id_value AS Value
            FROM bridge_ids b
            JOIN owners o ON o.entity_id = b.entity_id
            WHERE b.id_type IN ({keyList})
            UNION
            SELECT o.root_id, cv.key, cv.value
            FROM canonical_values cv
            JOIN owners o ON o.entity_id = cv.entity_id
            WHERE cv.key IN ({keyList})
            """);

        var result = new Dictionary<Guid, List<KeyValuePair<string, string>>>();
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Value))
            {
                continue;
            }

            if (!result.TryGetValue(row.WorkId, out var list))
            {
                list = [];
                result[row.WorkId] = list;
            }

            list.Add(new KeyValuePair<string, string>(row.Name, row.Value.Trim()));
        }

        return result;
    }

    // works.external_identifiers is JSON such as {"isbn_13":"...","tmdb_id":"..."}.
    private static void AddExternalIdentifiers(Dictionary<string, string> ids, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!IdentifierKeys.Contains(property.Name, StringComparer.Ordinal))
                {
                    continue;
                }

                var value = property.Value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    ids.TryAdd(property.Name, value.Trim());
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // A work's stored identifier text that is not JSON simply adds no identifiers; the stored bridge ids and canonical
            // values above still match, and the work is never lost for it.
        }
    }

    private sealed class RootRow
    {
        public Guid WorkId { get; init; }
        public string? MediaType { get; init; }
        public string? Title { get; init; }
        public string? Year { get; init; }
        public string? Creator { get; init; }
        public string? ExternalIdentifiers { get; init; }
    }

    private sealed class IdentifierRow
    {
        public Guid WorkId { get; init; }
        public string Name { get; init; } = string.Empty;
        public string? Value { get; init; }
    }
}
