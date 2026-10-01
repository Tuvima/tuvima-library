using System.Security.Cryptography;
using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Matching;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using Microsoft.Extensions.Caching.Memory;

namespace MediaEngine.Api.Endpoints;

public static partial class MetadataEndpoints
{
    private sealed record SharedArtworkReview(
        Guid RouteEntityId, string PairingReviewToken,
        RequestAuthority Actor, Guid? CredentialId, string ChoiceSignature,
        Guid ShowWorkId, Guid OwnerWorkId, string Scope, string Role,
        Guid ArtworkAssetId, string VariantContentHash, string OwnerRevision,
        IReadOnlyList<VerifiedTvArtworkAssetReview> AffectedAssets,
        DateTimeOffset ExpiresAt);

    private static void MapParentFirstSharedArtworkEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/{entityId:guid}/pairing-shared-artwork-preview", async (
            Guid entityId, MediaEditorPairingSharedArtworkPreviewRequestDto request,
            HttpContext http, CatalogueResourceAuthorizationService resources,
            IRequestAuthorityResolver authorityResolver, IMemoryCache cache,
            PairingAssetReadService pairingAssets,
            TvPairingLocalTargetReadService localTargetReader,
            EpisodeStillReviewReadService artworkReads,
            MediaEditorCommitRepository commits, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.ReviewToken)
                || request.Accepted is null || request.ExcludedAssetIds is null
                || request.OwnerWorkId == Guid.Empty || request.ArtworkAssetId == Guid.Empty
                || !IsSupportedSharedArtworkRole(request.Scope, request.Role)
                || !TryChoiceSignature(request.Accepted, request.ExcludedAssetIds,
                    new TvPairingReviewTokenService(cache).Get(request.ReviewToken)?
                        .SelectedAssets.Keys ?? [], out var signature))
                return ApiErrors.BadRequest("Choose a reviewed TV show or season artwork preference.");

            var pairing = new TvPairingReviewTokenService(cache).Get(request.ReviewToken);
            if (pairing is null || pairing.RouteEntityId != entityId)
                return ApiErrors.Conflict("The pairing review expired. Refresh and review the files again.");
            var actor = await authorityResolver.ResolveAsync(http, ct);
            if (!TvPairingReviewTokenService.TryBindActor(http, actor, out var credentialId)
                || actor != pairing.Actor || credentialId != pairing.ApplicationCredentialId)
                return ApiErrors.Conflict("The editing session changed. Refresh and review again.");
            if (request.Scope == "TvShow" && request.OwnerWorkId != pairing.ShowWorkId)
                return ApiErrors.Conflict("The artwork owner is outside the reviewed show.");

            var currentSources = pairingAssets.Load(
                request.Accepted.Select(item => item.AssetId).ToArray(), ct);
            var currentTargets = localTargetReader.Resolve(pairing.ShowWorkId,
                pairing.TvdbSeriesId, pairing.Catalogue, ct);
            var chosen = new List<(PairingAssetRow Source, TvPairingLocalTarget Target)>();
            foreach (var accepted in request.Accepted)
            {
                if (!pairing.SelectedAssets.TryGetValue(accepted.AssetId, out var source)
                    || !currentSources.TryGetValue(accepted.AssetId, out var currentSource)
                    || !SameReviewedSource(source, currentSource)
                    || !pairing.Targets.TryGetValue(accepted.CandidateId, out var target)
                    || !currentTargets.TryGetValue(accepted.CandidateId, out var currentTarget)
                    || target.WorkId != currentTarget.WorkId
                    || target.WorkKind != currentTarget.WorkKind
                    || target.IdentityRevision != currentTarget.IdentityRevision
                    || target.ShowWorkId != pairing.ShowWorkId
                    || target.SeriesId != pairing.TvdbSeriesId
                    || target.WorkId == source.WorkId
                    || target.WorkKind is not ("child" or "catalog"))
                    return ApiErrors.Conflict("A selected file or target changed after review.");
                if (await resources.EvaluateAssetAsync(http, accepted.AssetId,
                        ApplicationPermissionIds.MetadataMatch, ct) != CatalogueResourceAccess.Allowed
                    || await resources.EvaluateAssetAsync(http, accepted.AssetId,
                        ApplicationPermissionIds.MetadataWrite, ct) != CatalogueResourceAccess.Allowed)
                    return ApiErrors.NotFound("A selected file is no longer editable.");
                chosen.Add((source, target));
            }
            if (request.Scope == "TvSeason" && !chosen.Any(item =>
                    item.Source.SeasonWorkId == request.OwnerWorkId
                    || item.Target.SeasonWorkId == request.OwnerWorkId))
                return ApiErrors.Conflict("The artwork season is outside this pairing choice.");

            if (!await CanEditSharedArtworkOwnerAsync(http, resources, artworkReads,
                    pairing.ShowWorkId, request.OwnerWorkId, request.Scope, ct)
                || !await CanUseSharedManagedArtworkAsync(http, resources,
                    artworkReads, request.ArtworkAssetId, ct))
                return ApiErrors.NotFound("The artwork owner or managed variant is unavailable.");
            var variant = artworkReads.LoadManagedVariant(request.ArtworkAssetId);
            if (variant is null || string.IsNullOrWhiteSpace(variant.ContentHash)
                || string.IsNullOrWhiteSpace(variant.OriginalPath)
                || !File.Exists(variant.OriginalPath))
                return ApiErrors.Conflict("The managed artwork file is unavailable.");
            var ownerRevision = await commits.GetTvPreferredArtworkOwnerRevisionAsync(
                request.OwnerWorkId, request.Scope, request.Role, ct);
            if (ownerRevision is null)
                return ApiErrors.Conflict("The artwork owner changed after review.");
            var impact = artworkReads.ReadPostMoveSharedImpact(request.OwnerWorkId,
                request.Scope, chosen);
            if (impact is null || impact.Count is < 1 or > 1000)
                return ApiErrors.Conflict("The artwork impact is unavailable or exceeds 1,000 files.");
            foreach (var affected in impact)
                if (await resources.EvaluateAssetAsync(http, affected.AssetId,
                        ApplicationPermissionIds.MetadataRead, ct) != CatalogueResourceAccess.Allowed
                    || await resources.EvaluateAssetAsync(http, affected.AssetId,
                        ApplicationPermissionIds.MetadataWrite, ct) != CatalogueResourceAccess.Allowed)
                    return ApiErrors.NotFound("An affected file is not available for artwork editing.");
            var revisions = await commits.GetTvArtworkAssetRevisionsAsync(
                impact.Select(item => item.AssetId).ToArray(), ct);
            if (revisions.Count != impact.Count)
                return ApiErrors.Conflict("An affected file changed after artwork review.");
            var reviewedAssets = impact.Select(item => new VerifiedTvArtworkAssetReview(
                item.AssetId, item.LibraryId, revisions[item.AssetId])).ToArray();
            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
            if (expiresAt > pairing.ExpiresAt) expiresAt = pairing.ExpiresAt;
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
            cache.Set(SharedArtworkReviewKey(token), new SharedArtworkReview(entityId,
                request.ReviewToken, actor, credentialId, signature, pairing.ShowWorkId,
                request.OwnerWorkId, request.Scope, request.Role,
                request.ArtworkAssetId, variant.ContentHash, ownerRevision,
                reviewedAssets, expiresAt), expiresAt);
            return Results.Ok(new MediaEditorPairingSharedArtworkPreviewDto(token,
                expiresAt, request.OwnerWorkId, request.Scope, request.Role,
                request.ArtworkAssetId, ownerRevision,
                impact.Select(item => new MediaEditorPairingArtworkAffectedFileDto(
                    item.AssetId, item.LibraryId)).ToArray()));
        })
        .WithName("PreviewReviewedTvSharedArtwork")
        .WithSummary("Review one TV show or season artwork preference with every affected file.")
        .Produces<MediaEditorPairingSharedArtworkPreviewDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite);
    }

    private static async Task<(VerifiedTvPreferredArtworkAssignment? Assignment, IResult? Error)>
        ValidateReviewedSharedArtworkForSaveAsync(
            Guid entityId, MediaEditorPairingSaveRequestDto request,
            TvPairingReviewSnapshot pairing, IReadOnlyList<VerifiedTvEpisodeMove> moves,
            HttpContext http, CatalogueResourceAuthorizationService resources,
            IRequestAuthorityResolver authorityResolver, IMemoryCache cache,
            EpisodeStillReviewReadService artworkReads,
            MediaEditorCommitRepository commits, CancellationToken ct,
            bool receiptProbe = false)
    {
        if (string.IsNullOrWhiteSpace(request.SharedArtworkReviewToken)) return (null, null);
        if (!TryChoiceSignature(request.Accepted, request.ExcludedAssetIds,
                pairing.SelectedAssets.Keys, out var signature))
            return (null, ApiErrors.BadRequest("The reviewed pairing selection changed."));
        if (!cache.TryGetValue<SharedArtworkReview>(
                SharedArtworkReviewKey(request.SharedArtworkReviewToken), out var review)
            || review is null || review.ExpiresAt <= DateTimeOffset.UtcNow
            || review.RouteEntityId != entityId
            || review.PairingReviewToken != request.ReviewToken
            || review.ChoiceSignature != signature
            || review.ShowWorkId != pairing.ShowWorkId
            || !IsSupportedSharedArtworkRole(review.Scope, review.Role)
            || (review.Scope == "TvShow" && review.OwnerWorkId != pairing.ShowWorkId)
            || (review.Scope == "TvSeason" && !moves.Any(move =>
                move.ExpectedSourceSeasonWorkId == review.OwnerWorkId
                || move.ExpectedTargetSeasonWorkId == review.OwnerWorkId)))
            return (null, ApiErrors.Conflict("The shared-artwork review expired or pairing choice changed."));
        var actor = await authorityResolver.ResolveAsync(http, ct);
        if (!TvPairingReviewTokenService.TryBindActor(http, actor, out var credentialId)
            || actor != review.Actor || credentialId != review.CredentialId)
            return (null, ApiErrors.Conflict("The editing session changed. Refresh artwork review."));
        if (!await CanEditSharedArtworkOwnerAsync(http, resources, artworkReads,
                pairing.ShowWorkId, review.OwnerWorkId, review.Scope, ct)
            || !await CanUseSharedManagedArtworkAsync(http, resources,
                artworkReads, review.ArtworkAssetId, ct))
            return (null, ApiErrors.NotFound("The artwork owner or variant is no longer editable."));
        foreach (var affected in review.AffectedAssets)
            if (await resources.EvaluateAssetAsync(http, affected.AssetId,
                    ApplicationPermissionIds.MetadataRead, ct) != CatalogueResourceAccess.Allowed
                || await resources.EvaluateAssetAsync(http, affected.AssetId,
                    ApplicationPermissionIds.MetadataWrite, ct) != CatalogueResourceAccess.Allowed)
                return (null, ApiErrors.NotFound("An artwork-affected file is no longer editable."));

        // Receipt probes still reauthorize every resource, but use the frozen
        // plan so Storage can compare its exact hash after a successful move.
        if (!receiptProbe)
        {
            var variant = artworkReads.LoadManagedVariant(review.ArtworkAssetId);
            if (variant is null || variant.ContentHash != review.VariantContentHash
                || string.IsNullOrWhiteSpace(variant.OriginalPath)
                || !File.Exists(variant.OriginalPath))
                return (null, ApiErrors.Conflict("The managed artwork variant changed after review."));
            if (await commits.GetTvPreferredArtworkOwnerRevisionAsync(review.OwnerWorkId,
                    review.Scope, review.Role, ct) != review.OwnerRevision)
                return (null, ApiErrors.Conflict("The preferred artwork changed after review."));
            var revisions = await commits.GetTvArtworkAssetRevisionsAsync(
                review.AffectedAssets.Select(item => item.AssetId).ToArray(), ct);
            if (revisions.Count != review.AffectedAssets.Count
                || review.AffectedAssets.Any(item =>
                    !revisions.TryGetValue(item.AssetId, out var current)
                    || current != item.IdentityRevision))
                return (null, ApiErrors.Conflict("An affected file changed after artwork review."));
            var chosen = moves.Select(move =>
                (Source: pairing.SelectedAssets[move.AssetId],
                 Target: pairing.Targets[move.TargetTvdbEpisodeId])).ToArray();
            var impact = artworkReads.ReadPostMoveSharedImpact(review.OwnerWorkId,
                review.Scope, chosen);
            if (impact is null || !impact.SequenceEqual(review.AffectedAssets.Select(item =>
                    new VerifiedArtworkAssetLibrary(item.AssetId, item.LibraryId))))
                return (null, ApiErrors.Conflict("The artwork's affected file set changed after review."));
        }
        return (new VerifiedTvPreferredArtworkAssignment(review.OwnerWorkId,
            review.Scope, review.Role, review.ArtworkAssetId,
            review.VariantContentHash, review.OwnerRevision,
            review.AffectedAssets), null);
    }

    private static bool IsSupportedSharedArtworkRole(string scope, string role) =>
        (scope, role) is (("TvShow", "Primary") or ("TvShow", "Background")
            or ("TvShow", "Logo") or ("TvSeason", "Primary"));

    private static async Task<bool> CanEditSharedArtworkOwnerAsync(HttpContext http,
        CatalogueResourceAuthorizationService resources,
        EpisodeStillReviewReadService reads, Guid showWorkId, Guid ownerWorkId,
        string scope, CancellationToken ct)
    {
        if (await resources.EvaluateEntityAsync(http, "Work", showWorkId,
                ApplicationPermissionIds.MetadataRead, ct) != CatalogueResourceAccess.Allowed
            || await resources.EvaluateEntityAsync(http, "Work", showWorkId,
                ApplicationPermissionIds.MetadataWrite, ct) != CatalogueResourceAccess.Allowed)
            return false;
        if (scope == "TvShow") return ownerWorkId == showWorkId;
        // An unowned target season has no leaf asset through which the resource
        // authorizer can prove access. The owned show plus exact same-show
        // lineage and every affected file establish that case.
        if (!reads.HasCurrentOwnedAsset(ownerWorkId, scope)) return true;
        return await resources.EvaluateEntityAsync(http, "Work", ownerWorkId,
                ApplicationPermissionIds.MetadataRead, ct) == CatalogueResourceAccess.Allowed
            && await resources.EvaluateEntityAsync(http, "Work", ownerWorkId,
                ApplicationPermissionIds.MetadataWrite, ct) == CatalogueResourceAccess.Allowed;
    }

    private static async Task<bool> CanUseSharedManagedArtworkAsync(HttpContext http,
        CatalogueResourceAuthorizationService resources,
        EpisodeStillReviewReadService reads, Guid artId, CancellationToken ct)
    {
        foreach (var linkId in reads.GetManagedArtworkLinkIds(artId, ct))
            if (await resources.EvaluateArtworkLinkAsync(http, linkId,
                    ApplicationPermissionIds.MetadataRead, ct) == CatalogueResourceAccess.Allowed
                && await resources.EvaluateArtworkLinkAsync(http, linkId,
                    ApplicationPermissionIds.MetadataWrite, ct) == CatalogueResourceAccess.Allowed)
                return true;
        return false;
    }

    private static string SharedArtworkReviewKey(string token) =>
        $"media-editor:tv-shared-artwork:{token}";
}
