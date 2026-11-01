using System.Globalization;
using System.Text.Json;
using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Services;

namespace MediaEngine.Api.Services.Collections.ListImport.Sources;

/// <summary>
/// MDBList lists (Collections and lists spec D7, 4d): <c>mdblist.com/lists/{user}/{list}</c>, read through MDBList's documented API
/// with the person's own free MDBList key. Entries carry IMDb, TMDB and TVDB ids, which the matcher tries first. Only the list's
/// address and the key go to MDBList.
/// </summary>
public sealed class MdbListSource(
    IConfigurationLoader configuration,
    IHttpClientFactory http,
    IProviderRateLimiterCoordinator limiter,
    ILogger<MdbListSource> logger,
    IProviderHealthMonitor? health = null)
    : WebListSourceBase(configuration, http, limiter, health, logger)
{
    private const int PageSize = 500;

    public override string ProviderName => "mdblist";

    public override string DisplayName => "MDBList lists";

    public override bool NeedsKey => true;

    protected override string DefaultApiBase => "https://api.mdblist.com";

    public override WebListAddress? Recognise(Uri address)
    {
        if (!IsHost(address, "mdblist.com"))
        {
            return null;
        }

        var segments = Segments(address);
        if (segments.Length < 3 || !segments[0].Equals("lists", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var user = segments[1].ToLowerInvariant();
        var list = segments[2].ToLowerInvariant();
        return new WebListAddress(
            ProviderName,
            ListSourceKinds.MdbList,
            $"https://mdblist.com/lists/{user}/{list}",
            new Dictionary<string, string> { ["user"] = user, ["list"] = list });
    }

    protected override async Task<ParsedList> ReadAsync(WebListAddress address, string apiBase, string? key, CancellationToken ct)
    {
        var user = Uri.EscapeDataString(address.Part("user"));
        var list = Uri.EscapeDataString(address.Part("list"));
        var name = await ReadNameAsync($"{apiBase}/lists/{user}/{list}", key, address.Part("list"), ct).ConfigureAwait(false);

        var all = new List<(double Rank, int Order, ParsedListEntry Entry)>();
        for (var page = 0; page < MaximumPages && all.Count < MaximumEntries; page++)
        {
            using var document = await GetJsonAsync(
                Query(
                    $"{apiBase}/lists/{user}/{list}/items",
                    ("apikey", key),
                    ("limit", PageSize.ToString(CultureInfo.InvariantCulture)),
                    ("offset", (page * PageSize).ToString(CultureInfo.InvariantCulture))),
                ct).ConfigureAwait(false);

            var received = 0;
            foreach (var (property, mediaType) in new[] { ("movies", "Movies"), ("shows", "TV") })
            {
                if (!document.RootElement.TryGetProperty(property, out var items) || items.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in items.EnumerateArray())
                {
                    received++;
                    if (EntryOf(item, mediaType) is { } entry)
                    {
                        var rank = item.TryGetProperty("rank", out var r) && r.TryGetDouble(out var value) ? value : double.MaxValue;
                        all.Add((rank, all.Count, entry));
                    }
                }
            }

            if (received < PageSize)
            {
                break;
            }
        }

        // The list's own order is its rank; movies and shows come back apart, so they are put back in one line.
        var entries = all
            .OrderBy(item => item.Rank)
            .ThenBy(item => item.Order)
            .Select((item, index) => item.Entry with { Position = index + 1 })
            .Take(MaximumEntries)
            .ToList();
        return new ParsedList { Name = name, Entries = entries };
    }

    // The list's own name is a nicety: a failure here must not stop the entries from being read.
    private async Task<string> ReadNameAsync(string url, string? key, string fallback, CancellationToken ct)
    {
        try
        {
            using var document = await GetJsonAsync(Query(url, ("apikey", key)), ct).ConfigureAwait(false);
            var root = document.RootElement;
            var first = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
            if (StringOf(first, "name")?.Trim() is { Length: > 0 } name)
            {
                return name;
            }
        }
        catch (WebListFailureException ex) when (ex.Failure == WebListFailure.Down)
        {
            Logger.LogDebug("MDBList did not give the list's name: {Message}", ex.Message);
        }

        return fallback.Replace('-', ' ');
    }

    private static ParsedListEntry? EntryOf(JsonElement item, string mediaType)
    {
        var title = StringOf(item, "title");
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var ids = new Dictionary<string, string>();
        Add(ids, ListIdentifierKeys.Imdb, StringOf(item, "imdb_id") ?? IdOf(item, "imdb"));
        Add(ids, ListIdentifierKeys.Tmdb, IdOf(item, "tmdb") ?? StringOf(item, "tmdb_id") ?? StringOf(item, "id"));
        Add(ids, ListIdentifierKeys.Tvdb, StringOf(item, "tvdb_id") ?? IdOf(item, "tvdb"));

        int? year = int.TryParse(StringOf(item, "release_year"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : null;
        return new ParsedListEntry { Title = title.Trim(), Year = year, Ids = ids, MediaTypes = [mediaType] };
    }

    private static string? IdOf(JsonElement item, string name) =>
        item.TryGetProperty("ids", out var ids) ? StringOf(ids, name) : null;

    private static void Add(Dictionary<string, string> ids, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && value != "0")
        {
            ids[key] = value.Trim();
        }
    }
}
