using System.Globalization;
using System.Text.RegularExpressions;
using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Matching;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Domain.Authorization;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Caching.Memory;

namespace MediaEngine.Api.Endpoints;

public static partial class MetadataEndpoints
{
    private static void MapParentFirstPairingPreviewEndpoints(RouteGroupBuilder group)
    {
        MapMusicTrackMoveEndpoints(group);
        group.MapPost("/{entityId:guid}/pairing-preview", async (
            Guid entityId,
            MediaEditorPairingPreviewRequestDto request,
            HttpContext http,
            CatalogueResourceAuthorizationService resources,
            IMediaEditorOwnedChildReadService ownedChildren,
            PairingAssetReadService pairingAssets,
            TvPairingLocalTargetReadService localTargetReader,
            TvdbRetailClient tvdb,
            MusicBrainzReleaseClient musicBrainz,
            IRequestAuthorityResolver authorityResolver,
            IMemoryCache cache,
            CancellationToken ct) =>
        {
            if (request.AssetIds is null || request.AssetIds.Count is < 1 or > 1000
                || request.AssetIds.Any(id => id == Guid.Empty)
                || request.AssetIds.Distinct().Count() != request.AssetIds.Count)
            {
                return ApiErrors.BadRequest("Select between 1 and 1,000 distinct owned files.");
            }
            if (request.TargetParentId?.Length > 128)
            {
                return ApiErrors.BadRequest("The target parent ID is too long.");
            }

            // Authorization is evaluated for every asset. A selection launched
            // outside a single parent is allowed, but each file must be visible.
            foreach (var assetId in request.AssetIds)
            {
                if (await resources.EvaluateAssetAsync(http, assetId,
                        ApplicationPermissionIds.MetadataRead, ct) != CatalogueResourceAccess.Allowed)
                {
                    return ApiErrors.NotFound("One or more selected files are unavailable.");
                }
            }

            var rows = pairingAssets.Load(request.AssetIds, ct);
            if (rows.Count != request.AssetIds.Count)
            {
                return ApiErrors.NotFound("One or more selected owned files are unavailable.");
            }
            var selected = request.AssetIds.Select(id => rows[id]).ToArray();
            var currentRevisions = await ownedChildren.GetSelectionRevisionsForAssetsAsync(
                entityId, request.AssetIds, ct);
            if (currentRevisions.Count != request.AssetIds.Count)
            {
                return ApiErrors.Conflict("The selected files changed. Refresh the file list and review again.");
            }
            if (request.ExpectedSelectionRevisions is not { } expectedRevisions)
            {
                return ApiErrors.BadRequest("Include the selection revision for every selected file.");
            }
            if (expectedRevisions.Count != request.AssetIds.Count
                || expectedRevisions.Any(item => item.Key == Guid.Empty
                    || string.IsNullOrWhiteSpace(item.Value) || item.Value.Length > 128)
                || request.AssetIds.Any(id => !expectedRevisions.TryGetValue(id, out var expected)
                    || !string.Equals(expected, currentRevisions[id], StringComparison.Ordinal)))
            {
                return ApiErrors.Conflict("The selected files changed since selection. Refresh the file list and review again.");
            }
            var mediaTypes = selected.Select(row => row.MediaType).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (mediaTypes.Length != 1 || mediaTypes[0] is not ("TV" or "Music"))
            {
                return ApiErrors.BadRequest("Select only TV episodes or only music tracks for this preview.");
            }

            var tv = mediaTypes[0] == "TV";
            var provider = tv ? "tvdb" : "musicbrainz";
            if (selected.Any(row => tv
                    ? IdsDisagree(row.TvdbSeriesId, row.TvdbSeriesBridgeId)
                    : IdsDisagree(row.MusicBrainzReleaseId, row.MusicBrainzReleaseBridgeId)))
            {
                return ApiErrors.Conflict("A selected file has conflicting stored parent IDs. Review its identity before pairing.");
            }
            var selectedParentIds = selected
                .Select(row => tv
                    ? row.TvdbSeriesBridgeId ?? row.TvdbSeriesId
                    : row.MusicBrainzReleaseBridgeId ?? row.MusicBrainzReleaseId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var targetId = request.TargetParentId?.Trim();
            if (string.IsNullOrWhiteSpace(targetId))
            {
                if (!tv && selected.Any(row => string.IsNullOrWhiteSpace(
                        row.MusicBrainzReleaseBridgeId ?? row.MusicBrainzReleaseId)))
                {
                    return ApiErrors.Conflict(
                            "A selected track has no exact Edition release ID. Choose an exact MusicBrainz release to review.");
                }
                if (selectedParentIds.Length != 1)
                {
                    return ApiErrors.Conflict("Selected files do not share one matched parent. Choose a target show or exact release.");
                }
                targetId = selectedParentIds[0];
            }
            if (string.IsNullOrWhiteSpace(targetId))
            {
                return ApiErrors.Conflict("Choose a target show or exact release.");
            }

            IReadOnlyList<PairingCatalogueChild> catalogue;
            var catalogueComplete = false;
            var catalogueWarning = "Catalogue completeness has not been verified; review every proposed mapping.";
            if (tv)
            {
                if (!targetId.All(char.IsDigit))
                {
                    return ApiErrors.BadRequest("Choose a numeric TheTVDB show ID.");
                }
                if (!tvdb.IsConfigured())
                {
                    return ApiErrors.BadRequest("Connect TheTVDB before previewing TV episode matches.");
                }
                try
                {
                    var show = await tvdb.GetSeriesAsync(targetId, ct);
                    if (show is null || !string.Equals(show["id"]?.ToString(), targetId, StringComparison.Ordinal))
                    {
                        return ApiErrors.BadRequest("The selected TheTVDB show is unavailable.");
                    }
                    var episodes = await tvdb.GetAllEpisodesAsync(targetId, "default", "eng", ct);
                    catalogue = ParentFirstCatalogueAdapters.FromTvdbDefaultEpisodes(targetId, episodes);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return ApiErrors.Problem(StatusCodes.Status502BadGateway,
                        "TheTVDB episode catalogue is unavailable.", ex.Message);
                }
            }
            else
            {
                if (!Guid.TryParse(targetId, out var parsedRelease))
                {
                    return ApiErrors.BadRequest("Choose an exact MusicBrainz release ID.");
                }
                targetId = parsedRelease.ToString("D", CultureInfo.InvariantCulture);
                var release = await musicBrainz.FetchReleaseAsync(targetId, ct);
                if (release is null)
                {
                    return ApiErrors.Problem(StatusCodes.Status502BadGateway,
                            "MusicBrainz release catalogue is unavailable.", "Refresh or choose another exact release.");
                }
                try
                {
                    catalogue = ParentFirstCatalogueAdapters.FromMusicBrainzReleaseManifest(targetId, release.ManifestJson);
                    if (catalogue.Count < release.TrackCount)
                    {
                        catalogueWarning = "Some MusicBrainz tracks lack a unique release Track MBID and were omitted. Refresh the exact release before reviewing mappings.";
                    }
                    else if (catalogue.Count == release.TrackCount && release.TrackCount > 0)
                    {
                        catalogueComplete = true;
                        catalogueWarning = null;
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    return ApiErrors.Problem(StatusCodes.Status502BadGateway,
                        "MusicBrainz release catalogue is invalid.", "Refresh or choose another exact release.");
                }
            }

            var evidence = selected.Select(row => BuildEvidence(row, tv, catalogue)).ToArray();
            // Neither provider client currently exposes a proof of complete
            // pagination/track coverage. Report this honestly and require review.
            var preview = ParentFirstPairingEngine.Preview(new PairingRequest(
                tv ? PairingMediaKind.TvEpisode : PairingMediaKind.MusicReleaseTrack,
                provider, targetId, evidence, catalogue, CatalogueComplete: false));
            string? reviewToken = null;
            DateTimeOffset? reviewExpiresAt = null;
            IReadOnlyDictionary<string, TvPairingLocalTarget> localTargets =
                new Dictionary<string, TvPairingLocalTarget>(StringComparer.Ordinal);
            Guid? reviewedTargetShowWorkId = null;
            if (tv && catalogue.Count <= 10_000
                && selected.All(row => row.WorkKind == "child" && row.ShowWorkId.HasValue
                    && row.SeasonWorkId.HasValue))
            {
                var resolvedTarget = localTargetReader.ResolveBySeriesId(targetId, catalogue, ct);
                if (resolvedTarget is not null)
                {
                    reviewedTargetShowWorkId = resolvedTarget.Value.ShowWorkId;
                    localTargets = resolvedTarget.Value.Targets;
                }
                var actor = await authorityResolver.ResolveAsync(http, ct);
                if (localTargets.Count > 0
                    && reviewedTargetShowWorkId is { } showWorkId
                    && TvPairingReviewTokenService.TryBindActor(http, actor, out var credentialId))
                {
                    var receipt = new TvPairingReviewTokenService(cache).Store(
                        entityId, actor, credentialId, targetId, showWorkId,
                        selected.ToDictionary(row => row.AssetId), localTargets, catalogue,
                        currentRevisions);
                    reviewToken = receipt.Token;
                    reviewExpiresAt = receipt.ExpiresAt;
                }
            }
            else if (!tv && catalogueComplete
                && selected.All(row => GetMusicCandidateSaveLimitation(row, targetId,
                    selected, hasReviewToken: true) is null))
            {
                var actor = await authorityResolver.ResolveAsync(http, ct);
                if (TvPairingReviewTokenService.TryBindActor(http, actor, out var credentialId))
                {
                    var receipt = new MusicPairingReviewTokenService(cache).Store(
                        entityId, actor, credentialId, targetId,
                        selected.ToDictionary(row => row.AssetId), currentRevisions, catalogue);
                    reviewToken = receipt.Token;
                    reviewExpiresAt = receipt.ExpiresAt;
                }
            }
            return Results.Ok(new MediaEditorPairingPreviewDto(
                tv ? "tv_episode" : "music_release_track", provider, targetId,
                catalogueComplete, catalogueWarning,
                preview.Rows.Select(row => ToContract(row, tv, localTargets, rows,
                    reviewToken is not null)).ToArray(),
                reviewToken, reviewExpiresAt,
                reviewToken is null ? null : reviewedTargetShowWorkId));
        })
        .WithName("PreviewParentFirstMediaEditorPairing")
        .WithSummary("Preview episode or exact-release track mappings for selected owned files without mutating the library.")
        .Produces<MediaEditorPairingPreviewDto>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead)
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead);

        group.MapPost("/{entityId:guid}/pairing-children", async (
            Guid entityId,
            MediaEditorPairingChildSearchRequestDto request,
            HttpContext http,
            CatalogueResourceAuthorizationService resources,
            IRequestAuthorityResolver authorityResolver,
            IMemoryCache cache,
            CancellationToken ct) =>
        {
            if (request.AssetId == Guid.Empty || request.Query?.Length > 100
                || request.Offset < 0 || request.Limit is < 1 or > 100)
            {
                return ApiErrors.BadRequest("Choose a selected file and a bounded catalogue search.");
            }
            var snapshot = new TvPairingReviewTokenService(cache).Get(request.ReviewToken ?? string.Empty);
            if (snapshot is null || snapshot.RouteEntityId != entityId
                || !snapshot.SelectedAssets.TryGetValue(request.AssetId, out var source))
            {
                return ApiErrors.Conflict("This pairing review expired or changed. Refresh and review the files again.");
            }
            var actor = await authorityResolver.ResolveAsync(http, ct);
            if (!TvPairingReviewTokenService.TryBindActor(http, actor, out var credentialId)
                || actor != snapshot.Actor || credentialId != snapshot.ApplicationCredentialId)
            {
                return ApiErrors.Conflict("The editing session changed. Refresh and review the files again.");
            }
            if (await resources.EvaluateAssetAsync(http, request.AssetId,
                    ApplicationPermissionIds.MetadataRead, ct) != CatalogueResourceAccess.Allowed)
            {
                return ApiErrors.NotFound("The selected file is no longer available.");
            }

            var query = request.Query?.Trim() ?? string.Empty;
            var filtered = snapshot.Catalogue
                .Where(child => request.SeasonNumber is null || child.SeasonNumber == request.SeasonNumber)
                .Where(child => query.Length == 0
                    || child.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || child.ChildId.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || $"S{child.SeasonNumber:00}E{child.EpisodeNumber:00}"
                        .Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(child => child.SeasonNumber).ThenBy(child => child.EpisodeNumber)
                .ThenBy(child => child.ChildId, StringComparer.Ordinal)
                .ToArray();
            var items = filtered.Skip(request.Offset).Take(request.Limit)
                .Select(child =>
                {
                    var limitation = GetTvCandidateSaveLimitation(child.ChildId, source,
                        snapshot.Targets, hasReviewToken: true);
                    return new MediaEditorPairingChildSearchItemDto(ToChildContract(child,
                            snapshot.Targets.TryGetValue(child.ChildId, out var localTarget)
                                ? localTarget.SeasonWorkId : null),
                        limitation is null, limitation);
                }).ToArray();
            return Results.Ok(new MediaEditorPairingChildSearchDto(request.AssetId, filtered.Length, items));
        })
        .WithName("SearchReviewedTvPairingChildren")
        .WithSummary("Search the reviewed TheTVDB default-order catalogue across seasons without another provider request.")
        .Produces<MediaEditorPairingChildSearchDto>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead)
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead);

