using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Services;

namespace MediaEngine.Api.Services.Collections.ListImport.Sources;

/// <summary>
/// Open Library lists (Collections and lists spec D7, 4d): <c>openlibrary.org/people/{user}/lists/{OL…L}</c>, read through Open
/// Library's public list API. It needs no key. A list names works and editions, not ISBNs, so entries match by title; only the
/// list's address goes to Open Library.
/// </summary>
public sealed partial class OpenLibraryListSource(
    IConfigurationLoader configuration,
    IHttpClientFactory http,
    IProviderRateLimiterCoordinator limiter,
    ILogger<OpenLibraryListSource> logger,
    IProviderHealthMonitor? health = null)
    : WebListSourceBase(configuration, http, limiter, health, logger)
{
    private const int PageSize = 100;

    public override string ProviderName => "openlibrary";

    public override string DisplayName => "Open Library lists";

    protected override string DefaultApiBase => "https://openlibrary.org";

    [GeneratedRegex(@"^OL\d+L$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex ListIdPattern();

    [GeneratedRegex(@"^/(works|books)/OL\d+[WM]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex BookKeyPattern();

    public override WebListAddress? Recognise(Uri address)
    {
        if (!IsHost(address, "openlibrary.org"))
        {
            return null;
        }

        var segments = Segments(address);
        if (segments.Length < 4
            || !segments[0].Equals("people", StringComparison.OrdinalIgnoreCase)
            || !segments[2].Equals("lists", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var id = Regex.Replace(segments[3], @"\.json$", string.Empty, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
        if (!ListIdPattern().IsMatch(id))
        {
            return null;
        }

        var user = segments[1];
        var listId = id.ToUpperInvariant();
        return new WebListAddress(
            ProviderName,
            ListSourceKinds.OpenLibrary,
            $"https://openlibrary.org/people/{user}/lists/{listId}",
            new Dictionary<string, string> { ["user"] = user, ["list"] = listId });
    }

    protected override async Task<ParsedList> ReadAsync(WebListAddress address, string apiBase, string? key, CancellationToken ct)
    {
        var path = $"{apiBase}/people/{Uri.EscapeDataString(address.Part("user"))}/lists/{Uri.EscapeDataString(address.Part("list"))}";
        string name;
        using (var info = await GetJsonAsync($"{path}.json", ct).ConfigureAwait(false))
        {
            name = StringOf(info.RootElement, "name")?.Trim() ?? string.Empty;
        }

        var entries = new List<ParsedListEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; page < MaximumPages && entries.Count < MaximumEntries; page++)
        {
            using var document = await GetJsonAsync(
                Query(
                    $"{path}/seeds.json",
                    ("limit", PageSize.ToString(CultureInfo.InvariantCulture)),
                    ("offset", (page * PageSize).ToString(CultureInfo.InvariantCulture))),
                ct).ConfigureAwait(false);
            var root = document.RootElement;
            if (!root.TryGetProperty("entries", out var seeds) || seeds.ValueKind != JsonValueKind.Array || seeds.GetArrayLength() == 0)
            {
                break;
            }

            var added = 0;
            foreach (var seed in seeds.EnumerateArray())
            {
                var url = StringOf(seed, "url") ?? string.Empty;
                if (!seen.Add(url))
                {
                    continue;
                }

                added++;
                var title = StringOf(seed, "title");
                if (string.IsNullOrWhiteSpace(title) || !BookKeyPattern().IsMatch(url))
                {
                    // A subject, an author or a place on the list is not a book.
                    continue;
                }

                entries.Add(new ParsedListEntry
                {
                    Position = entries.Count + 1,
                    Title = title.Trim(),
                    MediaTypes = ["Books", "Audiobooks"],
                });
            }

            // A site that ignores paging sends the whole list each time: nothing new means the list is read.
            var size = root.TryGetProperty("size", out var total) && total.TryGetInt32(out var count) ? count : 0;
            if (added == 0 || seeds.GetArrayLength() < PageSize || (size > 0 && (page + 1) * PageSize >= size))
            {
                break;
            }
        }

        return new ParsedList { Name = name, Entries = entries };
    }
}
