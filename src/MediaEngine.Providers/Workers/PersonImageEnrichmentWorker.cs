using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace MediaEngine.Providers.Workers;

/// <summary>
/// Post-identity person image enrichment.
/// Wikidata remains the identity/facts source; this worker upgrades movie/TV
/// people to a higher-quality TMDB profile image when it can verify the same person.
/// </summary>
public sealed class PersonImageEnrichmentWorker
{
    private const string TmdbProviderName = "tmdb";
    private const string TmdbApiBaseUrl = "https://api.themoviedb.org/3";
    private const string TmdbImageBaseUrl = "https://image.tmdb.org/t/p/";
    private const int MinimumProfileHeight = 450;
    private const int MinimumProfileWidth = 300;

    private readonly IPersonRepository _personRepo;
    private readonly IEntityAssetRepository _assetRepo;
    private readonly IProviderConfigurationRepository _providerConfigRepo;
    private readonly IConfigurationLoader _configLoader;
    private readonly IHttpClientFactory _httpFactory;
    private readonly AssetPathService _assetPaths;
    private readonly IImageCacheRepository? _imageCache;
    private readonly ImageDownloadCoordinator _imageDownloadCoordinator;
    private readonly ILogger<PersonImageEnrichmentWorker> _logger;

    public PersonImageEnrichmentWorker(
        IPersonRepository personRepo,
        IEntityAssetRepository assetRepo,
        IProviderConfigurationRepository providerConfigRepo,
        IConfigurationLoader configLoader,
        IHttpClientFactory httpFactory,
        AssetPathService assetPaths,
        ILogger<PersonImageEnrichmentWorker> logger,
        IImageCacheRepository? imageCache = null,
        ImageDownloadCoordinator? imageDownloadCoordinator = null)
    {
        ArgumentNullException.ThrowIfNull(personRepo);
        ArgumentNullException.ThrowIfNull(assetRepo);
        ArgumentNullException.ThrowIfNull(providerConfigRepo);
        ArgumentNullException.ThrowIfNull(configLoader);
        ArgumentNullException.ThrowIfNull(httpFactory);
        ArgumentNullException.ThrowIfNull(assetPaths);
        ArgumentNullException.ThrowIfNull(logger);

        _personRepo = personRepo;
        _assetRepo = assetRepo;
        _providerConfigRepo = providerConfigRepo;
        _configLoader = configLoader;
        _httpFactory = httpFactory;
        _assetPaths = assetPaths;
        _imageCache = imageCache;
        _imageDownloadCoordinator = imageDownloadCoordinator ?? ImageDownloadCoordinator.Shared;
        _logger = logger;
    }

    public async Task EnrichAsync(
        Guid personId,
        string? role,
        MediaType mediaType,
        CancellationToken ct = default,
        int? tmdbPersonId = null,
        string? tmdbProfileUrl = null)
    {
        ct.ThrowIfCancellationRequested();

        if (!IsEligibleMediaPerson(role, mediaType))
        {
            return;
        }

        var person = await _personRepo.FindByIdAsync(personId, ct).ConfigureAwait(false);
        if (person is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(person.WikidataQid)
            && !tmdbPersonId.HasValue
            && string.IsNullOrWhiteSpace(tmdbProfileUrl))
        {
            return;
        }

        var apiKey = await ResolveTmdbApiKeyAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogDebug("TMDB person image enrichment skipped for {PersonId}: no API key configured", personId);
            return;
        }

        var resolvedTmdbPersonId = tmdbPersonId;
        if (!resolvedTmdbPersonId.HasValue && !string.IsNullOrWhiteSpace(person.WikidataQid))
        {
            resolvedTmdbPersonId = await ResolveTmdbPersonIdAsync(person.WikidataQid, apiKey, ct).ConfigureAwait(false);
        }

        if (!resolvedTmdbPersonId.HasValue && string.IsNullOrWhiteSpace(tmdbProfileUrl))
        {
            return;
        }

        var imageBaseUrl = await ResolveImageBaseUrlAsync(apiKey, ct).ConfigureAwait(false);
        var candidates = resolvedTmdbPersonId.HasValue
            ? await FetchProfileImagesAsync(resolvedTmdbPersonId.Value, apiKey, ct).ConfigureAwait(false)
            : [];
        var selected = candidates
            .Where(IsUsableProfile)
            .OrderByDescending(candidate => candidate.VoteAverage)
            .ThenByDescending(candidate => candidate.VoteCount)
            .ThenByDescending(candidate => candidate.Width * candidate.Height)
            .FirstOrDefault();