        group.MapPost("/{entityId:guid}/pairing-save", async (
            Guid entityId,
            MediaEditorPairingSaveRequestDto request,
            HttpContext http,
            CatalogueResourceAuthorizationService resources,
            IMediaEditorOwnedChildReadService ownedChildren,
            IRequestAuthorityResolver authorityResolver,
            IMemoryCache cache,
            EpisodeStillReviewReadService artworkReads,
            MediaEditorCommitRepository commits,
            MusicPairingCommitRepository musicCommits,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.ReviewToken)
                || !Guid.TryParse(request.OperationToken, out var operationId)
                || request.Accepted is null || request.ExcludedAssetIds is null
                || request.Accepted.Count is < 1 or > 1000
                || request.Accepted.Any(item => item.AssetId == Guid.Empty
                    || string.IsNullOrWhiteSpace(item.CandidateId) || item.CandidateId.Length > 64)
                || request.Accepted.Select(item => item.AssetId).Distinct().Count() != request.Accepted.Count
                || request.ExcludedAssetIds.Any(id => id == Guid.Empty)
                || request.ExcludedAssetIds.Distinct().Count() != request.ExcludedAssetIds.Count)
            {
                return ApiErrors.BadRequest("Submit a valid reviewed selection and operation token.");
            }

            var musicSnapshot = new MusicPairingReviewTokenService(cache).Get(request.ReviewToken);
            if (musicSnapshot is not null)
            {
                if (musicSnapshot.RouteEntityId != entityId)
                {
                    return ApiErrors.Conflict("This pairing review expired or changed. Refresh and review the files again.");
                }
                var musicActor = await authorityResolver.ResolveAsync(http, ct);
                if (!TvPairingReviewTokenService.TryBindActor(http, musicActor, out var musicCredential)
                    || musicActor != musicSnapshot.Actor || musicCredential != musicSnapshot.ApplicationCredentialId)
                {
                    return ApiErrors.Conflict("The editing session changed. Refresh and review the files again.");
                }
                var musicAcceptedIds = request.Accepted.Select(item => item.AssetId).ToHashSet();
                var musicExcludedIds = request.ExcludedAssetIds.ToHashSet();
                if (musicAcceptedIds.Overlaps(musicExcludedIds)
                    || !musicAcceptedIds.Union(musicExcludedIds).ToHashSet().SetEquals(musicSnapshot.SelectedAssets.Keys))
                {
                    return ApiErrors.BadRequest("Accepted and excluded files must account for the entire reviewed selection.");
                }
                foreach (var assetId in musicSnapshot.SelectedAssets.Keys)
                {
                    if (await resources.EvaluateAssetAsync(http, assetId, ApplicationPermissionIds.MetadataMatch, ct) != CatalogueResourceAccess.Allowed
                            || await resources.EvaluateAssetAsync(http, assetId, ApplicationPermissionIds.MetadataWrite, ct) != CatalogueResourceAccess.Allowed)
                    {
                        return ApiErrors.NotFound("One or more selected files are no longer editable.");
                    }
                }
                var musicRows = new List<VerifiedMusicReleaseTrackPairing>(request.Accepted.Count);
                foreach (var accepted in request.Accepted)
                {
                    if (!musicSnapshot.SelectedAssets.TryGetValue(accepted.AssetId, out var source)
                        || !musicSnapshot.ReleaseTracks.TryGetValue(accepted.CandidateId, out var target)
                        || !string.Equals(target.ParentId, musicSnapshot.ReleaseId, StringComparison.OrdinalIgnoreCase)
                        || !Guid.TryParse(target.ChildId, out var trackId) || trackId == Guid.Empty
                        || !Guid.TryParse(musicSnapshot.ReleaseId, out var releaseId) || releaseId == Guid.Empty
                        || !Guid.TryParse(source.LibraryIdValue, out var libraryId) || libraryId == Guid.Empty
                        || GetMusicCandidateSaveLimitation(source, musicSnapshot.ReleaseId,
                            musicSnapshot.SelectedAssets.Values.ToArray(), true) is not null)
                    {
                        return ApiErrors.Conflict("A selected mapping is outside the reviewed exact MusicBrainz release.");
                    }
                    musicRows.Add(new VerifiedMusicReleaseTrackPairing(operationId.ToString("D"),
                        source.AssetId, source.EditionId, source.WorkId, source.RootWorkId, libraryId,
                        source.SourceIdentityRevision, releaseId.ToString("D"), trackId.ToString("D")));
                }
                var musicReplay = await musicCommits.TryReplayAsync(musicRows, ct);
                if (musicReplay is not null)
                {
                    return ToParentFirstSaveResult(musicReplay);
                }
                var currentMusicRevisions = await ownedChildren.GetSelectionRevisionsForAssetsAsync(
                    entityId, musicSnapshot.SelectedAssets.Keys.ToArray(), ct);
                if (currentMusicRevisions.Count != musicSnapshot.SelectionRevisions.Count
                    || musicSnapshot.SelectionRevisions.Any(item => !currentMusicRevisions.TryGetValue(item.Key, out var current)
                        || !string.Equals(item.Value, current, StringComparison.Ordinal)))
                {
                    return ApiErrors.Conflict("A reviewed file changed after preview. Refresh and review again.");
                }
                return ToParentFirstSaveResult(await musicCommits.CommitAsync(musicRows, ct));
            }

