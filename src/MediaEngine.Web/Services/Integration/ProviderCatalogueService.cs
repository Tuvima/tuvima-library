using MediaEngine.Contracts.Settings;
using MediaEngine.Domain;
using MediaEngine.Web.Models.ViewDTOs;
using Microsoft.Extensions.Caching.Memory;
using MudBlazor;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// Caches the provider catalogue fetched from the Engine's <c>GET /providers/catalogue</c>
/// endpoint. This service replaces hardcoded provider display names, accent colours,
/// material icons, and field capability chips across Dashboard files.
///
/// <para>
/// The catalogue is loaded lazily on first use and cached in memory for the session.
/// All lookup methods return safe fallback values when the Engine is unreachable.
/// Static fallback metadata lives here too, so provider identity does not drift when
/// the Engine is unavailable or a static display helper cannot use dependency injection.
/// </para>
/// </summary>
public sealed class ProviderCatalogueService
{
    private readonly IEngineApiClient _api;
    private readonly IMemoryCache _cache;
    private IReadOnlyList<ProviderCatalogueDto>? _catalogue;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private const string CatalogueCacheKey = "provider-catalogue:v1";
    private static readonly IReadOnlyDictionary<Guid, string> ProviderNamesById =
        new Dictionary<Guid, string>
        {
            [WellKnownProviders.LocalProcessor] = "File Scan",
            [WellKnownProviders.LibraryScanner] = "Library Scanner",
            [WellKnownProviders.AppleApi] = "Apple API",
            [WellKnownProviders.Wikidata] = "Wikidata",
            [WellKnownProviders.Wikipedia] = "Wikipedia",
            [WellKnownProviders.MusicBrainz] = "MusicBrainz",
            [WellKnownProviders.Tmdb] = "TMDB",
            [WellKnownProviders.Tvdb] = "TheTVDB",
            [WellKnownProviders.AiProvider] = "AI-generated metadata",
            [WellKnownProviders.UserManual] = "Manual Match",
        };

    private static readonly IReadOnlyDictionary<string, ProviderFallback> ProviderFallbacks =
        new Dictionary<string, ProviderFallback>(StringComparer.OrdinalIgnoreCase)
        {
            ["apple_api"] = new("Apple API", "#FF2D55", Icons.Material.Filled.MenuBook),
            ["wikidata"] = new("Wikidata", "#339966", Icons.Material.Filled.Collections),
            ["wikidata_reconciliation"] = new("Wikidata", "#339966", Icons.Material.Filled.Collections),
            ["tmdb"] = new("TMDB", "#01B4E4", Icons.Material.Filled.Movie),
            ["tvdb"] = new("TheTVDB", "#21B6A8", Icons.Material.Filled.Tv),
            ["comicvine"] = new("Comic Vine", "#04C8FF", Icons.Material.Filled.AutoStories),
            ["musicbrainz"] = new("MusicBrainz", "#BA478F", Icons.Material.Filled.MusicNote),
            ["lrclib"] = new("LRCLIB", "#3BA55D", Icons.Material.Filled.Lyrics),
            ["opensubtitles"] = new("OpenSubtitles", "#0F8BFD", Icons.Material.Filled.Subtitles),
            ["local_filesystem"] = new("Local Filesystem", "#90A4AE", Icons.Material.Filled.FolderOpen),
        };

