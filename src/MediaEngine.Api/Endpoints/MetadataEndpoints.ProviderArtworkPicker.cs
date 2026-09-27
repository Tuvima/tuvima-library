using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Metadata;
using MediaEngine.Contracts.Artwork;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Caching.Memory;

namespace MediaEngine.Api.Endpoints;

public static partial class MetadataEndpoints
{
    private sealed record ProviderPickerSession(Guid OwnerId, string OwnerType, string Role,
        string SourceAssetType, string Title, string MediaType, IReadOnlyList<ProviderArtworkCandidate> Items);

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
            Guid entityId, string scopeId, string role, HttpContext http, ArtworkScopeService scopes,
            IImageEnrichmentService images, ArtworkAssetService assets, IMemoryCache cache, CancellationToken ct) =>
        {
            var scope = await ResolveProviderPickerScopeAsync(http, entityId, scopeId);
            if (scope is null) return ApiErrors.NotFound("Artwork scope not found.");
            var sourceType = role switch { "Primary" => scopeId == "episode" ? "EpisodeStill" : scopeId == "season" ? "SeasonPoster" : "CoverArt", "Background" => scopeId == "season" ? "SeasonThumb" : "Background", "Logo" => "Logo", _ => "" };
            if (sourceType.Length == 0 || !(ArtworkScopeService.IsProviderArtworkRefreshSupported(scope) || (NormalizeEditorMediaType(scope.MediaType) == "Comics" && scopeId == "series")))
                return Results.Ok(new ProviderArtworkDiscoveryDto([], "Provider discovery is not available for this artwork type. Use Match & Identity to check the item's identity."));
            var target = await scopes.ResolveProviderArtworkRefreshTargetAsync(scope, ct, discoveryOnly: true);
            if (target.Skipped is not null || target.RepresentativeAssetId is not { } assetId)
                return Results.Ok(new ProviderArtworkDiscoveryDto([], target.Skipped?.Message ?? "No provider identity is available. Use Match & Identity first."));
            if (await http.RequestServices.GetRequiredService<CatalogueResourceAuthorizationService>().EvaluateAssetAsync(
                    http, assetId, ApplicationPermissionIds.MetadataEnrichmentRun, ct) != CatalogueResourceAccess.Allowed)
                return ApiErrors.NotFound("Artwork scope not found.");
            ProviderArtworkDiscovery discovery;
            if (!string.IsNullOrWhiteSpace(target.CoverUrl))
                discovery = role == "Primary"
                    ? new([new("metadata-cover", "Metadata provider", target.CoverUrl, ProviderArtworkThumbnails.ForCover(target.CoverUrl), null, null)])
                    : new([], "This metadata provider only supplies cover artwork.");
            else discovery = await images.DiscoverArtworkAsync(assetId, scopeId, role, ct);
            var ownerId = scope.ArtworkOwnerEntityId ?? scope.FieldEntityId;
            var ownerType = scope.ArtworkOwnerEntityKind ?? scope.FieldEntityKind;
            var workspace = await assets.GetEntityAsync(ownerType, ownerId, ct);
            cache.Set(ProviderPickerKey(entityId, scopeId, role), new ProviderPickerSession(ownerId, ownerType, role,
                sourceType, scope.DisplayTitle, scope.MediaType, discovery.Items), TimeSpan.FromMinutes(15));
            return Results.Ok(new ProviderArtworkDiscoveryDto(discovery.Items.Select(c => new ProviderArtworkCandidateDto(
                c.Id, c.Provider, c.ThumbnailUrl, c.Width, c.Height,
                workspace.Variants.Any(v => v.Role == role && v.SourceUrl == c.Url))).ToList(), discovery.Message));
        }).WithName("DiscoverScopedProviderArtwork")
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataEnrichmentRun)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataEnrichmentRun);

        group.MapPost("/{entityId:guid}/artwork/{scopeId}/provider-candidates/{role}", async (
            Guid entityId, string scopeId, string role, ProviderArtworkImportRequest request, HttpContext http,
            ArtworkScopeService scopes, ArtworkAssetService assets, IMemoryCache cache, CancellationToken ct) =>
        {
            var scope = await ResolveProviderPickerScopeAsync(http, entityId, scopeId);
            if (scope is null) return ApiErrors.NotFound("Artwork scope not found.");
            var target = await scopes.ResolveProviderArtworkRefreshTargetAsync(scope, ct, discoveryOnly: true);
            if (target.Skipped is not null) return ApiErrors.BadRequest(target.Skipped.Message ?? "Provider access is unavailable.");
            if (target.RepresentativeAssetId is not { } assetId || await http.RequestServices.GetRequiredService<CatalogueResourceAuthorizationService>().EvaluateAssetAsync(
                    http, assetId, ApplicationPermissionIds.MetadataEnrichmentRun, ct) != CatalogueResourceAccess.Allowed)
                return ApiErrors.NotFound("Artwork scope not found.");
            if (request.CandidateIds.Count is < 1 or > 150) return ApiErrors.BadRequest("Select between 1 and 150 images.");
            if (!cache.TryGetValue<ProviderPickerSession>(ProviderPickerKey(entityId, scopeId, role), out var session) || session is null
                || session.OwnerId != (scope.ArtworkOwnerEntityId ?? scope.FieldEntityId))
                return ApiErrors.BadRequest("Artwork results expired. Reload the provider gallery and try again.");
            return Results.Ok(await assets.ImportProviderCandidatesAsync(session.OwnerType, session.OwnerId,
                session.Role, session.SourceAssetType, session.Title, session.MediaType, session.Items, request.CandidateIds, ct));
        }).WithName("ImportSelectedProviderArtwork")
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite);
    }
}
