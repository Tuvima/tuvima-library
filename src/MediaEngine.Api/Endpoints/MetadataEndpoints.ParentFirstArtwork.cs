using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    private sealed record EpisodeStillReview(
        Guid RouteEntityId,
        string PairingReviewToken,
        RequestAuthority Actor,
        Guid? CredentialId,
        string ChoiceSignature,
        string TargetCandidateId,
        Guid OwnerWorkId,
        string TargetWorkKind,
        Guid ShowWorkId,
        Guid ArtworkAssetId,
        string VariantContentHash,
        string PreferenceRevision,
        IReadOnlyList<VerifiedArtworkAssetLibrary> AffectedLibraries,
        DateTimeOffset ExpiresAt);

    private static void MapParentFirstArtworkEndpoints(RouteGroupBuilder group)
    {
        MapParentFirstSharedArtworkEndpoints(group);
        group.MapPost("/{entityId:guid}/pairing-artwork-preview", async (
            Guid entityId,
            MediaEditorPairingArtworkPreviewRequestDto request,
            HttpContext http,
            CatalogueResourceAuthorizationService resources,
            IRequestAuthorityResolver authorityResolver,
            IMemoryCache cache,
            PairingAssetReadService pairingAssets,
            TvPairingLocalTargetReadService localTargetReader,
            EpisodeStillReviewReadService artworkReads,
            MediaEditorCommitRepository commits,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.ReviewToken)
                || request.Accepted is null || request.ExcludedAssetIds is null
                || request.Accepted.Count is < 1 or > 1000
                || request.TargetCandidateId is null
                || request.TargetCandidateId.Length is < 1 or > 64
                || request.ArtworkAssetId == Guid.Empty)
                return ApiErrors.BadRequest("Choose a reviewed target episode and managed artwork variant.");

            var pairing = new TvPairingReviewTokenService(cache).Get(request.ReviewToken);
            if (pairing is null || pairing.RouteEntityId != entityId)
                return ApiErrors.Conflict("The pairing review expired. Refresh and review the files again.");
            var actor = await authorityResolver.ResolveAsync(http, ct);
            if (!TvPairingReviewTokenService.TryBindActor(http, actor, out var credentialId)
                || actor != pairing.Actor || credentialId != pairing.ApplicationCredentialId)
                return ApiErrors.Conflict("The editing session changed. Refresh and review the files again.");
            if (!TryChoiceSignature(request.Accepted, request.ExcludedAssetIds,
                    pairing.SelectedAssets.Keys, out var choiceSignature))
                return ApiErrors.BadRequest("Accepted and excluded files must account for the reviewed selection.");
            if (!pairing.Targets.TryGetValue(request.TargetCandidateId, out var target)
                || target.WorkKind is not ("child" or "catalog")
                || target.ShowWorkId != pairing.ShowWorkId
                || target.SeriesId != pairing.TvdbSeriesId)
                return ApiErrors.Conflict("The episode is outside the reviewed TV catalogue.");

            // Re-resolve the local target; the original token is an immutable
            // review, while the local catalogue can change before this preview.
            var currentTargets = localTargetReader.Resolve(
                pairing.ShowWorkId, pairing.TvdbSeriesId, pairing.Catalogue, ct);
            if (!currentTargets.TryGetValue(request.TargetCandidateId, out var currentTarget)
                || currentTarget.WorkId != target.WorkId
                || currentTarget.WorkKind != target.WorkKind
                || currentTarget.IdentityRevision != target.IdentityRevision)
                return ApiErrors.Conflict("The target episode changed after pairing review.");

            var acceptedIds = request.Accepted.Select(item => item.AssetId).ToArray();
            var currentAssets = pairingAssets.Load(acceptedIds, ct);
            var chosen = new List<(PairingAssetRow Source, TvPairingLocalTarget Target)>();
            foreach (var accepted in request.Accepted)
            {
                if (!pairing.SelectedAssets.TryGetValue(accepted.AssetId, out var source)
                    || !currentAssets.TryGetValue(accepted.AssetId, out var current)
                    || !SameReviewedSource(source, current)
                    || !pairing.Targets.TryGetValue(accepted.CandidateId, out var selectedTarget)
                    || selectedTarget.ShowWorkId != pairing.ShowWorkId
                    || selectedTarget.SeriesId != pairing.TvdbSeriesId
                    || selectedTarget.WorkId == source.WorkId
                    || selectedTarget.WorkKind is not ("child" or "catalog"))
                    return ApiErrors.Conflict("A selected file or episode mapping changed after review.");
                chosen.Add((source, selectedTarget));
            }
            if (!chosen.Any(item => item.Target.WorkId == target.WorkId))
                return ApiErrors.BadRequest("The artwork target must receive at least one accepted file.");

            if (!await CanEditEpisodeArtworkOwnerAsync(http, resources, target.WorkId,
                    target.WorkKind, pairing.ShowWorkId, ct))
                return ApiErrors.NotFound("The target episode is no longer editable.");
            if (!await CanUseManagedArtworkAsync(http, resources, artworkReads, request.ArtworkAssetId, ct))
                return ApiErrors.NotFound("The managed artwork variant is unavailable.");

            var variant = artworkReads.LoadManagedVariant(request.ArtworkAssetId);
            if (variant is null || string.IsNullOrWhiteSpace(variant.OriginalPath)
                || !File.Exists(variant.OriginalPath))
                return ApiErrors.Conflict("The managed artwork file is unavailable. Refresh the artwork gallery.");
            var revision = await commits.GetEpisodeStillPreferenceRevisionAsync(target.WorkId, ct);
            if (revision is null)
                return ApiErrors.Conflict("The target is no longer a TV episode.");

            var impact = artworkReads.ReadPostMoveImpact(target.WorkId, chosen);
            if (impact is null)
                return ApiErrors.Conflict("The target episode contains an unavailable or unowned file.");
            if (impact.Count is < 1 or > 1000)
                return ApiErrors.Conflict("Episode artwork affects more than 1,000 files. Narrow this edit.");
            foreach (var affected in impact)
            {
                if (await resources.EvaluateAssetAsync(http, affected.AssetId,
                        ApplicationPermissionIds.MetadataRead, ct) != CatalogueResourceAccess.Allowed
                    || await resources.EvaluateAssetAsync(http, affected.AssetId,
                        ApplicationPermissionIds.MetadataWrite, ct) != CatalogueResourceAccess.Allowed)
                    return ApiErrors.NotFound("One or more affected files are not available for artwork editing.");
            }

            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
            if (expiresAt > pairing.ExpiresAt) expiresAt = pairing.ExpiresAt;
            var review = new EpisodeStillReview(entityId, request.ReviewToken, actor, credentialId,
                choiceSignature, request.TargetCandidateId, target.WorkId, target.WorkKind,
                pairing.ShowWorkId, request.ArtworkAssetId, variant.ContentHash,
                revision, impact, expiresAt);
            cache.Set(EpisodeStillReviewKey(token), review, expiresAt);
            return Results.Ok(new MediaEditorPairingArtworkPreviewDto(token, expiresAt,
                target.WorkId, request.ArtworkAssetId, revision,
                impact.Select(item => new MediaEditorPairingArtworkAffectedFileDto(
                    item.AssetId, item.LibraryId)).ToArray()));
        })
        .WithName("PreviewReviewedTvEpisodeStill")
        .WithSummary("Review one managed episode still and every file it will affect alongside TV pairing.")
        .Produces<MediaEditorPairingArtworkPreviewDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite);
    }

    private static async Task<(VerifiedEpisodeStillAssignment? Assignment, IResult? Error)>
        ValidateReviewedEpisodeStillForSaveAsync(
            Guid entityId, MediaEditorPairingSaveRequestDto request, TvPairingReviewSnapshot pairing,
            IReadOnlyList<VerifiedTvEpisodeMove> moves, HttpContext http,
            CatalogueResourceAuthorizationService resources, IRequestAuthorityResolver authorityResolver,
            IMemoryCache cache, EpisodeStillReviewReadService artworkReads,
            MediaEditorCommitRepository commits,
            CancellationToken ct, bool receiptProbe = false)
    {
        if (string.IsNullOrWhiteSpace(request.ArtworkReviewToken)) return (null, null);
        if (!TryChoiceSignature(request.Accepted, request.ExcludedAssetIds,
                pairing.SelectedAssets.Keys, out var signature))
            return (null, ApiErrors.BadRequest("The reviewed pairing selection changed."));
        if (!cache.TryGetValue<EpisodeStillReview>(EpisodeStillReviewKey(request.ArtworkReviewToken), out var review)
            || review is null || review.ExpiresAt <= DateTimeOffset.UtcNow
            || review.RouteEntityId != entityId || review.PairingReviewToken != request.ReviewToken
            || review.ChoiceSignature != signature || review.ShowWorkId != pairing.ShowWorkId
            || !pairing.Targets.TryGetValue(review.TargetCandidateId, out var target)
            || target.WorkId != review.OwnerWorkId || target.WorkKind != review.TargetWorkKind)
            return (null, ApiErrors.Conflict("The artwork review expired or the pairing choice changed."));
        var actor = await authorityResolver.ResolveAsync(http, ct);
        if (!TvPairingReviewTokenService.TryBindActor(http, actor, out var credentialId)
            || actor != review.Actor || credentialId != review.CredentialId)
            return (null, ApiErrors.Conflict("The editing session changed. Refresh the artwork review."));
        if (!moves.Any(move => move.TargetWorkId == review.OwnerWorkId))
            return (null, ApiErrors.Conflict("The artwork target is no longer in the reviewed pairing plan."));
        if (!await CanEditEpisodeArtworkOwnerAsync(http, resources, review.OwnerWorkId,
                review.TargetWorkKind, review.ShowWorkId, ct)
            || !await CanUseManagedArtworkAsync(http, resources, artworkReads, review.ArtworkAssetId, ct))
            return (null, ApiErrors.NotFound("The artwork owner or variant is no longer editable."));
        foreach (var affected in review.AffectedLibraries)
        {
            if (await resources.EvaluateAssetAsync(http, affected.AssetId,
                    ApplicationPermissionIds.MetadataRead, ct) != CatalogueResourceAccess.Allowed
                || await resources.EvaluateAssetAsync(http, affected.AssetId,
                    ApplicationPermissionIds.MetadataWrite, ct) != CatalogueResourceAccess.Allowed)
                return (null, ApiErrors.NotFound("One or more artwork-affected files are no longer editable."));
        }
        if (!receiptProbe)
        {
            var variant = artworkReads.LoadManagedVariant(review.ArtworkAssetId);
            if (variant is null || variant.ContentHash != review.VariantContentHash
                || string.IsNullOrWhiteSpace(variant.OriginalPath) || !File.Exists(variant.OriginalPath))
                return (null, ApiErrors.Conflict("The managed artwork variant changed after review."));
            var currentRevision = await commits.GetEpisodeStillPreferenceRevisionAsync(review.OwnerWorkId, ct);
            if (currentRevision != review.PreferenceRevision)
                return (null, ApiErrors.Conflict("The episode artwork changed after review."));
        }

        return (new VerifiedEpisodeStillAssignment(review.OwnerWorkId, review.ArtworkAssetId,
            review.VariantContentHash, review.PreferenceRevision,
            review.AffectedLibraries.Select(item => item.AssetId).ToArray(),
            review.AffectedLibraries), null);
    }

    private static bool TryChoiceSignature(
        IReadOnlyList<MediaEditorPairingAcceptedDto> accepted, IReadOnlyList<Guid> excluded,
        IEnumerable<Guid> selected, out string signature)
    {
        signature = string.Empty;
        if (accepted.Count is < 1 or > 1000 || accepted.Any(item => item.AssetId == Guid.Empty
                || string.IsNullOrWhiteSpace(item.CandidateId) || item.CandidateId.Length > 64)
            || accepted.Select(item => item.AssetId).Distinct().Count() != accepted.Count
            || excluded.Any(id => id == Guid.Empty) || excluded.Distinct().Count() != excluded.Count)
            return false;
        var acceptedIds = accepted.Select(item => item.AssetId).ToHashSet();
        if (acceptedIds.Overlaps(excluded)
            || !acceptedIds.Union(excluded).ToHashSet().SetEquals(selected)) return false;
        var data = JsonSerializer.Serialize(new
        {
            Accepted = accepted.OrderBy(item => item.AssetId)
                .Select(item => new { item.AssetId, item.CandidateId }),
            Excluded = excluded.OrderBy(id => id)
        });
        signature = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
        return true;
    }

    private static bool SameReviewedSource(PairingAssetRow expected, PairingAssetRow current) =>
        expected.AssetId == current.AssetId && expected.EditionId == current.EditionId
        && expected.WorkId == current.WorkId && expected.SeasonWorkId == current.SeasonWorkId
        && expected.ShowWorkId == current.ShowWorkId
        && expected.LibraryIdValue == current.LibraryIdValue
        && expected.SourceIdentityRevision == current.SourceIdentityRevision
        && expected.ShowIdentityRevision == current.ShowIdentityRevision;

    private static async Task<bool> CanEditEpisodeArtworkOwnerAsync(HttpContext http,
        CatalogueResourceAuthorizationService resources, Guid ownerWorkId,
        string workKind, Guid showWorkId, CancellationToken ct)
    {
        // Catalog targets have no asset to authorize. Their same-show lineage is
        // frozen in the pairing token and rechecked by Storage at commit.
        var authorizedOwner = workKind == "catalog" ? showWorkId : ownerWorkId;
        return await resources.EvaluateEntityAsync(http, "Work", authorizedOwner,
                ApplicationPermissionIds.MetadataRead, ct) == CatalogueResourceAccess.Allowed
            && await resources.EvaluateEntityAsync(http, "Work", authorizedOwner,
                ApplicationPermissionIds.MetadataWrite, ct) == CatalogueResourceAccess.Allowed;
    }

    private static async Task<bool> CanUseManagedArtworkAsync(HttpContext http,
        CatalogueResourceAuthorizationService resources, EpisodeStillReviewReadService artworkReads,
        Guid artworkAssetId, CancellationToken ct)
    {
        foreach (var linkId in artworkReads.GetManagedArtworkLinkIds(artworkAssetId, ct))
            if (await resources.EvaluateArtworkLinkAsync(http, linkId,
                    ApplicationPermissionIds.MetadataRead, ct) == CatalogueResourceAccess.Allowed)
                return true;
        return false;
    }

    private static string EpisodeStillReviewKey(string token) => $"media-editor:tv-artwork:{token}";
}
