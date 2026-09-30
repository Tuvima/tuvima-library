using MediaEngine.Api.Http;
using MediaEngine.Api.Models;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Canonical;
using MediaEngine.Api.Services.Collections;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Application.ReadModels;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Items;
using MediaEngine.Contracts.Matching;
using MediaEngine.Contracts.Paging;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Providers.Workers;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Caching.Memory;

namespace MediaEngine.Api.Endpoints;

public static class ItemCanonicalEndpoints
{
    private static readonly HashSet<string> AllowedDisplayOverrideKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "title",
        "tagline",
        "description",
        "sort_title",
        "genre",
    };

    public static IEndpointRouteBuilder MapItemCanonicalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/library/items")
            .WithTags("Items");

        group.MapGet("/editor-suggestions/{field}", (
            string field,
            Guid? profileId,
            int? limit,
            EditorSuggestionReadService suggestions,
            CancellationToken ct) =>
        {
            if (field is not ("genre" or "tag" or "tags" or "custom_tags"))
            {
                return ApiErrors.BadRequest("Suggestions are available only for genres and tags.");
            }

            var page = PagedRequest.From(0, limit, defaultLimit: 100, maxLimit: 500);
            return Results.Ok(suggestions.GetValues(field, profileId, page.Limit, ct));
        })
        .WithName("GetItemEditorSuggestions")
        .WithSummary("Returns existing genre or profile-local tag values for editor autocomplete.")
        .Produces<IReadOnlyList<string>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead);

        group.MapPut("/{entityId:guid}/preferences", async (
            Guid entityId,
            ItemPreferencesRequest request,
            IMetadataClaimRepository claimRepo,
            ICanonicalValueRepository canonicalRepo,
            ISystemActivityRepository activityRepo,
            IWriteBackService writeBack,
            IEventPublisher publisher,
            IWorkRepository workRepo,
            IItemCanonicalRepository itemCanonicalData,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (request.Fields.Count == 0)
            {
                return ApiErrors.BadRequest("At least one preference field is required.");
            }

            var context = await itemCanonicalData.ResolveWorkAssetContextAsync(entityId, ct);
            if (context is null)
            {
                return ApiErrors.NotFound($"No current media asset or work target found for {entityId}.");
            }

            var now = DateTimeOffset.UtcNow;
            var claims = new List<MetadataClaim>();
            var canonicals = new List<CanonicalValue>();
            var updatedKeys = new List<string>();
            var lineage = await workRepo.GetLineageByAssetAsync(context.AssetId, ct);

            foreach (var (key, value) in request.Fields)
            {
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var targetId = ResolveScopedTarget(context.AssetId, lineage, key);
                claims.Add(new MetadataClaim
                {
                    Id = Guid.NewGuid(),
                    EntityId = targetId,
                    ProviderId = WellKnownProviders.UserManual,
                    ClaimKey = key,
                    ClaimValue = value,
                    ClaimedAt = now,
                    Confidence = 1.0,
                    IsUserLocked = true,
                });

                canonicals.Add(new CanonicalValue
                {
                    EntityId = targetId,
                    Key = key,
                    Value = value,
                    LastScoredAt = now,
                    IsConflicted = false,
                    NeedsReview = false,
                    WinningProviderId = WellKnownProviders.UserManual,
                });

                updatedKeys.Add(key);
            }

            if (updatedKeys.Count == 0)
            {
                return ApiErrors.BadRequest("No valid preference fields were provided.");
            }

            await claimRepo.InsertBatchAsync(claims, ct);
            await canonicalRepo.UpsertBatchAsync(canonicals, ct);

            await activityRepo.LogAsync(new SystemActivityEntry
            {
                OccurredAt = now,
                ActionType = SystemActionType.MetadataManualOverride,
                EntityId = entityId,
                EntityType = "Work",
                CollectionName = context.WorkTitle,
                Detail = $"Saved item preferences for {updatedKeys.Count} field(s): {string.Join(", ", updatedKeys)}.",
            }, ct);

            await publisher.PublishAsync(
                SignalREvents.MetadataHarvested,
                new ManualMetadataHarvestedEvent(
                    entityId,
                    "user_manual",
                    updatedKeys.ToArray()),
                ct);

            try
            {
                await writeBack.WriteMetadataAsync(context.AssetId, "item_preferences", ct);
            }
            catch (Exception ex)
            {
                loggerFactory
                    .CreateLogger("MediaEngine.Api.Endpoints.ItemCanonicalEndpoints")
                    .LogWarning(ex, "Write-back failed after saving item preferences for {EntityId}.", entityId);
            }

            return Results.Ok(new ItemPreferencesResponse
            {
                EntityId = entityId,
                FieldsUpdated = updatedKeys.Count,
                UpdatedKeys = updatedKeys,
                Message = $"Saved {updatedKeys.Count} preference field(s).",
            });
        })
        .WithName("SaveItemPreferences")
        .WithSummary("Save user-preferred item fields without changing external IDs.")
        .Produces<ItemPreferencesResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite);

        group.MapPut("/{entityId:guid}/display-overrides", async (
            Guid entityId,
            ItemDisplayOverridesRequest request,
            ISystemActivityRepository activityRepo,
            IItemCanonicalRepository itemCanonicalData,
            CancellationToken ct) =>
        {
            if (request.Fields.Count == 0)
            {
                return ApiErrors.BadRequest("At least one display override is required.");
            }

            var unsupportedKeys = request.Fields.Keys
                .Where(key => !AllowedDisplayOverrideKeys.Contains(key.Trim()))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (unsupportedKeys.Count > 0)
            {
                return ApiErrors.BadRequest($"Unsupported display override field(s): {string.Join(", ", unsupportedKeys)}.");
            }

            var displayOverrideState = await itemCanonicalData.LoadDisplayOverridesAsync(entityId, ct);
            if (!displayOverrideState.WorkExists)
            {
                return ApiErrors.NotFound($"No work found for {entityId}.");
            }

            var current = displayOverrideState.Values;
            var updatedKeys = new List<string>();
            foreach (var (key, value) in request.Fields)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                var normalizedKey = key.Trim();
                var normalizedValue = (value ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(normalizedValue))
                {
                    if (current.Remove(normalizedKey))
                    {
                        updatedKeys.Add(normalizedKey);
                    }

                    continue;
                }

                current[normalizedKey] = normalizedValue;
                updatedKeys.Add(normalizedKey);
            }

            if (updatedKeys.Count == 0)
            {
                return ApiErrors.BadRequest("No valid display override fields were provided.");
            }

            if (!await itemCanonicalData.SaveDisplayOverridesAsync(entityId, current, ct))
            {
                return ApiErrors.NotFound($"No work found for {entityId}.");
            }

            await activityRepo.LogAsync(new SystemActivityEntry
            {
                OccurredAt = DateTimeOffset.UtcNow,
                ActionType = SystemActionType.MetadataManualOverride,
                EntityId = entityId,
                EntityType = "Work",
                Detail = $"Saved display overrides for {updatedKeys.Count} field(s): {string.Join(", ", updatedKeys.Distinct(StringComparer.OrdinalIgnoreCase))}.",
            }, ct);

            return Results.Ok(new ItemDisplayOverridesResponse
            {
                EntityId = entityId,
                FieldsUpdated = updatedKeys.Count,
                UpdatedKeys = updatedKeys.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToList(),
                DisplayOverrides = current,
                Message = $"Saved {updatedKeys.Count} display override field(s).",
            });
        })
        .WithName("SaveItemDisplayOverrides")
        .WithSummary("Save presentation-only display overrides without changing canonical values.")
        .Produces<ItemDisplayOverridesResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite);

        group.MapPost("/{entityId:guid}/canonical-search", async (
            Guid entityId,
            ItemCanonicalSearchRequestDto request,
            RetailMatchPreviewService retailMatchPreview,
            WikidataMatchPreviewService wikidataMatchPreview,
            IItemCanonicalRepository itemCanonicalData,
            IHttpClientFactory httpFactory,
            IMemoryCache cache,
            CancellationToken ct) =>
        {
            var context = await itemCanonicalData.ResolveWorkAssetContextAsync(entityId, ct);
            if (context is null)
            {
                return ApiErrors.NotFound($"No current media asset or work target found for {entityId}.");
            }

            var mediaType = ResolveMediaType(request.MediaType, context.MediaType);
            var policy = ResolveTargetPolicy(mediaType, request.TargetKind, request.TargetFieldGroup);
            if (policy is null)
            {
                return ApiErrors.BadRequest($"Unsupported target field group '{request.TargetFieldGroup}' for media type '{mediaType}'.");
            }

            var draftFields = request.DraftFields
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

            var query = CanonicalCandidateBuilder.BuildCanonicalQuery(policy, draftFields, request.QueryOverride);
            if (string.IsNullOrWhiteSpace(query))
            {
                return ApiErrors.BadRequest("A search query or draft field values are required.");
            }

            var missingRequired = policy.RequiredFieldKeys
                .Where(key => !draftFields.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                .ToList();

            var retailCandidates = new List<ItemCanonicalRetailCandidateDto>();
            var linkedCandidates = new List<ItemCanonicalLinkedCandidateDto>();

            var searchMode = NormalizeSearchMode(request.SearchMode);
            var shouldSearchRetail = (searchMode is "retail_only" or "combined") && policy.SearchRetail;
            var shouldSearchUniverse = (searchMode is "wikidata_only" or "combined") && policy.SearchUniverse;

            var providerErrors = new List<string>();
            if (shouldSearchRetail)
            {
                // Explicit user search is allowed to discover a different
                // container. Keep the current draft as soft file hints, but do
                // not send it as structured search fields that a provider can
                // interpret as a hard show/season constraint.
                // Search and score against the identity being edited.  An editor
                // can be opened from a child (a track, issue, or episode), but a
                // container lookup must never compare that child title to an
                // album, series, or show candidate.
                var retailSearch = BuildRetailSearchContext(
                    policy,
                    draftFields,
                    request.QueryOverride,
                    context.WorkTitle,
                    context.PrimaryCreator,
                    context.Year);
                var retail = await retailMatchPreview.SearchAsync(
                    new Domain.Models.SearchRetailRequest(
                        Query: query,
                        MediaType: mediaType,
                        MaxCandidates: Math.Clamp(request.MaxCandidates, 1, 10),
                        LocalTitle: retailSearch.LocalTitle,
                        LocalAuthor: retailSearch.LocalAuthor,
                        LocalYear: retailSearch.LocalYear,
                        FileHints: retailSearch.FileHints,
                        SearchFields: retailSearch.SearchFields),
                    ct);

                providerErrors.AddRange(retail.ProviderErrors);
                retailCandidates = retail.Candidates
                    .Select(candidate => CanonicalCandidateBuilder.BuildRetailCandidate(candidate, mediaType, policy))
                    .ToList();
                // Result tiles never load provider originals directly. A small,
                // allow-listed preview keeps the editor responsive and gives all
                // supported provider results the same artwork contract.
                await Task.WhenAll(retailCandidates.Select(async candidate =>
                    candidate.PreviewUrl = await RetailCandidateArtworkPreview.LoadAsync(
                        candidate.ProviderName, candidate.CoverUrl, httpFactory, cache, ct)));
            }

            if (shouldSearchUniverse)
            {
                var universe = await wikidataMatchPreview.PreviewCandidatesAsync(
                    context.AssetId,
                    mediaType,
                    query,
                    draftFields,
                    Math.Clamp(request.MaxCandidates, 1, 10),
                    ct);

                linkedCandidates = universe.Candidates
                    .Select(candidate => CanonicalCandidateBuilder.BuildLinkedCandidate(candidate, mediaType, policy))
                    .ToList();
            }

            var unlinkedFields = policy.RequiredFieldKeys
                .Concat(policy.SuggestedFieldKeys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(draftFields.ContainsKey)
                .ToDictionary(key => key, key => draftFields[key], StringComparer.OrdinalIgnoreCase);

            return Results.Ok(new ItemCanonicalSearchResponseDto
            {
                EntityId = entityId,
                MediaType = mediaType,
                TargetKind = policy.TargetKind,
                TargetFieldGroup = policy.TargetFieldGroup,
                Query = query,
                RetailCandidates = retailCandidates,
                ProviderErrors = providerErrors,
                LinkedCandidates = linkedCandidates,
                DraftFields = draftFields,
                FallbackActions =
                [
                    "Keep current canonical value",
                    "Save as preference only",
                    "Apply as unlinked canonical value",
                ],
                NoResultMessage = providerErrors.Count > 0 && retailCandidates.Count == 0
                    ? "Retail search could not complete. Retry before treating this as no match."
                    : retailCandidates.Count == 0 && linkedCandidates.Count == 0
                    ? searchMode == "wikidata_only"
                        ? "No Wikidata identity was found. Keep the retail match as provider-only, mark Wikidata missing, or try a different query."
                        : "No retail results were found. Keep the current value, save the draft as a preference, or apply an unlinked canonical value when the required anchors are present."
                    : null,
                CanApplyUnlinkedCanonical = policy.AllowsTextOnly && missingRequired.Count == 0 && unlinkedFields.Count > 0,
                MissingRequiredFields = missingRequired,
                UnlinkedFields = unlinkedFields,
            });
        })
        .WithName("SearchItemCanonicalCandidates")
        .WithSummary("Run a targeted canonical search for a specific item field group.")
        .Produces<ItemCanonicalSearchResponseDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataMatch)
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataMatch);

        group.MapPost("/{entityId:guid}/canonical-apply", async (
            Guid entityId,
            ItemCanonicalApplyRequestDto request,
            IMetadataClaimRepository claimRepo,
            ICanonicalValueRepository canonicalRepo,
            IBridgeIdRepository bridgeIdRepo,
            ISystemActivityRepository activityRepo,
            IWriteBackService writeBack,
            IEventPublisher publisher,
            ICollectionRepository collectionRepo,
            IWorkRepository workRepo,
            IHydrationPipelineService pipeline,
            TimelineRecorder timeline,
            IItemCanonicalRepository itemCanonicalData,
            CanonicalCandidateBuilder candidateBuilder,
            IHierarchyAlignmentService hierarchyAlignment,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var context = await itemCanonicalData.ResolveWorkAssetContextAsync(entityId, ct);
            if (context is null)
            {
                return ApiErrors.NotFound($"No current media asset or work target found for {entityId}.");
            }

            var policy = ResolveTargetPolicy(context.MediaType, request.TargetKind, request.TargetFieldGroup);
            if (policy is null)
            {
                return ApiErrors.BadRequest($"Unsupported target field group '{request.TargetFieldGroup}' for media type '{context.MediaType}'.");
            }

            var now = DateTimeOffset.UtcNow;
            var selectedSuggested = request.AcceptedSuggestedKeys
                .Where(key => request.SuggestedFields.ContainsKey(key))
                .ToDictionary(key => key, key => request.SuggestedFields[key], StringComparer.OrdinalIgnoreCase);
            var selectedFields = request.RequiredFields
                .Concat(selectedSuggested)
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

            if (selectedFields.Count == 0 && request.BridgeIds.Count == 0 && request.QidFields.Count == 0)
            {
                return ApiErrors.BadRequest("No canonical data was selected to apply.");
            }

            var claims = new List<MetadataClaim>();
            var canonicals = new List<CanonicalValue>();
            var lineage = await workRepo.GetLineageByAssetAsync(context.AssetId, ct);
            var alignmentEntityId = await itemCanonicalData.ResolveWorkIdForAssetAsync(context.AssetId, ct) ?? context.AssetId;
            var hierarchyRequest = BuildHierarchyAlignmentRequest(policy, selectedFields, request);
            var hierarchyImpact = hierarchyRequest is null
                ? null
                : await hierarchyAlignment.PreviewAsync(alignmentEntityId, hierarchyRequest, ct);
            if (hierarchyImpact is { CanApply: false }
                && string.Equals(hierarchyImpact.Action, "conflict", StringComparison.OrdinalIgnoreCase))
            {
                return ApiErrors.Conflict(hierarchyImpact.ConflictMessage ?? "The requested hierarchy position is already occupied.");
            }

            foreach (var (key, value) in selectedFields.Concat(request.QidFields))
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var targetId = ResolveScopedTarget(context.AssetId, lineage, key);
                claims.Add(new MetadataClaim
                {
                    Id = Guid.NewGuid(),
                    EntityId = targetId,
                    ProviderId = WellKnownProviders.UserManual,
                    ClaimKey = key,
                    ClaimValue = value,
                    ClaimedAt = now,
                    Confidence = 1.0,
                    IsUserLocked = true,
                });

                canonicals.Add(new CanonicalValue
                {
                    EntityId = targetId,
                    Key = key,
                    Value = value,
                    LastScoredAt = now,
                    IsConflicted = false,
                    NeedsReview = false,
                    WinningProviderId = WellKnownProviders.UserManual,
                });
            }

            if (claims.Count > 0)
            {
                await claimRepo.InsertBatchAsync(claims, ct);
            }

            if (canonicals.Count > 0)
            {
                await canonicalRepo.UpsertBatchAsync(canonicals, ct);
            }

            var clearedIds = await candidateBuilder.ClearStaleIdsAsync(context.AssetId, lineage, policy, request, ct);

            if (request.BridgeIds.Count > 0)
            {
                var bridgeEntries = request.BridgeIds
                    .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                    .Select(kv => new BridgeIdEntry
                    {
                        EntityId = ResolveScopedTarget(context.AssetId, lineage, kv.Key),
                        IdType = kv.Key,
                        IdValue = kv.Value,
                        ProviderId = WellKnownProviders.UserManual.ToString(),
                        CreatedAt = now,
                    })
                    .ToList();

                await bridgeIdRepo.UpsertBatchAsync(bridgeEntries, ct);

                var bridgeClaims = bridgeEntries.Select(entry => new MetadataClaim
                {
                    Id = Guid.NewGuid(),
                    EntityId = entry.EntityId,
                    ProviderId = WellKnownProviders.UserManual,
                    ClaimKey = entry.IdType,
                    ClaimValue = entry.IdValue,
                    ClaimedAt = now,
                    Confidence = 1.0,
                    IsUserLocked = true,
                }).ToList();
                var bridgeCanonicals = bridgeEntries.Select(entry => new CanonicalValue
                {
                    EntityId = entry.EntityId,
                    Key = entry.IdType,
                    Value = entry.IdValue,
                    LastScoredAt = now,
                    IsConflicted = false,
                    NeedsReview = false,
                    WinningProviderId = WellKnownProviders.UserManual,
                }).ToList();

                await claimRepo.InsertBatchAsync(bridgeClaims, ct);
                await canonicalRepo.UpsertBatchAsync(bridgeCanonicals, ct);
            }

            if (request.QidFields.TryGetValue(BridgeIdKeys.WikidataQid, out var globalQid) && !string.IsNullOrWhiteSpace(globalQid))
            {
                var qidTargetId = ResolveScopedTarget(context.AssetId, lineage, BridgeIdKeys.WikidataQid);
                await itemCanonicalData.UpdateWorkIdentityAsync(qidTargetId, globalQid, ct);
                await collectionRepo.UpdateWorkWikidataMatchStateAsync(qidTargetId, WorkWikidataStatus.UserConfirmed, WorkWikidataMatchSource.User, true, globalQid, ct: ct);

                await pipeline.EnqueueAsync(new HarvestRequest
                {
                    EntityId = context.AssetId,
                    EntityType = EntityType.MediaAsset,
                    MediaType = MediaTypeParser.Parse(context.MediaType),
                    Hints = selectedFields,
                    PreResolvedQid = globalQid,
                    IsUserResolution = true,
                    SuppressReviewCreation = true,
                }, ct);
            }
            else if (string.Equals(NormalizeLinkState(request.LinkState), "provider_only", StringComparison.Ordinal))
            {
                await collectionRepo.UpdateWorkWikidataMatchStateAsync(
                    ResolveScopedTarget(context.AssetId, lineage, BridgeIdKeys.WikidataQid),
                    WorkWikidataStatus.ProviderOnly,
                    WorkWikidataMatchSource.Retail,
                    false,
                    ct: ct);

                if (request.BridgeIds.Count > 0)
                {
                    await timeline.RecordRetailMatchedAsync(
                        context.AssetId,
                        request.ProviderName ?? WellKnownProviders.UserManual.ToString(),
                        Math.Max(1, selectedFields.Count + request.BridgeIds.Count),
                        ct: ct);

                    await pipeline.EnqueueAsync(new HarvestRequest
                    {
                        EntityId = context.AssetId,
                        EntityType = EntityType.MediaAsset,
                        MediaType = MediaTypeParser.Parse(context.MediaType),
                        Hints = selectedFields,
                        SkipRetailStage = true,
                        IsUserResolution = true,
                    }, ct);
                }
            }

            await activityRepo.LogAsync(new SystemActivityEntry
            {
                OccurredAt = now,
                ActionType = SystemActionType.MetadataManualOverride,
                EntityId = entityId,
                EntityType = "Work",
                CollectionName = context.WorkTitle,
                Detail = $"Applied canonical {policy.TargetFieldGroup} selection ({NormalizeLinkState(request.LinkState)}) with {selectedFields.Count} field(s).",
            }, ct);

            await publisher.PublishAsync(
                SignalREvents.MetadataHarvested,
                new ManualMetadataHarvestedEvent(
                    entityId,
                    "user_manual",
                    selectedFields.Keys
                        .Concat(request.QidFields.Keys)
                        .Concat(request.BridgeIds.Keys)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray()),
                ct);

            try
            {
                await writeBack.WriteMetadataAsync(context.AssetId, "item_canonical_apply", ct);
            }
            catch (Exception ex)
            {
                loggerFactory
                    .CreateLogger("MediaEngine.Api.Endpoints.ItemCanonicalEndpoints")
                    .LogWarning(ex, "Write-back failed after applying canonical candidate for {EntityId}.", entityId);
            }

            return Results.Ok(new ItemCanonicalApplyResponseDto
            {
                EntityId = entityId,
                LinkState = NormalizeLinkState(request.LinkState),
                LinkStatusLabel = GetLinkStatusLabel(request.LinkState),
                FieldsApplied = selectedFields.Count,
                IdsCleared = clearedIds.ToList(),
                Message = $"Applied canonical {policy.TargetFieldGroup} fields.",
                HierarchyChanged = false,
                SelectedEntityId = hierarchyImpact?.SelectedEntityId ?? context.AssetId,
                TargetRootEntityId = hierarchyImpact?.TargetRootEntityId ?? lineage?.TargetForParentScope ?? context.AssetId,
                TargetParentEntityId = hierarchyImpact?.TargetParentEntityId,
                PreviousPath = hierarchyImpact?.CurrentPath,
                TargetPath = hierarchyImpact?.TargetPath,
            });
        })
        .WithName("ApplyItemCanonicalCandidate")
        .WithSummary("Apply a targeted canonical candidate and clear stale IDs for the same field group.")
        .Produces<ItemCanonicalApplyResponseDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataMatch)
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataMatch);

        group.MapGet("/{entityId:guid}/editor-preferences/{profileId:guid}", async (
            Guid entityId,
            Guid profileId,
            HttpContext httpContext,
            IProfileWorkPreferencesRepository preferences,
            CancellationToken ct) =>
        {
            if (!IsAuthorizedProfile(httpContext, profileId))
            {
                return Results.Forbid();
            }

            var current = await preferences.GetAsync(profileId, entityId, ct);
            return Results.Ok(ToEditorPreferencesResponse(current));
        })
        .WithName("GetItemEditorPreferences")
        .WithSummary("Returns profile-owned editor preferences and their optimistic-concurrency revision.")
        .Produces<ItemEditorPreferencesResponse>(StatusCodes.Status200OK)
        .RequireProfileOperation(ApplicationPermissionIds.MetadataRead)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead, "Work", "entityId");

        group.MapPut("/{entityId:guid}/editor-preferences/{profileId:guid}", async (
            Guid entityId,
            Guid profileId,
            ItemEditorPreferencesRequest request,
            HttpContext httpContext,
            IProfileWorkPreferencesRepository preferences,
            ISystemActivityRepository activityRepo,
            CancellationToken ct) =>
        {
            if (!IsAuthorizedProfile(httpContext, profileId))
            {
                return Results.Forbid();
            }

            var unsupportedKeys = request.DisplayOverrides.Keys
                .Where(key => !AllowedDisplayOverrideKeys.Contains(key.Trim()))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (unsupportedKeys.Count > 0)
            {
                return ApiErrors.BadRequest($"Unsupported display override field(s): {string.Join(", ", unsupportedKeys)}.");
            }

            var localTags = request.LocalTags
                .Select(tag => tag.Trim())
                .Where(tag => tag.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (localTags.Count > 30 || localTags.Any(tag => tag.Length > 64))
            {
                return ApiErrors.BadRequest("Use at most 30 local tags, each no longer than 64 characters.");
            }

            var result = await preferences.SaveAsync(new EditorPreferencesSaveCommand(
                profileId,
                entityId,
                request.ExpectedRevision,
                request.DisplayOverrides,
                localTags), ct);

            if (!result.WorkExists)
            {
                return ApiErrors.NotFound($"No work found for {entityId}.");
            }

            if (!result.ProfileExists)
            {
                return ApiErrors.NotFound($"No profile found for {profileId}.");
            }

            var response = ToEditorPreferencesResponse(result.Preferences, result.DisplayOverrides);
            if (result.Conflict)
            {
                // Not a bare ad-hoc error: this returns the same typed ItemEditorPreferencesResponse
                // body declared by .Produces<ItemEditorPreferencesResponse>(Status409Conflict) below,
                // so it deliberately does not route through ApiErrors (which only emits a string
                // "detail" problem+json body and would change this endpoint's documented 409 shape).
                return Results.Json(response, statusCode: StatusCodes.Status409Conflict);
            }

            await activityRepo.LogAsync(new SystemActivityEntry
            {
                OccurredAt = DateTimeOffset.UtcNow,
                ActionType = SystemActionType.MetadataManualOverride,
                EntityId = entityId,
                EntityType = "Work",
                Detail = "Saved local display and profile preferences from the inline editor.",
            }, ct);

            return Results.Ok(response);
        })
        .WithName("SaveItemEditorPreferences")
        .WithSummary("Atomically saves presentation overrides and profile-owned library preferences.")
        .Produces<ItemEditorPreferencesResponse>(StatusCodes.Status200OK)
        .Produces<ItemEditorPreferencesResponse>(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireProfileOperation(ApplicationPermissionIds.MetadataWrite)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite, "Work", "entityId");

        group.MapPost("/{entityId:guid}/retail-match", async (
            Guid entityId,
            ReplaceRetailMatchRequestDto request,
            IMetadataClaimRepository claimRepo,
            ICanonicalValueRepository canonicalRepo,
            IBridgeIdRepository bridgeIdRepo,
            ISystemActivityRepository activityRepo,
            IEventPublisher publisher,
            ICollectionRepository collectionRepo,
            IWorkRepository workRepo,
            IReviewQueueRepository reviewRepo,
            IHydrationPipelineService pipeline,
            AlbumTrackManifestService albumTrackManifestService,
            CoverArtWorker coverArtWorker,
            TimelineRecorder timeline,
            IItemCanonicalRepository itemCanonicalData,
            CanonicalCandidateBuilder candidateBuilder,
            IHierarchyAlignmentService hierarchyAlignment,
            TvdbRetailClient tvdb,
            MusicBrainzReleaseClient musicBrainz,
            AppleRetailClient apple,
            IEnumerable<IExternalMetadataProvider> externalProviders,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var context = await itemCanonicalData.ResolveWorkAssetContextAsync(entityId, ct);
            if (context is null)
            {
                return ApiErrors.NotFound($"No current media asset or work target found for {entityId}.");
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

            if (policy.MediaType == MediaType.TV.ToString() && policy.TargetFieldGroup == "show")
            {
                if (!tvdb.IsConfigured())
                    return ApiErrors.BadRequest("Connect TheTVDB in Settings before matching a show.");
                try
                {
                    var remoteSeries = await tvdb.GetSeriesAsync(request.ProviderItemId, ct);
                    if (!IsTvdbSeriesRecord(remoteSeries, request.ProviderItemId))
                        return ApiErrors.BadRequest("The selected TheTVDB series is no longer available. Search again before applying it.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    loggerFactory.CreateLogger("RetailShowMatch").LogWarning(ex,
                        "Could not verify TheTVDB series {SeriesId} before applying a match", request.ProviderItemId);
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

            var hierarchyRequest = BuildHierarchyAlignmentRequest(
                policy,
                selectedFields,
                request.ProviderName,
                request.ProviderItemId,
                request.BridgeIds);
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

            var identityTarget = ResolvePolicyIdentityTarget(context.AssetId, lineage, policy);
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
                    externalIdentifierMutations),
                ct);
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
                    "Manual retail update completed for {Title} ({AssetId}): provider={Provider}, providerItemId={ProviderItemId}, artworkChanged={ArtworkChanged}, identityJobId={IdentityJobId}",
                    context.WorkTitle,
                    context.AssetId,
                    request.ProviderName,
                    request.ProviderItemId,
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
            });
        })
        .WithName("ReplaceItemRetailMatch")
        .WithSummary("Replace or confirm the item retail/provider match.")
        .Produces<ItemCanonicalApplyResponseDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataMatch)
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataMatch);

        group.MapPost("/{entityId:guid}/wikidata-match", async (
            Guid entityId,
            ReplaceWikidataMatchRequestDto request,
            IMetadataClaimRepository claimRepo,
            ICanonicalValueRepository canonicalRepo,
            IHydrationPipelineService pipeline,
            ISystemActivityRepository activityRepo,
            IEventPublisher publisher,
            ICollectionRepository collectionRepo,
            IWorkRepository workRepo,
            IReviewQueueRepository reviewRepo,
            IItemCanonicalRepository itemCanonicalData,
            CancellationToken ct) =>
        {
            var context = await itemCanonicalData.ResolveWorkAssetContextAsync(entityId, ct);
            if (context is null)
            {
                return ApiErrors.NotFound($"No current media asset or work target found for {entityId}.");
            }

            var action = (request.Action ?? "replace").Trim().ToLowerInvariant();
            var policy = ResolveTargetPolicy(context.MediaType, request.TargetKind, request.TargetFieldGroup);
            if (policy is null)
            {
                return ApiErrors.BadRequest($"Unsupported target field group '{request.TargetFieldGroup}' for media type '{context.MediaType}'.");
            }

            var lineage = await workRepo.GetLineageByAssetAsync(context.AssetId, ct);
            if (lineage is null)
            {
                return ApiErrors.NotFound($"No work lineage found for {entityId}.");
            }

            var workId = ResolvePolicyWorkTarget(lineage, policy, BridgeIdKeys.WikidataQid);
            var now = DateTimeOffset.UtcNow;
            var fieldsApplied = 0;
            Guid? identityJobId = null;
            var message = action switch
            {
                "replace" => "Match confirmed. This file was queued for the full enrichment cycle. People, artwork, relationships, and file write-back will continue in the background.",
                "clear" => "Wikidata identity cleared; retail match kept.",
                "mark_missing" => "Retail match kept; Wikidata marked missing.",
                "reject" => "Wikidata identity rejected; retail match kept.",
                _ => "Wikidata identity updated.",
            };

            if (action == "replace")
            {
                if (string.IsNullOrWhiteSpace(request.Qid))
                {
                    return ApiErrors.BadRequest("QID is required when replacing a Wikidata match.");
                }

                var qid = request.Qid.Trim();
                var allowedCandidateFieldKeys = policy.RequiredFieldKeys
                    .Concat(policy.SuggestedFieldKeys)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var acceptedCandidateFields = request.AcceptedSuggestedKeys
                    .Where(allowedCandidateFieldKeys.Contains)
                    .Where(key => request.SuggestedFields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(key => key, key => request.SuggestedFields[key], StringComparer.OrdinalIgnoreCase);

                var replacementClaims = acceptedCandidateFields.Select(pair => new MetadataClaim
                {
                    Id = Guid.NewGuid(),
                    EntityId = ResolvePolicyWorkTarget(lineage, policy, pair.Key),
                    ProviderId = WellKnownProviders.UserManual,
                    DecisionSourceProviderId = WellKnownProviders.UserManual,
                    ClaimKey = pair.Key,
                    ClaimValue = pair.Value,
                    Confidence = 1.0,
                    ClaimedAt = now,
                    IsUserLocked = true,
                }).ToList();
                replacementClaims.Add(new MetadataClaim
                {
                    Id = Guid.NewGuid(),
                    EntityId = workId,
                    ProviderId = WellKnownProviders.Wikidata,
                    DecisionSourceProviderId = WellKnownProviders.UserManual,
                    ClaimKey = BridgeIdKeys.WikidataQid,
                    ClaimValue = qid,
                    Confidence = 1.0,
                    ClaimedAt = now,
                });
                await claimRepo.InsertBatchAsync(replacementClaims, ct);

                var replacementCanonicals = acceptedCandidateFields.Select(pair => new CanonicalValue
                {
                    EntityId = ResolvePolicyWorkTarget(lineage, policy, pair.Key),
                    Key = pair.Key,
                    Value = pair.Value,
                    LastScoredAt = now,
                    IsConflicted = false,
                    NeedsReview = false,
                    WinningProviderId = WellKnownProviders.UserManual,
                }).ToList();
                replacementCanonicals.Add(new CanonicalValue
                {
                    EntityId = workId,
                    Key = BridgeIdKeys.WikidataQid,
                    Value = qid,
                    LastScoredAt = now,
                    IsConflicted = false,
                    NeedsReview = false,
                    WinningProviderId = WellKnownProviders.Wikidata,
                });
                await canonicalRepo.UpsertBatchAsync(replacementCanonicals, ct);

                await collectionRepo.UpdateWorkWikidataMatchStateAsync(workId, WorkWikidataStatus.UserReplaced, WorkWikidataMatchSource.User, true, qid, ct: ct);
                fieldsApplied = 1 + acceptedCandidateFields.Count;

                if (request.RehydrateNow)
                {
                    var canonicals = await canonicalRepo.GetByEntityAsync(workId, ct);
                    var hints = canonicals
                        .ToDictionary(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);
                    foreach (var (key, value) in acceptedCandidateFields)
                    {
                        hints[key] = value;
                    }

                    identityJobId = await pipeline.EnqueueAsync(new HarvestRequest
                    {
                        EntityId = context.AssetId,
                        EntityType = EntityType.MediaAsset,
                        MediaType = MediaTypeParser.Parse(context.MediaType),
                        Hints = hints,
                        PreResolvedQid = qid,
                        SuppressReviewCreation = true,
                        IsUserResolution = true,
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
                }
            }
            else if (action == "clear")
            {
                await collectionRepo.UpdateWorkWikidataMatchStateAsync(workId, WorkWikidataStatus.ProviderOnly, WorkWikidataMatchSource.User, false, "", ct: ct);
            }
            else if (action == "mark_missing")
            {
                await collectionRepo.UpdateWorkWikidataMatchStateAsync(workId, WorkWikidataStatus.Missing, WorkWikidataMatchSource.User, false, "", ct: ct);
            }
            else if (action == "reject")
            {
                var rejected = await itemCanonicalData.AppendRejectedQidAsync(workId, request.RejectedQid ?? request.Qid, ct);
                await collectionRepo.UpdateWorkWikidataMatchStateAsync(workId, WorkWikidataStatus.UserRejected, WorkWikidataMatchSource.User, false, "", rejected, ct);
            }
            else
            {
                return ApiErrors.BadRequest("Unsupported Wikidata match action.");
            }

            await activityRepo.LogAsync(new SystemActivityEntry
            {
                OccurredAt = now,
                ActionType = SystemActionType.MetadataManualOverride,
                EntityId = entityId,
                EntityType = "Work",
                CollectionName = context.WorkTitle,
                Detail = $"Wikidata match action '{action}' applied. QID: {request.Qid ?? request.RejectedQid ?? "none"}.",
            }, ct);

            await publisher.PublishAsync(
                SignalREvents.MetadataHarvested,
                new WikidataMetadataHarvestedEvent(
                    entityId,
                    request.TargetScopeId,
                    policy.TargetFieldGroup,
                    "user_manual",
                    request.AcceptedSuggestedKeys
                        .Append(BridgeIdKeys.WikidataQid)
                        .Append("wikidata_status")
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray()),
                ct);

            if (request.ReviewItemId is { } reviewItemId)
            {
                await reviewRepo.UpdateStatusAsync(reviewItemId, ReviewStatus.Resolved, "user", ct);
            }

            return Results.Ok(new ItemCanonicalApplyResponseDto
            {
                EntityId = entityId,
                LinkState = action == "replace" ? "linked" : "provider_only",
                LinkStatusLabel = message,
                FieldsApplied = fieldsApplied,
                Message = message,
                IdentityJobId = identityJobId,
                PipelineState = identityJobId.HasValue ? IdentityJobState.QidResolved.ToString() : null,
            });
        })
        .WithName("ReplaceItemWikidataMatch")
        .WithSummary("Replace, clear, reject, or mark missing the item Wikidata identity.")
        .Produces<ItemCanonicalApplyResponseDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataMatch)
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataMatch);

        return app;
    }

    private static bool IsAuthorizedProfile(HttpContext context, Guid requestedProfileId) =>
        context.Items.TryGetValue(ProfileOperationAccessFilter.ActiveProfileItemKey, out var value)
        && value is Guid activeProfileId
        && activeProfileId == requestedProfileId;

    internal sealed record CanonicalTargetPolicy(
        string MediaType,
        string TargetKind,
        string TargetFieldGroup,
        IReadOnlyList<string> RequiredFieldKeys,
        IReadOnlyList<string> SuggestedFieldKeys,
        IReadOnlyList<string> BridgeIdKeys,
        IReadOnlyList<string> QidFieldKeys,
        IReadOnlyList<string> QueryFieldKeys,
        bool SearchRetail,
        bool SearchUniverse,
        bool AllowsTextOnly);

    private static bool IsAutomationOwnedWikidataState(string? status, string? source, bool locked) =>
        !locked
        && !string.Equals(source, "user", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(status, WorkWikidataStatus.UserConfirmed, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(status, WorkWikidataStatus.UserReplaced, StringComparison.OrdinalIgnoreCase);

    private static string ResolveMediaType(string? requestedMediaType, string fallbackMediaType) =>
        !string.IsNullOrWhiteSpace(requestedMediaType)
            ? requestedMediaType
            : (string.IsNullOrWhiteSpace(fallbackMediaType) ? MediaType.Unknown.ToString() : fallbackMediaType);

    internal static bool IsRetailProviderAllowed(string mediaType, string providerName, Guid providerId) =>
        MediaTypeParser.Parse(mediaType) != MediaType.TV
        || (string.Equals(providerName, "tvdb", StringComparison.OrdinalIgnoreCase)
            && providerId == WellKnownProviders.Tvdb);

    internal static bool IsTvdbShowCandidate(string providerItemId, IReadOnlyDictionary<string, string> bridgeIds) =>
        bridgeIds.TryGetValue(BridgeIdKeys.TvdbId, out var showId)
        && string.Equals(providerItemId, showId, StringComparison.Ordinal)
        && !bridgeIds.ContainsKey(BridgeIdKeys.TvdbEpisodeId);

    internal static bool IsTvdbSeriesRecord(System.Text.Json.Nodes.JsonNode? remote, string expectedId) =>
        remote is not null
        && string.Equals(remote["id"]?.ToString(), expectedId, StringComparison.Ordinal);

    internal sealed record RetailSearchContext(
        string? LocalTitle,
        string? LocalAuthor,
        string? LocalYear,
        IReadOnlyDictionary<string, string>? FileHints,
        IReadOnlyDictionary<string, string>? SearchFields);

    internal static RetailSearchContext BuildRetailSearchContext(
        CanonicalTargetPolicy policy,
        IReadOnlyDictionary<string, string> draftFields,
        string? queryOverride,
        string? fallbackTitle,
        string? fallbackAuthor,
        string? fallbackYear)
    {
        var fields = BuildRetailScopeFields(policy, draftFields);
        var title = !string.IsNullOrWhiteSpace(queryOverride) && IsContainerIdentityPolicy(policy)
            ? queryOverride.Trim()
            : ResolveRetailScopeTitle(policy, fields,
                IsContainerIdentityPolicy(policy) ? null : fallbackTitle);
        // A child creator or release year is not evidence about its parent.
        // Container fields must be supplied by that container's editor scope;
        // otherwise score only the identity that is actually known.
        var author = ResolveRetailScopeAuthor(
            policy,
            fields,
            IsContainerIdentityPolicy(policy) ? null : fallbackAuthor);
        var year = fields.GetValueOrDefault(MetadataFieldConstants.Year)
            ?? (IsContainerIdentityPolicy(policy) ? null : fallbackYear);

        // The scoring service uses title for every media type except TV
        // episodes. Add the scoped title explicitly so an album or series
        // candidate is compared to its container identity even when the draft
        // only carried `album`, `series`, or `show_name`.
        if (!string.IsNullOrWhiteSpace(title)
            && policy.TargetFieldGroup != "show_episode")
        {
            fields[MetadataFieldConstants.Title] = title;
        }

        return new RetailSearchContext(
            title,
            author,
            year,
            fields.Count == 0 ? null : fields,
            BuildRetailSearchFields(policy, fields, queryOverride));
    }

    internal static IReadOnlyDictionary<string, string>? BuildRetailSearchFields(
        CanonicalTargetPolicy policy, IReadOnlyDictionary<string, string> draftFields, string? queryOverride)
    {
        if (policy.MediaType == MediaType.Comics.ToString() && policy.TargetFieldGroup == "series")
        {
            var series = !string.IsNullOrWhiteSpace(queryOverride)
                ? queryOverride.Trim()
                : draftFields.GetValueOrDefault(MetadataFieldConstants.Series);
            if (string.IsNullOrWhiteSpace(series)) return null;
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [MetadataFieldConstants.Series] = series,
                ["container_search"] = "true",
            };
        }
        if (!string.IsNullOrWhiteSpace(queryOverride) || draftFields.Count == 0) return null;
        return BuildRetailScopeFields(policy, draftFields);
    }

    private static Dictionary<string, string> BuildRetailScopeFields(
        CanonicalTargetPolicy policy,
        IReadOnlyDictionary<string, string> draftFields)
    {
        var allowedKeys = policy.TargetFieldGroup switch
        {
            // Parent searches must not carry child ordinals or child titles to
            // a provider. Several providers treat those as an episode/track
            // lookup, even when the visible query is a show or album name.
            "show" => new[] { MetadataFieldConstants.ShowName, MetadataFieldConstants.Year, MetadataFieldConstants.Network },
            "album" => new[] { MetadataFieldConstants.Album, MetadataFieldConstants.Artist, "album_artist", MetadataFieldConstants.Year },
            "series" => new[] { MetadataFieldConstants.Series, MetadataFieldConstants.Author, MetadataFieldConstants.Artist, MetadataFieldConstants.Year },
            "artist" => new[] { MetadataFieldConstants.Artist },
            "narrator" => new[] { MetadataFieldConstants.Narrator, MetadataFieldConstants.Author },
            _ => null,
        };

        var scoped = allowedKeys is null
            ? new Dictionary<string, string>(draftFields, StringComparer.OrdinalIgnoreCase)
            : draftFields
                .Where(pair => allowedKeys.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        return scoped;
    }

    private static string? ResolveRetailScopeTitle(
        CanonicalTargetPolicy policy,
        IReadOnlyDictionary<string, string> fields,
        string? fallbackTitle) => policy.TargetFieldGroup switch
    {
        "show" => fields.GetValueOrDefault(MetadataFieldConstants.ShowName) ?? fallbackTitle,
        "album" => fields.GetValueOrDefault(MetadataFieldConstants.Album) ?? fallbackTitle,
        "series" => fields.GetValueOrDefault(MetadataFieldConstants.Series) ?? fallbackTitle,
        "artist" => fields.GetValueOrDefault(MetadataFieldConstants.Artist) ?? fallbackTitle,
        "narrator" => fields.GetValueOrDefault(MetadataFieldConstants.Narrator) ?? fallbackTitle,
        "show_episode" => fields.GetValueOrDefault(MetadataFieldConstants.EpisodeTitle)
                          ?? fields.GetValueOrDefault(MetadataFieldConstants.Title)
                          ?? fallbackTitle,
        _ => fields.GetValueOrDefault(MetadataFieldConstants.Title) ?? fallbackTitle,
    };

    private static string? ResolveRetailScopeAuthor(
        CanonicalTargetPolicy policy,
        IReadOnlyDictionary<string, string> fields,
        string? fallbackAuthor) => policy.TargetFieldGroup switch
    {
        "album" or "track" or "artist" => fields.GetValueOrDefault(MetadataFieldConstants.Artist)
                                               ?? fields.GetValueOrDefault("album_artist")
                                               ?? fallbackAuthor,
        _ => fields.GetValueOrDefault(MetadataFieldConstants.Author) ?? fallbackAuthor,
    };

    internal static bool IsRetailCandidateCompatible(
        CanonicalTargetPolicy policy,
        string providerName,
        string providerItemId,
        IReadOnlyDictionary<string, string> bridgeIds,
        out string? incompatibility)
    {
        incompatibility = null;
        if (policy.MediaType == MediaType.TV.ToString() && policy.TargetFieldGroup == "show"
            && !IsTvdbShowCandidate(providerItemId, bridgeIds))
        {
            incompatibility = "Select a TheTVDB series result to match the show; an episode or season result cannot be applied to the series.";
            return false;
        }

        if (policy.TargetFieldGroup == "album"
            && bridgeIds.ContainsKey(BridgeIdKeys.MusicBrainzRecordingId)
            && !bridgeIds.ContainsKey(BridgeIdKeys.MusicBrainzReleaseId)
            && !bridgeIds.ContainsKey(BridgeIdKeys.MusicBrainzReleaseGroupId)
            && !bridgeIds.ContainsKey(BridgeIdKeys.AppleMusicCollectionId))
        {
            incompatibility = "Select an album result to match the album; this result only identifies an individual track.";
            return false;
        }

        if (policy.TargetFieldGroup == "track"
            && (bridgeIds.ContainsKey(BridgeIdKeys.MusicBrainzReleaseId)
                || bridgeIds.ContainsKey(BridgeIdKeys.MusicBrainzReleaseGroupId)
                || bridgeIds.ContainsKey(BridgeIdKeys.AppleMusicCollectionId))
            && !bridgeIds.ContainsKey(BridgeIdKeys.MusicBrainzRecordingId)
            && !bridgeIds.ContainsKey(BridgeIdKeys.AppleMusicId)
            && !bridgeIds.ContainsKey(BridgeIdKeys.Isrc))
        {
            incompatibility = "Select a track result to match this song; this result only identifies an album.";
            return false;
        }

        if (policy.MediaType == MediaType.Comics.ToString()
            && policy.TargetFieldGroup == "series"
            && (string.Equals(providerName, "comicvine", StringComparison.OrdinalIgnoreCase)
                || string.Equals(providerName, "comic_vine", StringComparison.OrdinalIgnoreCase))
            && !bridgeIds.ContainsKey(BridgeIdKeys.ComicVineVolumeId))
        {
            incompatibility = "Select a Comic Vine volume result to match the series; an issue result cannot be applied to the series.";
            return false;
        }

        return true;
    }

    private static async Task<IResult?> VerifyProviderParentChildRelationAsync(
        CanonicalTargetPolicy policy,
        string providerName,
        IReadOnlyDictionary<string, string> selectedBridgeIds,
        WorkLineage lineage,
        IBridgeIdRepository bridgeIds,
        ItemCanonicalWorkAssetContext context,
        MusicBrainzReleaseClient musicBrainz,
        AppleRetailClient apple,
        IEnumerable<IExternalMetadataProvider> externalProviders,
        CancellationToken ct)
    {
        if (policy.MediaType == MediaType.Music.ToString()
            && policy.TargetFieldGroup == "track"
            && string.Equals(providerName, "musicbrainz", StringComparison.OrdinalIgnoreCase)
            && selectedBridgeIds.TryGetValue(BridgeIdKeys.MusicBrainzRecordingId, out var recordingId))
        {
            if (!selectedBridgeIds.TryGetValue(BridgeIdKeys.MusicBrainzReleaseId, out var releaseId))
            {
                return ApiErrors.Conflict("This MusicBrainz track result does not identify an exact release. Choose a release-specific track result before changing the album.");
            }

            var release = await musicBrainz.FetchReleaseAsync(releaseId, ct);
            var containsRecording = MusicBrainzAlbumManifestJson.ContainsRecording(release?.ManifestJson, recordingId);
            if (containsRecording is null)
            {
                return ApiErrors.Problem(StatusCodes.Status502BadGateway, "MusicBrainz verification is unavailable.", "MusicBrainz could not verify the selected track's release. Try again later.");
            }

            if (!containsRecording.Value)
            {
                return ApiErrors.Conflict("The selected MusicBrainz recording is not part of the selected release. Choose the release that contains this track.");
            }
        }

        if (policy.MediaType == MediaType.Music.ToString()
            && policy.TargetFieldGroup == "track"
            && (providerName.Equals("apple_api", StringComparison.OrdinalIgnoreCase)
                || providerName.Equals("apple_music", StringComparison.OrdinalIgnoreCase))
            && selectedBridgeIds.TryGetValue(BridgeIdKeys.AppleMusicId, out var trackId))
        {
            if (!selectedBridgeIds.TryGetValue(BridgeIdKeys.AppleMusicCollectionId, out var collectionId))
                return ApiErrors.Conflict("This Apple Music track result has no album ID. Choose a track from an album result.");
            try
            {
                var tracks = await apple.FetchAlbumTracksAsync(collectionId, "us", "en", ct);
                if (!tracks.Any(track => track["trackId"]?.ToString() == trackId))
                    return ApiErrors.Conflict("The selected Apple Music track is not part of this album.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return ApiErrors.Problem(StatusCodes.Status502BadGateway,
                    "Apple Music verification is unavailable.", "Try again later.");
            }
        }

        if (policy.MediaType == MediaType.Comics.ToString()
            && policy.TargetFieldGroup == "series"
            && (string.Equals(providerName, "comicvine", StringComparison.OrdinalIgnoreCase)
                || string.Equals(providerName, "comic_vine", StringComparison.OrdinalIgnoreCase))
            && selectedBridgeIds.TryGetValue(BridgeIdKeys.ComicVineVolumeId, out var selectedVolumeId))
        {
            var localIssue = await bridgeIds.FindAsync(lineage.TargetForSelfScope, BridgeIdKeys.ComicVineId, ct);
            if (localIssue is null || string.IsNullOrWhiteSpace(localIssue.IdValue))
            {
                // A run may be confirmed before its owned issues have individual
                // Comic Vine identities. Preserve that valid parent selection;
                // a later issue-level match will establish and verify its volume.
                return null;
            }

            var comicVine = externalProviders.FirstOrDefault(provider =>
                string.Equals(provider.Name, "comicvine", StringComparison.OrdinalIgnoreCase));
            if (comicVine is null)
            {
                return ApiErrors.Problem(StatusCodes.Status502BadGateway, "Comic Vine verification is unavailable.", "Try again later.");
            }

            var claims = await comicVine.FetchAsync(new ProviderLookupRequest
            {
                EntityId = lineage.TargetForSelfScope,
                EntityType = EntityType.Work,
                MediaType = MediaType.Comics,
                Title = context.WorkTitle,
                Hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [BridgeIdKeys.ComicVineId] = localIssue.IdValue,
                },
            }, ct);
            var resolvedVolumeId = claims.FirstOrDefault(claim =>
                string.Equals(claim.Key, BridgeIdKeys.ComicVineVolumeId, StringComparison.OrdinalIgnoreCase))?.Value;
            if (string.IsNullOrWhiteSpace(resolvedVolumeId))
            {
                return ApiErrors.Problem(StatusCodes.Status502BadGateway, "Comic Vine verification is unavailable.", "Comic Vine could not verify this issue's run. Try again later.");
            }

            if (!string.Equals(resolvedVolumeId, selectedVolumeId, StringComparison.OrdinalIgnoreCase))
            {
                return ApiErrors.Conflict("This Comic Vine issue belongs to a different run. Choose the run reported for this issue.");
            }
        }

        if (policy.MediaType == MediaType.Comics.ToString()
            && policy.TargetFieldGroup == "issue"
            && (string.Equals(providerName, "comicvine", StringComparison.OrdinalIgnoreCase)
                || string.Equals(providerName, "comic_vine", StringComparison.OrdinalIgnoreCase)))
        {
            var currentRun = await bridgeIds.FindAsync(lineage.TargetForParentScope, BridgeIdKeys.ComicVineVolumeId, ct);
            if (!string.IsNullOrWhiteSpace(currentRun?.IdValue))
            {
                selectedBridgeIds.TryGetValue(BridgeIdKeys.ComicVineVolumeId, out var candidateVolumeId);
                var conflict = ValidateComicIssueVolumeAlignment(currentRun.IdValue, candidateVolumeId);
                if (conflict is not null)
                {
                    return ApiErrors.Conflict(conflict);
                }
            }
        }

        return null;
    }

    internal static string? ValidateComicIssueVolumeAlignment(string? confirmedVolumeId, string? candidateVolumeId)
    {
        if (string.IsNullOrWhiteSpace(confirmedVolumeId))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(candidateVolumeId))
        {
            return "This Comic Vine issue result does not identify its run. Choose an issue result with a Comic Vine volume before applying it to the confirmed run.";
        }

        return string.Equals(confirmedVolumeId, candidateVolumeId, StringComparison.OrdinalIgnoreCase)
            ? null
            : "This Comic Vine issue result belongs to a different run than the run already matched for this issue. Match the run first or choose an issue from the current run.";
    }

    private static CanonicalTargetPolicy? ResolveTargetPolicy(string mediaType, string targetKind, string targetFieldGroup) =>
        (mediaType.Trim(), targetFieldGroup.Trim().ToLowerInvariant()) switch
        {
            ("Music", "album") => new CanonicalTargetPolicy(mediaType, "container", "album",
                [MetadataFieldConstants.Artist, MetadataFieldConstants.Album],
                [MetadataFieldConstants.Year, MetadataFieldConstants.Genre, MetadataFieldConstants.Description, "album_artist"],
                [BridgeIdKeys.AppleArtistId, BridgeIdKeys.AppleMusicCollectionId, BridgeIdKeys.MusicBrainzId, BridgeIdKeys.MusicBrainzReleaseId, BridgeIdKeys.MusicBrainzReleaseGroupId],
                [BridgeIdKeys.WikidataQid],
                [MetadataFieldConstants.Artist, MetadataFieldConstants.Album],
                true, true, true),
            ("Music", "track") => new CanonicalTargetPolicy(mediaType, "item", "track",
                [MetadataFieldConstants.Title, MetadataFieldConstants.Artist, MetadataFieldConstants.Album],
                [MetadataFieldConstants.TrackNumber, MetadataFieldConstants.DurationField, MetadataFieldConstants.Composer, MetadataFieldConstants.Year, "disc_number"],
                // Keep the parent album IDs in the selected result as well. The
                // hierarchy transaction remaps parent-scoped IDs to the album
                // chosen for this one track, which lets a track rematch move it
                // without relabelling the album it came from.
                [BridgeIdKeys.AppleMusicId, BridgeIdKeys.AppleMusicCollectionId, BridgeIdKeys.MusicBrainzRecordingId, BridgeIdKeys.MusicBrainzWorkId, BridgeIdKeys.MusicBrainzReleaseId, BridgeIdKeys.MusicBrainzReleaseGroupId, BridgeIdKeys.Isrc],
                [BridgeIdKeys.WikidataQid],
                [MetadataFieldConstants.Title, MetadataFieldConstants.Artist, MetadataFieldConstants.Album],
                true, true, true),
            ("Music", "artist") => new CanonicalTargetPolicy(mediaType, "person", "artist",
                [MetadataFieldConstants.Artist],
                [MetadataFieldConstants.Genre],
                [BridgeIdKeys.AppleArtistId, BridgeIdKeys.MusicBrainzId],
                ["artist_qid"],
                [MetadataFieldConstants.Artist],
                true, true, true),
            ("Audiobooks", "narrator") => new CanonicalTargetPolicy(mediaType, "person", "narrator",
                [MetadataFieldConstants.Narrator],
                [MetadataFieldConstants.Title, MetadataFieldConstants.Author, MetadataFieldConstants.Series, MetadataFieldConstants.SeriesPosition],
                [],
                ["narrator_qid"],
                [MetadataFieldConstants.Narrator, MetadataFieldConstants.Title, MetadataFieldConstants.Author],
                false, true, true),
            ("Audiobooks", "series") => new CanonicalTargetPolicy(mediaType, "container", "series",
                [MetadataFieldConstants.Series],
                [MetadataFieldConstants.SeriesPosition, MetadataFieldConstants.Title, MetadataFieldConstants.Author, MetadataFieldConstants.Year],
                // Current retail providers expose an audiobook/work ID, not a
                // stable series ID. Keep retail identity unavailable here until
                // a provider contract can identify the series itself.
                [],
                ["series_qid"],
                [MetadataFieldConstants.Series, MetadataFieldConstants.Author],
                false, true, true),
            ("Audiobooks", "audiobook_identity") => new CanonicalTargetPolicy(mediaType, string.IsNullOrWhiteSpace(targetKind) ? "item" : targetKind, "audiobook_identity",
                [MetadataFieldConstants.Title, MetadataFieldConstants.Author],
                [MetadataFieldConstants.Narrator, MetadataFieldConstants.Series, MetadataFieldConstants.SeriesPosition, MetadataFieldConstants.Year, MetadataFieldConstants.PublisherField, MetadataFieldConstants.DurationField, MetadataFieldConstants.Genre],
                [BridgeIdKeys.AudibleId, BridgeIdKeys.Isbn, BridgeIdKeys.Asin],
                [BridgeIdKeys.WikidataQid],
                [MetadataFieldConstants.Title, MetadataFieldConstants.Author],
                true, true, true),
            ("Books", "series") => new CanonicalTargetPolicy(mediaType, "container", "series",
                [MetadataFieldConstants.Series],
                [MetadataFieldConstants.SeriesPosition, MetadataFieldConstants.Title, MetadataFieldConstants.Author, MetadataFieldConstants.Year],
                // ISBN and ASIN identify an edition/work, not a book series.
                [],
                ["series_qid"],
                [MetadataFieldConstants.Series, MetadataFieldConstants.Author],
                false, true, true),
            ("Books", "book_identity") => new CanonicalTargetPolicy(mediaType, string.IsNullOrWhiteSpace(targetKind) ? "item" : targetKind, "book_identity",
                [MetadataFieldConstants.Title, MetadataFieldConstants.Author],
                [MetadataFieldConstants.Series, MetadataFieldConstants.SeriesPosition, MetadataFieldConstants.Year, MetadataFieldConstants.PublisherField, MetadataFieldConstants.Genre, MetadataFieldConstants.Language],
                [BridgeIdKeys.Isbn, BridgeIdKeys.Asin, BridgeIdKeys.AppleBooksId, BridgeIdKeys.OpenLibraryId],
                [BridgeIdKeys.WikidataQid],
                [MetadataFieldConstants.Title, MetadataFieldConstants.Author, MetadataFieldConstants.Series],
                true, true, true),
            ("Movies", "movie_identity") => new CanonicalTargetPolicy(mediaType, string.IsNullOrWhiteSpace(targetKind) ? "item" : targetKind, "movie_identity",
                [MetadataFieldConstants.Title, MetadataFieldConstants.Year],
                [MetadataFieldConstants.Director, MetadataFieldConstants.OriginalTitle, MetadataFieldConstants.Runtime, MetadataFieldConstants.Genre, MetadataFieldConstants.Composer, MetadataFieldConstants.CastMember],
                [BridgeIdKeys.TmdbId, BridgeIdKeys.ImdbId],
                [BridgeIdKeys.WikidataQid],
                [MetadataFieldConstants.Title, MetadataFieldConstants.Year, MetadataFieldConstants.Director],
                true, true, true),
            ("TV", "show") => new CanonicalTargetPolicy(mediaType, "container", "show",
                [MetadataFieldConstants.ShowName],
                [MetadataFieldConstants.Year, MetadataFieldConstants.Network],
                [BridgeIdKeys.TvdbId, BridgeIdKeys.ImdbId],
                [BridgeIdKeys.WikidataQid],
                [MetadataFieldConstants.ShowName, MetadataFieldConstants.Year],
                true, true, true),
            ("TV", "show_episode") => new CanonicalTargetPolicy(mediaType, string.IsNullOrWhiteSpace(targetKind) ? "item" : targetKind, "show_episode",
                [MetadataFieldConstants.ShowName, MetadataFieldConstants.SeasonNumber, MetadataFieldConstants.EpisodeNumber],
                [MetadataFieldConstants.EpisodeTitle, MetadataFieldConstants.Year, MetadataFieldConstants.Runtime, MetadataFieldConstants.Director, MetadataFieldConstants.CastMember],
                [BridgeIdKeys.TvdbId, BridgeIdKeys.TvdbEpisodeId, BridgeIdKeys.ImdbId],
                [BridgeIdKeys.WikidataQid],
                [MetadataFieldConstants.ShowName, MetadataFieldConstants.SeasonNumber, MetadataFieldConstants.EpisodeNumber, MetadataFieldConstants.EpisodeTitle],
                true, true, true),
            ("Comics", "series") => new CanonicalTargetPolicy(mediaType, "container", "series",
                [MetadataFieldConstants.Series],
                [MetadataFieldConstants.SeriesPosition, MetadataFieldConstants.Title, MetadataFieldConstants.Year, MetadataFieldConstants.Author, MetadataFieldConstants.Illustrator, MetadataFieldConstants.PublisherField, MetadataFieldConstants.Genre],
                [BridgeIdKeys.ComicVineVolumeId],
                ["series_qid"],
                [MetadataFieldConstants.Series, MetadataFieldConstants.SeriesPosition, MetadataFieldConstants.Title],
                true, true, true),
            ("Comics", "issue") => new CanonicalTargetPolicy(mediaType, string.IsNullOrWhiteSpace(targetKind) ? "item" : targetKind, "issue",
                [MetadataFieldConstants.Series, MetadataFieldConstants.SeriesPosition],
                [MetadataFieldConstants.Title, MetadataFieldConstants.Year, MetadataFieldConstants.Author, MetadataFieldConstants.Illustrator, MetadataFieldConstants.PublisherField, MetadataFieldConstants.Genre],
                [BridgeIdKeys.ComicVineId, BridgeIdKeys.ComicVineVolumeId, BridgeIdKeys.Isbn],
                [BridgeIdKeys.WikidataQid],
                [MetadataFieldConstants.Series, MetadataFieldConstants.SeriesPosition, MetadataFieldConstants.Title],
                true, true, true),
            _ => null,
        };

    private static Guid ResolvePolicyIdentityTarget(
        Guid assetId,
        WorkLineage lineage,
        CanonicalTargetPolicy policy) =>
        IsContainerIdentityPolicy(policy)
            ? lineage.TargetForParentScope
            : assetId;

    private static Guid ResolvePolicyScopedTarget(
        Guid assetId,
        WorkLineage lineage,
        CanonicalTargetPolicy policy,
        string key) =>
        IsContainerIdentityPolicy(policy) || ClaimScopeCatalog.IsParentScoped(key, lineage.MediaType)
            ? lineage.TargetForParentScope
            : assetId;

    private static Guid ResolvePolicyWorkTarget(
        WorkLineage lineage,
        CanonicalTargetPolicy policy,
        string key) =>
        IsContainerIdentityPolicy(policy) || ClaimScopeCatalog.IsParentScoped(key, lineage.MediaType)
            ? lineage.TargetForParentScope
            : lineage.TargetForSelfScope;

    internal static bool IsContainerIdentityPolicy(CanonicalTargetPolicy policy) =>
        string.Equals(policy.TargetKind, "container", StringComparison.OrdinalIgnoreCase);

    private static bool SupportsImmediateRetailArtworkReplacement(CanonicalTargetPolicy policy) =>
        policy.TargetFieldGroup is
            "book_identity" or
            "audiobook_identity" or
            "issue" or
            "movie_identity" or
            "album";

    private static async Task ReplaceScopedExternalIdentifiersAsync(
        WorkLineage lineage,
        CanonicalTargetPolicy policy,
        IReadOnlyCollection<string> staleKeys,
        IReadOnlyDictionary<string, string> replacements,
        IItemCanonicalRepository itemCanonicalData,
        CancellationToken ct)
    {
        var targetWorkIds = policy.BridgeIdKeys
            .Concat(replacements.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .GroupBy(key => ResolvePolicyWorkTarget(lineage, policy, key));

        foreach (var target in targetWorkIds)
        {
            var keysForTarget = staleKeys
                .Where(key => ResolvePolicyWorkTarget(lineage, policy, key) == target.Key)
                .ToList();
            var replacementsForTarget = replacements
                .Where(pair => ResolvePolicyWorkTarget(lineage, policy, pair.Key) == target.Key)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            await itemCanonicalData.ReplaceExternalIdentifiersAsync(
                target.Key,
                keysForTarget,
                replacementsForTarget,
                ct);
        }
    }

    internal static Guid ResolveScopedTarget(Guid assetId, WorkLineage? lineage, string key)
    {
        if (lineage is null)
        {
            return assetId;
        }

        return ClaimScopeCatalog.IsParentScoped(key, lineage.MediaType)
            ? lineage.TargetForParentScope
            : assetId;
    }

    private static MembershipPreviewRequest? BuildHierarchyAlignmentRequest(
        CanonicalTargetPolicy policy,
        IReadOnlyDictionary<string, string> selectedFields,
        ItemCanonicalApplyRequestDto request) =>
        BuildHierarchyAlignmentRequest(
            policy,
            selectedFields,
            request.ProviderName,
            request.ProviderItemId,
            request.BridgeIds);

    private static MembershipPreviewRequest? BuildHierarchyAlignmentRequest(
        CanonicalTargetPolicy policy,
        IReadOnlyDictionary<string, string> selectedFields,
        string? providerName,
        string? providerItemId,
        IReadOnlyDictionary<string, string> bridgeIds)
    {
        var fields = selectedFields.ToDictionary(
            pair => pair.Key,
            pair => (string?)pair.Value,
            StringComparer.OrdinalIgnoreCase);
        var suggestions = new Dictionary<string, MembershipSuggestionSelection>(StringComparer.OrdinalIgnoreCase);

        if (policy.TargetFieldGroup == "show_episode"
            && fields.TryGetValue(MetadataFieldConstants.ShowName, out var showName)
            && !string.IsNullOrWhiteSpace(showName))
        {
            bridgeIds.TryGetValue(BridgeIdKeys.TmdbId, out var tmdbId);
            bridgeIds.TryGetValue(BridgeIdKeys.TvdbId, out var tvdbId);
            var showIdKey = !string.IsNullOrWhiteSpace(tvdbId)
                && string.Equals(providerName, "tvdb", StringComparison.OrdinalIgnoreCase)
                ? BridgeIdKeys.TvdbId : BridgeIdKeys.TmdbId;
            var showId = showIdKey == BridgeIdKeys.TvdbId ? tvdbId : tmdbId;
            suggestions["show"] = new MembershipSuggestionSelection(
                EntityId: null,
                Source: string.IsNullOrWhiteSpace(showId) ? "local" : "retail",
                LocalExisting: false,
                Kind: "show",
                Label: showName,
                Subtitle: null,
                ProviderName: providerName,
                ProviderItemId: providerItemId,
                ExternalIdKey: string.IsNullOrWhiteSpace(showId) ? null : showIdKey,
                ExternalIdValue: showId);
        }

        if (policy.TargetFieldGroup == "track"
            && fields.TryGetValue(MetadataFieldConstants.Album, out var album)
            && !string.IsNullOrWhiteSpace(album))
        {
            bridgeIds.TryGetValue(BridgeIdKeys.MusicBrainzReleaseGroupId, out var releaseGroupId);
            suggestions["album"] = new MembershipSuggestionSelection(
                EntityId: null,
                Source: string.IsNullOrWhiteSpace(releaseGroupId) ? "local" : "retail",
                LocalExisting: false,
                Kind: "album",
                Label: album,
                Subtitle: fields.GetValueOrDefault(MetadataFieldConstants.Artist),
                ProviderName: providerName,
                ProviderItemId: providerItemId,
                ExternalIdKey: string.IsNullOrWhiteSpace(releaseGroupId) ? null : BridgeIdKeys.MusicBrainzReleaseGroupId,
                ExternalIdValue: releaseGroupId);
        }

        return new MembershipPreviewRequest(
            ScopeId: null,
            FieldValues: fields,
            SelectedTargetIds: null,
            SelectedSuggestions: suggestions);
    }

    private static string NormalizeLinkState(string linkState) => linkState.Trim().ToLowerInvariant() switch
    {
        "linked" => "linked",
        "provider_only" => "provider_only",
        _ => "text_only",
    };

    private static string NormalizeSearchMode(string? searchMode) =>
        (searchMode ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "wikidata_only" => "wikidata_only",
            "combined" => "combined",
            _ => "retail_only",
        };

    private static string GetLinkStatusLabel(string linkState) => NormalizeLinkState(linkState) switch
    {
        "linked" => "Linked to Wikidata",
        "provider_only" => "Linked to provider only",
        _ => "Saved without external link",
    };

    private static ItemEditorPreferencesResponse ToEditorPreferencesResponse(
        ProfileWorkPreferences preferences,
        IReadOnlyDictionary<string, string>? displayOverrides = null) => new()
        {
            ProfileId = preferences.ProfileId,
            WorkId = preferences.WorkId,
            LocalTags = preferences.LocalTags,
            Revision = preferences.Revision,
            UpdatedAt = preferences.UpdatedAt,
            DisplayOverrides = displayOverrides ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        };

}
