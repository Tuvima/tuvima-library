using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Services;

namespace MediaEngine.Api.Services.Collections.ListImport.Sources;

/// <summary>
/// MusicBrainz collections (Collections and lists spec D7, 4d): <c>musicbrainz.org/collection/{id}</c>, a person's public
/// collection of albums (releases or release groups) or recordings, read through MusicBrainz's documented web service. It
/// needs no key and follows the MusicBrainz source's own rate limit (one request a second) and User-Agent. MusicBrainz
/// identifiers are tried first by the matcher. Only the collection's id goes to MusicBrainz.
/// </summary>
public sealed partial class MusicBrainzCollectionSource(
    IConfigurationLoader configuration,
    IHttpClientFactory http,
    IProviderRateLimiterCoordinator limiter,
    ILogger<MusicBrainzCollectionSource> logger,
    IProviderHealthMonitor? health = null)
    : WebListSourceBase(configuration, http, limiter, health, logger)
{
    private const int PageSize = 100;

    // A collection holds one kind of thing, and the web service names the kind only by accepting or refusing the question.
    private static readonly (string Entity, string Plural, string IdKey)[] Entities =
    [
        ("release", "releases", "musicbrainz_release_id"),
        ("release-group", "release-groups", "musicbrainz_release_group_id"),
        ("recording", "recordings", "musicbrainz_recording_id"),
    ];

    public override string ProviderName => "musicbrainz";

    public override string DisplayName => "MusicBrainz collections";

    protected override string DefaultApiBase => "https://musicbrainz.org/ws/2";

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex IdPattern();

    public override WebListAddress? Recognise(Uri address)
    {
        if (!IsHost(address, "musicbrainz.org"))
        {
            return null;
        }

        var segments = Segments(address);
        if (segments.Length < 2 || !segments[0].Equals("collection", StringComparison.OrdinalIgnoreCase) || !IdPattern().IsMatch(segments[1]))
        {
            return null;
        }

        var id = segments[1].ToLowerInvariant();
        return new WebListAddress(
            ProviderName,
            ListSourceKinds.MusicBrainz,
            $"https://musicbrainz.org/collection/{id}",
            new Dictionary<string, string> { ["id"] = id });
    }

    // MusicBrainz answers 400 when a collection does not hold the kind that was asked for: that means "try the next kind".
    protected override WebListFailure? FailureFor(HttpStatusCode status) =>
        status == HttpStatusCode.BadRequest ? WebListFailure.NotFound : base.FailureFor(status);

    protected override async Task<ParsedList> ReadAsync(WebListAddress address, string apiBase, string? key, CancellationToken ct)
    {
        var id = Uri.EscapeDataString(address.Part("id"));
        var answeredEmpty = false;
        foreach (var (entity, plural, idKey) in Entities)
        {
            List<ParsedListEntry>? entries;
            try
            {
                entries = await ReadEntityAsync($"{apiBase}/{entity}", id, plural, entity, idKey, ct).ConfigureAwait(false);
            }
            catch (WebListFailureException ex) when (ex.Failure == WebListFailure.NotFound)
            {
                continue;
            }

            if (entries is { Count: > 0 })
            {
                return new ParsedList { Entries = entries };
            }

            answeredEmpty |= entries is not null;
        }

        // Every kind answered with nothing: the collection exists and is empty. No kind accepted it: it is missing or private.
        if (answeredEmpty)
        {
            return new ParsedList { Entries = [] };
        }

        throw new WebListFailureException(WebListFailure.NotFound, "MusicBrainz has no public collection with that id");
    }

    private async Task<List<ParsedListEntry>?> ReadEntityAsync(string url, string collectionId, string plural, string entity, string idKey, CancellationToken ct)
    {
        var entries = new List<ParsedListEntry>();
        for (var page = 0; page < MaximumPages && entries.Count < MaximumEntries; page++)
        {
            using var document = await GetJsonAsync(
                Query(
                    url,
                    ("collection", collectionId),
                    ("fmt", "json"),
                    ("inc", "artist-credits"),
                    ("limit", PageSize.ToString(CultureInfo.InvariantCulture)),
                    ("offset", (page * PageSize).ToString(CultureInfo.InvariantCulture))),
                ct).ConfigureAwait(false);
            var root = document.RootElement;
            if (!root.TryGetProperty(plural, out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var item in items.EnumerateArray())
            {
                if (EntryOf(item, entity, idKey, entries.Count + 1) is { } entry)
                {
                    entries.Add(entry);
                }
            }

            var total = root.TryGetProperty($"{entity}-count", out var count) && count.TryGetInt32(out var number) ? number : 0;
            if (items.GetArrayLength() < PageSize || (page + 1) * PageSize >= total)
            {
                break;
            }
        }

        return entries;
    }

    private static ParsedListEntry? EntryOf(JsonElement item, string entity, string idKey, int position)
    {
        var title = StringOf(item, "title");
        var id = StringOf(item, "id");
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var ids = new Dictionary<string, string>
        {
            // The canonical name first, so the entry keeps one identity across refreshes; the specific name beside it.
            [ListIdentifierKeys.MusicBrainz] = id.ToLowerInvariant(),
            [idKey] = id.ToLowerInvariant(),
        };
        if (entity == "release" && item.TryGetProperty("release-group", out var group) && StringOf(group, "id") is { Length: > 0 } groupId)
        {
            ids["musicbrainz_release_group_id"] = groupId.ToLowerInvariant();
        }

        var date = StringOf(item, entity == "release" ? "date" : "first-release-date");
        return new ParsedListEntry
        {
            Position = position,
            Title = title.Trim(),
            Year = YearOf(date),
            Creator = ArtistOf(item),
            Ids = ids,
            MediaTypes = ["Music"],
        };
    }

    private static string? ArtistOf(JsonElement item)
    {
        if (!item.TryGetProperty("artist-credit", out var credits) || credits.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var text = string.Concat(credits.EnumerateArray().Select(credit =>
            (StringOf(credit, "name") ?? string.Empty) + (StringOf(credit, "joinphrase") ?? string.Empty))).Trim();
        return text.Length == 0 ? null : text;
    }
}