        var imageUrl = selected is not null && !string.IsNullOrWhiteSpace(selected.FilePath)
            ? BuildTmdbImageUrl(imageBaseUrl, "original", selected.FilePath)
            : NormalizeTmdbProfileUrl(tmdbProfileUrl);
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return;
        }

        await using var downloadLease = await _imageDownloadCoordinator
            .AcquireAsync(imageUrl, ct)
            .ConfigureAwait(false);

        var existingAssets = (await _assetRepo.GetByEntityAsync(personId.ToString("D"), "Headshot", ct).ConfigureAwait(false)).ToList();
        var existingTmdbAsset = existingAssets.FirstOrDefault(asset =>
            string.Equals(asset.ImageUrl, imageUrl, StringComparison.OrdinalIgnoreCase));

        if (existingTmdbAsset is not null)
        {
            await PromoteExistingAssetAsync(personId, existingTmdbAsset, ct).ConfigureAwait(false);
            return;
        }

        if (selected is not null && !ShouldReplaceCurrentHeadshot(person, existingAssets, selected))
        {
            return;
        }

        byte[]? bytes = null;
        var sourceCachedPath = _imageCache is null
            ? null
            : await _imageCache.FindBySourceUrlAsync(imageUrl, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(sourceCachedPath) && File.Exists(sourceCachedPath))
        {
            bytes = await File.ReadAllBytesAsync(sourceCachedPath, ct).ConfigureAwait(false);
        }

        bytes ??= await DownloadImageAsync(imageUrl, ct).ConfigureAwait(false);
        if (bytes is null || bytes.Length == 0)
        {
            return;
        }

        var asset = new EntityAsset
        {
            Id = Guid.NewGuid(),
            EntityId = personId.ToString("D"),
            EntityType = "Person",
            AssetTypeValue = "Headshot",
            ImageUrl = imageUrl,
            SourceProvider = TmdbProviderName,
            AssetClassValue = "Artwork",
            StorageLocationValue = "Central",
            OwnerScope = "Person",
            IsPreferred = false,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        asset.LocalImagePath = _assetPaths.GetPersonHeadshotPath(personId, InferExtension(imageUrl));
        AssetPathService.EnsureDirectory(asset.LocalImagePath);
        string? imageContentHash = null;
        if (_imageCache is null)
        {
            await BoundedHttpContent.WriteFileAtomicallyAsync(asset.LocalImagePath, bytes, ct).ConfigureAwait(false);
        }
        else
        {
            imageContentHash = Hashing.Sha256Hex(bytes);
            var contentCachedPath = await _imageCache.FindByHashAsync(imageContentHash, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(contentCachedPath) && File.Exists(contentCachedPath))
            {
                if (!string.Equals(contentCachedPath, asset.LocalImagePath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(contentCachedPath, asset.LocalImagePath, overwrite: true);
                }
            }
            else
            {
                await BoundedHttpContent.WriteFileAtomicallyAsync(asset.LocalImagePath, bytes, ct).ConfigureAwait(false);
            }
        }

        ArtworkVariantHelper.StampMetadataAndRenditions(asset, _assetPaths);
        if ((asset.WidthPx ?? 0) < MinimumProfileWidth || (asset.HeightPx ?? 0) < MinimumProfileHeight)
        {
            TryDelete(asset.LocalImagePath);
            return;
        }

        if (selected is null && !ShouldReplaceCurrentHeadshot(person, existingAssets, asset.WidthPx ?? 0, asset.HeightPx ?? 0))
        {
            TryDelete(asset.LocalImagePath);
            return;
        }

        if (_imageCache is not null && imageContentHash is not null)
        {
            await _imageCache.InsertAsync(imageContentHash, asset.LocalImagePath, imageUrl, ct)
                .ConfigureAwait(false);
        }

        await _assetRepo.UpsertAsync(asset, ct).ConfigureAwait(false);
        await _assetRepo.SetPreferredAsync(asset.Id, ct).ConfigureAwait(false);
        await _personRepo.UpdateLocalHeadshotPathAsync(personId, asset.LocalImagePath, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Person {PersonId}: upgraded headshot from TMDB profile {TmdbPersonId} ({Width}x{Height})",
            personId,
            resolvedTmdbPersonId,
            asset.WidthPx,
            asset.HeightPx);
    }

    public async Task EnrichProviderUrlAsync(Guid personId, string imageUrl,
        string provider, CancellationToken ct = default)
    {
        if (provider != "tvdb"
            || !Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !(uri.Host.Equals("thetvdb.com", StringComparison.OrdinalIgnoreCase)
                 || uri.Host.EndsWith(".thetvdb.com", StringComparison.OrdinalIgnoreCase))) return;
        var existing = (await _assetRepo.GetByEntityAsync(personId.ToString("D"), "Headshot", ct)
            .ConfigureAwait(false)).ToList();
        if (existing.Any(asset => asset.IsPreferred && asset.IsUserOverride)) return;
        var same = existing.FirstOrDefault(asset =>
            string.Equals(asset.ImageUrl, imageUrl, StringComparison.OrdinalIgnoreCase));
        if (same is not null && !string.IsNullOrWhiteSpace(same.LocalImagePath)
            && File.Exists(same.LocalImagePath))
        {
            await PromoteExistingAssetAsync(personId, same, ct).ConfigureAwait(false);
            return;
        }

        await using var lease = await _imageDownloadCoordinator.AcquireAsync(imageUrl, ct)
            .ConfigureAwait(false);
        var bytes = await DownloadImageAsync(imageUrl, ct).ConfigureAwait(false);
        if (bytes is null || bytes.Length == 0) return;
        var asset = same ?? new EntityAsset
        {
            Id = Guid.NewGuid(), EntityId = personId.ToString("D"), EntityType = "Person",
            AssetTypeValue = "Headshot", ImageUrl = imageUrl, SourceProvider = "tvdb",
            AssetClassValue = "Artwork", StorageLocationValue = "Central",
            OwnerScope = "Person", CreatedAt = DateTimeOffset.UtcNow,
        };
        asset.LocalImagePath ??= _assetPaths.GetPersonHeadshotPath(personId, InferExtension(imageUrl));
        AssetPathService.EnsureDirectory(asset.LocalImagePath);
        await BoundedHttpContent.WriteFileAtomicallyAsync(asset.LocalImagePath, bytes, ct)
            .ConfigureAwait(false);
        ArtworkVariantHelper.StampMetadataAndRenditions(asset, _assetPaths);
        if ((asset.WidthPx ?? 0) < MinimumProfileWidth
            || (asset.HeightPx ?? 0) < MinimumProfileHeight) return;
        await _assetRepo.UpsertAsync(asset, ct).ConfigureAwait(false);
        await _assetRepo.SetPreferredAsync(asset.Id, ct).ConfigureAwait(false);
        await _personRepo.UpdateLocalHeadshotPathAsync(personId, asset.LocalImagePath, ct)
            .ConfigureAwait(false);
    }

    private async Task PromoteExistingAssetAsync(Guid personId, EntityAsset asset, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(asset.LocalImagePath) && File.Exists(asset.LocalImagePath))
        {
            await _assetRepo.SetPreferredAsync(asset.Id, ct).ConfigureAwait(false);
            await _personRepo.UpdateLocalHeadshotPathAsync(personId, asset.LocalImagePath, ct).ConfigureAwait(false);
        }
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

        var stored = await _providerConfigRepo.GetDecryptedValueAsync(
            WellKnownProviders.Tmdb.ToString(),
            "api_key",
            ct).ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(stored) ? null : stored;
    }

    private async Task<int?> ResolveTmdbPersonIdAsync(string wikidataQid, string apiKey, CancellationToken ct)
    {
        var url = $"{TmdbApiBaseUrl}/find/{Uri.EscapeDataString(wikidataQid)}?external_source=wikidata_id&api_key={Uri.EscapeDataString(apiKey)}";
        try
        {
            using var client = _httpFactory.CreateClient(TmdbProviderName);
            var result = await client.GetFromJsonAsync<TmdbFindResponse>(url, ct).ConfigureAwait(false);
            return result?.PersonResults?.FirstOrDefault()?.Id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "TMDB person lookup failed for Wikidata QID {Qid}", wikidataQid);
            return null;
        }
    }

    private async Task<string> ResolveImageBaseUrlAsync(string apiKey, CancellationToken ct)
    {
        var url = $"{TmdbApiBaseUrl}/configuration?api_key={Uri.EscapeDataString(apiKey)}";
        try
        {
            using var client = _httpFactory.CreateClient(TmdbProviderName);
            var config = await client.GetFromJsonAsync<TmdbConfigurationResponse>(url, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(config?.Images?.SecureBaseUrl)
                ? TmdbImageBaseUrl
                : config.Images.SecureBaseUrl;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "TMDB image configuration lookup failed; falling back to default image base URL");
            return TmdbImageBaseUrl;
        }
    }

    private async Task<IReadOnlyList<TmdbProfileImage>> FetchProfileImagesAsync(
        int tmdbPersonId,
        string apiKey,
        CancellationToken ct)
    {
        var url = $"{TmdbApiBaseUrl}/person/{tmdbPersonId}/images?api_key={Uri.EscapeDataString(apiKey)}";
        try
        {
            using var client = _httpFactory.CreateClient(TmdbProviderName);
            var result = await client.GetFromJsonAsync<TmdbPersonImagesResponse>(url, ct).ConfigureAwait(false);
            return result?.Profiles ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "TMDB profile image lookup failed for person {TmdbPersonId}", tmdbPersonId);
            return [];
        }
    }

    private async Task<byte[]?> DownloadImageAsync(string imageUrl, CancellationToken ct)
    {
        try
        {
            using var client = _httpFactory.CreateClient("headshot_download");
            using var response = await client.GetAsync(imageUrl, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is not null && !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return await BoundedHttpContent.ReadImageAsync(response.Content, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "TMDB profile image download failed for {Url}", imageUrl);
            return null;
        }
    }

    private static bool ShouldReplaceCurrentHeadshot(
        Person person,
        IReadOnlyList<EntityAsset> existingAssets,
        TmdbProfileImage selected)
        => ShouldReplaceCurrentHeadshot(person, existingAssets, selected.Width, selected.Height);

    private static bool ShouldReplaceCurrentHeadshot(
        Person person,
        IReadOnlyList<EntityAsset> existingAssets,
        int candidateWidth,
        int candidateHeight)
    {
        var preferred = existingAssets.FirstOrDefault(asset => asset.IsPreferred);
        if (preferred?.IsUserOverride == true)
        {
            return false;
        }

        if (preferred is not null
            && string.Equals(preferred.SourceProvider, TmdbProviderName, StringComparison.OrdinalIgnoreCase)
            && (preferred.WidthPx ?? 0) * (preferred.HeightPx ?? 0) >= candidateWidth * candidateHeight)
        {
            return false;
        }

        if (preferred is null
            || !string.Equals(preferred.SourceProvider, TmdbProviderName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(person.LocalHeadshotPath) || !File.Exists(person.LocalHeadshotPath))
        {
            return true;
        }

        var currentSize = TryMeasureImage(person.LocalHeadshotPath);
        if (currentSize is null)
        {
            return true;
        }

        var currentPixels = currentSize.Value.Width * currentSize.Value.Height;
        var candidatePixels = candidateWidth * candidateHeight;
        return candidatePixels > currentPixels;
    }

    private static (int Width, int Height)? TryMeasureImage(string path)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(path);
            return bitmap is null ? null : (bitmap.Width, bitmap.Height);
        }
        catch
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup only; a failed cleanup should not fail enrichment.
        }
    }

    private static bool IsEligibleMediaPerson(string? role, MediaType mediaType)
    {
        if (mediaType is not (MediaType.Movies or MediaType.TV))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(role))
        {
            return true;
        }

        var normalized = role.Trim();
        return normalized.Contains("actor", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("cast", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("director", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("writer", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("screenwriter", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("composer", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("creator", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("producer", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUsableProfile(TmdbProfileImage image)
    {
        if (string.IsNullOrWhiteSpace(image.FilePath))
        {
            return false;
        }

        if (image.Width < MinimumProfileWidth || image.Height < MinimumProfileHeight)
        {
            return false;
        }

        var ratio = image.Width / (double)image.Height;
        return ratio is >= 0.45d and <= 0.90d;
    }

    private static string BuildTmdbImageUrl(string baseUrl, string size, string filePath)
        => $"{baseUrl.TrimEnd('/')}/{size.Trim('/')}/{filePath.TrimStart('/')}";

    private static string? NormalizeTmdbProfileUrl(string? profileUrl)
    {
        if (string.IsNullOrWhiteSpace(profileUrl))
        {
            return null;
        }

        if (Uri.TryCreate(profileUrl, UriKind.Absolute, out _))
        {
            return profileUrl;
        }

        return BuildTmdbImageUrl(TmdbImageBaseUrl, "original", profileUrl);
    }

    private static string InferExtension(string imageUrl)
    {
        if (Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
        {
            var extension = Path.GetExtension(uri.AbsolutePath);
            if (string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase))
            {
                return ".png";
            }
        }

        return ".jpg";
    }

    private sealed class TmdbFindResponse
    {
        [JsonPropertyName("person_results")]
        public List<TmdbPersonResult> PersonResults { get; set; } = [];
    }

    private sealed class TmdbPersonResult
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }
    }

    private sealed class TmdbConfigurationResponse
    {
        [JsonPropertyName("images")]
        public TmdbImageConfiguration? Images { get; set; }
    }

    private sealed class TmdbImageConfiguration
    {
        [JsonPropertyName("secure_base_url")]
        public string? SecureBaseUrl { get; set; }
    }

    private sealed class TmdbPersonImagesResponse
    {
        [JsonPropertyName("profiles")]
        public List<TmdbProfileImage> Profiles { get; set; } = [];
    }

    private sealed class TmdbProfileImage
    {
        [JsonPropertyName("file_path")]
        public string? FilePath { get; set; }

        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }

        [JsonPropertyName("vote_average")]
        public double VoteAverage { get; set; }

        [JsonPropertyName("vote_count")]
        public int VoteCount { get; set; }
    }
}
