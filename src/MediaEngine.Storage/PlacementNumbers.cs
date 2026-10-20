using System.Data;
using Dapper;
using MediaEngine.Domain;

namespace MediaEngine.Storage;

/// <summary>
/// The values that say where an item sits in its list (track, disc, episode, season, series position, audiobook part), and the rule that
/// keeps an item added anyway unnumbered (Details overlay D13). Such an item (<c>works.placement_unmatched</c> or
/// <c>editions.placement_unmatched</c> = 1) is never given a number by a scan, a re-read of its file, a provider or a re-identify: the
/// number values are simply not written for it. Only a later Match, which clears the mark, numbers it.
/// A mark covers only the numbers of its own list. A title's mark covers the title-level numbers (track, disc, episode, season, series
/// position, series index); an Edition's mark covers only the audiobook part number. So an audiobook added anyway into a series keeps
/// the part numbers of its files.
/// </summary>
internal static class PlacementNumbers
{
    internal const string AudiobookPartNumberKey = "audiobook_part_number";

    /// <summary>The numbers a title (Work) holds in its album, season or series. A title's "not matched" mark covers these.</summary>
    internal static readonly string[] WorkKeys =
    [
        MetadataFieldConstants.TrackNumber,
        MetadataFieldConstants.DiscNumber,
        MetadataFieldConstants.EpisodeNumber,
        MetadataFieldConstants.SeasonNumber,
        MetadataFieldConstants.SeriesPosition,
        "series_index",
    ];

    /// <summary>The numbers an audiobook part (an Edition) holds in its audiobook. An Edition's "not matched" mark covers these.</summary>
    internal static readonly string[] PartKeys = [AudiobookPartNumberKey];

    internal static readonly string[] Keys = [.. WorkKeys, .. PartKeys];

    internal static bool IsNumberKey(string key) => Keys.Contains(key, StringComparer.OrdinalIgnoreCase);

    internal static bool IsWorkKey(string key) => WorkKeys.Contains(key, StringComparer.OrdinalIgnoreCase);

    internal static bool IsPartKey(string key) => PartKeys.Contains(key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Of the given entities, the ones whose title-level numbers (<see cref="WorkKeys"/>) must stay unwritten: a title added anyway and
    /// the files (media assets) of it. Entities that are neither are absent from the answer.
    /// </summary>
    internal static HashSet<Guid> WorkUnnumberedAmong(IDbConnection connection, IDbTransaction? transaction, IReadOnlyCollection<Guid> entityIds)
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
            SELECT ma.id
            FROM media_assets ma
            JOIN editions e ON e.id = ma.edition_id
            JOIN works w ON w.id = e.work_id
            WHERE ma.id IN @ids AND w.placement_unmatched = 1;
            """,
            new { ids = entityIds.Select(GuidSql.ToBlob).ToArray() },
            transaction).ToHashSet();
    }

    /// <summary>
    /// Of the given entities, the ones whose part number (<see cref="PartKeys"/>) must stay unwritten: an audiobook part added anyway and
    /// the files (media assets) of it. Entities that are neither are absent from the answer.
    /// </summary>
    internal static HashSet<Guid> PartUnnumberedAmong(IDbConnection connection, IDbTransaction? transaction, IReadOnlyCollection<Guid> entityIds)
    {
        if (entityIds.Count == 0)
        {
            return [];
        }

        return connection.Query<Guid>(
            """
            -- readiness: only checks the mark on the parts a write is about to number, by id
            SELECT e.id FROM editions e WHERE e.id IN @ids AND e.placement_unmatched = 1
            UNION
            SELECT ma.id
            FROM media_assets ma
            JOIN editions e ON e.id = ma.edition_id
            WHERE ma.id IN @ids AND e.placement_unmatched = 1;
            """,
            new { ids = entityIds.Select(GuidSql.ToBlob).ToArray() },
            transaction).ToHashSet();
    }
}
