using System.Data;
using Dapper;

namespace MediaEngine.Storage;

/// <summary>
/// Every place the data store records the path of a file Tuvima keeps in its own data folder (artwork renditions, entity pictures,
/// people and character portraits, text tracks). One list, shared by the asset-store sweep (which deletes files nothing references)
/// and by deleting a title (which must not delete a file another row still serves: artwork rows are content-addressed, so
/// two titles can point at the same file).
/// </summary>
public static class AssetFileReferences
{
    /// <summary>Queries that each return a single column of file paths, nulls allowed.</summary>
    public static IReadOnlyList<string> PathQueries { get; } =
    [
        """
        SELECT local_image_path FROM entity_assets WHERE local_image_path IS NOT NULL
        UNION ALL SELECT local_image_path_t FROM entity_assets WHERE local_image_path_t IS NOT NULL
        UNION ALL SELECT local_image_path_s FROM entity_assets WHERE local_image_path_s IS NOT NULL
        UNION ALL SELECT local_image_path_m FROM entity_assets WHERE local_image_path_m IS NOT NULL
        UNION ALL SELECT local_image_path_l FROM entity_assets WHERE local_image_path_l IS NOT NULL
        UNION ALL SELECT original_path FROM artwork_assets WHERE original_path IS NOT NULL
        UNION ALL SELECT small_path FROM artwork_assets WHERE small_path IS NOT NULL
        UNION ALL SELECT medium_path FROM artwork_assets WHERE medium_path IS NOT NULL
        UNION ALL SELECT large_path FROM artwork_assets WHERE large_path IS NOT NULL
        """,
        "SELECT local_headshot_path FROM persons WHERE local_headshot_path IS NOT NULL",
        "SELECT local_image_path FROM character_portraits WHERE local_image_path IS NOT NULL",
        "SELECT local_image_path FROM fictional_entities WHERE local_image_path IS NOT NULL",
        "SELECT local_path FROM text_tracks WHERE local_path IS NOT NULL",
        "SELECT sidecar_path FROM text_tracks WHERE sidecar_path IS NOT NULL",
        "SELECT character_image_path FROM character_performer_links WHERE character_image_path IS NOT NULL",
        """
        SELECT cover_artwork_path FROM collections WHERE cover_artwork_path IS NOT NULL
        UNION ALL SELECT background_artwork_path FROM collections WHERE background_artwork_path IS NOT NULL
        UNION ALL SELECT banner_artwork_path FROM collections WHERE banner_artwork_path IS NOT NULL
        UNION ALL SELECT logo_artwork_path FROM collections WHERE logo_artwork_path IS NOT NULL
        """,
    ];

    /// <summary>
    /// Whether <paramref name="path"/> is in a set from <see cref="LoadReferencedPaths"/>. A path that cannot be
    /// normalised is compared as written, the way the set keeps one.
    /// </summary>
    internal static bool IsReferenced(HashSet<string> referenced, string path)
    {
        try
        {
            return referenced.Contains(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // When in doubt a file counts as still in use.
            return true;
        }
    }

    /// <summary>The full path of every file any row still references, on the caller's connection (and transaction, when given).</summary>
    internal static HashSet<string> LoadReferencedPaths(IDbConnection connection, IDbTransaction? transaction = null)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sql in PathQueries)
        {
            foreach (var path in connection.Query<string?>(sql, transaction: transaction))
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                try
                {
                    referenced.Add(Path.GetFullPath(path));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // A stored path that cannot be normalised is kept as written: when in doubt a file counts as still in use.
                    referenced.Add(path);
                }
            }
        }

        return referenced;
    }
}
