using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Helpers;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Downloads TMDB movie and television artwork into the managed central asset
/// store. Retail matching owns first-poster and episode-still acquisition;
/// this stage adds ranked backdrops, title logos, poster variants, and season art.
/// </summary>
public sealed class ImageEnrichmentService : IImageEnrichmentService
{
    private const string TmdbProviderName = "tmdb";
    private const string TmdbApiBaseUrl = "https://api.themoviedb.org/3";
    private const string TmdbImageBaseUrl = "https://image.tmdb.org/t/p/original";
    private const int MaxVariantsPerAssetType = 3;

    public async Task<ProviderArtworkDiscovery> DiscoverArtworkAsync(Guid assetId, string scope, string role, CancellationToken ct = default)
    {
        var context = await ResolveContextAsync(assetId, ct);
        if (context.MediaType == MediaType.Comics)
        {
            return await DiscoverComicArtworkAsync(context, scope, role, ct);
        }
        if (context.MediaType == MediaType.TV)
        {
            return new([], "Match this TV item to TheTVDB to browse provider artwork.");
        }
        if (context.MediaType is not (MediaType.Movies or MediaType.TV))
        {
            return new([], "No artwork gallery is available for this provider.");
        }
        var values = await LoadCanonicalsAsync(context, ct);
        var id = GetValue(values, BridgeIdKeys.TmdbId);
        if (string.IsNullOrWhiteSpace(id))
        {
            return new([], "Choose a provider match in Match & Identity first.");
        }
        if (_configLoader.LoadProvider(TmdbProviderName)?.Enabled == false)
        {
            return new([], "TMDB is disabled in provider settings.");
        }
        var key = await ResolveTmdbApiKeyAsync(ct);
        if (string.IsNullOrWhiteSpace(key))
        {
            return new([], "Configure TMDB in provider settings to discover artwork.");
        }
        var path = context.MediaType == MediaType.Movies ? $"movie/{Uri.EscapeDataString(id)}" : $"tv/{Uri.EscapeDataString(id)}";
        var field = role switch { "Primary" => "posters", "Background" => "backdrops", "Logo" => "logos", _ => "" };
        if (context.MediaType == MediaType.TV && scope is "season" or "episode")
        {
            if (context.SeasonNumber is not { } season)
            {
                return new([], "This item needs a season number in Match & Identity.");
            }
            path += $"/season/{season}";
            if (scope == "episode")
            {
                var own = await _canonicalRepo.GetByEntityAsync(context.AssetId, ct);
                var episode = own.FirstOrDefault(v => v.Key == "episode_number")?.Value ?? GetValue(values, "episode_number");
                if (!int.TryParse(episode, out var number))
                {
                    return new([], "This item needs an episode number in Match & Identity.");
                }
                path += $"/episode/{number}";
                field = role == "Primary" ? "stills" : "";
            }
        }
        if (field.Length == 0)
        {
            return new([], "The provider has no gallery for this artwork type.");
        }
        var response = await GetImagesAsync($"{TmdbApiBaseUrl}/{path}/images", key, ResolveMetadataLanguage(), ct);
        if (response.Json is null)
        {
            return new([], response.Message ?? "Provider artwork could not be loaded. Try again.");
        }
        var items = (response.Json[field]?.AsArray() ?? []).Where(n => n?["file_path"] is not null)
            .Select(n => new ProviderArtworkCandidate("tmdb:" + n!["file_path"]!.GetValue<string>(), "TMDB",
                TmdbImageBaseUrl + n["file_path"]!.GetValue<string>(),
                "https://image.tmdb.org/t/p/w300" + n["file_path"]!.GetValue<string>(),
                n["width"]?.GetValue<int>(), n["height"]?.GetValue<int>())).DistinctBy(n => n.Id).Take(150).ToList();
        return new(items, items.Count == 0 ? "The provider returned no artwork for this type." : null);
    }

    private async Task<ProviderArtworkDiscovery> DiscoverComicArtworkAsync(ArtworkContext context, string scope, string role, CancellationToken ct)
    {
        if (role != "Primary")
        {
            return new([], "Comic Vine supplies cover artwork; it does not provide backgrounds or logos.");
        }
        var config = _configLoader.LoadProvider("comicvine");
        if (config?.Enabled == false)
        {
            return new([], "Comic Vine is disabled in provider settings.");
        }
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var series = scope is "series" or "volume";
        foreach (var entity in (series ? new[] { context.RootWorkId, context.SelfWorkId, context.AssetId } : new[] { context.AssetId, context.SelfWorkId }).Distinct())
        {
            foreach (var value in await _canonicalRepo.GetByEntityAsync(entity, ct))
            {
                if (!string.IsNullOrWhiteSpace(value.Value))
                {
                    values.TryAdd(value.Key, value.Value);
                }
            }
        }
        var id = GetValue(values, series ? BridgeIdKeys.ComicVineVolumeId : BridgeIdKeys.ComicVineId);
        var prefix = series ? "4050-" : "4000-";
        if (id?.StartsWith(prefix, StringComparison.Ordinal) == true)
        {
            id = id[prefix.Length..];
        }
        if (!long.TryParse(id, out var numericId) || numericId <= 0)
        {
            return new([], $"Match this {(series ? "series" : "issue")} to Comic Vine in Match & Identity first.");
        }
        var key = config?.HttpClient?.ApiKeyOverride;
        if (string.IsNullOrWhiteSpace(key))
        {
            key = config?.HttpClient?.ApiKey;
        }
        if (string.IsNullOrWhiteSpace(key))
        {
            key = await _providerConfigRepo.GetDecryptedValueAsync(WellKnownProviders.ComicVine.ToString(), "api_key", ct);
        }
        if (string.IsNullOrWhiteSpace(key))
        {
            return new([], "Configure Comic Vine in provider settings to discover artwork.");
        }
        try
        {
            using var client = _httpFactory.CreateClient("comicvine");
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://comicvine.gamespot.com/api/{(series ? "volume" : "issue")}/{prefix}{numericId}/?api_key={Uri.EscapeDataString(key)}&format=json&field_list=id,image,associated_images");
            request.Headers.UserAgent.ParseAdd("TuvimaLibrary/1.0");
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return new([], $"Comic Vine could not load artwork (HTTP {(int)response.StatusCode}). Check the provider connection and try again.");
            }
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            if (json?["status_code"]?.GetValue<int>() != 1)
            {
                return new([], "Comic Vine could not load this gallery. Check the provider connection and identity.");
            }
            var result = json["results"];
            var nodes = new List<JsonNode?> { result?["image"] };
            if (result?["associated_images"] is JsonArray associated)
            {
                nodes.AddRange(associated);
            }
            var candidates = new List<ProviderArtworkCandidate>();
            foreach (var node in nodes.Where(n => n is not null))
            {
                var original = node?["original_url"]?.GetValue<string>();
                var thumbnail = node?["small_url"]?.GetValue<string>() ?? node?["thumb_url"]?.GetValue<string>();
                if (!Uri.TryCreate(original, UriKind.Absolute, out var uri) || uri.Scheme != "https")
                {
                    continue;
                }
                // Comic Vine associated images expose only original_url; its CDN supports explicit thumbnail sizes.
                if (string.IsNullOrWhiteSpace(thumbnail) && uri.Host.EndsWith("gamespot.com", StringComparison.OrdinalIgnoreCase))
                {
                    thumbnail = original!.Replace("/original/", "/scale_small/", StringComparison.Ordinal);
                }
                if (thumbnail == original || string.IsNullOrWhiteSpace(thumbnail))
                {
                    continue;
                }
                candidates.Add(new("comicvine:" + original, "Comic Vine", original!, thumbnail, null, null));
            }
            var items = candidates.DistinctBy(c => c.Id).Take(150).ToList();
            return new(items, items.Count == 0 ? "Comic Vine returned no cover images for this identity." : null);
        }
        catch (HttpRequestException) { return new([], "Comic Vine could not be reached. Try again."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return new([], "Comic Vine timed out. Try again."); }
    }

    private readonly IEntityAssetRepository _assetRepo;
    private readonly IMediaAssetRepository _mediaAssetRepo;
    private readonly ICanonicalValueRepository _canonicalRepo;
    private readonly IWorkRepository _workRepo;
    private readonly IProviderConfigurationRepository _providerConfigRepo;
    private readonly IConfigurationLoader _configLoader;
    private readonly IImageCacheRepository _imageCache;
    private readonly AssetPathService _assetPaths;
    private readonly IAssetExportService? _assetExportService;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ImageDownloadCoordinator _imageDownloadCoordinator;
    private readonly ILogger<ImageEnrichmentService> _logger;
    private readonly ProviderApiClient? _tvdb;

