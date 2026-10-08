using System.Text.Json;
using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Matching;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Domain.Authorization;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Caching.Memory;

namespace MediaEngine.Api.Endpoints;

public static partial class MetadataEndpoints
{
    private sealed record MusicMoveReview(Guid RouteId, RequestAuthority Actor, Guid? Credential,
        PairingAssetRow Source, MusicBrainzAlbumRelease Release, DateTimeOffset ExpiresAt);

    private static void MapMusicTrackMoveEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/{entityId:guid}/music-track-move-preview", async (
            Guid entityId, MusicTrackMovePreviewRequest request, HttpContext http,
            CatalogueResourceAuthorizationService resources, PairingAssetReadService assets,
            MusicBrainzReleaseClient musicBrainz, IRequestAuthorityResolver authority,
            IMemoryCache cache, CancellationToken ct) =>
        {
            if (request.AssetId == Guid.Empty || !Guid.TryParse(request.ReleaseId, out var releaseId) || releaseId == Guid.Empty)
            {
                return ApiErrors.BadRequest("Choose one owned file and an exact MusicBrainz release.");
            }
            if (await resources.EvaluateAssetAsync(http, request.AssetId, ApplicationPermissionIds.MetadataMatch, ct) != CatalogueResourceAccess.Allowed
                || await resources.EvaluateAssetAsync(http, request.AssetId, ApplicationPermissionIds.MetadataWrite, ct) != CatalogueResourceAccess.Allowed)
            {
                return ApiErrors.NotFound("This file is unavailable for matching.");
            }
            if (!assets.Load([request.AssetId], ct).TryGetValue(request.AssetId, out var source)
                || source.MediaType != "Music" || source.WorkKind != "child")
            {
                return ApiErrors.BadRequest("Choose an owned music track file.");
            }
            var actor = await authority.ResolveAsync(http, ct);
            if (!TvPairingReviewTokenService.TryBindActor(http, actor, out var credential))
            {
                return ApiErrors.NotFound("The editing session is unavailable.");
            }
            var release = await musicBrainz.FetchReleaseAsync(releaseId.ToString("D"), ct);
            if (release is null)
            {
                return ApiErrors.Problem(StatusCodes.Status502BadGateway, "The exact release is unavailable.", "Try another release or retry later.");
            }
            var catalogue = ParentFirstCatalogueAdapters.FromMusicBrainzReleaseManifest(release.ReleaseId, release.ManifestJson);
            using var manifest = JsonDocument.Parse(release.ManifestJson);
            var album = manifest.RootElement.GetProperty("album").GetString();
            if (string.IsNullOrWhiteSpace(album) || catalogue.Count == 0)
            {
                return ApiErrors.BadRequest("This release has no verified track placements.");
            }
            var artist = manifest.RootElement.GetProperty("artist").GetString();
            var token = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
            var expires = DateTimeOffset.UtcNow.AddMinutes(10);
            cache.Set("music-move:" + token, new MusicMoveReview(entityId, actor, credential, source, release, expires), expires);
            return Results.Ok(new MusicTrackMovePreview(token, expires, source.AssetId, release.ReleaseId, album, artist,
                catalogue.Where(track => track.DiscNumber is > 0 && track.TrackNumber is > 0)
                    .Select(track => new MusicTrackMoveChoice(track.ChildId, track.RecordingId, track.Title, track.DiscNumber!.Value, track.TrackNumber!.Value)).ToArray()));
        }).WithName("PreviewMusicTrackMove")
          .Produces<MusicTrackMovePreview>(StatusCodes.Status200OK)
          .ProducesProblem(StatusCodes.Status400BadRequest)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataMatch)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataMatch);

        group.MapPost("/{entityId:guid}/music-track-move", async (
            Guid entityId, MusicTrackMoveSaveRequest request, HttpContext http,
            CatalogueResourceAuthorizationService resources, IRequestAuthorityResolver authority,
            MusicTrackRelocationRepository relocation, IMemoryCache cache, CancellationToken ct) =>
        {
            if (request.OperationId == Guid.Empty || request.ReviewToken is null || request.ReviewToken.Length != 48
                || !request.ReviewToken.All(Uri.IsHexDigit) || !Guid.TryParse(request.ReleaseTrackId, out _))
            {
                return ApiErrors.BadRequest("Choose an exact release track from a current review.");
            }
            if (!cache.TryGetValue<MusicMoveReview>("music-move:" + request.ReviewToken, out var review)
                || review is null || review.RouteId != entityId || review.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                return ApiErrors.Conflict("This release review expired. Search the release again.");
            }
            var actor = await authority.ResolveAsync(http, ct);
            if (!TvPairingReviewTokenService.TryBindActor(http, actor, out var credential)
                || actor != review.Actor || credential != review.Credential)
            {
                return ApiErrors.Conflict("The editing session changed. Review the release again.");
            }
            if (await resources.EvaluateAssetAsync(http, review.Source.AssetId, ApplicationPermissionIds.MetadataMatch, ct) != CatalogueResourceAccess.Allowed
                || await resources.EvaluateAssetAsync(http, review.Source.AssetId, ApplicationPermissionIds.MetadataWrite, ct) != CatalogueResourceAccess.Allowed)
            {
                return ApiErrors.NotFound("This file is no longer editable.");
            }
            var track = ParentFirstCatalogueAdapters.FromMusicBrainzReleaseManifest(review.Release.ReleaseId, review.Release.ManifestJson)
                .SingleOrDefault(candidate => string.Equals(candidate.ChildId, request.ReleaseTrackId, StringComparison.OrdinalIgnoreCase));
            if (track is null || track.DiscNumber is not > 0 || track.TrackNumber is not > 0
                || !Guid.TryParse(review.Source.LibraryIdValue, out var library))
            {
                return ApiErrors.BadRequest("This track is not part of the reviewed release.");
            }
            using var manifest = JsonDocument.Parse(review.Release.ManifestJson);
            var move = new VerifiedMusicTrackRelocation(request.OperationId.ToString("D"), review.Source.AssetId,
                review.Source.EditionId, review.Source.WorkId, library, review.Source.SourceIdentityRevision,
                review.Release.ReleaseId, track.ChildId, track.RecordingId, manifest.RootElement.GetProperty("album").GetString()!,
                manifest.RootElement.GetProperty("artist").GetString(), track.Title, track.DiscNumber.Value, track.TrackNumber.Value, review.Release.ManifestJson);
            try { return ToParentFirstSaveResult(await relocation.CommitAsync(move, ct)); }
            catch (InvalidOperationException error) { return ApiErrors.Conflict(error.Message); }
        }).WithName("SaveMusicTrackMove")
          .Produces<MediaEditorPairingSaveResultDto>(StatusCodes.Status200OK)
          .ProducesProblem(StatusCodes.Status400BadRequest)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataMatch)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataMatch);
    }
}
