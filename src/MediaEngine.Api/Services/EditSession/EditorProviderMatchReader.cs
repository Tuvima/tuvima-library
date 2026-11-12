using System.Data;
using Dapper;
using MediaEngine.Storage;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// Reads whether items are matched to a provider by the Match tab's own rule (<see cref="EditorMatchRule.FindProviderSource"/>) without the Match
/// tab's lookups, so the editor's header can say the same thing before the Match tab is ever opened: a provider id on the item itself (its Work,
/// then its first file, <see cref="EditorFirstFileRule"/>), plus the show's id for a season or show, never for an episode. A Wikidata id alone does
/// not count, and nothing an episode could inherit from its show does. This is a separate answer from the family dropdown's mark.
/// </summary>
internal static class EditorProviderMatchReader
{
    /// <summary>One item to read: its Work id and its kind (<c>works.work_kind</c>).</summary>
    public readonly record struct Item(Guid WorkId, string WorkKind);

    /// <summary>The ids of the given items that are matched to a provider. <paramref name="rootWorkId"/> is the top of their tree (the show); a season or show borrows its id.</summary>
    public static HashSet<Guid> Read(IDbConnection connection, string mediaType, Guid rootWorkId, IReadOnlyCollection<Item> items)
    {
        var matched = new HashSet<Guid>();
        var hexes = items.Select(item => item.WorkId.ToString("N")).Append(rootWorkId.ToString("N")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (items.Count == 0)
        {
            return matched;
        }

        var keys = EditorMatchRule.ProviderMatchKeys;
        var workValues = ReadKeyValues(connection, hexes, keys);
        var firstFile = connection.Query<FirstFileRow>($"""
            SELECT DISTINCT e.work_id AS WorkId, {EditorFirstFileRule.FirstAssetIdOfWork("e.work_id")} AS AssetId
            FROM editions e
            WHERE LOWER(HEX(e.work_id)) IN @hexes;
            """, new { hexes }).ToDictionary(row => row.WorkId, row => row.AssetId);
        var assetValues = ReadKeyValues(connection, firstFile.Values.Select(id => id.ToString("N")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), keys);

        var empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var root = workValues.GetValueOrDefault(rootWorkId) ?? empty;
        var isTv = string.Equals(mediaType, "TV", StringComparison.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var work = workValues.GetValueOrDefault(item.WorkId) ?? empty;
            var asset = !EditorMatchRule.IsContainer(item.WorkKind) && firstFile.TryGetValue(item.WorkId, out var assetId) ? assetValues.GetValueOrDefault(assetId) : null;
            var own = EditorMatchRule.OwnValues(work, asset, item.WorkKind);
            var isEpisode = string.Equals(item.WorkKind, "child", StringComparison.OrdinalIgnoreCase);
            if (EditorMatchRule.FindProviderSource(mediaType, isTv, isEpisode, own, root) is not null)
            {
                matched.Add(item.WorkId);
            }
        }

        return matched;
    }

    // The newest non-empty value of each wanted key, per entity (the newest scoring wins, as in the Match tab's read).
    private static Dictionary<Guid, Dictionary<string, string>> ReadKeyValues(IDbConnection connection, string[] entityHexes, IReadOnlyList<string> keys)
    {
        var result = new Dictionary<Guid, Dictionary<string, string>>();
        if (entityHexes.Length == 0)
        {
            return result;
        }

        foreach (var row in connection.Query<KeyValueRow>("""
            SELECT cv.entity_id AS EntityId, cv.key AS Key, cv.value AS Value
            FROM canonical_values cv
            WHERE LOWER(HEX(cv.entity_id)) IN @entityHexes
              AND cv.key IN @keys AND NULLIF(cv.value, '') IS NOT NULL
            ORDER BY cv.last_scored_at;
            """, new { entityHexes, keys }))
        {
            if (!result.TryGetValue(row.EntityId, out var map))
            {
                result[row.EntityId] = map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            map[row.Key] = row.Value;
        }

        return result;
    }

    private sealed class FirstFileRow
    {
        public Guid WorkId { get; init; }
        public Guid AssetId { get; init; }
    }

    private sealed class KeyValueRow
    {
        public Guid EntityId { get; init; }
        public string Key { get; init; } = string.Empty;
        public string Value { get; init; } = string.Empty;
    }
}