    // Keep the established constructor shape so composition and existing callers stay stable.
    public ImageEnrichmentService(
        IEntityAssetRepository assetRepo, IMediaAssetRepository mediaAssetRepo,
        ICharacterPortraitRepository portraitRepo, ICanonicalValueRepository canonicalRepo,
        IWorkRepository workRepo, IFictionalEntityRepository entityRepo, IPersonRepository personRepo,
        IProviderConfigurationRepository providerConfigRepo, IConfigurationLoader configLoader,
        IImageCacheRepository imageCache, AssetPathService assetPaths, IAssetExportService? assetExportService,
        IHttpClientFactory httpFactory, IFuzzyMatchingService fuzzy, ILogger<ImageEnrichmentService> logger,
        ImageDownloadCoordinator? imageDownloadCoordinator = null,
        ProviderApiClient? tvdb = null)
    {
        _assetRepo = assetRepo;
        _mediaAssetRepo = mediaAssetRepo;
        _canonicalRepo = canonicalRepo;
        _workRepo = workRepo;
        _providerConfigRepo = providerConfigRepo;
        _configLoader = configLoader;
        _imageCache = imageCache;
        _assetPaths = assetPaths;
        _assetExportService = assetExportService;
        _httpFactory = httpFactory;
        _imageDownloadCoordinator = imageDownloadCoordinator ?? ImageDownloadCoordinator.Shared;
        _logger = logger;
        _tvdb = tvdb;
    }

    public Task<ImageEnrichmentResult> EnrichWorkImagesAsync(Guid assetId, string? workQid, CancellationToken ct = default) =>
        EnrichWorkImagesCoreAsync(assetId, workQid, forceRefresh: false, ct);

    public Task<ImageEnrichmentResult> RefreshWorkImagesAsync(Guid assetId, string? workQid, CancellationToken ct = default) =>
        EnrichWorkImagesCoreAsync(assetId, workQid, forceRefresh: true, ct);

    public async Task<ImageEnrichmentResult> RefreshTvdbScopeImagesAsync(
        Guid ownerWorkId, string scopeId, string tvdbId, CancellationToken ct = default)
    {
        if (_tvdb?.IsConfigured("tvdb") != true || scopeId is not ("series" or "season" or "episode")
            || !tvdbId.All(char.IsDigit))
        {
            return new ImageEnrichmentResult
            {
                Provider = "tvdb",
                ProviderName = "TheTVDB",
                Status = "Skipped",
                MediaType = "TV",
                BridgeId = tvdbId,
                LastCheckedAt = DateTimeOffset.UtcNow,
                SkippedReason = "missing_provider_or_identity",
            };
        }

        var detail = scopeId switch
        {
            "series" => await _tvdb.GetAsync("tvdb", "series_extended", ProviderApiClient.Args(("id", tvdbId)), ct).ConfigureAwait(false),
            "season" => await _tvdb.GetAsync("tvdb", "season_extended", ProviderApiClient.Args(("id", tvdbId)), ct).ConfigureAwait(false),
            _ => await _tvdb.GetAsync("tvdb", "episode_extended", ProviderApiClient.Args(("id", tvdbId)), ct).ConfigureAwait(false),
        };
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var held = 0;
        var preferred = 0;
        if (detail is not null)
        {
            var typeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (scopeId != "episode" && await _tvdb.GetAsync("tvdb", "artwork_types", null, ct).ConfigureAwait(false) is JsonArray types)
            {
                foreach (var type in types.Where(value => value is not null))
                {
                    if (type?["id"]?.ToString() is { } id && type?["name"]?.ToString() is { } name)
                    {
                        typeNames[id] = name;
                    }
                }
            }

            var art = scopeId == "episode"
                ? new[] { (Url: detail["image"]?.ToString(), Type: (AssetType?)AssetType.EpisodeStill) }
                : detail["artworks"] is JsonArray artworks
                    ? artworks.Where(node => node is not null)
                        .Select(node => (Url: node!["image"]?.ToString(),
                            Type: ResolveTvdbArtworkType(node!, scopeId, typeNames)))
                        .Where(item => item.Type.HasValue)
                        .GroupBy(item => item.Type!.Value)
                        .SelectMany(group => group.Take(MaxVariantsPerAssetType)).ToArray()
                    : [];
            foreach (var (url, type) in art)
            {
                if (!type.HasValue || !IsSafeTvdbImageUrl(url))
                {
                    continue;
                }
                var processed = await ProcessRemoteImageAsync(url!, type.Value, ownerWorkId, ct, "tvdb")
                    .ConfigureAwait(false);
                AddCount(counts, type.Value, processed.StoredCount);
                held += processed.HeldCount;
                preferred += processed.UpdatedPreferredCount;
            }
            if (scopeId != "episode" && held == 0
                && IsSafeTvdbImageUrl(detail["image"]?.ToString()))
            {
                var fallbackType = scopeId == "season" ? AssetType.SeasonPoster : AssetType.CoverArt;
                var processed = await ProcessRemoteImageAsync(detail["image"]!.ToString(), fallbackType,
                    ownerWorkId, ct, "tvdb").ConfigureAwait(false);
                AddCount(counts, fallbackType, processed.StoredCount);
                held += processed.HeldCount;
                preferred += processed.UpdatedPreferredCount;
            }
        }
        return new ImageEnrichmentResult
        {
            Provider = "tvdb",
            ProviderName = "TheTVDB",
            Status = held > 0 || preferred > 0 ? "Completed" : "NoImages",
            MediaType = "TV",
            BridgeKey = scopeId switch
            {
                "series" => BridgeIdKeys.TvdbId,
                "season" => BridgeIdKeys.TvdbSeasonId,
                _ => BridgeIdKeys.TvdbEpisodeId,
            },
            BridgeId = tvdbId,
            LastCheckedAt = DateTimeOffset.UtcNow,
            DownloadedCount = counts.Values.Sum(),
            HeldCount = held,
            UpdatedPreferredCount = preferred,
            StoredVariantCounts = counts,
            Message = counts.Values.Sum() > 0
                ? $"Stored {counts.Values.Sum()} managed TheTVDB image(s)."
                : held > 0
                    ? "TheTVDB artwork for this scope is already stored."
                    : "TheTVDB returned no usable images for this scope.",
        };
    }

