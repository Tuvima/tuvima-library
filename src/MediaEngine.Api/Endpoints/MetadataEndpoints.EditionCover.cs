using System.Security.Cryptography;
using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Matching;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Domain.Authorization;
using MediaEngine.Storage;
using Microsoft.Extensions.Caching.Memory;

namespace MediaEngine.Api.Endpoints;

public static partial class MetadataEndpoints
{
    private sealed record EditionCoverReview(
        Guid RouteEntityId,
        RequestAuthority Actor,
        Guid? CredentialId,
        EditionCoverReviewFacts Facts,
        string CurrentOwnerKind,
        Guid? CurrentOwnerId,
        DateTimeOffset ExpiresAt);

    private static void MapEditionCoverEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/{entityId:guid}/edition-cover-preview", async (
            Guid entityId, MediaEditorEditionCoverPreviewRequestDto request,
            HttpContext http, CatalogueResourceAuthorizationService resources,
            IRequestAuthorityResolver authorityResolver, IMemoryCache cache,
            EpisodeStillReviewReadService artworkReads,
            MediaEditorEditionArtworkRepository editions, CancellationToken ct) =>
        {
            if (request.AssetId == Guid.Empty || request.ArtworkAssetId == Guid.Empty)
                return ApiErrors.BadRequest("Choose an owned file and managed cover variant.");
            if (await resources.EvaluateAssetAsync(http, request.AssetId,
                    ApplicationPermissionIds.MetadataRead, ct) != CatalogueResourceAccess.Allowed
                || await resources.EvaluateAssetAsync(http, request.AssetId,
                    ApplicationPermissionIds.MetadataWrite, ct) != CatalogueResourceAccess.Allowed)
                return ApiErrors.NotFound("Owned file not found.");
            var facts = await editions.ReviewAssetCoverAsync(
                request.AssetId, request.ArtworkAssetId, ct);
            if (facts is null || facts.WorkId != entityId)
                return ApiErrors.Conflict("The file has no editable Edition cover scope.");
            if (!await CanUseEditionManagedArtworkAsync(http, resources, artworkReads,
                    request.ArtworkAssetId, ct))
                return ApiErrors.NotFound("The managed cover variant is unavailable.");
            if (!File.Exists(facts.VariantOriginalPath))
                return ApiErrors.Conflict("The managed cover file is unavailable.");
            foreach (var affected in facts.AffectedAssetLibraries)
                if (await resources.EvaluateAssetAsync(http, affected.AssetId,
                        ApplicationPermissionIds.MetadataRead, ct) != CatalogueResourceAccess.Allowed
                    || await resources.EvaluateAssetAsync(http, affected.AssetId,
                        ApplicationPermissionIds.MetadataWrite, ct) != CatalogueResourceAccess.Allowed)
                    return ApiErrors.NotFound("One or more inheriting files are unavailable for cover editing.");

            var actor = await authorityResolver.ResolveAsync(http, ct);
            if (!TvPairingReviewTokenService.TryBindActor(http, actor, out var credentialId))
                return ApiErrors.Conflict("The editing session cannot be bound to this review.");
            var current = await editions.GetEffectiveAssetCoverAsync(request.AssetId, ct);
            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
            var review = new EditionCoverReview(entityId, actor, credentialId, facts,
                current?.SourceEntityType ?? "None", current?.SourceEntityId, expiresAt);
            cache.Set(EditionCoverReviewKey(token), review, expiresAt);
            return Results.Ok(new MediaEditorEditionCoverPreviewDto(token, expiresAt,
                facts.AssetId, facts.EditionId, facts.WorkId, facts.MediaType,
                review.CurrentOwnerKind, review.CurrentOwnerId, facts.ArtworkAssetId,
                facts.EditionRevision, facts.MusicBrainzReleaseId,
                facts.AffectedAssetLibraries.Select(item =>
                    new MediaEditorEditionCoverAffectedFileDto(
                        item.AssetId, item.LibraryId)).ToArray()));
        })
        .WithName("PreviewEditionCoverPreference")
        .WithSummary("Reviews an Edition cover preference and every owned file that will inherit it.")
        .Produces<MediaEditorEditionCoverPreviewDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite);

        group.MapPost("/{entityId:guid}/edition-cover", async (
            Guid entityId, MediaEditorEditionCoverSaveRequestDto request,
            HttpContext http, CatalogueResourceAuthorizationService resources,
            IRequestAuthorityResolver authorityResolver, IMemoryCache cache,
            EpisodeStillReviewReadService artworkReads,
            MediaEditorEditionArtworkRepository editions, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.ReviewToken)
                || string.IsNullOrWhiteSpace(request.OperationToken)
                || request.OperationToken.Length > 128)
                return ApiErrors.BadRequest("A review token and operation token are required.");
            if (!cache.TryGetValue<EditionCoverReview>(EditionCoverReviewKey(request.ReviewToken),
                    out var review) || review is null || review.ExpiresAt <= DateTimeOffset.UtcNow
                || review.RouteEntityId != entityId)
                return ApiErrors.Conflict("The Edition cover review expired. Review the impact again.");
            var actor = await authorityResolver.ResolveAsync(http, ct);
            if (!TvPairingReviewTokenService.TryBindActor(http, actor, out var credentialId)
                || actor != review.Actor || credentialId != review.CredentialId)
                return ApiErrors.Conflict("The editing session changed. Review the impact again.");
            if (!await CanUseEditionManagedArtworkAsync(http, resources, artworkReads,
                    review.Facts.ArtworkAssetId, ct))
                return ApiErrors.NotFound("The managed cover variant is unavailable.");
            foreach (var affected in review.Facts.AffectedAssetLibraries)
                if (await resources.EvaluateAssetAsync(http, affected.AssetId,
                        ApplicationPermissionIds.MetadataRead, ct) != CatalogueResourceAccess.Allowed
                    || await resources.EvaluateAssetAsync(http, affected.AssetId,
                        ApplicationPermissionIds.MetadataWrite, ct) != CatalogueResourceAccess.Allowed)
                    return ApiErrors.NotFound("One or more inheriting files are unavailable for cover editing.");

            var current = await editions.ReviewAssetCoverAsync(review.Facts.AssetId,
                review.Facts.ArtworkAssetId, ct);
            if (current is null || !SameEditionCoverReview(current, review.Facts))
                return ApiErrors.Conflict("The Edition, cover, or complete file impact changed after review.");
            var result = await editions.CommitReviewedCoverAsync(
                request.OperationToken, review.Facts, ct);
            if (result.Outcome == EditionCoverCommitOutcome.Conflict)
                return ApiErrors.Conflict(result.ConflictReason ?? "The Edition cover changed after review.");
            return Results.Ok(new MediaEditorEditionCoverSaveResultDto(
                result.Outcome.ToString(), review.Facts.EditionId,
                review.Facts.ArtworkAssetId));
        })
        .WithName("SaveEditionCoverPreference")
        .WithSummary("Saves a reviewed Edition cover preference with replay-safe conflict checks.")
        .Produces<MediaEditorEditionCoverSaveResultDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite);
    }

    private static async Task<bool> CanUseEditionManagedArtworkAsync(HttpContext http,
        CatalogueResourceAuthorizationService resources,
        EpisodeStillReviewReadService reads, Guid artworkAssetId, CancellationToken ct)
    {
        foreach (var linkId in reads.GetManagedArtworkLinkIds(artworkAssetId, ct))
            if (await resources.EvaluateArtworkLinkAsync(http, linkId,
                    ApplicationPermissionIds.MetadataRead, ct) == CatalogueResourceAccess.Allowed
                && await resources.EvaluateArtworkLinkAsync(http, linkId,
                    ApplicationPermissionIds.MetadataWrite, ct) == CatalogueResourceAccess.Allowed)
                return true;
        return false;
    }

    private static string EditionCoverReviewKey(string token) =>
        $"media-editor:edition-cover:{token}";

    private static bool SameEditionCoverReview(
        EditionCoverReviewFacts current, EditionCoverReviewFacts reviewed) =>
        current.AssetId == reviewed.AssetId
        && current.EditionId == reviewed.EditionId
        && current.WorkId == reviewed.WorkId
        && current.MediaType == reviewed.MediaType
        && current.ArtworkAssetId == reviewed.ArtworkAssetId
        && current.VariantContentHash == reviewed.VariantContentHash
        && current.VariantOriginalPath == reviewed.VariantOriginalPath
        && current.EditionRevision == reviewed.EditionRevision
        && current.MusicBrainzReleaseId == reviewed.MusicBrainzReleaseId
        && current.AffectedAssetLibraries.SequenceEqual(reviewed.AffectedAssetLibraries);
}
