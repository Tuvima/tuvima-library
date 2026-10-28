using MediaEngine.Api.Http;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Canonical;
using MediaEngine.Api.Services.Collections;
using MediaEngine.Api.Services.Matching;
using MediaEngine.Contracts.Matching;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Services;
using MediaEngine.Providers.Workers;

namespace MediaEngine.Api.Endpoints;

public static partial class ItemCanonicalEndpoints
{
    /// <summary>
    /// Replaces the item's retail match. The one write path behind both the editor's retail-match action and the edit session's
    /// identity change. <paramref name="allowParentMove"/> false leaves the item where it is (only identity and details change);
    /// <paramref name="validateOnly"/> runs the cheap local checks and writes nothing.
    /// </summary>
    internal static async Task<IResult> ReplaceRetailMatchCoreAsync(
        RetailMatchWriter writer,
        Guid entityId,
        ReplaceRetailMatchRequestDto request,
        bool allowParentMove,
        bool validateOnly,
        CancellationToken ct)
    {
        var claimRepo = writer.ClaimRepo;
        var canonicalRepo = writer.CanonicalRepo;
        var bridgeIdRepo = writer.BridgeIdRepo;
        var activityRepo = writer.ActivityRepo;
        var publisher = writer.Publisher;
        var collectionRepo = writer.CollectionRepo;
        var workRepo = writer.WorkRepo;
        var reviewRepo = writer.ReviewRepo;
        var pipeline = writer.Pipeline;
        var albumTrackManifestService = writer.AlbumTrackManifestService;
        var coverArtWorker = writer.CoverArtWorker;
        var timeline = writer.Timeline;
        var itemCanonicalData = writer.ItemCanonicalData;
        var hierarchyAlignment = writer.HierarchyAlignment;
        var tvdb = writer.Tvdb;
        var musicBrainz = writer.MusicBrainz;
        var apple = writer.Apple;
        var externalProviders = writer.ExternalProviders;
        var loggerFactory = writer.LoggerFactory;
        var lookupGuard = writer.LookupGuard;

        var context = await itemCanonicalData.ResolveWorkAssetContextAsync(entityId, ct);
        if (context is null)
        {
            return ApiErrors.NotFound($"No current media asset or work target found for {entityId}.");
        }

        // A library that never looks anything up (a plain kind such as Other audio) takes no retail match: nothing is sent to a
        // provider and no provider claim is written. This is refused here, not left to the identity queue to ignore.
        if (await RefuseInLibraryThatNeverLooksAnythingUpAsync(lookupGuard, context.AssetId, ct) is { } refused)
        {
            return refused;
        }

        if (string.IsNullOrWhiteSpace(request.ProviderName) || string.IsNullOrWhiteSpace(request.ProviderItemId))
        {
            return ApiErrors.BadRequest("Provider name and provider item ID are required.");
        }

        if (!Guid.TryParse(request.ProviderId, out var providerId))
        {
            return ApiErrors.BadRequest("A valid provider ID is required for a retail replacement.");
        }

        if (!IsRetailProviderAllowed(context.MediaType, request.ProviderName, providerId))
        {
            return ApiErrors.BadRequest("Television retail matches must use TheTVDB. Match the show to TheTVDB before selecting a season or episode.");
        }

        var policy = ResolveTargetPolicy(context.MediaType, request.TargetKind, request.TargetFieldGroup);
        if (policy is null)
        {
            return ApiErrors.BadRequest($"Unsupported target field group '{request.TargetFieldGroup}' for media type '{context.MediaType}'.");
        }


        if (!IsRetailCandidateCompatible(policy, request.ProviderName, request.ProviderItemId, request.BridgeIds, out var incompatibility))
        {
            return ApiErrors.BadRequest(incompatibility!);
        }

        if (validateOnly)
        {
            // The edit session's check: everything above is cheap and local. Nothing below (provider calls, writes) runs.
            return await workRepo.GetLineageByAssetAsync(context.AssetId, ct) is null
                ? ApiErrors.NotFound($"No work lineage found for {entityId}.")
                : Results.Ok(new ItemCanonicalApplyResponseDto { EntityId = entityId });
        }

        if (policy.MediaType == MediaType.TV.ToString() && policy.TargetFieldGroup == "show")
        {
            if (!tvdb.IsConfigured("tvdb"))
            {
                return ApiErrors.BadRequest("Connect TheTVDB in Settings before matching a show.");
            }
            try
            {
                var remoteSeries = await tvdb.GetAsync("tvdb", "series_extended", ProviderApiClient.Args(("id", request.ProviderItemId)), ct);
                if (!IsTvdbSeriesRecord(remoteSeries, request.ProviderItemId))
                {
                    return ApiErrors.BadRequest("The selected TheTVDB series is no longer available. Search again before applying it.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                loggerFactory.CreateLogger("RetailShowMatch").LogWarning(ex,
                    "Could not verify TheTVDB series before applying a match");
                return ApiErrors.Problem(StatusCodes.Status502BadGateway, "TheTVDB verification is unavailable.", "Try matching the show again later.");
            }
        }

        var now = DateTimeOffset.UtcNow;
        var lineage = await workRepo.GetLineageByAssetAsync(context.AssetId, ct);
        if (lineage is null)
        {
            return ApiErrors.NotFound($"No work lineage found for {entityId}.");
        }
        var alignmentEntityId = await itemCanonicalData.ResolveWorkIdForAssetAsync(context.AssetId, ct) ?? context.AssetId;

        var allowedFieldKeys = policy.RequiredFieldKeys
            .Concat(policy.SuggestedFieldKeys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedFields = request.RequiredFields
            .Concat(request.SuggestedFields)
            .Where(kv => allowedFieldKeys.Contains(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        var selectedBridgeIds = request.BridgeIds
            .Where(kv => policy.BridgeIdKeys.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)
                         && !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        if (IsGenericTvChildPolicy(policy))
        {
            var (tvdbVerification, verifiedShowName) = await VerifyTvdbEpisodeMoveAsync(tvdb, request.ProviderItemId,
                selectedBridgeIds, selectedFields, ct);
            if (tvdbVerification is not null)
            {
                return tvdbVerification;
            }
            selectedFields[MetadataFieldConstants.ShowName] = verifiedShowName!;
        }

        var relationVerification = await VerifyProviderParentChildRelationAsync(
            policy,
            request.ProviderName,
            selectedBridgeIds,
            lineage,
            bridgeIdRepo,
            context,
            musicBrainz,
            apple,
            externalProviders,
            ct);
        if (relationVerification is not null)
        {
            return relationVerification;
        }

        var identityTarget = ResolvePolicyIdentityTarget(context.AssetId, lineage, policy);
        var currentIdentityRevision = await GetIdentityRevisionAsync(canonicalRepo, identityTarget, ct);
        if (HasStaleExpectedRevision(request.ExpectedIdentityRevision, currentIdentityRevision))
        {
            return ApiErrors.Conflict("This retail match changed while the item was open. Reload and review it again.");
        }

        // Matching a record never moves anything on its own (universal editor spec D6): the edit session passes
        // allowParentMove false, and the parent changes only through its own relationship change.
        var hierarchyRequest = allowParentMove
            ? BuildHierarchyAlignmentRequest(
                policy,
                selectedFields,
                request.ProviderName,
                request.ProviderItemId,
                request.BridgeIds)
            : null;
        var hierarchyPreview = await hierarchyAlignment.PreviewAsync(
            alignmentEntityId,
            hierarchyRequest ?? new MembershipPreviewRequest(null, null, null, null),
            ct);
        if (hierarchyPreview is null)
        {
            return ApiErrors.NotFound($"No hierarchy target found for {entityId}.");
        }
        if (hierarchyPreview is { CanApply: false }
            && string.Equals(hierarchyPreview.Action, "conflict", StringComparison.OrdinalIgnoreCase))
        {
            return ApiErrors.Conflict(hierarchyPreview.ConflictMessage ?? hierarchyPreview.Message);
        }
        if (RequiresExpectedRevision(hierarchyPreview) && string.IsNullOrWhiteSpace(request.ExpectedIdentityRevision))
        {
            return ApiErrors.BadRequest("Include expected_identity_revision before confirming a parent move.");
        }
        MembershipPreviewEnvelope? hierarchyImpact;

        // A candidate can deliberately identify a different parent.  Structural
        // validation belongs to IHierarchyAlignmentService, which only returns
        // a conflict for an actual owned-leaf/ordinal collision; do not reject
        // a valid cross-parent identity before it can be aligned.

        var staleBridgeKeys = policy.BridgeIdKeys
            .Where(key => !selectedBridgeIds.ContainsKey(key))
            // A child candidate may omit its parent release/show ID. That
            // absence must not erase an already confirmed parent identity.
            .Where(key => IsContainerIdentityPolicy(policy)
                          || !ClaimScopeCatalog.IsParentScoped(key, lineage.MediaType))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var claims = selectedFields.Select(kv => new MetadataClaim
        {
            Id = Guid.NewGuid(),
            EntityId = ResolvePolicyScopedTarget(context.AssetId, lineage, policy, kv.Key),
            ProviderId = providerId,
            DecisionSourceProviderId = WellKnownProviders.UserManual,
            ClaimKey = kv.Key,
            ClaimValue = kv.Value,
            ClaimedAt = now,
            Confidence = 1.0,
            IsUserLocked = false,
        }).ToList();

        var canonicals = selectedFields.Select(kv => new CanonicalValue
        {
            EntityId = ResolvePolicyScopedTarget(context.AssetId, lineage, policy, kv.Key),
            Key = kv.Key,
            Value = kv.Value,
            LastScoredAt = now,
            IsConflicted = false,
            NeedsReview = false,
            WinningProviderId = providerId,
        }).ToList();

        var identityRevision = Guid.NewGuid().ToString("N");
        claims.AddRange(
        [
            new MetadataClaim
            {
                Id = Guid.NewGuid(),
                EntityId = identityTarget,
                ProviderId = providerId,
                DecisionSourceProviderId = WellKnownProviders.UserManual,
                ClaimKey = MetadataFieldConstants.IdentityProvider,
                ClaimValue = request.ProviderName,
                ClaimedAt = now,
                Confidence = 1.0,
            },
            new MetadataClaim
            {
                Id = Guid.NewGuid(),
                EntityId = identityTarget,
                ProviderId = providerId,
                DecisionSourceProviderId = WellKnownProviders.UserManual,
                ClaimKey = MetadataFieldConstants.IdentityProviderItemId,
                ClaimValue = request.ProviderItemId,
                ClaimedAt = now,
                Confidence = 1.0,
            },
        ]);
        canonicals.AddRange(
        [
            new CanonicalValue
            {
                EntityId = identityTarget,
                Key = MetadataFieldConstants.IdentityProvider,
                Value = request.ProviderName,
                LastScoredAt = now,
                WinningProviderId = providerId,
            },
            new CanonicalValue
            {
                EntityId = identityTarget,
                Key = MetadataFieldConstants.IdentityProviderItemId,
                Value = request.ProviderItemId,
                LastScoredAt = now,
                WinningProviderId = providerId,
            },
            new CanonicalValue
            {
                EntityId = identityTarget,
                Key = MetadataFieldConstants.IdentityRevision,
                Value = identityRevision,
                LastScoredAt = now,
                WinningProviderId = WellKnownProviders.UserManual,
            },
        ]);

        var bridgeEntries = selectedBridgeIds
            .Select(kv => new BridgeIdEntry
            {
                EntityId = ResolvePolicyScopedTarget(context.AssetId, lineage, policy, kv.Key),
                IdType = kv.Key,
                IdValue = kv.Value,
                ProviderId = request.ProviderName,
                CreatedAt = now,
            })
            .ToList();
        claims.AddRange(bridgeEntries.Select(entry => new MetadataClaim
        {
            Id = Guid.NewGuid(),
            EntityId = entry.EntityId,
            ProviderId = providerId,
            DecisionSourceProviderId = WellKnownProviders.UserManual,
            ClaimKey = entry.IdType,
            ClaimValue = entry.IdValue,
            ClaimedAt = now,
            Confidence = 1.0,
        }));
        canonicals.AddRange(bridgeEntries.Select(entry => new CanonicalValue
        {
            EntityId = entry.EntityId,
            Key = entry.IdType,
            Value = entry.IdValue,
            LastScoredAt = now,
            IsConflicted = false,
            NeedsReview = false,
            WinningProviderId = providerId,
        }));

        // Structural placement, selected retail values, bridge IDs, stale
        // identity cleanup, and the legacy external-ID projection commit in
        // one managed write callback. The queue and
        // all provider/network work remain below this durable boundary.
        var workId = ResolvePolicyWorkTarget(lineage, policy, BridgeIdKeys.WikidataQid);
        var externalIdentifierMutations = policy.BridgeIdKeys
            .Concat(selectedBridgeIds.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .GroupBy(key => ResolvePolicyWorkTarget(lineage, policy, key))
            .Select(group => new HierarchyExternalIdentifierMutation(
                group.Key,
                staleBridgeKeys.Where(key => group.Contains(key, StringComparer.OrdinalIgnoreCase)).ToList(),
                selectedBridgeIds
                    .Where(pair => group.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        // A sibling track's retail job compares with the album's stored reports under this lock; taking it here
        // keeps the user's choice from landing between that job's read and its write (SharedParentIdentity).
        using (await SharedParentIdentity.AcquireForLineageAsync(lineage, ct))
        {
            hierarchyImpact = await hierarchyAlignment.ApplyRetailIdentityAsync(
                alignmentEntityId,
                hierarchyRequest ?? new MembershipPreviewRequest(null, null, null, null),
                new HierarchyIdentityMutation(
                    claims.Select(claim => new HierarchyClaimMutation(
                        claim.EntityId,
                        claim.ProviderId,
                        claim.DecisionSourceProviderId ?? WellKnownProviders.UserManual,
                        claim.ClaimKey,
                        claim.ClaimValue,
                        claim.Confidence,
                        claim.IsUserLocked,
                        claim.ClaimedAt)).ToList(),
                    canonicals.Select(value => new HierarchyCanonicalMutation(
                        value.EntityId,
                        value.Key,
                        value.Value,
                        value.WinningProviderId,
                        value.NeedsReview,
                        value.LastScoredAt)).ToList(),
                    bridgeEntries.Select(entry => new HierarchyBridgeIdMutation(
                        entry.EntityId,
                        entry.IdType,
                        entry.IdValue,
                        entry.ProviderId ?? string.Empty,
                        entry.CreatedAt)).ToList(),
                    staleBridgeKeys.Select(key => new HierarchyIdentityArtifactMutation(
                        ResolvePolicyScopedTarget(context.AssetId, lineage, policy, key), key)).ToList(),
                    externalIdentifierMutations,
                    request.ExpectedIdentityRevision,
                    identityTarget),
                ct);
        }
        if (hierarchyImpact is { CanApply: false }
            && string.Equals(hierarchyImpact.Action, "conflict", StringComparison.OrdinalIgnoreCase))
        {
            return ApiErrors.Conflict(hierarchyImpact.ConflictMessage ?? hierarchyImpact.Message);
        }

        // The hierarchy transaction may have moved an episode/track under a
        // different container. Resolve container-scoped Wikidata state only
        // after that move, so the old show/album is never overwritten.
        var postAlignmentWorkId = ClaimScopeCatalog.IsParentScoped(BridgeIdKeys.WikidataQid, lineage.MediaType)
            && hierarchyImpact?.TargetRootEntityId is { } targetRootId
            && targetRootId != Guid.Empty
            ? targetRootId
            : workId;

        // Commit the durable identity job before optional network-backed artwork or
        // manifest refreshes. A transient provider failure must never leave a match
        // confirmed without a corresponding full enrichment cycle.
        if (string.Equals(policy.TargetFieldGroup, "album", StringComparison.OrdinalIgnoreCase))
        {
            await canonicalRepo.DeleteByKeyAsync(postAlignmentWorkId, MetadataFieldConstants.ChildEntitiesJson, ct);
            await canonicalRepo.DeleteByKeyAsync(postAlignmentWorkId, MetadataFieldConstants.TrackCount, ct);
        }
        else if (string.Equals(policy.TargetFieldGroup, "show", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(policy.TargetFieldGroup, "season", StringComparison.OrdinalIgnoreCase))
        {
            await canonicalRepo.DeleteByKeyAsync(postAlignmentWorkId, MetadataFieldConstants.ChildEntitiesJson, ct);
            await canonicalRepo.DeleteByKeyAsync(postAlignmentWorkId, MetadataFieldConstants.SeasonCount, ct);
            await canonicalRepo.DeleteByKeyAsync(postAlignmentWorkId, MetadataFieldConstants.EpisodeCount, ct);
        }

        var currentState = await itemCanonicalData.LoadWorkWikidataStateAsync(postAlignmentWorkId, ct);
        if (request.ClearAutoAlignedWikidata
            && !string.IsNullOrWhiteSpace(currentState?.Qid)
            && IsAutomationOwnedWikidataState(currentState.Status, currentState.Source, currentState.Locked))
        {
            await collectionRepo.UpdateWorkWikidataMatchStateAsync(postAlignmentWorkId, WorkWikidataStatus.Pending, WorkWikidataMatchSource.Retail, false, "", ct: ct);
        }
        else
        {
            await collectionRepo.UpdateWorkWikidataMatchStateAsync(postAlignmentWorkId, WorkWikidataStatus.ProviderOnly, WorkWikidataMatchSource.Retail, false, ct: ct);
        }

        var identityJobId = await pipeline.EnqueueAsync(new HarvestRequest
        {
            EntityId = context.AssetId,
            EntityType = EntityType.MediaAsset,
            MediaType = MediaTypeParser.Parse(context.MediaType),
            Hints = selectedFields
                .Append(new KeyValuePair<string, string>(MetadataFieldConstants.IdentityProvider, request.ProviderName))
                .Append(new KeyValuePair<string, string>(MetadataFieldConstants.IdentityRevision, identityRevision))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
            SkipRetailStage = true,
            IsUserResolution = true,
        }, ct);

        RetailArtworkReplacementResult? artworkResult = null;
        if (SupportsImmediateRetailArtworkReplacement(policy))
        {
            if (!string.IsNullOrWhiteSpace(request.CoverUrl))
            {
                await claimRepo.InsertBatchAsync(
                [
                    new MetadataClaim
                    {
                        Id = Guid.NewGuid(),
                        EntityId = ResolvePolicyScopedTarget(context.AssetId, lineage, policy, MetadataFieldConstants.Cover),
                        ProviderId = providerId,
                        DecisionSourceProviderId = WellKnownProviders.UserManual,
                        ClaimKey = MetadataFieldConstants.Cover,
                        ClaimValue = request.CoverUrl.Trim(),
                        ClaimedAt = now,
                        Confidence = 1.0,
                    },
                ], ct);
            }

            artworkResult = await coverArtWorker.ReplaceProviderArtworkAsync(
                context.AssetId,
                request.CoverUrl,
                request.ProviderName,
                providerId,
                ct);
        }

        string? albumManifestRefreshDetail = null;
        if (string.Equals(policy.MediaType, MediaType.Music.ToString(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(policy.TargetFieldGroup, "album", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var rootCanonicalValues = await canonicalRepo.GetByEntityAsync(postAlignmentWorkId, ct);
                var existingManifest = rootCanonicalValues.FirstOrDefault(value =>
                    string.Equals(value.Key, MetadataFieldConstants.ChildEntitiesJson, StringComparison.OrdinalIgnoreCase))?.Value;
                var refreshedManifest = await albumTrackManifestService.EnsureAlbumTrackManifestAsync(
                    postAlignmentWorkId,
                    selectedFields.GetValueOrDefault(MetadataFieldConstants.Artist)
                        ?? selectedFields.GetValueOrDefault("album_artist"),
                    selectedFields.GetValueOrDefault(MetadataFieldConstants.Album),
                    existingManifest,
                    rootCanonicalValues,
                    ct,
                    forceProviderRefresh: true);
                if (!string.IsNullOrWhiteSpace(refreshedManifest))
                {
                    var refreshedTrackCount = AlbumTrackManifestService.CountManifestTracks(refreshedManifest);
                    albumManifestRefreshDetail = $"Refreshed the confirmed album track listing with {refreshedTrackCount} track(s).";
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                loggerFactory
                    .CreateLogger("MediaEngine.Api.Endpoints.ItemCanonicalEndpoints")
                    .LogWarning(
                        ex,
                        "Confirmed album match for {EntityId}, but the provider track manifest could not be refreshed immediately",
                        entityId);
            }
        }

        await activityRepo.LogAsync(new SystemActivityEntry
        {
            OccurredAt = now,
            ActionType = SystemActionType.MetadataManualOverride,
            EntityId = entityId,
            EntityType = "Work",
            CollectionName = context.WorkTitle,
            Detail = $"Retail match replaced with {request.ProviderName} {request.ProviderItemId}.",
        }, ct);

        await timeline.RecordRetailMatchedAsync(
            context.AssetId,
            request.ProviderName,
            Math.Max(1, selectedFields.Count + request.BridgeIds.Count),
            ct: ct);

        await activityRepo.LogAsync(new SystemActivityEntry
        {
            OccurredAt = DateTimeOffset.UtcNow,
            ActionType = SystemActionType.MediaUpdated,
            EntityId = context.AssetId,
            EntityType = "MediaAsset",
            CollectionName = context.WorkTitle,
            Detail = artworkResult is null
                ? $"Retail identity changed to {request.ProviderName} {request.ProviderItemId}. {albumManifestRefreshDetail}".Trim()
                : $"Retail identity changed to {request.ProviderName} {request.ProviderItemId}. {artworkResult.Message} {albumManifestRefreshDetail}".Trim(),
        }, ct);
        await activityRepo.LogAsync(new SystemActivityEntry
        {
            OccurredAt = DateTimeOffset.UtcNow,
            ActionType = SystemActionType.HydrationEnqueued,
            EntityId = context.AssetId,
            EntityType = "MediaAsset",
            CollectionName = context.WorkTitle,
            Detail = $"Queued identity job {identityJobId} for the full enrichment cycle, including people, artwork, relationships, and file write-back.",
        }, ct);

        loggerFactory
            .CreateLogger("MediaEngine.Api.Endpoints.ItemCanonicalEndpoints")
            .LogInformation(
                "Manual retail update completed for {AssetId}: artworkChanged={ArtworkChanged}, identityJobId={IdentityJobId}",
                context.AssetId,
                artworkResult?.ArtworkChanged ?? false,
                identityJobId);

        await publisher.PublishAsync(
            SignalREvents.MetadataHarvested,
            new RetailMetadataHarvestedEvent(
                entityId,
                request.TargetScopeId,
                policy.TargetFieldGroup,
                request.ProviderName,
                request.ProviderItemId,
                selectedFields.Keys
                    .Concat(request.BridgeIds.Keys)
                    .Concat(artworkResult?.ArtworkChanged == true
                        ? [MetadataFieldConstants.Cover, MetadataFieldConstants.CoverUrl, MetadataFieldConstants.CoverState]
                        : [])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()),
            ct);

        if (request.ReviewItemId is { } reviewItemId)
        {
            await reviewRepo.UpdateStatusAsync(reviewItemId, ReviewStatus.Resolved, "user", ct);
        }

        const string enrichmentQueuedMessage = "Match confirmed. This file was queued for the full enrichment cycle. People, artwork, relationships, and file write-back will continue in the background.";
        return Results.Ok(new ItemCanonicalApplyResponseDto
        {
            EntityId = entityId,
            LinkState = "provider_only",
            LinkStatusLabel = enrichmentQueuedMessage,
            FieldsApplied = selectedFields.Count,
            IdsCleared = staleBridgeKeys,
            Message = enrichmentQueuedMessage,
            IdentityJobId = identityJobId,
            PipelineState = IdentityJobState.RetailMatched.ToString(),
            ArtworkChanged = artworkResult?.ArtworkChanged ?? false,
            ArtworkRemovedCount = artworkResult?.RemovedVariantCount ?? 0,
            ArtworkMessage = artworkResult?.Message,
            HierarchyChanged = hierarchyImpact?.Applied == true
                && !string.Equals(hierarchyImpact.Action, "none", StringComparison.OrdinalIgnoreCase),
            SelectedEntityId = hierarchyImpact?.SelectedEntityId ?? context.AssetId,
            TargetRootEntityId = hierarchyImpact?.TargetRootEntityId ?? lineage.TargetForParentScope,
            TargetParentEntityId = hierarchyImpact?.TargetParentEntityId,
            PreviousPath = hierarchyImpact?.CurrentPath,
            TargetPath = hierarchyImpact?.TargetPath,
            IdentityRevision = identityRevision,
        });
    }
}
