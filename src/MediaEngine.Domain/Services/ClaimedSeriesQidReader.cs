namespace MediaEngine.Domain.Services;

/// <summary>
/// The one reader of the Series a Work's own claims name, used to break a Series tie that is not at main sequence.
/// Collection assignment and manifest linking both call it, so the same Work gets the same claimed Series (and so the same
/// shelf) from either path.
///
/// Read order over one merged set of values (the lineage root's canonicals first, then the asset's, so the first value for a
/// key wins): the <c>series_qid</c> value; then the <c>series</c> value when it is itself a QID; then, for comics only, the
/// <c>wikidata_qid</c> value.
/// </summary>
public static class ClaimedSeriesQidReader
{
    public const string SeriesQidKey = "series_qid";
    public const string SeriesKey = "series";
    public const string WikidataQidKey = "wikidata_qid";

    /// <summary>The canonical keys the reader looks at; a caller loading values only needs these.</summary>
    public static IReadOnlyList<string> Keys { get; } = [SeriesQidKey, SeriesKey, WikidataQidKey];

    /// <param name="workMediaType">The Work's media type name; only Comics may use the <c>wikidata_qid</c> fallback.</param>
    /// <param name="values">The merged canonical values, root values first.</param>
    public static string? Resolve(string? workMediaType, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        // series_qid is a QID field by definition; the other keys can hold a name, so they must look like a QID.
        if (TryQid(values, SeriesQidKey, requireQidShape: false, out var qid)
            || TryQid(values, SeriesKey, requireQidShape: true, out qid))
        {
            return qid;
        }

        return string.Equals(workMediaType?.Trim(), "Comics", StringComparison.OrdinalIgnoreCase)
            && TryQid(values, WikidataQidKey, requireQidShape: true, out qid)
            ? qid
            : null;
    }

    /// <summary>Merges per-entity values root first: the first entity to hold a key keeps it.</summary>
    public static Dictionary<string, string> Merge(IEnumerable<IEnumerable<KeyValuePair<string, string>>> entitiesInPriorityOrder)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in entitiesInPriorityOrder)
        {
            foreach (var (key, value) in entity)
            {
                merged.TryAdd(key, value);
            }
        }

        return merged;
    }

    private static bool TryQid(IReadOnlyDictionary<string, string> values, string key, bool requireQidShape, out string qid)
    {
        qid = string.Empty;
        if (!TryGetIgnoreCase(values, key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var candidate = raw.Contains('/') ? raw.Split('/')[^1] : raw;
        candidate = candidate.Split("::", 2)[0].Trim();
        if (candidate.Length == 0)
        {
            return false;
        }

        if (requireQidShape && !(candidate.Length > 1 && candidate[0] is 'Q' or 'q' && candidate.Skip(1).All(char.IsDigit)))
        {
            return false;
        }

        qid = requireQidShape ? candidate.ToUpperInvariant() : candidate;
        return true;
    }

    private static bool TryGetIgnoreCase(IReadOnlyDictionary<string, string> values, string key, out string value)
    {
        if (values.TryGetValue(key, out var direct))
        {
            value = direct;
            return true;
        }

        foreach (var (k, v) in values)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                value = v;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }
}