    private static bool IsSafeTvdbImageUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var url)
        && url.Scheme == Uri.UriSchemeHttps
        && (url.Host.Equals("thetvdb.com", StringComparison.OrdinalIgnoreCase)
            || url.Host.EndsWith(".thetvdb.com", StringComparison.OrdinalIgnoreCase));

    private async Task<ImageEnrichmentResult> EnrichWorkImagesCoreAsync(
        Guid assetId,
        string? workQid,
        bool forceRefresh,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var checkedAt = DateTimeOffset.UtcNow;
        var context = await ResolveContextAsync(assetId, ct).ConfigureAwait(false);
        var mediaType = context.MediaType.ToString();
        if (context.MediaType is not (MediaType.Movies or MediaType.TV))
        {
            return await PersistDiagnosticsAsync(context, CreateResult("Skipped", checkedAt, mediaType,
                    skippedReason: "unsupported_media_type", message: "TMDB artwork enrichment supports movies and TV only."), ct);
        }

        var canonicals = await LoadCanonicalsAsync(context, ct).ConfigureAwait(false);
        if (context.MediaType == MediaType.TV && _tvdb?.IsConfigured("tvdb") == true)
        {
            var source = GetValue(canonicals, MetadataFieldConstants.IdentityProvider);
            var tvdbId = GetValue(canonicals, BridgeIdKeys.TvdbId);
            var episodeId = GetValue(canonicals, BridgeIdKeys.TvdbEpisodeId);
            if (!string.IsNullOrWhiteSpace(tvdbId)
                && (string.Equals(source, "tvdb", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(episodeId)))
            {
                return await EnrichTvdbImagesAsync(context, canonicals, tvdbId, checkedAt, ct).ConfigureAwait(false);
            }
        }
        if (context.MediaType == MediaType.TV)
        {
            return await PersistDiagnosticsAsync(context, new ImageEnrichmentResult
            {
                Provider = "tvdb",
                ProviderName = "TheTVDB",
                Status = "Skipped",
                MediaType = "TV",
                LastCheckedAt = checkedAt,
                SkippedReason = "missing_tvdb_match_or_connection",
                Message = "Match this show to TheTVDB and connect it in Settings before refreshing TV artwork.",
            }, ct).ConfigureAwait(false);
        }
        var tmdbId = GetValue(canonicals, BridgeIdKeys.TmdbId);
        if (string.IsNullOrWhiteSpace(tmdbId))
        {
            return await PersistDiagnosticsAsync(context, CreateResult("Skipped", checkedAt, mediaType,
                    skippedReason: "missing_bridge_id", message: "This item needs a TMDB ID before artwork can be refreshed."), ct);
        }

        var apiKey = await ResolveTmdbApiKeyAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return await PersistDiagnosticsAsync(context, CreateResult("Skipped", checkedAt, mediaType, BridgeIdKeys.TmdbId, tmdbId,
                    skippedReason: "missing_api_key", message: "TMDB is not configured."), ct);
        }

        var metadataLanguage = ResolveMetadataLanguage();

        var endpoint = context.MediaType == MediaType.Movies
            ? $"{TmdbApiBaseUrl}/movie/{Uri.EscapeDataString(tmdbId)}/images"
            : $"{TmdbApiBaseUrl}/tv/{Uri.EscapeDataString(tmdbId)}/images";
        var rootAlreadyChecked = !forceRefresh && LastPassAnsweredFor(canonicals, tmdbId);
        var response = rootAlreadyChecked
            ? (Json: (JsonNode?)null, Status: "Completed", HttpStatusCode: (int?)null, SkippedReason: (string?)null, Message: (string?)null)
            : await GetImagesAsync(endpoint, apiKey, metadataLanguage, ct).ConfigureAwait(false);
        if (!rootAlreadyChecked && response.Json is null)
        {
            return await PersistDiagnosticsAsync(context, CreateResult(response.Status, checkedAt, mediaType, BridgeIdKeys.TmdbId, tmdbId,
                    endpoint, response.HttpStatusCode, response.SkippedReason, response.Message), ct);
        }

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var held = 0;
        var preferredUpdates = 0;
        var owner = context.MediaType == MediaType.TV ? context.RootWorkId : context.SelfWorkId;
        var reidentified = !rootAlreadyChecked && HeldArtworkIsForAnotherIdentity(canonicals, TmdbProviderName, tmdbId);
        foreach (var mapping in RootMappings)
        {
            if (rootAlreadyChecked)
            {
                // A repeat pass skips the provider call; report what the earlier pass left in place
                // so the diagnostics do not flip to NoImages depending on which pass ran last.
                held += await CountHeldVariantsAsync(owner, mapping.AssetType, TmdbProviderName, ct).ConfigureAwait(false);
                continue;
            }

            var imageNodes = response.Json?[mapping.JsonField]?.AsArray() ?? [];
            var processed = await ProcessRankedImagesAsync(imageNodes, mapping.AssetType,
                owner, mapping.UpdatePreferred, metadataLanguage, ct).ConfigureAwait(false);
            if (mapping.AssetType == AssetType.CoverArt && processed.PreferredLocalPath is not null)
            {
                await RecordPosterChoiceAsync(owner, TmdbProviderName, tmdbId, ct).ConfigureAwait(false);
            }
            AddCount(counts, mapping.AssetType, processed.StoredCount);
            held += processed.HeldCount;
            preferredUpdates += processed.UpdatedPreferredCount;
            if (reidentified)
            {
                var offered = imageNodes
                    .Select(node => node?["file_path"]?.GetValue<string>())
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => BuildImageUrl(path!));
                await RemoveEarlierIdentityArtworkAsync(owner, mapping.AssetType, offered,
                    GetValue(canonicals, MetadataFieldConstants.Cover), ct).ConfigureAwait(false);
            }
        }

        foreach (var mapping in BrandMappings)
        {
            var imageUrl = await ResolveBrandImageUrlAsync(GetValue(canonicals, mapping.CanonicalKey), mapping.AssetType, owner, ct)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                continue;
            }

            var processed = await ProcessRemoteImageAsync(imageUrl, mapping.AssetType, owner, ct).ConfigureAwait(false);
            AddCount(counts, mapping.AssetType, processed.StoredCount);
            held += processed.HeldCount;
            preferredUpdates += processed.UpdatedPreferredCount;
        }

        if (context.MediaType == MediaType.TV)
        {
            foreach (var season in await ResolveRepresentedSeasonsAsync(context, ct).ConfigureAwait(false))
            {
                var seasonCanonicals = await _canonicalRepo.GetByEntityAsync(season.WorkId, ct).ConfigureAwait(false);
                var seasonSource = seasonCanonicals.FirstOrDefault(value =>
                    string.Equals(value.Key, MetadataFieldConstants.IdentityProvider, StringComparison.OrdinalIgnoreCase))?.Value;
                var tvdbSeasonId = seasonCanonicals.FirstOrDefault(value =>
                    string.Equals(value.Key, BridgeIdKeys.TvdbSeasonId, StringComparison.OrdinalIgnoreCase))?.Value;
                if (string.Equals(seasonSource, "tvdb", StringComparison.OrdinalIgnoreCase))
                {
                    if (_tvdb?.IsConfigured("tvdb") == true && !string.IsNullOrWhiteSpace(tvdbSeasonId))
                    {
                        var seasonResult = await RefreshTvdbScopeImagesAsync(season.WorkId, "season", tvdbSeasonId, ct)
                            .ConfigureAwait(false);
                        MergeCounts(counts, seasonResult.StoredVariantCounts);
                        held += seasonResult.HeldCount;
                        preferredUpdates += seasonResult.UpdatedPreferredCount;
                    }
                    continue;
                }
                if (!forceRefresh && seasonCanonicals.Any(value =>
                        string.Equals(value.Key, "tmdb_season_artwork_last_checked_at", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var seasonEndpoint = $"{TmdbApiBaseUrl}/tv/{Uri.EscapeDataString(tmdbId)}/season/{season.SeasonNumber}/images";
                var seasonResponse = await GetImagesAsync(seasonEndpoint, apiKey, metadataLanguage, ct).ConfigureAwait(false);
                if (seasonResponse.Json is null)
                {
                    continue;
                }

                foreach (var mapping in SeasonMappings)
                {
                    var processed = await ProcessRankedImagesAsync(
                        seasonResponse.Json[mapping.JsonField]?.AsArray() ?? [],
                        mapping.AssetType,
                        season.WorkId,
                        updatePreferred: true,
                        metadataLanguage,
                        ct).ConfigureAwait(false);
                    AddCount(counts, mapping.AssetType, processed.StoredCount);
                    held += processed.HeldCount;
                    preferredUpdates += processed.UpdatedPreferredCount;
                }

                await _canonicalRepo.UpsertBatchAsync(
                [
                    new CanonicalValue
                    {
                        EntityId = season.WorkId,
                        Key = "tmdb_season_artwork_last_checked_at",
                        Value = checkedAt.ToString("O", CultureInfo.InvariantCulture),
                        LastScoredAt = checkedAt,
                        WinningProviderId = WellKnownProviders.Tmdb,
                    },
                ], ct).ConfigureAwait(false);
            }

            var ownCanonicals = await _canonicalRepo.GetByEntityAsync(context.SelfWorkId, ct).ConfigureAwait(false);
            if (string.Equals(ownCanonicals.FirstOrDefault(value =>
                    string.Equals(value.Key, MetadataFieldConstants.IdentityProvider, StringComparison.OrdinalIgnoreCase))?.Value,
                    "tvdb", StringComparison.OrdinalIgnoreCase)
                && ownCanonicals.FirstOrDefault(value =>
                    string.Equals(value.Key, BridgeIdKeys.TvdbEpisodeId, StringComparison.OrdinalIgnoreCase))?.Value is { } tvdbEpisodeId
                && _tvdb?.IsConfigured("tvdb") == true)
            {
                var episodeResult = await RefreshTvdbScopeImagesAsync(context.SelfWorkId, "episode", tvdbEpisodeId, ct)
                    .ConfigureAwait(false);
                MergeCounts(counts, episodeResult.StoredVariantCounts);
                held += episodeResult.HeldCount;
                preferredUpdates += episodeResult.UpdatedPreferredCount;
            }
        }

        var downloaded = counts.Values.Sum();
        return await PersistDiagnosticsAsync(context, CreateResult(held > 0 || preferredUpdates > 0 ? "Completed" : "NoImages",
            checkedAt, mediaType, BridgeIdKeys.TmdbId, tmdbId, endpoint, response.HttpStatusCode,
            message: downloaded > 0 ? $"Stored {downloaded} TMDB artwork variant(s)."
                : held > 0 ? "TMDB artwork is already stored." : "TMDB returned no compatible artwork variants.",
            storedCounts: counts, updatedPreferredCount: preferredUpdates, heldCount: held), ct);
    }

    /// <summary>
    /// A repeat pass may skip the provider call only when the last pass got TMDB's answer for the item's
    /// current id. After re-identification the stored artwork belongs to the earlier film, and a pass that
    /// was skipped or failed has nothing to repeat, so both call TMDB again.
    /// </summary>
    private static bool LastPassAnsweredFor(IReadOnlyDictionary<string, string> canonicals, string tmdbId) =>
        !string.IsNullOrWhiteSpace(GetValue(canonicals, "tmdb_artwork_last_checked_at"))
        && string.Equals(GetValue(canonicals, "tmdb_artwork_bridge_id"), tmdbId, StringComparison.OrdinalIgnoreCase)
        && GetValue(canonicals, "tmdb_artwork_status") is "Completed" or "NoImages" or "NoResult";

    /// <summary>
    /// True when the provider artwork held for the item was found for a different bridge id than the current
    /// one: the item was re-identified without a manual rematch (a rematch replaces the artwork itself).
    /// </summary>
    private static bool HeldArtworkIsForAnotherIdentity(IReadOnlyDictionary<string, string> canonicals, string provider, string bridgeId) =>
        new[] { GetValue(canonicals, $"{provider}_artwork_bridge_id"), GetValue(canonicals, ArtworkCanonicalHelper.PosterChoiceKey(provider)) }
            .Any(earlier => !string.IsNullOrWhiteSpace(earlier) && !string.Equals(earlier, bridgeId, StringComparison.OrdinalIgnoreCase));

    private static readonly ArtworkMapping[] RootMappings =
    [new("backdrops", AssetType.Background, true), new("logos", AssetType.Logo, true), new("posters", AssetType.CoverArt, true)];
    private static readonly ArtworkMapping[] SeasonMappings =
    [new("posters", AssetType.SeasonPoster, true), new("backdrops", AssetType.SeasonThumb, true)];
    private static readonly BrandArtworkMapping[] BrandMappings =
    [new("network_logo_url", AssetType.NetworkLogo), new("studio_logo_url", AssetType.StudioLogo)];

    private async Task<ImageEnrichmentResult> EnrichTvdbImagesAsync(
        ArtworkContext context, IReadOnlyDictionary<string, string> canonicals, string showId, DateTimeOffset checkedAt,
        CancellationToken ct)
    {
        var types = await _tvdb!.GetAsync("tvdb", "artwork_types", null, ct).ConfigureAwait(false);
        var typeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (types is JsonArray typeArray)
        {
            foreach (var type in typeArray.Where(value => value is not null))
            {
                if (type?["id"]?.ToString() is { } id && type?["name"]?.ToString() is { } name)
                {
                    typeNames[id] = name;
                }
            }
        }

        var show = await _tvdb.GetAsync("tvdb", "series_extended", ProviderApiClient.Args(("id", showId)), ct).ConfigureAwait(false);
        if (show is null)
        {
            return await PersistDiagnosticsAsync(context, new ImageEnrichmentResult
            {
                Provider = "tvdb",
                ProviderName = "TheTVDB",
                Status = "NoResult",
                MediaType = "TV",
                BridgeKey = BridgeIdKeys.TvdbId,
                BridgeId = showId,
                LastCheckedAt = checkedAt,
                SkippedReason = "provider_no_result",
                Message = "TheTVDB show could not be found.",
            }, ct);
        }

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var heldTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var held = 0;
        var showHeld = 0;
        var preferred = 0;
        var offered = new Dictionary<(Guid Owner, AssetType Type), List<string>>();
        async Task AddAsync(string? value, AssetType type, Guid owner)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || !(uri.Host.Equals("thetvdb.com", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.EndsWith(".thetvdb.com", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }
            if (!offered.TryGetValue((owner, type), out var urls))
            {
                offered[(owner, type)] = urls = [];
            }
            urls.Add(uri.ToString());
            var processed = await ProcessRemoteImageAsync(uri.ToString(), type, owner, ct, "tvdb")
                .ConfigureAwait(false);
            AddCount(counts, type, processed.StoredCount);
            if (processed.HeldCount > 0)
            {
                heldTypes.Add(type.ToString());
            }
            held += processed.HeldCount;
            showHeld += owner == context.RootWorkId ? processed.HeldCount : 0;
            preferred += processed.UpdatedPreferredCount;
        }
        async Task AddArtworksAsync(JsonNode? record, string scope, Guid owner)
        {
            if (record?["artworks"] is not JsonArray artworks)
            {
                return;
            }
            var selected = artworks.Where(node => node is not null)
                .Select(node => (Node: node!, Type: ResolveTvdbArtworkType(node!, scope, typeNames)))
                .Where(item => item.Type.HasValue)
                .GroupBy(item => item.Type!.Value)
                .SelectMany(group => group.Take(MaxVariantsPerAssetType));
            foreach (var item in selected)
            {
                await AddAsync(item.Node["image"]?.ToString(), item.Type!.Value, owner).ConfigureAwait(false);
            }
        }

        await AddArtworksAsync(show, "series", context.RootWorkId).ConfigureAwait(false);
        if (!heldTypes.Contains(AssetType.CoverArt.ToString()))
        {
            await AddAsync(show["image"]?.ToString(), AssetType.CoverArt, context.RootWorkId).ConfigureAwait(false);
        }
        if (heldTypes.Contains(AssetType.CoverArt.ToString()))
        {
            await RecordPosterChoiceAsync(context.RootWorkId, "tvdb", showId, ct).ConfigureAwait(false);
        }

        foreach (var season in await ResolveRepresentedSeasonsAsync(context, ct).ConfigureAwait(false))
        {
            var seasonValues = await _canonicalRepo.GetByEntityAsync(season.WorkId, ct).ConfigureAwait(false);
            var seasonSource = seasonValues.FirstOrDefault(value =>
                string.Equals(value.Key, MetadataFieldConstants.IdentityProvider, StringComparison.OrdinalIgnoreCase))?.Value;
            if (!string.IsNullOrWhiteSpace(seasonSource)
                && !string.Equals(seasonSource, "tvdb", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var storedSeasonId = seasonValues.FirstOrDefault(value => value.Key == BridgeIdKeys.TvdbSeasonId)?.Value;
            var showSeasons = show["seasons"] as JsonArray;
            // A season id found under the show's earlier identity is not one of this show's seasons.
            var seasonId = storedSeasonId is not null
                && (showSeasons is not { Count: > 0 } || showSeasons.Any(node => node?["id"]?.ToString() == storedSeasonId))
                    ? storedSeasonId
                    : showSeasons?.FirstOrDefault(node =>
                        node?["number"]?.ToString() == season.SeasonNumber.ToString(CultureInfo.InvariantCulture)
                        && (show["defaultSeasonType"] is null || node?["type"]?["id"]?.ToString()
                            == show["defaultSeasonType"]?.ToString()))?["id"]?.ToString();
            if (string.IsNullOrWhiteSpace(seasonId))
            {
                continue;
            }
            if (!string.Equals(seasonId, storedSeasonId, StringComparison.Ordinal))
            {
                await _canonicalRepo.UpsertBatchAsync([new CanonicalValue
                    {
                        EntityId = season.WorkId, Key = BridgeIdKeys.TvdbSeasonId,
                        Value = seasonId, WinningProviderId = WellKnownProviders.Tvdb,
                        LastScoredAt = checkedAt,
                    }], ct).ConfigureAwait(false);
            }
            var detail = await _tvdb.GetAsync("tvdb", "season_extended", ProviderApiClient.Args(("id", seasonId)), ct).ConfigureAwait(false);
            await AddArtworksAsync(detail, "season", season.WorkId).ConfigureAwait(false);
            if (!heldTypes.Contains(AssetType.SeasonPoster.ToString()))
            {
                await AddAsync(detail?["image"]?.ToString(), AssetType.SeasonPoster, season.WorkId).ConfigureAwait(false);
            }
        }

        if (HeldArtworkIsForAnotherIdentity(canonicals, "tvdb", showId))
        {
            // The show was identified as a different show; the earlier show's artwork and its seasons' artwork go.
            var scopes = new List<(Guid Owner, AssetType Type)>
            {
                (context.RootWorkId, AssetType.CoverArt), (context.RootWorkId, AssetType.Background), (context.RootWorkId, AssetType.Logo),
            };
            scopes.AddRange(offered.Keys.Where(key => key.Owner != context.RootWorkId
                && key.Type is AssetType.SeasonPoster or AssetType.SeasonThumb));
            foreach (var (owner, type) in scopes.Distinct())
            {
                await RemoveEarlierIdentityArtworkAsync(owner, type, offered.GetValueOrDefault((owner, type)) ?? [],
                    GetValue(canonicals, MetadataFieldConstants.Cover), ct).ConfigureAwait(false);
            }
        }

        var own = await _canonicalRepo.GetByEntityAsync(context.SelfWorkId, ct).ConfigureAwait(false);
        var ownSource = own.FirstOrDefault(value =>
            string.Equals(value.Key, MetadataFieldConstants.IdentityProvider, StringComparison.OrdinalIgnoreCase))?.Value;
        var episodeId = own.FirstOrDefault(value => value.Key == BridgeIdKeys.TvdbEpisodeId)?.Value;
        if (!string.IsNullOrWhiteSpace(episodeId)
            && (string.IsNullOrWhiteSpace(ownSource)
                || string.Equals(ownSource, "tvdb", StringComparison.OrdinalIgnoreCase)))
        {
            var episode = await _tvdb.GetAsync("tvdb", "episode_extended", ProviderApiClient.Args(("id", episodeId)), ct).ConfigureAwait(false);
            if (episode?["seriesId"]?.ToString() == showId)
            {
                await AddAsync(episode["image"]?.ToString(), AssetType.EpisodeStill,
                        context.SelfWorkId).ConfigureAwait(false);
            }
        }

        return await PersistDiagnosticsAsync(context, new ImageEnrichmentResult
        {
            Provider = "tvdb",
            ProviderName = "TheTVDB",
            Status = held > 0 ? "Completed" : "NoImages",
            MediaType = "TV",
            BridgeKey = BridgeIdKeys.TvdbId,
            BridgeId = showId,
            Endpoint = "/series/{id}/extended",
            LastCheckedAt = checkedAt,
            DownloadedCount = counts.Values.Sum(),
            HeldCount = held,
            UpdatedPreferredCount = preferred,
            StoredVariantCounts = counts,
            Message = counts.Values.Sum() > 0 ? "TheTVDB artwork was stored as managed images."
                : held > 0 ? "TheTVDB artwork for the owned TV scopes is already stored."
                : "TheTVDB returned no compatible images for the owned TV scopes.",
        }, ct, rootHeldCount: showHeld);
    }

    private static AssetType? ResolveTvdbArtworkType(JsonNode artwork, string scope,
        IReadOnlyDictionary<string, string> names)
    {
        var typeId = artwork["type"]?.ToString() ?? artwork["imageType"]?.ToString() ?? string.Empty;
        var typeObjectName = artwork["type"] is JsonObject typeObject ? typeObject["name"]?.ToString() : null;
        var name = (names.GetValueOrDefault(typeId) ?? typeObjectName ?? typeId)
            .ToLowerInvariant();
        if (scope == "series")
        {
            if (name.Contains("logo"))
            {
                return AssetType.Logo;
            }
            if (name.Contains("background") || name.Contains("fanart"))
            {
                return AssetType.Background;
            }
            if (name.Contains("poster"))
            {
                return AssetType.CoverArt;
            }
        }
        else if (scope == "season")
        {
            if (name.Contains("background"))
            {
                return AssetType.SeasonThumb;
            }
            if (name.Contains("poster"))
            {
                return AssetType.SeasonPoster;
            }
        }
        return null;
    }

    private async Task<ImageAssetProcessingResult> ProcessRankedImagesAsync(IEnumerable<JsonNode?> imageNodes, AssetType assetType,
        Guid ownerEntityId, bool updatePreferred, string metadataLanguage, CancellationToken ct)
    {
        var candidates = imageNodes.Where(node => node is not null && !string.IsNullOrWhiteSpace(node!["file_path"]?.GetValue<string>()))
            .Where(node => IsCompatibleImage(node!, assetType))
            .ToList();
        var useConfiguredPosterLanguage = assetType is AssetType.CoverArt or AssetType.SeasonPoster
            && candidates.Any(node => string.Equals(
                node!["iso_639_1"]?.GetValue<string>(),
                metadataLanguage,
                StringComparison.OrdinalIgnoreCase));
        var ranked = candidates
            .Where(node => IsAllowedLanguage(node!["iso_639_1"]?.GetValue<string>(), assetType, metadataLanguage))
            .Where(node => !useConfiguredPosterLanguage
                || string.Equals(
                    node!["iso_639_1"]?.GetValue<string>(),
                    metadataLanguage,
                    StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(node => LanguageRank(node!["iso_639_1"]?.GetValue<string>(), assetType, metadataLanguage))
            .ThenByDescending(node => node!["vote_average"]?.GetValue<double?>() ?? 0)
            .ThenByDescending(node => node!["vote_count"]?.GetValue<int?>() ?? 0)
            .ThenByDescending(node => (node!["width"]?.GetValue<int?>() ?? 0) * (node!["height"]?.GetValue<int?>() ?? 0))
            .ToList();
        if (ranked.Count == 0)
        {
            return ImageAssetProcessingResult.Empty;
        }

        var variants = (await _assetRepo.GetByEntityAsync(ownerEntityId.ToString(), assetType.ToString(), ct)).ToList();
        var currentPreferred = variants.FirstOrDefault(asset => asset.IsPreferred)?.Id;
        EntityAsset? preferred = updatePreferred ? variants.FirstOrDefault(asset => asset.IsPreferred && asset.IsUserOverride) : null;
        var stored = 0;
        var accepted = 0;
        foreach (var node in ranked)
        {
            if (accepted >= MaxVariantsPerAssetType)
            {
                break;
            }

            var url = BuildImageUrl(node!["file_path"]!.GetValue<string>());
            await using var lease = await _imageDownloadCoordinator.AcquireAsync(url, ct).ConfigureAwait(false);
            var existing = variants.FirstOrDefault(asset => string.Equals(asset.ImageUrl, url, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                // Another pass over the same owner may have stored this image while we waited for the lease.
                variants = (await _assetRepo.GetByEntityAsync(ownerEntityId.ToString(), assetType.ToString(), ct)).ToList();
                existing = variants.FirstOrDefault(asset => string.Equals(asset.ImageUrl, url, StringComparison.OrdinalIgnoreCase));
            }
            if (existing is not null && !string.IsNullOrWhiteSpace(existing.LocalImagePath) && File.Exists(existing.LocalImagePath))
            {
                if (assetType == AssetType.Logo)
                {
                    var existingBytes = await File.ReadAllBytesAsync(existing.LocalImagePath, ct).ConfigureAwait(false);
                    if (!IsUsableDownloadedImage(existingBytes, assetType, out var existingRejectionReason))
                    {
                        _logger.LogWarning(
                            "Ignoring unusable TMDB {AssetType} candidate {Url}: {Reason}",
                            assetType,
                            url,
                            existingRejectionReason);
                        continue;
                    }
                }

                accepted++;
                if (updatePreferred && preferred is null && !existing.IsUserOverride)
                {
                    preferred = existing;
                }
                continue;
            }
            var bytes = await GetCachedOrDownloadAsync(url, ct).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0)
            {
                continue;
            }
            if (!IsUsableDownloadedImage(bytes, assetType, out var rejectionReason))
            {
                _logger.LogWarning(
                    "Rejected unusable TMDB {AssetType} candidate {Url}: {Reason}",
                    assetType,
                    url,
                    rejectionReason);
                continue;
            }

            var variant = existing ?? new EntityAsset
            {
                Id = Guid.NewGuid(),
                EntityId = ownerEntityId.ToString(),
                EntityType = "Work",
                AssetTypeValue = assetType.ToString(),
                ImageUrl = url,
                SourceProvider = TmdbProviderName,
                AssetClassValue = "Artwork",
                StorageLocationValue = "Central",
                OwnerScope = OwnerScope(assetType),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            variant.LocalImagePath ??= _assetPaths.GetCentralAssetPath("Work", ownerEntityId, assetType.ToString(), variant.Id, InferExtension(url));
            await PersistImageAsync(bytes, variant.LocalImagePath, url, ct).ConfigureAwait(false);
            ArtworkVariantHelper.StampMetadataAndRenditions(variant, _assetPaths);
            await _assetRepo.UpsertAsync(variant, ct).ConfigureAwait(false);
            if (existing is null)
            {
                variants.Add(variant);
            }
            else
            {
                variants[variants.IndexOf(existing)] = variant;
            }
            stored++;
            accepted++;
            if (updatePreferred && preferred is null)
            {
                preferred = variant;
            }
        }
        if (!updatePreferred || preferred is null)
        {
            return new ImageAssetProcessingResult(preferred?.LocalImagePath, stored, accepted, 0);
        }
        await _assetRepo.SetPreferredAsync(preferred.Id, ct).ConfigureAwait(false);
        await PersistPreferredAssetCanonicalsAsync(ownerEntityId, preferred, ct).ConfigureAwait(false);
        if (_assetExportService is not null)
        {
            await _assetExportService.ReconcileArtworkAsync(preferred.EntityId, preferred.EntityType, preferred.AssetTypeValue, ct).ConfigureAwait(false);
        }
        return new ImageAssetProcessingResult(preferred.LocalImagePath, stored, accepted, currentPreferred == preferred.Id ? 0 : 1);
    }

    private async Task<ImageAssetProcessingResult> ProcessRemoteImageAsync(
        string url,
        AssetType assetType,
        Guid ownerEntityId,
        CancellationToken ct,
        string sourceProvider = TmdbProviderName)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return ImageAssetProcessingResult.Empty;
        }

        var variants = (await _assetRepo.GetByEntityAsync(ownerEntityId.ToString(), assetType.ToString(), ct)).ToList();
        var currentPreferred = variants.FirstOrDefault(asset => asset.IsPreferred);

        var existing = variants.FirstOrDefault(asset => string.Equals(asset.ImageUrl, url, StringComparison.OrdinalIgnoreCase));
        var stored = 0;
        if (!IsHeld(existing))
        {
            await using var lease = await _imageDownloadCoordinator.AcquireAsync(url, ct).ConfigureAwait(false);

            // Another pass over the same owner (a sibling episode sharing the show) may have stored
            // this image while we waited for the lease; reuse its row instead of adding a duplicate.
            variants = (await _assetRepo.GetByEntityAsync(ownerEntityId.ToString(), assetType.ToString(), ct)).ToList();
            currentPreferred = variants.FirstOrDefault(asset => asset.IsPreferred);
            existing = variants.FirstOrDefault(asset => string.Equals(asset.ImageUrl, url, StringComparison.OrdinalIgnoreCase));
            if (!IsHeld(existing))
            {
                var bytes = await GetCachedOrDownloadAsync(url, ct).ConfigureAwait(false);
                if (bytes is null || bytes.Length == 0)
                {
                    return ImageAssetProcessingResult.Empty;
                }

                existing = await StoreRemoteImageAsync(existing, bytes, url, assetType, ownerEntityId, sourceProvider, ct)
                    .ConfigureAwait(false);
                stored = 1;
            }
        }

        if (currentPreferred?.IsUserOverride == true)
        {
            return new ImageAssetProcessingResult(existing!.LocalImagePath, stored, 1, 0);
        }

        await _assetRepo.SetPreferredAsync(existing!.Id, ct).ConfigureAwait(false);
        await PersistPreferredAssetCanonicalsAsync(ownerEntityId, existing, ct).ConfigureAwait(false);
        return new ImageAssetProcessingResult(existing.LocalImagePath, stored, 1, currentPreferred?.Id == existing.Id ? 0 : 1);
    }

    /// <summary>
    /// Records which identity this pass chose the owner's poster for, so a later cover pass keeps that poster
    /// only while the item still has that identity.
    /// </summary>
    private Task RecordPosterChoiceAsync(Guid ownerEntityId, string provider, string bridgeId, CancellationToken ct) =>
        _canonicalRepo.UpsertBatchAsync(
        [
            new CanonicalValue
            {
                EntityId = ownerEntityId,
                Key = ArtworkCanonicalHelper.PosterChoiceKey(provider),
                Value = bridgeId,
                LastScoredAt = DateTimeOffset.UtcNow,
                WinningProviderId = provider == "tvdb" ? WellKnownProviders.Tvdb : WellKnownProviders.Tmdb,
            },
        ], ct);

    /// <summary>
    /// A stored brand logo replaces its provider address in the canonical with the stored copy's
    /// <c>/stream/artwork/{id}</c> address. A repeat pass maps that back to the copy's provider URL so it
    /// counts the same held logo instead of skipping it.
    /// </summary>
    private async Task<string?> ResolveBrandImageUrlAsync(string? value, AssetType assetType, Guid ownerEntityId, CancellationToken ct)
    {
        if (!ArtworkCanonicalHelper.TryParseStoredArtworkId(value, out var storedId))
        {
            return value;
        }

        var stored = (await _assetRepo.GetByEntityAsync(ownerEntityId.ToString(), assetType.ToString(), ct).ConfigureAwait(false))
            .FirstOrDefault(asset => asset.Id == storedId);
        return stored?.ImageUrl;
    }

    /// <summary>
    /// After re-identification, removes the owner's provider artwork of <paramref name="assetType"/> that the
    /// current identity did not offer. Uploads, user picks and local covers (embedded or folder art) stay, as
    /// does the retail cover for the current identity (<paramref name="currentCoverUrl"/>). If the preferred
    /// variant goes and nothing new replaced it, the next best survivor takes over.
    /// </summary>
    private async Task RemoveEarlierIdentityArtworkAsync(Guid ownerEntityId, AssetType assetType, IEnumerable<string> offeredUrls,
        string? currentCoverUrl, CancellationToken ct)
    {
        var keep = new HashSet<string>(offeredUrls, StringComparer.OrdinalIgnoreCase);
        if (assetType == AssetType.CoverArt && !string.IsNullOrWhiteSpace(currentCoverUrl))
        {
            keep.Add(currentCoverUrl);
        }

        var variants = await _assetRepo.GetByEntityAsync(ownerEntityId.ToString(), assetType.ToString(), ct).ConfigureAwait(false);
        var earlier = variants
            .Where(asset => string.Equals(asset.AssetClassValue, "Artwork", StringComparison.OrdinalIgnoreCase)
                && !asset.IsUserOverride
                && !string.Equals(asset.SourceProvider, "user_upload", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(asset.ImageUrl, UriKind.Absolute, out var url) && url.Scheme is "http" or "https"
                && !keep.Contains(asset.ImageUrl))
            .ToList();
        if (earlier.Count == 0)
        {
            return;
        }

        foreach (var asset in earlier)
        {
            await _assetRepo.DeleteAsync(asset.Id, ct).ConfigureAwait(false);
            ArtworkVariantHelper.DeleteManagedFiles(asset, _assetPaths, _logger);
        }
        _logger.LogInformation("Removed {Count} {AssetType} variant(s) of an earlier identity from {OwnerEntityId}",
            earlier.Count, assetType, ownerEntityId);

        if (!earlier.Any(asset => asset.IsPreferred))
        {
            return;
        }

        var survivors = variants.Except(earlier).ToList();
        var survivor = survivors.FirstOrDefault(asset => asset.IsUserOverride)
            ?? survivors.FirstOrDefault(asset => string.Equals(asset.ImageUrl, currentCoverUrl, StringComparison.OrdinalIgnoreCase))
            ?? survivors.FirstOrDefault();
        if (survivor is not null)
        {
            await _assetRepo.SetPreferredAsync(survivor.Id, ct).ConfigureAwait(false);
            await PersistPreferredAssetCanonicalsAsync(ownerEntityId, survivor, ct).ConfigureAwait(false);
            if (_assetExportService is not null)
            {
                await _assetExportService.ReconcileArtworkAsync(survivor.EntityId, survivor.EntityType, survivor.AssetTypeValue, ct)
                    .ConfigureAwait(false);
            }
            return;
        }

        foreach (var key in ArtworkCanonicalHelper.DisplayCanonicalKeys(assetType.ToString()))
        {
            await _canonicalRepo.DeleteByKeyAsync(ownerEntityId, key, ct).ConfigureAwait(false);
        }
        if (_assetExportService is not null)
        {
            await _assetExportService.ClearArtworkExportAsync(ownerEntityId.ToString(), "Work", assetType.ToString(), ct)
                .ConfigureAwait(false);
        }
    }

    private async Task<int> CountHeldVariantsAsync(Guid ownerEntityId, AssetType assetType, string sourceProvider, CancellationToken ct)
    {
        var variants = await _assetRepo.GetByEntityAsync(ownerEntityId.ToString(), assetType.ToString(), ct).ConfigureAwait(false);
        return Math.Min(MaxVariantsPerAssetType, variants.Count(asset =>
            string.Equals(asset.SourceProvider, sourceProvider, StringComparison.OrdinalIgnoreCase) && IsHeld(asset)));
    }

    private static bool IsHeld(EntityAsset? asset) =>
        asset is not null && !string.IsNullOrWhiteSpace(asset.LocalImagePath) && File.Exists(asset.LocalImagePath);

    private async Task<EntityAsset> StoreRemoteImageAsync(EntityAsset? existing, byte[] bytes, string url,
        AssetType assetType, Guid ownerEntityId, string sourceProvider, CancellationToken ct)
    {
        existing ??= new EntityAsset
        {
            Id = Guid.NewGuid(),
            EntityId = ownerEntityId.ToString(),
            EntityType = "Work",
            AssetTypeValue = assetType.ToString(),
            ImageUrl = url,
            SourceProvider = sourceProvider,
            AssetClassValue = "Artwork",
            StorageLocationValue = "Central",
            OwnerScope = "Work",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        existing.LocalImagePath ??= _assetPaths.GetCentralAssetPath(
            "Work", ownerEntityId, assetType.ToString(), existing.Id, InferExtension(url));
        await PersistImageAsync(bytes, existing.LocalImagePath, url, ct).ConfigureAwait(false);
        ArtworkVariantHelper.StampMetadataAndRenditions(existing, _assetPaths);
        await _assetRepo.UpsertAsync(existing, ct).ConfigureAwait(false);
        return existing;
    }

    /// <summary>
    /// Writes the display canonicals for a newly preferred asset. A preferred cover also
    /// asserts <c>cover_state=present</c>, so a stale "missing" marker left by a claim-less
    /// cover pass (for example TheTVDB show posters, which have no <c>cover</c> claim)
    /// cannot outlive real artwork.
    /// </summary>
    private async Task PersistPreferredAssetCanonicalsAsync(Guid ownerEntityId, EntityAsset preferred, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var canonicals = ArtworkCanonicalHelper.CreatePreferredAssetCanonicals(ownerEntityId, preferred, now);
        if (string.Equals(preferred.AssetTypeValue, AssetType.CoverArt.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            canonicals.Add(ArtworkCanonicalHelper.Create(ownerEntityId, MetadataFieldConstants.CoverState, "present", now));
            // Keep a more specific source recorded by the cover pass; replace only the
            // reason attached to a "missing" state (none, provider_unavailable, ...).
            var existing = await _canonicalRepo.GetByEntityAsync(ownerEntityId, ct).ConfigureAwait(false);
            var existingState = existing.FirstOrDefault(value =>
                string.Equals(value.Key, MetadataFieldConstants.CoverState, StringComparison.OrdinalIgnoreCase))?.Value;
            var existingSource = existing.FirstOrDefault(value =>
                string.Equals(value.Key, MetadataFieldConstants.CoverSource, StringComparison.OrdinalIgnoreCase))?.Value;
            if (string.IsNullOrWhiteSpace(existingSource)
                || !string.Equals(existingState, "present", StringComparison.OrdinalIgnoreCase))
            {
                canonicals.Add(ArtworkCanonicalHelper.Create(ownerEntityId, MetadataFieldConstants.CoverSource, "provider", now));
            }
        }

        await _canonicalRepo.UpsertBatchAsync(canonicals, ct).ConfigureAwait(false);
    }

    private async Task<(JsonNode? Json, string Status, int? HttpStatusCode, string? SkippedReason, string? Message)> GetImagesAsync(
        string endpoint,
        string apiKey,
        string metadataLanguage,
        CancellationToken ct)
    {
        var url = $"{endpoint}?include_image_language={Uri.EscapeDataString(metadataLanguage)},null&api_key={Uri.EscapeDataString(apiKey)}";
        try
        {
            using var client = _httpFactory.CreateClient(TmdbProviderName);
            using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (null, response.StatusCode == HttpStatusCode.NotFound ? "NoResult" : "Error", (int)response.StatusCode,
                        response.StatusCode == HttpStatusCode.NotFound ? "provider_no_result" : "provider_request_failed", $"TMDB returned {(int)response.StatusCode}.");
            }
            return (await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct).ConfigureAwait(false), "Completed", (int)response.StatusCode, null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TMDB artwork request failed for {Endpoint}", endpoint);
            return (null, "Error", null, "provider_call_failed", "TMDB artwork request failed.");
        }
    }

    private async Task<byte[]?> GetCachedOrDownloadAsync(string url, CancellationToken ct)
    {
        var cached = await _imageCache.FindBySourceUrlAsync(url, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(cached) && File.Exists(cached))
        {
            return await File.ReadAllBytesAsync(cached, ct).ConfigureAwait(false);
        }
        try
        {
            using var client = _httpFactory.CreateClient(TmdbProviderName);
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await BoundedHttpContent.ReadImageAsync(response.Content, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TMDB artwork download failed for {Url}", url);
            return null;
        }
    }

    private async Task PersistImageAsync(byte[] bytes, string destination, string sourceUrl, CancellationToken ct)
    {
        AssetPathService.EnsureDirectory(destination);
        var hash = Hashing.Sha256Hex(bytes);
        var cached = await _imageCache.FindByHashAsync(hash, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(cached) && File.Exists(cached) && !string.Equals(cached, destination, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(cached, destination, true);
        }
        else if (string.IsNullOrWhiteSpace(cached) || !File.Exists(cached))
        {
            await BoundedHttpContent.WriteFileAtomicallyAsync(destination, bytes, ct).ConfigureAwait(false);
        }
        await _imageCache.InsertAsync(hash, destination, sourceUrl, ct).ConfigureAwait(false);
    }

    private async Task<ArtworkContext> ResolveContextAsync(Guid entityId, CancellationToken ct)
    {
        var asset = await _mediaAssetRepo.FindByIdAsync(entityId, ct).ConfigureAwait(false);
        var lineage = await _workRepo.GetLineageByAssetAsync(asset?.Id ?? entityId, ct).ConfigureAwait(false);
        if (asset is null)
        {
            asset = await _mediaAssetRepo.FindFirstByWorkIdAsync(entityId, ct).ConfigureAwait(false);
            if (asset is not null)
            {
                lineage = await _workRepo.GetLineageByAssetAsync(asset.Id, ct).ConfigureAwait(false);
            }
        }
        if (lineage is null)
        {
            return new ArtworkContext(asset?.Id ?? entityId, entityId, entityId, null, null, MediaType.Unknown);
        }
        var own = await _canonicalRepo.GetByEntityAsync(lineage.WorkId, ct).ConfigureAwait(false);
        var seasonValue = own.FirstOrDefault(value => string.Equals(value.Key, MetadataFieldConstants.SeasonNumber, StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrWhiteSpace(seasonValue) && lineage.ParentWorkId is { } parentWorkId)
        {
            var parentCanonicals = await _canonicalRepo.GetByEntityAsync(parentWorkId, ct).ConfigureAwait(false);
            seasonValue = parentCanonicals.FirstOrDefault(value =>
                string.Equals(value.Key, MetadataFieldConstants.SeasonNumber, StringComparison.OrdinalIgnoreCase))?.Value;
        }
        return new ArtworkContext(asset?.Id ?? entityId, lineage.WorkId, lineage.RootParentWorkId, lineage.ParentWorkId,
            int.TryParse(seasonValue, out var season) ? season : null, lineage.MediaType);
    }

    private async Task<Dictionary<string, string>> LoadCanonicalsAsync(ArtworkContext context, CancellationToken ct)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in new[] { context.RootWorkId, context.SelfWorkId, context.AssetId }.Distinct())
        {
            foreach (var canonical in await _canonicalRepo.GetByEntityAsync(id, ct).ConfigureAwait(false))
            {
                if (!string.IsNullOrWhiteSpace(canonical.Key) && !string.IsNullOrWhiteSpace(canonical.Value) && !values.ContainsKey(canonical.Key))
                {
                    values[canonical.Key] = canonical.Value;
                }
            }
        }
        return values;
    }

    private async Task<IReadOnlyList<SeasonArtworkTarget>> ResolveRepresentedSeasonsAsync(
        ArtworkContext context,
        CancellationToken ct)
    {
        var targets = new Dictionary<Guid, SeasonArtworkTarget>();
        foreach (var child in await _workRepo.GetDirectChildrenAsync(context.RootWorkId, ct).ConfigureAwait(false))
        {
            if (child.IsCatalogOnly || child.WorkKind != WorkKind.Parent)
            {
                continue;
            }

            var seasonNumber = child.Ordinal;
            if (!seasonNumber.HasValue)
            {
                var canonicals = await _canonicalRepo.GetByEntityAsync(child.WorkId, ct).ConfigureAwait(false);
                var value = canonicals.FirstOrDefault(candidate =>
                    string.Equals(candidate.Key, MetadataFieldConstants.SeasonNumber, StringComparison.OrdinalIgnoreCase))?.Value;
                seasonNumber = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null;
            }

            if (seasonNumber.HasValue)
            {
                targets[child.WorkId] = new SeasonArtworkTarget(child.WorkId, seasonNumber.Value);
            }
        }

        if (context.SeasonWorkId is { } currentSeasonId
            && context.SeasonNumber is { } currentSeasonNumber)
        {
            targets.TryAdd(currentSeasonId, new SeasonArtworkTarget(currentSeasonId, currentSeasonNumber));
        }

        return targets.Values.OrderBy(target => target.SeasonNumber).ToList();
    }

    private string ResolveMetadataLanguage()
    {
        var configured = _configLoader.LoadCore().Language.Metadata;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return "en";
        }

        return configured.Split('-', '_')[0].Trim().ToLowerInvariant() switch
        {
            "" => "en",
            var language => language,
        };
    }

    private async Task<string?> ResolveTmdbApiKeyAsync(CancellationToken ct)
    {
        var config = _configLoader.LoadProvider(TmdbProviderName);
        if (!string.IsNullOrWhiteSpace(config?.HttpClient?.ApiKeyOverride))
        {
            return config.HttpClient.ApiKeyOverride;
        }
        if (!string.IsNullOrWhiteSpace(config?.HttpClient?.ApiKey))
        {
            return config.HttpClient.ApiKey;
        }
        return await _providerConfigRepo.GetDecryptedValueAsync(WellKnownProviders.Tmdb.ToString(), "api_key", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records the pass on the item, its work and its root. The held count is what the scope holds after
    /// the pass, not what this pass newly downloaded, so concurrent passes over a shared show record the
    /// same value whichever finishes first. When <paramref name="rootHeldCount"/> is given, a root that
    /// differs from the item (a TV show above an episode) records only its own show-level count, so
    /// sibling episodes cannot overwrite it with their per-episode totals.
    /// </summary>
    private async Task<ImageEnrichmentResult> PersistDiagnosticsAsync(ArtworkContext context, ImageEnrichmentResult result,
        CancellationToken ct, int? rootHeldCount = null)
    {
        var now = DateTimeOffset.UtcNow;
        var providerName = string.Equals(result.Provider, "tvdb", StringComparison.OrdinalIgnoreCase) ? "tvdb" : "tmdb";
        var providerId = providerName == "tvdb" ? WellKnownProviders.Tvdb : WellKnownProviders.Tmdb;
        var values = new List<CanonicalValue>();
        foreach (var id in new[] { context.RootWorkId, context.SelfWorkId, context.AssetId }.Distinct())
        {
            var isSeparateRoot = rootHeldCount.HasValue && id == context.RootWorkId && id != context.SelfWorkId;
            var heldCount = isSeparateRoot ? rootHeldCount!.Value : result.HeldCount;
            var status = isSeparateRoot && result.Success ? (heldCount > 0 ? "Completed" : "NoImages") : result.Status;
            AddDiagnostic(values, id, $"{providerName}_artwork_status", status, now, providerId);
            AddDiagnostic(values, id, $"{providerName}_artwork_last_checked_at", result.LastCheckedAt.ToString("O", CultureInfo.InvariantCulture), now, providerId);
            AddDiagnostic(values, id, $"{providerName}_artwork_held_count", heldCount.ToString(CultureInfo.InvariantCulture), now, providerId);
            AddDiagnostic(values, id, $"{providerName}_artwork_bridge_id", result.BridgeId, now, providerId);
            AddDiagnostic(values, id, $"{providerName}_artwork_http_status", result.HttpStatusCode?.ToString(CultureInfo.InvariantCulture), now, providerId);
            AddDiagnostic(values, id, $"{providerName}_artwork_skipped_reason", result.SkippedReason, now, providerId);
        }
        if (values.Count > 0)
        {
            await _canonicalRepo.UpsertBatchAsync(values, ct).ConfigureAwait(false);
        }
        return result;
    }

    private static ImageEnrichmentResult CreateResult(string status, DateTimeOffset checkedAt, string? mediaType, string? bridgeKey = null,
        string? bridgeId = null, string? endpoint = null, int? httpStatus = null, string? skippedReason = null, string? message = null,
        IReadOnlyDictionary<string, int>? storedCounts = null, int updatedPreferredCount = 0, int heldCount = 0) => new()
        {
            Provider = TmdbProviderName,
            ProviderName = "TMDB",
            Status = status,
            MediaType = mediaType,
            BridgeKey = bridgeKey,
            BridgeId = bridgeId,
            Endpoint = endpoint,
            HttpStatusCode = httpStatus,
            SkippedReason = skippedReason,
            Message = message,
            StoredVariantCounts = storedCounts ?? new Dictionary<string, int>(),
            DownloadedCount = storedCounts?.Values.Sum() ?? 0,
            HeldCount = heldCount,
            UpdatedPreferredCount = updatedPreferredCount,
            LastCheckedAt = checkedAt,
        };

    private static bool IsCompatibleImage(JsonNode node, AssetType assetType)
    {
        var width = node["width"]?.GetValue<int?>() ?? 0; var height = node["height"]?.GetValue<int?>() ?? 0;
        if (width <= 0 || height <= 0)
        {
            return true;
        }
        var ratio = width / (double)height;
        return assetType switch { AssetType.Background or AssetType.SeasonThumb => ratio >= 1.35, AssetType.CoverArt or AssetType.SeasonPoster => ratio <= .9, _ => true };
    }
    private static bool IsAllowedLanguage(string? language, AssetType type, string metadataLanguage) =>
        type switch
        {
            AssetType.CoverArt or AssetType.SeasonPoster or AssetType.Logo =>
                string.IsNullOrWhiteSpace(language)
                || string.Equals(language, metadataLanguage, StringComparison.OrdinalIgnoreCase),
            AssetType.Background or AssetType.SeasonThumb => string.IsNullOrWhiteSpace(language),
            _ => true,
        };

    private static int LanguageRank(string? language, AssetType type, string metadataLanguage) =>
        type switch
        {
            AssetType.Logo when string.Equals(language, metadataLanguage, StringComparison.OrdinalIgnoreCase) => 3,
            AssetType.Logo when string.IsNullOrWhiteSpace(language) => 2,
            AssetType.Background or AssetType.SeasonThumb when string.IsNullOrWhiteSpace(language) => 3,
            AssetType.CoverArt or AssetType.SeasonPoster when string.Equals(language, metadataLanguage, StringComparison.OrdinalIgnoreCase) => 3,
            AssetType.CoverArt or AssetType.SeasonPoster when string.IsNullOrWhiteSpace(language) => 2,
            _ => 0,
        };

    private static bool IsUsableDownloadedImage(byte[] bytes, AssetType assetType, out string? rejectionReason)
    {
        rejectionReason = null;
        if (assetType != AssetType.Logo)
        {
            return true;
        }

        SKBitmap? bitmap;
        try
        {
            bitmap = SKBitmap.Decode(bytes);
        }
        catch (Exception)
        {
            rejectionReason = "unsupported_or_undecodable_image";
            return false;
        }

        if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            rejectionReason = "unsupported_or_undecodable_image";
            return false;
        }

        using (bitmap)
        {
            var stepX = Math.Max(1, bitmap.Width / 256);
            var stepY = Math.Max(1, bitmap.Height / 128);
            var sampledPixels = 0;
            var visiblePixels = 0;
            for (var y = 0; y < bitmap.Height; y += stepY)
            {
                for (var x = 0; x < bitmap.Width; x += stepX)
                {
                    sampledPixels++;
                    if (bitmap.GetPixel(x, y).Alpha >= 24)
                    {
                        visiblePixels++;
                    }
                }
            }

            if (visiblePixels < Math.Max(8, sampledPixels / 1000))
            {
                rejectionReason = "insufficient_visible_pixels";
                return false;
            }

            return true;
        }
    }

    private static string BuildImageUrl(string filePath) => $"{TmdbImageBaseUrl}/{filePath.TrimStart('/')}";
    private static string InferExtension(string url) => url.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
    private static string OwnerScope(AssetType type) => type is AssetType.SeasonPoster or AssetType.SeasonThumb ? "Season" : "Work";
    private static string? GetValue(IReadOnlyDictionary<string, string> values, params string[] keys) => keys.Select(key => values.GetValueOrDefault(key)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    private static void AddCount(Dictionary<string, int> values, AssetType type, int count)
    {
        if (count > 0)
        {
            values[type.ToString()] = values.GetValueOrDefault(type.ToString()) + count;
        }
    }

    private static void MergeCounts(Dictionary<string, int> target, IReadOnlyDictionary<string, int> source)
    {
        foreach (var (type, count) in source)
        {
            if (count > 0)
            {
                target[type] = target.GetValueOrDefault(type) + count;
            }
        }
    }
    private static void AddDiagnostic(List<CanonicalValue> values, Guid id, string key, string? value, DateTimeOffset now, Guid providerId)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values.Add(new CanonicalValue { EntityId = id, Key = key, Value = value, LastScoredAt = now, WinningProviderId = providerId });
        }
    }
    private sealed record ArtworkMapping(string JsonField, AssetType AssetType, bool UpdatePreferred);
    private sealed record BrandArtworkMapping(string CanonicalKey, AssetType AssetType);
    /// <summary>
    /// <paramref name="StoredCount"/> is what this pass newly saved; <paramref name="HeldCount"/> is what the
    /// scope holds once the pass is done (newly saved or already present). Concurrent passes over a shared
    /// show see different stored counts depending on who arrives first, but always the same held count.
    /// </summary>
    private sealed record ImageAssetProcessingResult(string? PreferredLocalPath, int StoredCount, int HeldCount, int UpdatedPreferredCount) { public static readonly ImageAssetProcessingResult Empty = new(null, 0, 0, 0); }
    private sealed record ArtworkContext(Guid AssetId, Guid SelfWorkId, Guid RootWorkId, Guid? SeasonWorkId, int? SeasonNumber, MediaType MediaType);
    private sealed record SeasonArtworkTarget(Guid WorkId, int SeasonNumber);
}
