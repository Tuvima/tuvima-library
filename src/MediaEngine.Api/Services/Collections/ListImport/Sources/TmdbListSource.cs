using System.Text.Json;
using System.Text.RegularExpressions;
using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Services;

namespace MediaEngine.Api.Services.Collections.ListImport.Sources;

/// <summary>
/// TMDb lists and collections (Collections and lists spec D7, 4d): <c>themoviedb.org/list/…</c> and <c>themoviedb.org/collection/…</c>,
/// read through TMDb's documented v3 API with the person's own TMDb key (the same key and switch the TMDb source already has).
/// Only the list's id and the key go to TMDb. Entries carry TMDb ids, which the matcher tries first.
/// </summary>
public sealed partial class TmdbListSource(
    IConfigurationLoader configuration,
    IHttpClientFactory http,
    IProviderRateLimiterCoordinator limiter,
    ILogger<TmdbListSource> logger,
    IProviderHealthMonitor? health = null)
    : WebListSourceBase(configuration, http, limiter, health, logger)
{
    public override string ProviderName => "tmdb";

    public override string DisplayName => "TMDb lists";

    public override bool NeedsKey => true;

    protected override string DefaultApiBase => "https://api.themoviedb.org/3";

    [GeneratedRegex(@"^(?<id>\d+|[0-9a-f]{24})(-.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[a-z]{2}(-[A-Za-z]{2})?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex LocalePattern();

    public override WebListAddress? Recognise(Uri address)
    {
        if (!IsHost(address, "themoviedb.org"))
        {
            return null;
        }

        var segments = Segments(address).ToList();
        if (segments.Count > 0 && LocalePattern().IsMatch(segments[0]))
        {
            segments.RemoveAt(0);
        }

        if (segments.Count < 2 || IdPattern().Match(segments[1]) is not { Success: true } match)
        {
            return null;
        }

        var id = match.Groups["id"].Value.ToLowerInvariant();
        return segments[0].ToLowerInvariant() switch
        {
            "list" => new WebListAddress(ProviderName, ListSourceKinds.TmdbList, $"https://www.themoviedb.org/list/{id}", Parts(id)),
            "collection" => new WebListAddress(ProviderName, ListSourceKinds.TmdbCollection, $"https://www.themoviedb.org/collection/{id}", Parts(id)),
            _ => null,
        };
    }

    private static Dictionary<string, string> Parts(string id) => new() { ["id"] = id };

    protected override async Task<ParsedList> ReadAsync(WebListAddress address, string apiBase, string? key, CancellationToken ct)
    {
        var id = Uri.EscapeDataString(address.Part("id"));
        return address.Kind == ListSourceKinds.TmdbCollection
            ? await ReadCollectionAsync(id, apiBase, key, ct).ConfigureAwait(false)
            : await ReadListAsync(id, apiBase, key, ct).ConfigureAwait(false);
    }

    private async Task<ParsedList> ReadListAsync(string id, string apiBase, string? key, CancellationToken ct)
    {
        var entries = new List<ParsedListEntry>();
        var name = string.Empty;
        var totalPages = 1;
        for (var page = 1; page <= totalPages && page <= MaximumPages && entries.Count < MaximumEntries; page++)
        {
            using var document = await GetJsonAsync(
                Query($"{apiBase}/list/{id}", ("api_key", key), ("page", page.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("language", "en-US")),
                ct).ConfigureAwait(false);
            var root = document.RootElement;
            if (page == 1)
            {
                name = StringOf(root, "name")?.Trim() ?? string.Empty;
            }

            if (root.TryGetProperty("total_pages", out var pages) && pages.TryGetInt32(out var count))
            {
                totalPages = count;
            }

            if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            foreach (var item in items.EnumerateArray())
            {
                if (EntryOf(item, entries.Count + 1, defaultKind: "movie") is { } entry)
                {
                    entries.Add(entry);
                }
            }
        }

        return new ParsedList { Name = name, Entries = entries };
    }

    private async Task<ParsedList> ReadCollectionAsync(string id, string apiBase, string? key, CancellationToken ct)
    {
        using var document = await GetJsonAsync(
            Query($"{apiBase}/collection/{id}", ("api_key", key), ("language", "en-US")), ct).ConfigureAwait(false);
        var root = document.RootElement;
        var entries = new List<ParsedListEntry>();
        if (root.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
            {
                if (EntryOf(part, entries.Count + 1, defaultKind: "movie") is { } entry)
                {
                    entries.Add(entry);
                }
            }
        }

        return new ParsedList { Name = StringOf(root, "name")?.Trim() ?? string.Empty, Entries = entries };
    }

    private static ParsedListEntry? EntryOf(JsonElement item, int position, string defaultKind)
    {
        var tmdbId = StringOf(item, "id");
        var title = StringOf(item, "title") ?? StringOf(item, "name");
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var kind = StringOf(item, "media_type") ?? defaultKind;
        var isShow = string.Equals(kind, "tv", StringComparison.OrdinalIgnoreCase);
        var ids = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(tmdbId))
        {
            ids[ListIdentifierKeys.Tmdb] = tmdbId;
        }

        return new ParsedListEntry
        {
            Position = position,
            Title = title.Trim(),
            Year = YearOf(StringOf(item, isShow ? "first_air_date" : "release_date")),
            Ids = ids,
            MediaTypes = [isShow ? "TV" : "Movies"],
        };
    }
}