            var snapshot = new TvPairingReviewTokenService(cache).Get(request.ReviewToken);
            if (snapshot is null || snapshot.RouteEntityId != entityId)
            {
                return ApiErrors.Conflict("This pairing review expired or changed. Refresh and review the files again.");
            }
            var actor = await authorityResolver.ResolveAsync(http, ct);
            if (!TvPairingReviewTokenService.TryBindActor(http, actor, out var credentialId)
                || actor != snapshot.Actor || credentialId != snapshot.ApplicationCredentialId)
            {
                return ApiErrors.Conflict("The editing session changed. Refresh and review the files again.");
            }

            var acceptedIds = request.Accepted.Select(item => item.AssetId).ToHashSet();
            var excludedIds = request.ExcludedAssetIds.ToHashSet();
            if (acceptedIds.Overlaps(excludedIds)
                || !acceptedIds.Union(excludedIds).ToHashSet().SetEquals(snapshot.SelectedAssets.Keys))
            {
                return ApiErrors.BadRequest("Accepted and excluded files must account for the entire reviewed selection.");
            }

            // Both capabilities and every affected owner are checked again at
            // Save. Catalog-only targets are authorized through the same
            // confirmed show; Storage proves they still have zero actual files.
            foreach (var assetId in snapshot.SelectedAssets.Keys)
            {
                if (await resources.EvaluateAssetAsync(http, assetId, ApplicationPermissionIds.MetadataMatch, ct)
                        != CatalogueResourceAccess.Allowed
                    || await resources.EvaluateAssetAsync(http, assetId, ApplicationPermissionIds.MetadataWrite, ct)
                        != CatalogueResourceAccess.Allowed)
                {
                    return ApiErrors.NotFound("One or more selected files are no longer editable.");
                }
            }
            var moves = new List<VerifiedTvEpisodeMove>(request.Accepted.Count);
            var owners = new HashSet<Guid> { snapshot.ShowWorkId };
            foreach (var accepted in request.Accepted)
            {
                if (!snapshot.SelectedAssets.TryGetValue(accepted.AssetId, out var source)
                    || source.SeasonWorkId is not { } sourceSeason
                    || source.ShowWorkId is not { } sourceShowWorkId
                    || string.IsNullOrWhiteSpace(source.TvdbSeriesBridgeId)
                    || !Guid.TryParse(source.LibraryIdValue, out var libraryId)
                    || libraryId == Guid.Empty
                    || !snapshot.Targets.TryGetValue(accepted.CandidateId, out var target)
                    || target.WorkKind is not ("child" or "catalog")
                    || target.WorkKind == "catalog" && target.ActualAssetCount != 0
                    || target.WorkId == source.WorkId
                    || target.ShowWorkId != snapshot.ShowWorkId
                    || target.SeriesId != snapshot.TvdbSeriesId)
                {
                    return ApiErrors.Conflict("A selected mapping is outside the reviewed local TV catalogue.");
                }

                // Selected-asset authorization covers the source episode and
                // season. They may become unowned after Save, so requiring an
                // owned source Work here would break an idempotent replay.
                if (target.WorkKind == "child")
                {
                    owners.Add(target.WorkId);
                    owners.Add(target.SeasonWorkId);
                }
                owners.Add(sourceShowWorkId);
                moves.Add(new VerifiedTvEpisodeMove(operationId.ToString("D"), accepted.AssetId,
                    source.EditionId, source.WorkId, sourceSeason,
                    target.WorkId, target.SeasonWorkId, sourceShowWorkId,
                    snapshot.ShowWorkId, source.TvdbSeriesBridgeId,
                    snapshot.TvdbSeriesId, accepted.CandidateId,
                    source.SourceIdentityRevision, target.IdentityRevision,
                    source.ShowIdentityRevision, target.ShowIdentityRevision,
                    target.WorkKind, libraryId));
            }

