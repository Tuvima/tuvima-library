using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaEngine.Plugins;

namespace MediaEngine.Plugin.FandomLore;

public sealed class FandomUniverseLoreProvider : IUniverseLoreProvider
{
    private static readonly Regex WikiKeyPattern = new("^[a-z0-9][a-z0-9.-]*$", RegexOptions.CultureInvariant);
    private static readonly Regex QidPattern = new("^Q[1-9][0-9]{0,11}$", RegexOptions.CultureInvariant);

    public string Kind => "universe-lore-provider";

    public async Task<IReadOnlyList<PluginLoreSourceCandidate>> DiscoverSourcesAsync(
        PluginUniverseLoreContext universe,
        IPluginExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!FandomSettings.ReadBool(context.Settings, "auto_discovery_enabled", true))
        {
            return [];
        }

        var sourceMode = FandomSettings.ReadString(context.Settings, "source_mode", "hybrid_review");
        if (string.Equals(sourceMode, "manual_only", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        // The one thing sent to Wikidata is the Universe's own QID; nothing from the user's titles or files.
        if (!QidPattern.IsMatch(universe.UniverseQid))
        {
            return [];
        }

        var url = $"https://{FandomHostRule.WikidataHost}/w/api.php?action=wbgetclaims&format=json&entity={Uri.EscapeDataString(universe.UniverseQid)}&property=P6262";
        using var document = await GetJsonAsync(url, context, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return [];
        }

        var values = ReadP6262Values(document.RootElement);
        var candidates = new Dictionary<string, PluginLoreSourceCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (!TryBuildSourceCandidate(value, out var candidate))
            {
                continue;
            }

            candidates.TryAdd(candidate.SourceKey, candidate);
        }

        return candidates.Values.ToList();
    }

    public async Task<PluginUniverseLoreResult> EnrichUniverseAsync(
        PluginUniverseLoreContext universe,
        PluginLoreSource source,
        IPluginExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var contentMode = FandomSettings.ReadString(context.Settings, "content_mode", "structured_only");
        if (!string.Equals(contentMode, "structured_only", StringComparison.OrdinalIgnoreCase))
        {
            return new PluginUniverseLoreResult([], [], "Only structured-only extraction is supported.");
        }

        var maxPages = Math.Clamp(FandomSettings.ReadInt(context.Settings, "max_pages_per_run", 50), 1, 500);
        var threshold = Math.Clamp(FandomSettings.ReadDouble(context.Settings, "confidence_threshold", 0.65), 0.1, 1.0);
        var apiUrl = string.IsNullOrWhiteSpace(source.ApiUrl) ? BuildApiUrl(source.BaseUrl) : source.ApiUrl;
        if (!FandomHostRule.IsFandomAddress(apiUrl))
        {
            // A stored source on another host is never called.
            return new PluginUniverseLoreResult([], [], "Source address is not a Fandom wiki and was skipped.");
        }

        var url = $"{apiUrl}?action=query&format=json&formatversion=2&generator=allpages&gapnamespace=0&gaplimit={maxPages}&prop=info%7Ccategories%7Ctemplates&inprop=url&cllimit=max&tllimit=max";

        using var document = await GetJsonAsync(url, context, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return new PluginUniverseLoreResult([], [], "Fandom API did not return a usable response.");
        }

        var entities = new List<PluginLoreEntity>();
        foreach (var page in ReadPages(document.RootElement))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var classification = ClassifyPage(page.Title, page.Categories, page.Templates);
            if (classification.Type == "Unknown" || classification.Confidence < threshold)
            {
                continue;
            }

            var evidence = JsonSerializer.SerializeToElement(new
            {
                method = "mediawiki_query_pages",
                categories = page.Categories,
                templates = page.Templates,
                page_id = page.PageId,
            });

            entities.Add(new PluginLoreEntity(
                ExternalKey: page.PageId > 0 ? $"page:{page.PageId}" : $"title:{page.Title}",
                Label: page.Title,
                EntityType: classification.Type,
                WikidataQid: null,
                Description: null,
                Aliases: [],
                SourceUrl: string.IsNullOrWhiteSpace(page.FullUrl) ? BuildPageUrl(source.BaseUrl, page.Title) : page.FullUrl,
                Confidence: classification.Confidence,
                Evidence: evidence));
        }

