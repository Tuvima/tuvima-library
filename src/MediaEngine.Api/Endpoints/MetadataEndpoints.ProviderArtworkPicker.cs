using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Metadata;
using MediaEngine.Contracts.Artwork;
using MediaEngine.Domain;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Caching.Memory;

namespace MediaEngine.Api.Endpoints;

public static partial class MetadataEndpoints
{
    private sealed record ProviderPickerSession(Guid OwnerId, string OwnerType, string Role,
        string SourceAssetType, string Title, string MediaType, ProviderArtworkRequestContextDto Context,
        IReadOnlyList<ProviderArtworkCandidate> Items);

    private static string ProviderPickerKey(Guid entityId, string scope, string role) => $"artwork-picker:{entityId}:{scope}:{role}";

    private static async Task<EditorScopeResolution?> ResolveProviderPickerScopeAsync(HttpContext http, Guid entityId, string scopeId)
    {
        var services = http.RequestServices;
        var context = await ResolveEditorScopeContextAsync(entityId,
            services.GetRequiredService<ICanonicalValueRepository>(), services.GetRequiredService<ILibraryItemRepository>(),
            services.GetRequiredService<IMetadataEditorRepository>(), http.RequestAborted);
        var scope = context?.Scopes.FirstOrDefault(s => s.ScopeId == scopeId);
        return scope is not null && scope.CanEditArtwork && await HasEditorScopeAccessAsync(http,
            services.GetRequiredService<CatalogueResourceAuthorizationService>(), scope,
            ApplicationPermissionIds.MetadataEnrichmentRun, http.RequestAborted) ? scope : null;
    }

    private static void MapProviderArtworkPickerEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/{entityId:guid}/artwork/{scopeId}/provider-candidates/{role}", async (
            Guid entityId, string scopeId, string role, int? page, int? pageSize,
            string? sourceAssetType, string? provider, string? providerItemId, string? releaseId, string? orderContext,
            HttpContext http, ArtworkScopeService scopes,
            IImageEnrichmentService images, TvdbRetailClient tvdb,
            ICanonicalValueRepository canonicals, ArtworkAssetService assets, IMemoryCache cache, CancellationToken ct) =>
        {
            var scope = await ResolveProviderPickerScopeAsync(http, entityId, scopeId);
            if (scope is null)
            {
                return ApiErrors.NotFound("Artwork scope not found.");
            }
            var sourceType = role switch { "Primary" => scopeId == "episode" ? "EpisodeStill" : scopeId == "season" ? "SeasonPoster" : "CoverArt", "Background" => scopeId == "season" ? "SeasonThumb" : "Background", "Logo" => "Logo", _ => "" };
            if (sourceType.Length == 0 || !(ArtworkScopeService.IsProviderArtworkRefreshSupported(scope) || (NormalizeEditorMediaType(scope.MediaType) == "Comics" && scopeId == "series")))
            {
                return Results.Ok(new ProviderArtworkDiscoveryDto([], "Provider discovery is not available for this artwork type. Use Match & Identity to check the item's identity."));
            }
            var requestContext = await ResolveProviderArtworkRequestContextAsync(scope, role, sourceType, canonicals, ct);
            if (requestContext is null)
            {
                return Results.Ok(new ProviderArtworkDiscoveryDto([], "No confirmed provider identity is available for this exact artwork scope. Use Match & Identity first."));
            }
            if (!MatchesProviderArtworkRequestContext(requestContext, sourceAssetType, provider, providerItemId, releaseId, orderContext))
            {
                return Results.Ok(new ProviderArtworkDiscoveryDto([], "The selected artwork context changed. Reload this artwork role before contacting its provider.", Context: requestContext));
            }
            var target = await scopes.ResolveProviderArtworkRefreshTargetAsync(scope, ct, discoveryOnly: true);
            if (target.Skipped is not null || target.RepresentativeAssetId is not { } assetId)
            {
                return Results.Ok(new ProviderArtworkDiscoveryDto([], target.Skipped?.Message ?? "No provider identity is available. Use Match & Identity first."));
            }
            if (await http.RequestServices.GetRequiredService<CatalogueResourceAuthorizationService>().EvaluateAssetAsync(
                    http, assetId, ApplicationPermissionIds.MetadataEnrichmentRun, ct) != CatalogueResourceAccess.Allowed)
            {
                return ApiErrors.NotFound("Artwork scope not found.");
            }
            ProviderArtworkDiscovery discovery;
            string? attributionUrl = null;
            if (requestContext.Provider == "tvdb")
            {
                if (!tvdb.IsConfigured())
                {
                    return Results.Ok(new ProviderArtworkDiscoveryDto([], "Connect TheTVDB in Settings to browse its artwork."));
                }
                var found = await DiscoverTvdbArtworkAsync(scope, requestContext.ProviderItemId, tvdb, ct);
                discovery = new(found.Where(item => item.Role == role).Select(item => item.Candidate with
                    {
                        ThumbnailUrl = CreateTvdbPreviewUrl(cache, entityId, scope.FieldEntityId,
                            item.Candidate.ThumbnailUrl.Length > 0 ? item.Candidate.ThumbnailUrl : item.Candidate.Url) ?? string.Empty,
                    }).ToList(),
                    "Artwork from TheTVDB. Select images to add to managed artwork.");
                attributionUrl = $"https://thetvdb.com/{scopeId switch { "season" => "seasons", "episode" => "episodes", _ => "series" }}/{requestContext.ProviderItemId}";
            }
            else if (!string.IsNullOrWhiteSpace(target.CoverUrl))
            {
                discovery = role == "Primary"
                        ? new([new("metadata-cover", "Metadata provider", target.CoverUrl, ProviderArtworkThumbnails.ForCover(target.CoverUrl), null, null)])
                        : new([], "This metadata provider only supplies cover artwork.");
            }
            else
            {
                discovery = await images.DiscoverArtworkAsync(assetId, scopeId, role, ct);
            }
            var deduplicated = discovery.Items
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Id) && !string.IsNullOrWhiteSpace(candidate.Url))
                .DistinctBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
                .DistinctBy(candidate => candidate.Url, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var requestedPage = Math.Max(1, page ?? 1);
            var requestedPageSize = Math.Clamp(pageSize ?? 48, 1, 100);
            var pageItems = deduplicated.Skip((requestedPage - 1) * requestedPageSize).Take(requestedPageSize).ToList();
            var ownerId = scope.ArtworkOwnerEntityId ?? scope.FieldEntityId;
            var ownerType = scope.ArtworkOwnerEntityKind ?? scope.FieldEntityKind;
            var workspace = await assets.GetEntityAsync(ownerType, ownerId, ct);
            cache.Set(ProviderPickerKey(entityId, scopeId, role), new ProviderPickerSession(ownerId, ownerType, role,
                sourceType, scope.DisplayTitle, scope.MediaType, requestContext, deduplicated), TimeSpan.FromMinutes(15));
            return Results.Ok(new ProviderArtworkDiscoveryDto(pageItems.Select(c => new ProviderArtworkCandidateDto(
                c.Id, c.Provider, c.ThumbnailUrl, c.Width, c.Height,
                workspace.Variants.Any(v => v.Role == role && v.SourceUrl == c.Url))).ToList(), discovery.Message,
                attributionUrl, requestContext, requestedPage, requestedPageSize, deduplicated.Count,
                requestedPage * requestedPageSize < deduplicated.Count));
        }).WithName("DiscoverScopedProviderArtwork")
          .Produces<ProviderArtworkDiscoveryDto>(StatusCodes.Status200OK)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataEnrichmentRun)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataEnrichmentRun);

        group.MapPost("/{entityId:guid}/artwork/{scopeId}/provider-candidates/{role}", async (
            Guid entityId, string scopeId, string role, ProviderArtworkImportRequest request, HttpContext http,
            ArtworkScopeService scopes, ArtworkAssetService assets, ICanonicalValueRepository canonicals,
            IMemoryCache cache, CancellationToken ct) =>
        {
            var scope = await ResolveProviderPickerScopeAsync(http, entityId, scopeId);
            if (scope is null)
            {
                return ApiErrors.NotFound("Artwork scope not found.");
            }
            var target = await scopes.ResolveProviderArtworkRefreshTargetAsync(scope, ct, discoveryOnly: true);
            if (target.Skipped is not null)
            {
                return ApiErrors.BadRequest(target.Skipped.Message ?? "Provider access is unavailable.");
            }
            if (target.RepresentativeAssetId is not { } assetId || await http.RequestServices.GetRequiredService<CatalogueResourceAuthorizationService>().EvaluateAssetAsync(
                    http, assetId, ApplicationPermissionIds.MetadataEnrichmentRun, ct) != CatalogueResourceAccess.Allowed)
            {
                return ApiErrors.NotFound("Artwork scope not found.");
            }
            if (request.CandidateIds.Count is < 1 or > 150)
            {
                return ApiErrors.BadRequest("Select between 1 and 150 images.");
            }
            if (!cache.TryGetValue<ProviderPickerSession>(ProviderPickerKey(entityId, scopeId, role), out var session) || session is null
                || session.OwnerId != (scope.ArtworkOwnerEntityId ?? scope.FieldEntityId))
            {
                return ApiErrors.BadRequest("Artwork results expired. Reload the provider gallery and try again.");
            }
            var currentContext = await ResolveProviderArtworkRequestContextAsync(
                scope, role, session.SourceAssetType, canonicals, ct);
            if (currentContext is null || currentContext != session.Context)
            {
                return ApiErrors.Conflict("The provider identity or selected artwork context changed. Reload the provider gallery before importing.");
            }
            return Results.Ok(await assets.ImportProviderCandidatesAsync(session.OwnerType, session.OwnerId,
                session.Role, session.SourceAssetType, session.Title, session.MediaType, session.Items, request.CandidateIds, ct));
        }).WithName("ImportSelectedProviderArtwork")
          .Produces<ProviderArtworkImportResultDto>(StatusCodes.Status200OK)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite);
    }

    internal static bool MatchesProviderArtworkRequestContext(
        ProviderArtworkRequestContextDto current,
        string? sourceAssetType,
        string? provider,
        string? providerItemId,
        string? releaseId,
        string? orderContext) =>
        MatchesOptional(sourceAssetType, current.SourceAssetType)
        && MatchesOptional(provider, current.Provider)
        && MatchesOptional(providerItemId, current.ProviderItemId)
        && MatchesOptional(releaseId, current.ReleaseId)
        && MatchesOptional(orderContext, current.OrderContext);

    private static bool MatchesOptional(string? requested, string? current) =>
        string.IsNullOrWhiteSpace(requested)
        || string.Equals(requested.Trim(), current?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static async Task<ProviderArtworkRequestContextDto?> ResolveProviderArtworkRequestContextAsync(
        EditorScopeResolution scope,
        string role,
        string sourceAssetType,
        ICanonicalValueRepository canonicals,
        CancellationToken ct)
    {
        var values = BuildLatestCanonicalMap(await canonicals.GetByEntityAsync(scope.FieldEntityId, ct));
        var provider = GetCanonicalValue(values, MetadataFieldConstants.IdentityProvider)?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(provider))
        {
            return null;
        }
        var mediaType = NormalizeEditorMediaType(scope.MediaType);
        var idKey = (mediaType, scope.ScopeId, provider) switch
        {
            ("TV", "series", "tvdb") => BridgeIdKeys.TvdbId,
            ("TV", "season", "tvdb") => BridgeIdKeys.TvdbSeasonId,
            ("TV", "episode", "tvdb") => BridgeIdKeys.TvdbEpisodeId,
            ("Movies", _, "tmdb") => BridgeIdKeys.TmdbId,
            ("Comics", "series" or "volume", "comicvine") => BridgeIdKeys.ComicVineVolumeId,
            ("Comics", _, "comicvine") => BridgeIdKeys.ComicVineId,
            ("Music", _, "musicbrainz") => BridgeIdKeys.MusicBrainzReleaseGroupId,
            ("Music", _, "apple" or "apple_api" or "applemusic") => BridgeIdKeys.AppleMusicCollectionId,
            ("Books" or "Audiobooks", _, "apple" or "apple_api" or "applebooks") => BridgeIdKeys.AppleBooksId,
            ("Books" or "Audiobooks", _, "openlibrary" or "open_library") => BridgeIdKeys.OpenLibraryId,
            ("Audiobooks", _, "audible") => BridgeIdKeys.AudibleId,
            ("Books" or "Audiobooks", _, "amazon") => BridgeIdKeys.Asin,
            _ => string.Empty,
        };
        var providerId = idKey.Length == 0 ? null : GetCanonicalValue(values, idKey);
        providerId ??= GetCanonicalValue(values, "provider_item_id");
        providerId ??= mediaType is "Books" or "Audiobooks"
            ? StringHelpers.FirstNonBlank(
                GetCanonicalValue(values, BridgeIdKeys.Isbn13),
                GetCanonicalValue(values, BridgeIdKeys.Isbn),
                GetCanonicalValue(values, BridgeIdKeys.Asin))
            : null;
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return null;
        }
        var release = StringHelpers.FirstNonBlank(
            GetCanonicalValue(values, BridgeIdKeys.MusicBrainzReleaseId),
            GetCanonicalValue(values, "edition_release_id"));
        var order = string.Join(";", new[] { "season_number", "episode_number", "disc_number", "track_number", "volume_number" }
            .Select(key => (Key: key, Value: GetCanonicalValue(values, key)))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Value))
            .Select(entry => $"{entry.Key}={entry.Value}"));
        return new(scope.ScopeId, role, sourceAssetType, provider, providerId.Trim(), release,
            string.IsNullOrWhiteSpace(order) ? null : order);
    }
}
