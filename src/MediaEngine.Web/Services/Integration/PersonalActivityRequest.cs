using System.Globalization;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// What the Activity page asks the Engine for: a scope and the filters. The list, the date rail and the CSV export all use
/// the same filters, so the rail and the export always match what the list shows.
/// </summary>
public sealed record PersonalActivityRequest
{
    /// <summary>The Engine path of the CSV export. The Dashboard serves it so the browser never talks to the Engine itself.</summary>
    public const string ExportPath = "/engine-activity-export";

    /// <summary>The filter names the export passes on to the Engine; paging and grouping are never part of it.</summary>
    public static readonly IReadOnlySet<string> FilterKeys = new HashSet<string>(StringComparer.Ordinal) { "scope", "q", "kind", "from", "to", "outcome", "device" };

    public const string MeScope = "me";
    public const string EveryoneScope = "everyone";

    /// <summary><c>me</c>, <c>profile:{id}</c>, <c>household:{id}</c> or <c>everyone</c>.</summary>
    public string Scope { get; init; } = MeScope;

    public string? Search { get; init; }

    /// <summary><c>watch</c>, <c>listen</c>, <c>read</c> and <c>my_list</c>; empty means every kind.</summary>
    public IReadOnlyList<string> Kinds { get; init; } = [];

    /// <summary>Inclusive start (UTC).</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Exclusive end (UTC).</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary><c>finished</c> or <c>not_finished</c>; empty means both.</summary>
    public string? Outcome { get; init; }

    public string? Device { get; init; }

    /// <summary>One entry per title that expands to its attempts.</summary>
    public bool GroupByTitle { get; init; }

    /// <summary>Include the songs of each music session.</summary>
    public bool ExpandSongs { get; init; }

    public string? Cursor { get; init; }

    public int? Limit { get; init; }

    public bool IncludesOthers => !string.Equals(Scope, MeScope, StringComparison.OrdinalIgnoreCase);

    /// <summary>The filters, without paging: what the date rail and the export are asked with.</summary>
    public IReadOnlyDictionary<string, string?> ToFilterQuery() => new Dictionary<string, string?>
    {
        ["scope"] = IncludesOthers ? Scope : null,
        ["q"] = Search,
        ["kind"] = Kinds.Count == 0 ? null : string.Join(',', Kinds),
        ["from"] = From?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        ["to"] = To?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        ["outcome"] = Outcome,
        ["device"] = Device,
    };

    /// <summary>The filters and paging, for the list.</summary>
    public IReadOnlyDictionary<string, string?> ToListQuery()
    {
        var query = new Dictionary<string, string?>(ToFilterQuery())
        {
            ["group"] = GroupByTitle ? "title" : null,
            ["expand"] = ExpandSongs ? "songs" : null,
            ["cursor"] = Cursor,
            ["limit"] = Limit?.ToString(CultureInfo.InvariantCulture),
        };
        return query;
    }

    /// <summary>The Dashboard address that downloads exactly what these filters show as a CSV file.</summary>
    public string ExportHref()
    {
        var parts = ToFilterQuery()
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}")
            .ToArray();
        return parts.Length == 0 ? ExportPath : $"{ExportPath}?{string.Join('&', parts)}";
    }
}
