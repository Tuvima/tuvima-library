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
            return await DiscoverComicArtworkAsync(context, scope, role, ct);
        if (context.MediaType is not (MediaType.Movies or MediaType.TV))
            return new([], "No artwork gallery is available for this provider.");
        var values = await LoadCanonicalsAsync(context, ct);
        var id = GetValue(values, BridgeIdKeys.TmdbId);
        if (string.IsNullOrWhiteSpace(id)) return new([], "Choose a provider match in Match & Identity first.");
        if (_configLoader.LoadProvider(TmdbProviderName)?.Enabled == false)
            return new([], "TMDB is disabled in provider settings.");
        var key = await ResolveTmdbApiKeyAsync(ct);
        if (string.IsNullOrWhiteSpace(key)) return new([], "Configure TMDB in provider settings to discover artwork.");
        var path = context.MediaType == MediaType.Movies ? $"movie/{Uri.EscapeDataString(id)}" : $"tv/{Uri.EscapeDataString(id)}";
        var field = role switch { "Primary" => "posters", "Background" => "backdrops", "Logo" => "logos", _ => "" };
        if (context.MediaType == MediaType.TV && scope is "season" or "episode")
        {
            var seasonNumber = context.SeasonNumber;
            if (!seasonNumber.HasValue && int.TryParse(GetValue(values, MetadataFieldConstants.SeasonNumber), out var storedSeason))
                seasonNumber = storedSeason;
            if (seasonNumber is not { } season) return new([], "This owned season has no number. Review its placement before choosing TMDB artwork.");
            path += $"/season/{season}";
            if (scope == "episode")
            {
                var own = await _canonicalRepo.GetByEntityAsync(context.AssetId, ct);
                var episode = own.FirstOrDefault(v => v.Key == "episode_number")?.Value ?? GetValue(values, "episode_number");
                if (!int.TryParse(episode, out var number)) return new([], "This item needs an episode number in Match & Identity.");
                path += $"/episode/{number}";
                field = role == "Primary" ? "stills" : "";
            }
        }
        if (field.Length == 0) return new([], "The provider has no gallery for this artwork type.");
        var response = await GetImagesAsync($"{TmdbApiBaseUrl}/{path}/images", key, ResolveMetadataLanguage(), ct,
            includeAllLanguages: context.MediaType == MediaType.TV && scope == "season" && role == "Primary");
        if (response.Json is null) return new([], response.Message ?? "Provider artwork could not be loaded. Try again.");
        var items = (response.Json[field]?.AsArray() ?? []).Where(n => n?["file_path"] is not null)
            .Select(n => new ProviderArtworkCandidate("tmdb:" + n!["file_path"]!.GetValue<string>(), "TMDB",
                TmdbImageBaseUrl + n["file_path"]!.GetValue<string>(),
                "https://image.tmdb.org/t/p/w300" + n["file_path"]!.GetValue<string>(),
                n["width"]?.GetValue<int>(), n["height"]?.GetValue<int>())).DistinctBy(n => n.Id).Take(150).ToList();
        return new(items, items.Count == 0 ? "The provider returned no artwork for this type." : null);
    }

    private async Task<ProviderArtworkDiscovery> DiscoverComicArtworkAsync(ArtworkContext context, string scope, string role, CancellationToken ct)
    {
        if (role != "Primary") return new([], "Comic Vine supplies cover artwork; it does not provide backgrounds or logos.");
        var config = _configLoader.LoadProvider("comicvine");
        if (config?.Enabled == false) return new([], "Comic Vine is disabled in provider settings.");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var series = scope is "series" or "volume";
        foreach (var entity in (series ? new[] { context.RootWorkId, context.SelfWorkId, context.AssetId } : new[] { context.AssetId, context.SelfWorkId }).Distinct())
            foreach (var value in await _canonicalRepo.GetByEntityAsync(entity, ct))
                if (!string.IsNullOrWhiteSpace(value.Value)) values.TryAdd(value.Key, value.Value);
        var id = GetValue(values, series ? BridgeIdKeys.ComicVineVolumeId : BridgeIdKeys.ComicVineId);
        var prefix = series ? "4050-" : "4000-";
        if (id?.StartsWith(prefix, StringComparison.Ordinal) == true) id = id[prefix.Length..];
        if (!long.TryParse(id, out var numericId) || numericId <= 0)
            return new([], $"Match this {(series ? "series" : "issue")} to Comic Vine in Match & Identity first.");
        var key = config?.HttpClient?.ApiKeyOverride;
        if (string.IsNullOrWhiteSpace(key)) key = config?.HttpClient?.ApiKey;
        if (string.IsNullOrWhiteSpace(key)) key = await _providerConfigRepo.GetDecryptedValueAsync(WellKnownProviders.ComicVine.ToString(), "api_key", ct);
        if (string.IsNullOrWhiteSpace(key)) return new([], "Configure Comic Vine in provider settings to discover artwork.");
        try
        {
            using var client = _httpFactory.CreateClient("comicvine");
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://comicvine.gamespot.com/api/{(series ? "volume" : "issue")}/{prefix}{numericId}/?api_key={Uri.EscapeDataString(key)}&format=json&field_list=id,image,associated_images");
            request.Headers.UserAgent.ParseAdd("TuvimaLibrary/1.0");
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return new([], $"Comic Vine could not load artwork (HTTP {(int)response.StatusCode}). Check the provider connection and try again.");
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            if (json?["status_code"]?.GetValue<int>() != 1) return new([], "Comic Vine could not load this gallery. Check the provider connection and identity.");
            var result = json["results"];
            var nodes = new List<JsonNode?> { result?["image"] };
            if (result?["associated_images"] is JsonArray associated) nodes.AddRange(associated);
            var candidates = new List<ProviderArtworkCandidate>();
            foreach (var node in nodes.Where(n => n is not null))
            {
                var original = node?["original_url"]?.GetValue<string>();
                var thumbnail = node?["small_url"]?.GetValue<string>() ?? node?["thumb_url"]?.GetValue<string>();
                if (!Uri.TryCreate(original, UriKind.Absolute, out var uri) || uri.Scheme != "https") continue;
                // Comic Vine associated images expose only original_url; its CDN supports explicit thumbnail sizes.
                if (string.IsNullOrWhiteSpace(thumbnail) && uri.Host.EndsWith("gamespot.com", StringComparison.OrdinalIgnoreCase))
                    thumbnail = original!.Replace("/original/", "/scale_small/", StringComparison.Ordinal);
                if (thumbnail == original || string.IsNullOrWhiteSpace(thumbnail)) continue;
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

    // Keep the established constructor shape so composition and existing callers stay stable.
    public ImageEnrichmentService(
        IEntityAssetRepository assetRepo, IMediaAssetRepository mediaAssetRepo,
        ICharacterPortraitRepository portraitRepo, ICanonicalValueRepository canonicalRepo,
        IWorkRepository workRepo, IFictionalEntityRepository entityRepo, IPersonRepository personRepo,
        IProviderConfigurationRepository providerConfigRepo, IConfigurationLoader configLoader,
        IImageCacheRepository imageCache, AssetPathService assetPaths, IAssetExportService? assetExportService,
        IHttpClientFactory httpFactory, IFuzzyMatchingService fuzzy, ILogger<ImageEnrichmentService> logger,
        ImageDownloadCoordinator? imageDownloadCoordinator = null)
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
    }

    public Task<ImageEnrichmentResult> EnrichWorkImagesAsync(Guid assetId, string? workQid, CancellationToken ct = default) =>
        EnrichWorkImagesCoreAsync(assetId, workQid, forceRefresh: false, ct);

    public Task<ImageEnrichmentResult> RefreshWorkImagesAsync(Guid assetId, string? workQid, CancellationToken ct = default) =>
        EnrichWorkImagesCoreAsync(assetId, workQid, forceRefresh: true, ct);

    public async Task<(bool Changed, string Message)> RefreshTvSeasonArtworkAsync(
        Guid seasonWorkId, string showId, int seasonNumber, CancellationToken ct = default)
    {
        if (!long.TryParse(showId, out var numericShowId) || numericShowId <= 0
            || seasonNumber is < 0 or > 999)
            return (false, "The selected TV season identity is invalid.");
        var apiKey = await ResolveTmdbApiKeyAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey)) return (false, "TMDB is not configured for artwork.");
        var endpoint = $"{TmdbApiBaseUrl}/tv/{numericShowId}/season/{seasonNumber}/images";
        var images = await GetImagesAsync(endpoint, apiKey, ResolveMetadataLanguage(), ct,
            includeAllLanguages: true).ConfigureAwait(false);
        if (images.Json is null) return (false, images.Message ?? "TMDB season images could not be loaded.");
        var processed = await ProcessRankedImagesAsync(images.Json["posters"]?.AsArray() ?? [],
            AssetType.SeasonPoster, seasonWorkId, updatePreferred: true, ResolveMetadataLanguage(), ct).ConfigureAwait(false);
        var changed = processed.StoredCount > 0 || processed.UpdatedPreferredCount > 0;
        return (changed, changed ? "Season poster is available in managed artwork."
            : "TMDB has no new compatible poster for this season.");
    }

    public async Task<(bool Changed, string Message)> RefreshTvEpisodeStillAsync(
        Guid episodeWorkId, string showId, int seasonNumber, int episodeNumber,
        string? stillPath, CancellationToken ct = default)
    {
        if (!long.TryParse(showId, out var numericShowId) || numericShowId <= 0
            || seasonNumber is < 0 or > 999 || episodeNumber is < 0 or > 999)
            return (false, "The selected TV episode identity is invalid.");
        var existing = await _assetRepo.GetByEntityAsync(episodeWorkId.ToString(), AssetType.EpisodeStill.ToString(), ct);
        if (existing.Any(asset => asset.IsPreferred && asset.IsUserOverride))
            return (false, "Your selected episode still was kept.");
        var url = RetailRequestBuilder.BuildTmdbEpisodeStillUrl(stillPath);
        if (string.IsNullOrWhiteSpace(url))
        {
            var apiKey = await ResolveTmdbApiKeyAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(apiKey)) return (false, "TMDB is not configured for artwork.");
            var endpoint = $"{TmdbApiBaseUrl}/tv/{numericShowId}/season/{seasonNumber}/episode/{episodeNumber}/images";
            var images = await GetImagesAsync(endpoint, apiKey, ResolveMetadataLanguage(), ct).ConfigureAwait(false);
            if (images.Json is null) return (false, images.Message ?? "TMDB episode images could not be loaded.");
            var path = images.Json["stills"]?.AsArray()
                .FirstOrDefault(node => !string.IsNullOrWhiteSpace(node?["file_path"]?.ToString()))?["file_path"]?.ToString();
            url = RetailRequestBuilder.BuildTmdbEpisodeStillUrl(path);
        }
        if (string.IsNullOrWhiteSpace(url)) return (false, "TMDB has no still for this episode.");
        var saved = await ProcessRemoteImageAsync(url, AssetType.EpisodeStill, episodeWorkId, ct).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(saved.PreferredLocalPath)
            ? (saved.StoredCount > 0 || saved.UpdatedPreferredCount > 0, "Episode still is available in managed artwork.")
            : (false, "TMDB's episode still could not be downloaded. Retry from Artwork.");
    }

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
            return await PersistDiagnosticsAsync(context, CreateResult("Skipped", checkedAt, mediaType,
                skippedReason: "unsupported_media_type", message: "TMDB artwork enrichment supports movies and TV only."), ct);

        var canonicals = await LoadCanonicalsAsync(context, ct).ConfigureAwait(false);
        var tmdbId = GetValue(canonicals, BridgeIdKeys.TmdbId);
        if (string.IsNullOrWhiteSpace(tmdbId))
            return await PersistDiagnosticsAsync(context, CreateResult("Skipped", checkedAt, mediaType,
                skippedReason: "missing_bridge_id", message: "This item needs a TMDB ID before artwork can be refreshed."), ct);

        var apiKey = await ResolveTmdbApiKeyAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
            return await PersistDiagnosticsAsync(context, CreateResult("Skipped", checkedAt, mediaType, BridgeIdKeys.TmdbId, tmdbId,
                skippedReason: "missing_api_key", message: "TMDB is not configured."), ct);

        var metadataLanguage = ResolveMetadataLanguage();

        var endpoint = context.MediaType == MediaType.Movies
            ? $"{TmdbApiBaseUrl}/movie/{Uri.EscapeDataString(tmdbId)}/images"
            : $"{TmdbApiBaseUrl}/tv/{Uri.EscapeDataString(tmdbId)}/images";
        var rootAlreadyChecked = !forceRefresh
            && !string.IsNullOrWhiteSpace(GetValue(canonicals, "tmdb_artwork_last_checked_at"));
        var response = rootAlreadyChecked
            ? (Json: (JsonNode?)null, Status: "Completed", HttpStatusCode: (int?)null, SkippedReason: (string?)null, Message: (string?)null)
            : await GetImagesAsync(endpoint, apiKey, metadataLanguage, ct).ConfigureAwait(false);
        if (!rootAlreadyChecked && response.Json is null)
            return await PersistDiagnosticsAsync(context, CreateResult(response.Status, checkedAt, mediaType, BridgeIdKeys.TmdbId, tmdbId,
                endpoint, response.HttpStatusCode, response.SkippedReason, response.Message), ct);

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var preferredUpdates = 0;
        var owner = context.MediaType == MediaType.TV ? context.RootWorkId : context.SelfWorkId;
        foreach (var mapping in RootMappings)
        {
            var processed = await ProcessRankedImagesAsync(response.Json?[mapping.JsonField]?.AsArray() ?? [], mapping.AssetType,
                owner, mapping.UpdatePreferred, metadataLanguage, ct).ConfigureAwait(false);
            AddCount(counts, mapping.AssetType, processed.StoredCount);
            preferredUpdates += processed.UpdatedPreferredCount;
        }

        foreach (var mapping in BrandMappings)
        {
            var imageUrl = GetValue(canonicals, mapping.CanonicalKey);
            if (string.IsNullOrWhiteSpace(imageUrl)) continue;

            var processed = await ProcessRemoteImageAsync(imageUrl, mapping.AssetType, owner, ct).ConfigureAwait(false);
            AddCount(counts, mapping.AssetType, processed.StoredCount);
            preferredUpdates += processed.UpdatedPreferredCount;
        }

        if (context.MediaType == MediaType.TV)
        {
            foreach (var season in await ResolveRepresentedSeasonsAsync(context, ct).ConfigureAwait(false))
            {
                var seasonCanonicals = await _canonicalRepo.GetByEntityAsync(season.WorkId, ct).ConfigureAwait(false);
                if (!forceRefresh && seasonCanonicals.Any(value =>
                        string.Equals(value.Key, "tmdb_season_artwork_last_checked_at", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var seasonEndpoint = $"{TmdbApiBaseUrl}/tv/{Uri.EscapeDataString(tmdbId)}/season/{season.SeasonNumber}/images";
                var seasonResponse = await GetImagesAsync(seasonEndpoint, apiKey, metadataLanguage, ct,
                    includeAllLanguages: true).ConfigureAwait(false);
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
        }

        var downloaded = counts.Values.Sum();
        return await PersistDiagnosticsAsync(context, CreateResult(downloaded > 0 || preferredUpdates > 0 ? "Completed" : "NoImages",
            checkedAt, mediaType, BridgeIdKeys.TmdbId, tmdbId, endpoint, response.HttpStatusCode,
            message: downloaded > 0 ? $"Stored {downloaded} TMDB artwork variant(s)." : "TMDB returned no compatible artwork variants.",
            storedCounts: counts, updatedPreferredCount: preferredUpdates), ct);
    }

    private static readonly ArtworkMapping[] RootMappings =
    [new("backdrops", AssetType.Background, true), new("logos", AssetType.Logo, true), new("posters", AssetType.CoverArt, true)];
    private static readonly ArtworkMapping[] SeasonMappings =
    [new("posters", AssetType.SeasonPoster, true), new("backdrops", AssetType.SeasonThumb, true)];
    private static readonly BrandArtworkMapping[] BrandMappings =
    [new("network_logo_url", AssetType.NetworkLogo), new("studio_logo_url", AssetType.StudioLogo)];

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
        if (ranked.Count == 0) return ImageAssetProcessingResult.Empty;

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
                if (updatePreferred && preferred is null && !existing.IsUserOverride) preferred = existing;
                continue;
            }
            var bytes = await GetCachedOrDownloadAsync(url, ct).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0) continue;
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
                Id = Guid.NewGuid(), EntityId = ownerEntityId.ToString(), EntityType = "Work", AssetTypeValue = assetType.ToString(),
                ImageUrl = url, SourceProvider = TmdbProviderName, AssetClassValue = "Artwork", StorageLocationValue = "Central",
                OwnerScope = OwnerScope(assetType), CreatedAt = DateTimeOffset.UtcNow,
            };
            variant.LocalImagePath ??= _assetPaths.GetCentralAssetPath("Work", ownerEntityId, assetType.ToString(), variant.Id, InferExtension(url));
            await PersistImageAsync(bytes, variant.LocalImagePath, url, ct).ConfigureAwait(false);
            ArtworkVariantHelper.StampMetadataAndRenditions(variant, _assetPaths);
            await _assetRepo.UpsertAsync(variant, ct).ConfigureAwait(false);
            if (existing is null) variants.Add(variant); else variants[variants.IndexOf(existing)] = variant;
            stored++;
            accepted++;
            if (updatePreferred && preferred is null) preferred = variant;
        }
        if (!updatePreferred || preferred is null) return new ImageAssetProcessingResult(preferred?.LocalImagePath, stored, 0);
        await _assetRepo.SetPreferredAsync(preferred.Id, ct).ConfigureAwait(false);
        await _canonicalRepo.UpsertBatchAsync(ArtworkCanonicalHelper.CreatePreferredAssetCanonicals(ownerEntityId, preferred, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
        if (_assetExportService is not null) await _assetExportService.ReconcileArtworkAsync(preferred.EntityId, preferred.EntityType, preferred.AssetTypeValue, ct).ConfigureAwait(false);
        return new ImageAssetProcessingResult(preferred.LocalImagePath, stored, currentPreferred == preferred.Id ? 0 : 1);
    }

    private async Task<ImageAssetProcessingResult> ProcessRemoteImageAsync(
        string url,
        AssetType assetType,
        Guid ownerEntityId,
        CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return ImageAssetProcessingResult.Empty;

        var variants = (await _assetRepo.GetByEntityAsync(ownerEntityId.ToString(), assetType.ToString(), ct)).ToList();
        var currentPreferred = variants.FirstOrDefault(asset => asset.IsPreferred);
        if (currentPreferred?.IsUserOverride == true)
            return ImageAssetProcessingResult.Empty;

        var existing = variants.FirstOrDefault(asset => string.Equals(asset.ImageUrl, url, StringComparison.OrdinalIgnoreCase));
        var stored = 0;
        if (existing is null || string.IsNullOrWhiteSpace(existing.LocalImagePath) || !File.Exists(existing.LocalImagePath))
        {
            await using var lease = await _imageDownloadCoordinator.AcquireAsync(url, ct).ConfigureAwait(false);
            var bytes = await GetCachedOrDownloadAsync(url, ct).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0) return ImageAssetProcessingResult.Empty;

            existing ??= new EntityAsset
            {
                Id = Guid.NewGuid(),
                EntityId = ownerEntityId.ToString(),
                EntityType = "Work",
                AssetTypeValue = assetType.ToString(),
                ImageUrl = url,
                SourceProvider = TmdbProviderName,
                AssetClassValue = "Artwork",
                StorageLocationValue = "Central",
                OwnerScope = assetType == AssetType.EpisodeStill ? "Episode" : "Work",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            existing.LocalImagePath ??= _assetPaths.GetCentralAssetPath(
                "Work", ownerEntityId, assetType.ToString(), existing.Id, InferExtension(url));
            await PersistImageAsync(bytes, existing.LocalImagePath, url, ct).ConfigureAwait(false);
            ArtworkVariantHelper.StampMetadataAndRenditions(existing, _assetPaths);
            await _assetRepo.UpsertAsync(existing, ct).ConfigureAwait(false);
            stored = 1;
        }

        await _assetRepo.SetPreferredAsync(existing.Id, ct).ConfigureAwait(false);
        await _canonicalRepo.UpsertBatchAsync(
            ArtworkCanonicalHelper.CreatePreferredAssetCanonicals(ownerEntityId, existing, DateTimeOffset.UtcNow),
            ct).ConfigureAwait(false);
        return new ImageAssetProcessingResult(existing.LocalImagePath, stored, currentPreferred?.Id == existing.Id ? 0 : 1);
    }

    private async Task<(JsonNode? Json, string Status, int? HttpStatusCode, string? SkippedReason, string? Message)> GetImagesAsync(
        string endpoint,
        string apiKey,
        string metadataLanguage,
        CancellationToken ct,
        bool includeAllLanguages = false)
    {
        var url = includeAllLanguages
            ? $"{endpoint}?api_key={Uri.EscapeDataString(apiKey)}"
            : $"{endpoint}?include_image_language={Uri.EscapeDataString(metadataLanguage)},null&api_key={Uri.EscapeDataString(apiKey)}";
        try
        {
            using var client = _httpFactory.CreateClient(TmdbProviderName);
            using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (null, response.StatusCode == HttpStatusCode.NotFound ? "NoResult" : "Error", (int)response.StatusCode,
                    response.StatusCode == HttpStatusCode.NotFound ? "provider_no_result" : "provider_request_failed", $"TMDB returned {(int)response.StatusCode}.");
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
        if (!string.IsNullOrWhiteSpace(cached) && File.Exists(cached)) return await File.ReadAllBytesAsync(cached, ct).ConfigureAwait(false);
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
        if (!string.IsNullOrWhiteSpace(cached) && File.Exists(cached) && !string.Equals(cached, destination, StringComparison.OrdinalIgnoreCase)) File.Copy(cached, destination, true);
        else if (string.IsNullOrWhiteSpace(cached) || !File.Exists(cached)) await BoundedHttpContent.WriteFileAtomicallyAsync(destination, bytes, ct).ConfigureAwait(false);
        await _imageCache.InsertAsync(hash, destination, sourceUrl, ct).ConfigureAwait(false);
    }

    private async Task<ArtworkContext> ResolveContextAsync(Guid entityId, CancellationToken ct)
    {
        var asset = await _mediaAssetRepo.FindByIdAsync(entityId, ct).ConfigureAwait(false);
        var lineage = await _workRepo.GetLineageByAssetAsync(asset?.Id ?? entityId, ct).ConfigureAwait(false);
        if (asset is null)
        {
            asset = await _mediaAssetRepo.FindFirstByWorkIdAsync(entityId, ct).ConfigureAwait(false);
            if (asset is not null) lineage = await _workRepo.GetLineageByAssetAsync(asset.Id, ct).ConfigureAwait(false);
        }
        if (lineage is null) return new ArtworkContext(asset?.Id ?? entityId, entityId, entityId, null, null, MediaType.Unknown);
        string? seasonValue = null;
        if (lineage.MediaType == MediaType.TV && lineage.ParentWorkId is { } seasonWorkId)
        {
            var seasonChild = (await _workRepo.GetDirectChildrenAsync(lineage.RootParentWorkId, ct).ConfigureAwait(false))
                .FirstOrDefault(child => child.WorkId == seasonWorkId && child.WorkKind == WorkKind.Parent);
            seasonValue = seasonChild?.Ordinal?.ToString(CultureInfo.InvariantCulture);
        }
        var own = await _canonicalRepo.GetByEntityAsync(lineage.WorkId, ct).ConfigureAwait(false);
        seasonValue ??= own.FirstOrDefault(value => string.Equals(value.Key, MetadataFieldConstants.SeasonNumber, StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrWhiteSpace(seasonValue) && lineage.ParentWorkId is { } parentWorkId)
        {
            var parentCanonicals = await _canonicalRepo.GetByEntityAsync(parentWorkId, ct).ConfigureAwait(false);
            seasonValue = parentCanonicals.FirstOrDefault(value =>
                string.Equals(value.Key, MetadataFieldConstants.SeasonNumber, StringComparison.OrdinalIgnoreCase))?.Value;
        }
        if (string.IsNullOrWhiteSpace(seasonValue))
        {
            var assetCanonicals = await _canonicalRepo.GetByEntityAsync(asset?.Id ?? entityId, ct).ConfigureAwait(false);
            seasonValue = assetCanonicals.FirstOrDefault(value =>
                string.Equals(value.Key, MetadataFieldConstants.SeasonNumber, StringComparison.OrdinalIgnoreCase))?.Value;
        }
        return new ArtworkContext(asset?.Id ?? entityId, lineage.WorkId, lineage.RootParentWorkId, lineage.ParentWorkId,
            int.TryParse(seasonValue, out var season) ? season : null, lineage.MediaType);
    }

    private async Task<Dictionary<string, string>> LoadCanonicalsAsync(ArtworkContext context, CancellationToken ct)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in new[] { context.RootWorkId, context.SelfWorkId, context.AssetId }.Distinct())
            foreach (var canonical in await _canonicalRepo.GetByEntityAsync(id, ct).ConfigureAwait(false))
                if (!string.IsNullOrWhiteSpace(canonical.Key) && !string.IsNullOrWhiteSpace(canonical.Value) && !values.ContainsKey(canonical.Key)) values[canonical.Key] = canonical.Value;
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
        if (!string.IsNullOrWhiteSpace(config?.HttpClient?.ApiKeyOverride)) return config.HttpClient.ApiKeyOverride;
        if (!string.IsNullOrWhiteSpace(config?.HttpClient?.ApiKey)) return config.HttpClient.ApiKey;
        return await _providerConfigRepo.GetDecryptedValueAsync(WellKnownProviders.Tmdb.ToString(), "api_key", ct).ConfigureAwait(false);
    }

    private async Task<ImageEnrichmentResult> PersistDiagnosticsAsync(ArtworkContext context, ImageEnrichmentResult result, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var values = new List<CanonicalValue>();
        foreach (var id in new[] { context.RootWorkId, context.SelfWorkId, context.AssetId }.Distinct())
        {
            AddDiagnostic(values, id, "tmdb_artwork_status", result.Status, now);
            AddDiagnostic(values, id, "tmdb_artwork_last_checked_at", result.LastCheckedAt.ToString("O", CultureInfo.InvariantCulture), now);
            AddDiagnostic(values, id, "tmdb_artwork_downloaded_count", result.DownloadedCount.ToString(CultureInfo.InvariantCulture), now);
            AddDiagnostic(values, id, "tmdb_artwork_bridge_id", result.BridgeId, now);
            AddDiagnostic(values, id, "tmdb_artwork_http_status", result.HttpStatusCode?.ToString(CultureInfo.InvariantCulture), now);
            AddDiagnostic(values, id, "tmdb_artwork_skipped_reason", result.SkippedReason, now);
        }
        if (values.Count > 0) await _canonicalRepo.UpsertBatchAsync(values, ct).ConfigureAwait(false);
        return result;
    }

    private static ImageEnrichmentResult CreateResult(string status, DateTimeOffset checkedAt, string? mediaType, string? bridgeKey = null,
        string? bridgeId = null, string? endpoint = null, int? httpStatus = null, string? skippedReason = null, string? message = null,
        IReadOnlyDictionary<string, int>? storedCounts = null, int updatedPreferredCount = 0) => new()
    {
        Provider = TmdbProviderName, ProviderName = "TMDB", Status = status, MediaType = mediaType, BridgeKey = bridgeKey, BridgeId = bridgeId,
        Endpoint = endpoint, HttpStatusCode = httpStatus, SkippedReason = skippedReason, Message = message,
        StoredVariantCounts = storedCounts ?? new Dictionary<string, int>(), DownloadedCount = storedCounts?.Values.Sum() ?? 0,
        UpdatedPreferredCount = updatedPreferredCount, LastCheckedAt = checkedAt,
    };

    private static bool IsCompatibleImage(JsonNode node, AssetType assetType)
    {
        var width = node["width"]?.GetValue<int?>() ?? 0; var height = node["height"]?.GetValue<int?>() ?? 0;
        if (width <= 0 || height <= 0) return true;
        var ratio = width / (double)height;
        return assetType switch { AssetType.Background or AssetType.SeasonThumb => ratio >= 1.35, AssetType.CoverArt or AssetType.SeasonPoster => ratio <= .9, _ => true };
    }
    private static bool IsAllowedLanguage(string? language, AssetType type, string metadataLanguage) =>
        type switch
        {
            AssetType.CoverArt or AssetType.SeasonPoster => true,
            AssetType.Logo =>
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
    private static void AddCount(Dictionary<string, int> values, AssetType type, int count) { if (count > 0) values[type.ToString()] = values.GetValueOrDefault(type.ToString()) + count; }
    private static void AddDiagnostic(List<CanonicalValue> values, Guid id, string key, string? value, DateTimeOffset now) { if (!string.IsNullOrWhiteSpace(value)) values.Add(new CanonicalValue { EntityId = id, Key = key, Value = value, LastScoredAt = now, WinningProviderId = WellKnownProviders.Tmdb }); }
    private sealed record ArtworkMapping(string JsonField, AssetType AssetType, bool UpdatePreferred);
    private sealed record BrandArtworkMapping(string CanonicalKey, AssetType AssetType);
    private sealed record ImageAssetProcessingResult(string? PreferredLocalPath, int StoredCount, int UpdatedPreferredCount) { public static readonly ImageAssetProcessingResult Empty = new(null, 0, 0); }
    private sealed record ArtworkContext(Guid AssetId, Guid SelfWorkId, Guid RootWorkId, Guid? SeasonWorkId, int? SeasonNumber, MediaType MediaType);
    private sealed record SeasonArtworkTarget(Guid WorkId, int SeasonNumber);
}
