using System.Data;
using Dapper;
using MediaEngine.Domain;

namespace MediaEngine.Storage;

/// <summary>
/// The values that say where an item sits in its list (track, disc, episode, season, series position, audiobook part), and the rule that
/// keeps an item added anyway unnumbered (Details overlay D13). Such an item (<c>works.placement_unmatched</c> or
/// <c>editions.placement_unmatched</c> = 1) is never given a number by a scan, a re-read of its file, a provider or a re-identify: the
/// number values are simply not written for it. Only a later Match, which clears the mark, numbers it.
/// </summary>
internal static class PlacementNumbers
{
    internal const string AudiobookPartNumberKey = "audiobook_part_number";

    internal static readonly string[] Keys =
    [
        MetadataFieldConstants.TrackNumber,
        MetadataFieldConstants.DiscNumber,
        MetadataFieldConstants.EpisodeNumber,
        MetadataFieldConstants.SeasonNumber,
        MetadataFieldConstants.SeriesPosition,
        "series_index",
        AudiobookPartNumberKey,
    ];

    internal static bool IsNumberKey(string key) => Keys.Contains(key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Of the given entities, the ones that must stay unnumbered: a title added anyway, an audiobook part added anyway, and the files
    /// (media assets) of either. Entities that are neither are absent from the answer.
    /// </summary>
    internal static HashSet<Guid> UnnumberedAmong(IDbConnection connection, IDbTransaction? transaction, IReadOnlyCollection<Guid> entityIds)
    {
        if (entityIds.Count == 0)
        {
            return [];
        }

        return connection.Query<Guid>(
            """
            -- readiness: only checks the mark on the titles a write is about to number, by id
            SELECT w.id FROM works w WHERE w.id IN @ids AND w.placement_unmatched = 1
            UNION
            SELECT e.id FROM editions e WHERE e.id IN @ids AND e.placement_unmatched = 1
            UNION
            SELECT ma.id
            FROM media_assets ma
            JOIN editions e ON e.id = ma.edition_id
            JOIN works w ON w.id = e.work_id
            WHERE ma.id IN @ids AND (e.placement_unmatched = 1 OR w.placement_unmatched = 1);
            """,
            new { ids = entityIds.Select(GuidSql.ToBlob).ToArray() },
            transaction).ToHashSet();
    }
}