    private static readonly IReadOnlyDictionary<string, string> MaterialIcons =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MenuBook"] = Icons.Material.Filled.MenuBook,
            ["Headphones"] = Icons.Material.Filled.Headphones,
            ["AutoStories"] = Icons.Material.Filled.AutoStories,
            ["Movie"] = Icons.Material.Filled.Movie,
            ["Tv"] = Icons.Material.Filled.Tv,
            ["MusicNote"] = Icons.Material.Filled.MusicNote,
            ["Description"] = Icons.Material.Filled.Description,
            ["Folder"] = Icons.Material.Filled.Folder,
            ["FolderOpen"] = Icons.Material.Filled.FolderOpen,
            ["Photo"] = Icons.Material.Filled.Photo,
            ["VideoLibrary"] = Icons.Material.Filled.VideoLibrary,
            ["AudioFile"] = Icons.Material.Filled.AudioFile,
            ["Article"] = Icons.Material.Filled.Article,
            ["Book"] = Icons.Material.Filled.Book,
            ["LibraryBooks"] = Icons.Material.Filled.LibraryBooks,
            ["LocalLibrary"] = Icons.Material.Filled.LocalLibrary,
            ["Hearing"] = Icons.Material.Filled.Hearing,
            ["Mic"] = Icons.Material.Filled.Mic,
            ["Album"] = Icons.Material.Filled.Album,
            ["Camera"] = Icons.Material.Filled.Camera,
            ["Image"] = Icons.Material.Filled.Image,
            ["PictureAsPdf"] = Icons.Material.Filled.PictureAsPdf,
            ["Code"] = Icons.Material.Filled.Code,
            ["Science"] = Icons.Material.Filled.Science,
            ["School"] = Icons.Material.Filled.School,
            ["SportsEsports"] = Icons.Material.Filled.SportsEsports,
            ["Newspaper"] = Icons.Material.Filled.Newspaper,
            ["Dashboard"] = Icons.Material.Filled.Dashboard,
            ["Star"] = Icons.Material.Filled.Star,
            ["Lyrics"] = Icons.Material.Filled.Lyrics,
            ["Subtitles"] = Icons.Material.Filled.Subtitles,
            ["Cloud"] = Icons.Material.Filled.Cloud,
            ["Collection"] = Icons.Material.Filled.Collections,
        };

    public ProviderCatalogueService(IEngineApiClient api, IMemoryCache cache)
    {
        _api = api;
        _cache = cache;
    }

    // -- Catalogue access ------------------------------------------------------

    /// <summary>Returns the full catalogue, loading it from the Engine on first call.</summary>
    public async Task<IReadOnlyList<ProviderCatalogueDto>> GetCatalogueAsync(
        CancellationToken ct = default)
    {
        if (_cache.TryGetValue(CatalogueCacheKey, out IReadOnlyList<ProviderCatalogueDto>? cached) && cached is not null)
        {
            _catalogue = cached;
            return cached;
        }

        if (_catalogue is not null)
        {
            return _catalogue;
        }

        await _loadLock.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(CatalogueCacheKey, out cached) && cached is not null)
            {
                _catalogue = cached;
                return cached;
            }

            if (_catalogue is not null)
            {
                return _catalogue;
            }

            _catalogue = await _api.GetProviderCatalogueAsync(ct);
            _cache.Set(
                CatalogueCacheKey,
                _catalogue,
                new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
                    Size = Math.Max(1, _catalogue.Count),
                });
        }
        finally
        {
            _loadLock.Release();
        }

        return _catalogue;
    }

    /// <summary>Returns the entry for a provider by config name (e.g. "apple_api"), or null if not found.</summary>
    public ProviderCatalogueDto? GetByName(string providerName)
    {
        if (_catalogue is null)
        {
            return null;
        }

        return _catalogue.FirstOrDefault(p =>
            string.Equals(p.Name, providerName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Returns the entry for a provider by GUID string, or null if not found.</summary>
    public ProviderCatalogueDto? GetById(string providerId)
    {
        if (_catalogue is null)
        {
            return null;
        }

        return _catalogue.FirstOrDefault(p =>
            string.Equals(p.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
    }

    // -- Convenience accessors (safe fallbacks when catalogue not loaded) -------

    /// <summary>
    /// Returns the hex accent colour for a provider config or display name.
    /// Falls back to the built-in catalogue when live metadata is not yet loaded.
    /// </summary>
    public string GetAccentColor(string providerName)
    {
        var entry = FindEntry(providerName);
        if (!string.IsNullOrWhiteSpace(entry?.AccentColor))
        {
            return entry.AccentColor;
        }

        return GetFallback(providerName).Color;
    }

    /// <summary>
    /// Returns the display name for a provider config or display name.
    /// Falls back to the built-in catalogue when live metadata is not yet loaded.
    /// </summary>
    public string GetDisplayName(string providerName)
    {
        var entry = FindEntry(providerName);
        if (!string.IsNullOrWhiteSpace(entry?.DisplayName))
        {
            return entry.DisplayName;
        }

        return GetFallback(providerName).DisplayName;
    }

    /// <summary>Returns the resolved Material icon for a provider config or display name.</summary>
    public string GetMaterialIcon(string providerName)
    {
        var entry = FindEntry(providerName);
        return ResolveMaterialIcon(entry?.MaterialIcon) ?? GetFallback(providerName).Icon;
    }

    /// <summary>Returns the provider accent and resolved Material icon as one presentation value.</summary>
    public (string Color, string Icon) GetAccent(string providerName, string? customIconName = null)
    {
        var icon = ResolveMaterialIcon(customIconName);
        return (GetAccentColor(providerName), icon ?? GetMaterialIcon(providerName));
    }

    /// <summary>Returns whether the provider represents a user-facing metadata source.</summary>
    public static bool IsVisibleProvider(string providerName) =>
        !string.Equals(providerName, "local_filesystem", StringComparison.OrdinalIgnoreCase);

    /// <summary>Formats a provider config key without applying provider-specific branding.</summary>
    public static string FormatProviderName(string key) =>
        string.Join(' ', key.Split('_')
            .Select(word => word.Length > 0 ? char.ToUpperInvariant(word[0]) + word[1..] : word));

    /// <summary>Formats compact activity labels while preserving established provider branding.</summary>
    public static string FormatProviderLabel(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return "-";
        }

        var normalized = provider.Trim().Replace('_', ' ').Replace('-', ' ').ToLowerInvariant();
        var compact = normalized.Replace(" ", "", StringComparison.Ordinal);
        return compact switch
        {
            "tmdb" => "TMDB",
            "tvdb" => "TheTVDB",
            "wikidata" => "Wikidata",
            "apple" or "appleapi" or "applebooks" or "applemusic" => "Apple",
            "provider" or "providermatch" => "Retail match",
            _ => MediaEngine.Web.Services.Formatting.DisplayFormat.SplitWords(provider),
        };
    }

    /// <summary>Formats a technical source/provider key or well-known provider ID.</summary>
    public static string FormatSourceName(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return "Unknown";
        }

        if (Guid.TryParse(source, out var providerId))
        {
            return FormatProviderName(providerId);
        }

        return source.ToLowerInvariant() switch
        {
            "user_manual" => "Manual Match",
            "local_processor" or "local_filesystem" => "File Scan",
            "file_metadata" => "File Metadata",
            "wikidata_reconciliation" or "wikidata" => "Wikidata",
            "wikipedia" => "Wikipedia",
            "retail_provider" => "Retail Provider",
            "apple_api" => "Apple API",
            "musicbrainz" => "MusicBrainz",
            "tmdb" => "TMDB",
            "tvdb" => "TheTVDB",
            "library_scanner" => "Library Scanner",
            _ => source,
        };
    }

    /// <summary>Formats a well-known provider ID.</summary>
    public static string FormatProviderName(Guid providerId) =>
        ProviderNamesById.TryGetValue(providerId, out var name) ? name : providerId.ToString();

    /// <summary>
    /// Builds an external URL for a given bridge ID key and value using the catalogue's
    /// url templates. Returns null when no template matches.
    /// </summary>
    public (string Label, string Url)? GetExternalUrl(string bridgeKey, string value, string? mediaType = null)
    {
        var match = GetExternalUrls(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [bridgeKey] = value,
                },
                mediaType)
            .FirstOrDefault();

        return match is null ? null : (match.Label, match.Url);
    }

    /// <summary>
    /// Resolves every configured public source link supported by the supplied
    /// identifiers. Link availability, labels, and URL shapes all come from the
    /// provider catalogue rather than media-editor conditionals.
    /// </summary>
    public IReadOnlyList<ProviderExternalUrl> GetExternalUrls(
        IReadOnlyDictionary<string, string> identifiers,
        string? mediaType = null,
        string? providerName = null)
    {
        if (_catalogue is null || identifiers.Count == 0)
        {
            return [];
        }

        var links = new List<ProviderExternalUrl>();
        foreach (var provider in _catalogue)
        {
            if (!string.IsNullOrWhiteSpace(providerName)
                && !ProviderMatches(provider, providerName))
            {
                continue;
            }

            foreach (var (identifierKey, linkConfig) in provider.ExternalLinks)
            {
                if (!identifiers.TryGetValue(identifierKey, out var value)
                    || string.IsNullOrWhiteSpace(value)
                    || string.IsNullOrWhiteSpace(linkConfig.UrlTemplate))
                {
                    continue;
                }

                var url = ExpandExternalUrlTemplate(
                    linkConfig.UrlTemplate,
                    identifierKey,
                    value,
                    mediaType);
                if (string.IsNullOrWhiteSpace(url)
                    || links.Any(existing => string.Equals(existing.Url, url, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                links.Add(new ProviderExternalUrl(
                    identifierKey,
                    string.IsNullOrWhiteSpace(linkConfig.Label)
                        ? $"View on {provider.DisplayName}"
                        : linkConfig.Label,
                    url,
                    provider.DisplayName,
                    linkConfig.Tooltip));
            }
        }

        return links;
    }

    /// <summary>Returns search chip labels for a provider and media type.</summary>
    public IReadOnlyList<string> GetSearchChips(string providerName, string? mediaType = null)
    {
        var entry = GetByName(providerName);
        if (entry is null)
        {
            return [];
        }

        if (mediaType is not null && entry.SearchChips.TryGetValue(mediaType, out var chips))
        {
            return chips;
        }

        // Return union of all chips when media type not specified
        return entry.SearchChips.Values.SelectMany(c => c).Distinct().ToList();
    }

    /// <summary>Returns ranking chip labels for a provider and media type.</summary>
    public IReadOnlyList<string> GetRankingChips(string providerName, string? mediaType = null)
    {
        var entry = GetByName(providerName);
        if (entry is null)
        {
            return [];
        }

        if (mediaType is not null && entry.RankingChips.TryGetValue(mediaType, out var chips))
        {
            return chips;
        }

        return entry.RankingChips.Values.SelectMany(c => c).Distinct().ToList();
    }

    /// <summary>Invalidates the cached catalogue, forcing a reload on the next call.</summary>
    public void Invalidate()
    {
        _catalogue = null;
        _cache.Remove(CatalogueCacheKey);
    }

    // -- Private helpers -------------------------------------------------------

    private static string ResolveMediaTypePath(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return "movie";
        }

        return mediaType.Contains("TV", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
    }

    private static string? ExpandExternalUrlTemplate(
        string template,
        string identifierKey,
        string rawValue,
        string? mediaType)
    {
        var value = rawValue.Trim();
        var templateValue = Uri.TryCreate(value, UriKind.Absolute, out var absolute)
                            && absolute.Scheme is "http" or "https"
            ? value
            : Uri.EscapeDataString(value);
        var url = template
            .Replace($"{{{identifierKey}}}", templateValue, StringComparison.OrdinalIgnoreCase)
            .Replace("{value}", templateValue, StringComparison.OrdinalIgnoreCase)
            .Replace("{media_type}", ResolveMediaTypePath(mediaType), StringComparison.OrdinalIgnoreCase);

        return Uri.TryCreate(url, UriKind.Absolute, out var resolved)
               && resolved.Scheme is "http" or "https"
            ? resolved.ToString()
            : null;
    }

    private static bool ProviderMatches(ProviderCatalogueDto provider, string providerName)
    {
        var normalized = providerName.Trim().Replace('-', '_');
        return string.Equals(provider.Name, normalized, StringComparison.OrdinalIgnoreCase)
               || string.Equals(provider.DisplayName, providerName, StringComparison.OrdinalIgnoreCase)
               || string.Equals(provider.ProviderId, providerName, StringComparison.OrdinalIgnoreCase);
    }

    private ProviderCatalogueDto? FindEntry(string providerName)
    {
        if (_catalogue is null)
        {
            return null;
        }

        return _catalogue.FirstOrDefault(provider =>
            string.Equals(provider.Name, providerName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider.DisplayName, providerName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider.ProviderId, providerName, StringComparison.OrdinalIgnoreCase));
    }

    private static ProviderFallback GetFallback(string providerName)
    {
        if (ProviderFallbacks.TryGetValue(providerName, out var fallback))
        {
            return fallback;
        }

        var normalized = new string(providerName
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        var match = ProviderFallbacks.Values.FirstOrDefault(candidate =>
            string.Equals(
                new string(candidate.DisplayName
                    .Where(char.IsLetterOrDigit)
                    .Select(char.ToLowerInvariant)
                    .ToArray()),
                normalized,
                StringComparison.Ordinal));

        return match ?? new ProviderFallback(
            FormatProviderName(providerName),
            "#90A4AE",
            Icons.Material.Filled.Cloud);
    }

    private static string? ResolveMaterialIcon(string? iconName) =>
        !string.IsNullOrWhiteSpace(iconName) && MaterialIcons.TryGetValue(iconName, out var icon)
            ? icon
            : null;

    private sealed record ProviderFallback(string DisplayName, string Color, string Icon);

    public sealed record ProviderExternalUrl(
        string Key,
        string Label,
        string Url,
        string ProviderName,
        string? Tooltip);
}