            foreach (var owner in owners)
            {
                if (await resources.EvaluateEntityAsync(http, "Work", owner,
                        ApplicationPermissionIds.MetadataMatch, ct) != CatalogueResourceAccess.Allowed
                    || await resources.EvaluateEntityAsync(http, "Work", owner,
                        ApplicationPermissionIds.MetadataWrite, ct) != CatalogueResourceAccess.Allowed)
                {
                    return ApiErrors.NotFound("An affected episode, season, or show is no longer editable.");
                }
            }

            // An exact receipt may be replayed after the successful move has
            // changed the selected files' live revisions. The frozen review
            // token and every affected resource are still authorized first.
            var replayStill = await ValidateReviewedEpisodeStillForSaveAsync(
                entityId, request, snapshot, moves, http, resources,
                authorityResolver, cache, artworkReads, commits, ct, receiptProbe: true);
            if (replayStill.Error is not null)
            {
                return replayStill.Error;
            }
            var replayShared = await ValidateReviewedSharedArtworkForSaveAsync(
                entityId, request, snapshot, moves, http, resources,
                authorityResolver, cache, artworkReads, commits, ct, receiptProbe: true);
            if (replayShared.Error is not null)
            {
                return replayShared.Error;
            }
            var replay = await commits.TryReplayVerifiedTvEpisodePlanAsync(
                moves, replayStill.Assignment, replayShared.Assignment, ct);
            if (replay is not null)
            {
                return ToParentFirstSaveResult(replay);
            }