        return new PluginUniverseLoreResult(entities, [], $"Extracted {entities.Count} structured Fandom lore entities.");
    }

    private static async Task<JsonDocument?> GetJsonAsync(
        string url,
        IPluginExecutionContext context,
        CancellationToken ct)
    {
        var delayMs = Math.Clamp(FandomSettings.ReadInt(context.Settings, "request_delay_ms", 500), 100, 10000);
        await Task.Delay(delayMs, ct).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(BuildUserAgent(context));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await context.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    private static string BuildUserAgent(IPluginExecutionContext context)
    {
        var contact = FandomSettings.ReadString(context.Settings, "user_agent_contact", "").Trim();
        return string.IsNullOrWhiteSpace(contact)
            ? "TuvimaLibrary/0.1 (FandomLore; structured metadata enrichment)"
            : $"TuvimaLibrary/0.1 (FandomLore; {contact})";
    }

    private static IEnumerable<string> ReadP6262Values(JsonElement root)
    {
        if (!root.TryGetProperty("claims", out var claims)
            || !claims.TryGetProperty("P6262", out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var claim in values.EnumerateArray())
        {
            if (claim.TryGetProperty("mainsnak", out var mainsnak)
                && mainsnak.TryGetProperty("datavalue", out var datavalue)
                && datavalue.TryGetProperty("value", out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                yield return value.GetString()!;
            }
        }
    }

    private static bool TryBuildSourceCandidate(string p6262Value, out PluginLoreSourceCandidate candidate)
    {
        candidate = default!;
        var raw = p6262Value.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string baseUrl;
        string sourceName;
        string? pageTitle = null;
        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            // Only Fandom wikis are called. An address on any other host is ignored; the wiki is always reached over https.
            if (!FandomHostRule.IsFandomHost(uri.Host) || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo))
            {
                return false;
            }

            baseUrl = $"https://{uri.Host.ToLowerInvariant()}";
            sourceName = ToTitle(uri.Host.Replace(".fandom.com", "", StringComparison.OrdinalIgnoreCase));
            pageTitle = uri.Segments.LastOrDefault()?.Replace('_', ' ');
        }
        else
        {
            var parts = raw.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
            {
                return false;
            }

            var wikiKey = parts[0].Trim().ToLowerInvariant();
            pageTitle = parts.Length == 2 ? parts[1].Replace('_', ' ') : null;
            if (!WikiKeyPattern.IsMatch(wikiKey))
            {
                return false;
            }

            if (wikiKey.EndsWith("fandom.com", StringComparison.Ordinal))
            {
                baseUrl = $"https://{wikiKey}";
                sourceName = ToTitle(wikiKey.Replace(".fandom.com", "", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                baseUrl = $"https://{wikiKey}.fandom.com";
                sourceName = ToTitle(wikiKey);
            }
        }

        if (!FandomHostRule.IsFandomAddress(baseUrl))
        {
            return false;
        }

        var sourceKey = new Uri(baseUrl).Host.ToLowerInvariant();
        candidate = new PluginLoreSourceCandidate(
            SourceKey: sourceKey,
            SourceName: $"{sourceName} Fandom",
            BaseUrl: baseUrl,
            ApiUrl: BuildApiUrl(baseUrl),
            License: "Fandom wiki license varies by community; retain attribution.",
            Confidence: 0.9,
            Evidence: JsonSerializer.SerializeToElement(new
            {
                method = "wikidata_p6262",
                property = "P6262",
                value = p6262Value,
                page_title = pageTitle,
            }));
        return true;
    }

    private static IEnumerable<FandomPage> ReadPages(JsonElement root)
    {
        if (!root.TryGetProperty("query", out var query)
            || !query.TryGetProperty("pages", out var pages)
            || pages.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var page in pages.EnumerateArray())
        {
            var title = page.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == JsonValueKind.String
                ? titleElement.GetString() ?? ""
                : "";
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var pageId = page.TryGetProperty("pageid", out var pageIdElement) && pageIdElement.TryGetInt64(out var parsedPageId)
                ? parsedPageId
                : 0;
            var fullUrl = page.TryGetProperty("fullurl", out var fullUrlElement) && fullUrlElement.ValueKind == JsonValueKind.String
                ? fullUrlElement.GetString() ?? ""
                : "";

            yield return new FandomPage(
                pageId,
                title,
                fullUrl,
                ReadTitleArray(page, "categories"),
                ReadTitleArray(page, "templates"));
        }
    }

    private static IReadOnlyList<string> ReadTitleArray(JsonElement page, string propertyName)
    {
        if (!page.TryGetProperty(propertyName, out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return values.EnumerateArray()
            .Select(item => item.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String
                ? title.GetString()
                : null)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();
    }

    private static FandomClassification ClassifyPage(
        string title,
        IReadOnlyList<string> categories,
        IReadOnlyList<string> templates)
    {
        var haystack = string.Join(" ", [title, .. categories, .. templates]).ToLowerInvariant();
        if (ContainsAny(haystack, "characters", "character pages", "fictional characters"))
        {
            return new("Character", 0.78);
        }

        if (ContainsAny(haystack, "locations", "places", "planets", "cities", "settlements", "regions"))
        {
            return new("Location", 0.74);
        }

        if (ContainsAny(haystack, "factions", "organizations", "organisations", "groups", "teams", "houses", "guilds", "clans"))
        {
            return new("Organization", 0.74);
        }

        if (ContainsAny(haystack, "events", "battles", "wars", "incidents"))
        {
            return new("Event", 0.7);
        }

        return new("Unknown", 0.0);
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string BuildApiUrl(string baseUrl) => $"{baseUrl.TrimEnd('/')}/api.php";

    private static string BuildPageUrl(string baseUrl, string title) =>
        $"{baseUrl.TrimEnd('/')}/wiki/{Uri.EscapeDataString(title.Replace(' ', '_'))}";

    private static string ToTitle(string value)
    {
        var text = value.Replace('-', ' ').Replace('_', ' ').Trim();
        return string.IsNullOrWhiteSpace(text)
            ? value
            : string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private sealed record FandomPage(
        long PageId,
        string Title,
        string FullUrl,
        IReadOnlyList<string> Categories,
        IReadOnlyList<string> Templates);

    private sealed record FandomClassification(string Type, double Confidence);
}