            if (snapshot.SelectionRevisions.Count > 0)
            {
                var currentRevisions = await ownedChildren.GetSelectionRevisionsForAssetsAsync(
                    entityId, snapshot.SelectedAssets.Keys.ToArray(), ct);
                if (currentRevisions.Count != snapshot.SelectionRevisions.Count
                    || snapshot.SelectionRevisions.Any(item =>
                        !currentRevisions.TryGetValue(item.Key, out var current)
                        || !string.Equals(item.Value, current, StringComparison.Ordinal)))
                {
                    return ApiErrors.Conflict("A reviewed file changed after preview. Refresh and review again.");
                }
            }
            var art = await ValidateReviewedEpisodeStillForSaveAsync(
                entityId, request, snapshot, moves, http, resources,
                authorityResolver, cache, artworkReads, commits, ct);
            if (art.Error is not null)
            {
                return art.Error;
            }
            var sharedArt = await ValidateReviewedSharedArtworkForSaveAsync(
                entityId, request, snapshot, moves, http, resources,
                authorityResolver, cache, artworkReads, commits, ct);
            if (sharedArt.Error is not null)
            {
                return sharedArt.Error;
            }

            var result = await commits.CommitVerifiedTvEpisodePlanAsync(
                moves, art.Assignment, sharedArt.Assignment, ct);
            return ToParentFirstSaveResult(result);
        })
        .WithName("SaveReviewedParentFirstTvPairing")
        .WithSummary("Save reviewed TV episode moves to existing authorized local targets and queue file synchronization.")
        .Produces<MediaEditorPairingSaveResultDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataMatch)
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataMatch);
    }

    private static IResult ToParentFirstSaveResult(MediaEditorPlanCommitResult result)
    {
        var response = new MediaEditorPairingSaveResultDto(result.Outcome.ToString(),
            result.Items.Select(item => new MediaEditorPairingSavedRowDto(
                item.AssetId, item.Outcome.ToString(), item.SyncState,
                item.ConflictReason, item.TargetWorkId)).ToArray());
            if (result.Outcome == MediaEditorCommitOutcome.Conflict)
            {
                var reasons = result.Items
                    .Where(item => !string.IsNullOrWhiteSpace(item.ConflictReason))
                    .Take(3)
                    .Select(item => $"{item.AssetId:D}: {item.ConflictReason}");
                return ApiErrors.Conflict(string.Join("; ", reasons) is { Length: > 0 } detail
                    ? detail
                    : "The reviewed file selection changed. Refresh the preview and review it again.");
            }
            return Results.Ok(response);
    }

    private static PairingAssetEvidence BuildEvidence(
        PairingAssetRow row, bool tv, IReadOnlyList<PairingCatalogueChild> catalogue)
    {
        var fileName = Path.GetFileName(row.FilePath.Replace('\\', '/'));
        if (tv)
        {
            return new PairingAssetEvidence(row.AssetId, fileName, CurrentChildId: row.TvdbEpisodeId);
        }

        var match = MusicTrackFilePattern().Match(fileName);
        var trackNumber = match.Success && int.TryParse(match.Groups["track"].Value, out var parsedTrack)
            ? parsedTrack : (int?)null;
        var discNumber = match.Success && match.Groups["disc"].Success
            && int.TryParse(match.Groups["disc"].Value, out var parsedDisc)
                ? parsedDisc : (int?)null;
        if (discNumber is null && catalogue.Count > 0
            && catalogue.Select(child => child.DiscNumber).Distinct().Count() == 1)
        {
            discNumber = catalogue[0].DiscNumber;
        }
        return new PairingAssetEvidence(row.AssetId, fileName,
            CurrentChildId: row.RecordingId,
            DiscNumber: discNumber, TrackNumber: trackNumber);
    }

    private static MediaEditorPairingRowDto ToContract(
        PairingRow row, bool tv, IReadOnlyDictionary<string, TvPairingLocalTarget> localTargets,
        IReadOnlyDictionary<Guid, PairingAssetRow> assets, bool hasReviewToken)
    {
        var source = assets[row.Asset.AssetId];
        var proposed = row.Proposed is null ? null
            : ToContract(row.Proposed, source, tv, localTargets, hasReviewToken);
        return new MediaEditorPairingRowDto(
            row.Asset.AssetId, row.Asset.FileName, row.Asset.CurrentChildId,
            row.Asset.CurrentChildId is null ? null : tv ? "episode" : "recording",
            proposed,
            row.Alternatives.Select(candidate => ToContract(candidate, source, tv, localTargets, hasReviewToken)).ToArray(),
            row.Band.ToString(), row.CanPreselect, row.Limitation,
            proposed?.CanSave == true, proposed?.SaveLimitation);
    }

    private static MediaEditorPairingCandidateDto ToContract(
        PairingCandidate candidate, PairingAssetRow source, bool tv,
        IReadOnlyDictionary<string, TvPairingLocalTarget> localTargets, bool hasReviewToken)
    {
        var limitation = tv
            ? GetTvCandidateSaveLimitation(candidate.Child.ChildId, source, localTargets, hasReviewToken)
            : GetMusicCandidateSaveLimitation(source, candidate.Child.ParentId,
                assets: null, hasReviewToken);
        return new MediaEditorPairingCandidateDto(
            ToChildContract(candidate.Child,
                localTargets.TryGetValue(candidate.Child.ChildId, out var localTarget)
                    ? localTarget.SeasonWorkId : null),
            candidate.Band.ToString(), candidate.Reasons, candidate.Conflicts,
            limitation is null, limitation);
    }

    private static MediaEditorPairingChildDto ToChildContract(PairingCatalogueChild child,
        Guid? localSeasonWorkId = null) => new(
        child.ChildId, child.ParentId, child.Provider, child.Title, child.SeasonNumber,
        child.EpisodeNumber, child.DiscNumber, child.TrackNumber, child.RecordingId,
        localSeasonWorkId);

    private static string? GetTvCandidateSaveLimitation(string candidateId,
        PairingAssetRow source, IReadOnlyDictionary<string, TvPairingLocalTarget> targets,
        bool hasReviewToken)
    {
        if (!hasReviewToken)
        {
            return "This selection has no active reviewed TV Save session.";
        }
        if (!Guid.TryParse(source.LibraryIdValue, out var libraryId) || libraryId == Guid.Empty)
        {
            return "The selected file has no valid library ownership scope.";
        }
        if (!targets.TryGetValue(candidateId, out var target))
        {
            return "No unambiguous local episode Work exists for this provider episode.";
        }
        if (target.WorkId == source.WorkId)
        {
            return "This file is already assigned to this episode.";
        }
        if (target.WorkKind == "child")
        {
            return null;
        }
        if (target.WorkKind == "catalog" && target.ActualAssetCount == 0)
        {
            return null;
        }
        return "The local target episode has changed or contains files that need review.";
    }

    private static string? GetMusicCandidateSaveLimitation(PairingAssetRow source,
        string targetReleaseId, IReadOnlyCollection<PairingAssetRow>? assets, bool hasReviewToken)
    {
        if (!hasReviewToken)
        {
            return "This selection has no complete exact-release MusicBrainz review.";
        }
        if (!Guid.TryParse(source.LibraryIdValue, out var libraryId) || libraryId == Guid.Empty)
        {
            return "The selected file has no valid library ownership scope.";
        }
        if (source.WorkKind != "child" || source.WorkEditionCount != 1)
        {
            return "This track has legacy or multiple Edition structure that cannot represent one exact release-track identity.";
        }
        if (source.EditionAssetCount != 1 && assets is not null
            && assets.Count(row => row.EditionId == source.EditionId) != source.EditionAssetCount)
        {
            return "Every owned file in this Edition must be reviewed together.";
        }
        if (IdsDisagree(source.MusicBrainzReleaseId, source.MusicBrainzReleaseBridgeId)
            || IdsDisagree(source.MusicBrainzReleaseTrackId, source.MusicBrainzReleaseTrackBridgeId))
        {
            return "This track has conflicting stored MusicBrainz identity.";
        }
        var existingRelease = source.MusicBrainzReleaseBridgeId ?? source.MusicBrainzReleaseId;
        if (!string.IsNullOrWhiteSpace(existingRelease)
            && !string.Equals(existingRelease, targetReleaseId, StringComparison.OrdinalIgnoreCase))
        {
            return "This Edition already identifies a different exact MusicBrainz release.";
        }
        return null;
    }

    private static bool IdsDisagree(string? canonical, string? bridge) =>
        !string.IsNullOrWhiteSpace(canonical) && !string.IsNullOrWhiteSpace(bridge)
        && !string.Equals(canonical.Trim(), bridge.Trim(), StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^(?:(?<disc>\d{1,2})[-_.])?(?<track>\d{1,3})\s*[-_. ]", RegexOptions.IgnoreCase)]
    private static partial Regex MusicTrackFilePattern();

}
