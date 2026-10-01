using MediaEngine.Api.Http;
using MediaEngine.Api.Models;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.Paging;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Library endpoints for operational health, batch edits, and universe curation.
/// </summary>
public static class LibraryEndpoints
{
    public static IEndpointRouteBuilder MapLibraryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/library")
                       .WithTags("Library");

        // ── GET /library/overview ─────────────────────────────────────────────
        group.MapGet("/overview", async (
            ILibraryItemRepository libraryItemRepo,
            ILibraryOverviewReadService overviewReadService,
            CancellationToken ct) =>
        {
            // These aggregates are independent and each owns its SQLite connection.
            // Running them together removes avoidable serial latency from the dashboard.
            var fourStateTask = libraryItemRepo.GetFourStateCountsAsync(ct: ct);
            var projectionTask = libraryItemRepo.GetProjectionSummaryAsync(ct);
            var mediaTypeCountsTask = libraryItemRepo.GetOwnedMediaTypeCountsAsync(ct);
            var overviewTask = overviewReadService.GetOverviewAggregatesAsync(ct);
            await Task.WhenAll(fourStateTask, projectionTask, mediaTypeCountsTask, overviewTask);

            var fourState = await fourStateTask;
            var projection = await projectionTask;
            // Admin overview media type counts should reflect owned media assets,
            // not catalogue-only works discovered during enrichment.
            var mediaTypeCounts = await mediaTypeCountsTask;
            var ownedTotal = mediaTypeCounts.Values.Sum();

            // 4. Review-ready count from the shared libraryItem projection
            var reviewTotal = fourState.InReview;

            var overview = await overviewTask;

            var dto = new LibraryOverviewDto
            {
                TotalItems = ownedTotal,
                Added24h = overview.Added24h,
                Added7d = overview.Added7d,
                Added30d = overview.Added30d,
                PipelineStates = overview.PipelineStates.ToDictionary(kv => kv.Key, kv => kv.Value),
                PipelineSuccessRate = overview.PipelineSuccessRate,
                ReviewCategories = fourState.TriggerCounts.ToDictionary(kv => kv.Key, kv => kv.Value),
                ReviewTotal = reviewTotal,
                WithQid = projection.WithQid,
                WithoutQid = projection.WithoutQid,
                EnrichedStage3 = projection.EnrichedStage3,
                NotEnrichedStage3 = projection.NotEnrichedStage3,
                UniverseAssigned = projection.UniverseAssigned,
                UniverseUnassigned = projection.UniverseUnassigned,
                StaleItems = projection.StaleItems,
                MediaTypeCounts = mediaTypeCounts,
                HiddenByQualityGate = projection.HiddenByQualityGate,
                ArtPending = projection.ArtPending,
                RetailNeedsReview = projection.RetailNeedsReview,
                QidNoMatch = projection.QidNoMatch,
                CompletedWithArt = projection.CompletedWithArt,
            };

            return Results.Ok(dto);
        })
        .WithName("GetLibraryOverview")
        .WithSummary("Aggregated operational health summary for the library dashboard.")
        .Produces<LibraryOverviewDto>(StatusCodes.Status200OK)
        .RequireEffectiveAdministrator();

        group.MapGet("/works", async (
            ILibraryWorkFeedReadService workFeedReadService,
            IDisplayProjectionReadService display,
            ILoggerFactory loggerFactory,
            int? offset,
            int? limit,
            CancellationToken ct) =>
        {
            ct.ThrowIfCancellationRequested();

            var page = PagedRequest.From(offset, limit, defaultLimit: 100, maxLimit: 500);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var logger = loggerFactory.CreateLogger("MediaEngine.Api.LibraryWorks");
            var allowedWorkIds = (await display.LoadWorksAsync(ct))
                .Select(work => work.WorkId)
                .ToHashSet();
            var response = await workFeedReadService.GetWorksAsync(page, ct, allowedWorkIds);
            sw.Stop();
            if (sw.ElapsedMilliseconds >= 1000)
            {
                logger.LogWarning(
                    "Large-list read {Operation} took {ElapsedMs} ms with offset {Offset}, limit {Limit}, returned {ItemCount}, has_more {HasMore}",
                    "library.works",
                    sw.ElapsedMilliseconds,
                    response.Offset,
                    response.Limit,
                    response.Items.Count,
                    response.HasMore);
            }
            else
            {
                logger.LogDebug(
                    "Large-list read {Operation} took {ElapsedMs} ms with offset {Offset}, limit {Limit}, returned {ItemCount}, has_more {HasMore}",
                    "library.works",
                    sw.ElapsedMilliseconds,
                    response.Offset,
                    response.Limit,
                    response.Items.Count,
                    response.HasMore);
            }

            return Results.Ok(response);
        })
        .WithName("GetLibraryWorks")
        .WithSummary("Returns library-owned works for the home and browse surfaces.")
        .Produces<PagedResponse<LibraryWorkListItemDto>>(StatusCodes.Status200OK)
        .RequireClientScope(ApplicationPermissionIds.LibraryRead.Value);

        // ── POST /library/batch-edit/preview ────────────────────────────────
        group.MapPost("/batch-edit/preview", async (
            LibraryBatchEditRequest request,
            ICanonicalValueRepository canonicalRepo,
            IItemCanonicalRepository itemCanonicalRepo,
            ILibraryCurationReadService curationReadService,
            CancellationToken ct) =>
        {
            if (request.EntityIds.Count == 0 || request.FieldChanges.Count == 0)
            {
                return ApiErrors.BadRequest("Must provide entity IDs and field changes.");
            }

            var targetMap = await curationReadService.ResolveBatchEditTargetsAsync(
                request.EntityIds,
                request.FieldChanges.Select(change => change.Key).ToArray(),
                ct);
            var targetIds = targetMap.Values
                .SelectMany(changeTargets => changeTargets.Values)
                .Distinct()
                .ToList();
            var allCanonicals = await canonicalRepo.GetByEntitiesAsync(targetIds, ct);

            var changes = new List<LibraryFieldChangePreview>();
            foreach (var change in request.FieldChanges)
            {
                var displayTagChange = string.Equals(change.Key.Trim(), "custom_tags", StringComparison.OrdinalIgnoreCase);
                var oldValueCounts = new Dictionary<string, int>();
                foreach (var entityId in request.EntityIds)
                {
                    if (displayTagChange)
                    {
                        var workId = await itemCanonicalRepo.ResolveWorkIdForAssetAsync(entityId, ct) ?? entityId;
                        var overrides = await itemCanonicalRepo.LoadDisplayOverridesAsync(workId, ct);
                        var oldOverride = overrides.Values.TryGetValue("custom_tags", out var tags) && !string.IsNullOrWhiteSpace(tags)
                            ? tags
                            : "(empty)";
                        oldValueCounts[oldOverride] = oldValueCounts.GetValueOrDefault(oldOverride, 0) + 1;
                        continue;
                    }

                    var targetId = targetMap.TryGetValue(entityId, out var targets)
                        && targets.TryGetValue(change.Key, out var resolvedTargetId)
                        ? resolvedTargetId
                        : entityId;
                    var entityCanonicals = allCanonicals.TryGetValue(targetId, out var vals)
                        ? vals
                        : [];
                    var current = entityCanonicals.FirstOrDefault(c => c.Key == change.Key);
                    var oldVal = current?.Value ?? "(empty)";
                    oldValueCounts[oldVal] = oldValueCounts.GetValueOrDefault(oldVal, 0) + 1;
                }
                changes.Add(new LibraryFieldChangePreview
                {
                    Key = change.Key,
                    NewValue = change.Value,
                    OldValueCounts = oldValueCounts,
                });
            }

            return Results.Ok(new LibraryBatchEditPreview
            {
                AffectedCount = request.EntityIds.Count,
                Changes = changes,
            });
        })
        .WithName("PreviewBatchEdit")
        .WithSummary("Dry-run preview of a batch edit operation.")
        .Produces<LibraryBatchEditPreview>(StatusCodes.Status200OK)
        .RequireEffectiveAdministrator();

        // ── POST /library/batch-edit ────────────────────────────────────────
        group.MapPost("/batch-edit", async (
            LibraryBatchEditRequest request,
            ICanonicalValueRepository canonicalRepo,
            IMetadataClaimRepository claimRepo,
            IItemCanonicalRepository itemCanonicalRepo,
            ILibraryCurationReadService curationReadService,
            CancellationToken ct) =>
        {
            if (request.EntityIds.Count == 0 || request.FieldChanges.Count == 0)
            {
                return ApiErrors.BadRequest("Must provide entity IDs and field changes.");
            }

            var customTagChanges = request.FieldChanges
                .Where(change => string.Equals(change.Key.Trim(), "custom_tags", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (customTagChanges.Count > 1)
            {
                return ApiErrors.BadRequest("Only one custom_tags change may be provided.");
            }

            var customTags = (string?)null;
            if (customTagChanges.Count == 1)
            {
                if (!DetailDisplayOverrideCatalog.TryValidateValue("custom_tags", customTagChanges[0].Value, out var normalizedTags, out var tagError))
                {
                    return ApiErrors.BadRequest(tagError ?? "Invalid custom tags value.");
                }

                customTags = normalizedTags;
            }

            var canonicalChanges = request.FieldChanges
                .Where(change => !string.Equals(change.Key.Trim(), "custom_tags", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var updatedCount = 0;
            var failedIds = new List<Guid>();
            var errors = new List<string>();
            var targetMap = await curationReadService.ResolveBatchEditTargetsAsync(
                request.EntityIds,
                request.FieldChanges.Select(change => change.Key).ToArray(),
                ct);
            var claimsByTargetAndKey = new Dictionary<(Guid TargetId, string Key), MetadataClaim>();
            var canonicalsByTargetAndKey = new Dictionary<(Guid TargetId, string Key), CanonicalValue>();
            var now = DateTimeOffset.UtcNow;

            foreach (var entityId in request.EntityIds)
            {
                try
                {
                    if (!targetMap.TryGetValue(entityId, out var targets))
                    {
                        failedIds.Add(entityId);
                        errors.Add($"{entityId}: no owned media asset was found for batch edit.");
                        continue;
                    }

                    if (customTagChanges.Count == 1)
                    {
                        var workId = await itemCanonicalRepo.ResolveWorkIdForAssetAsync(entityId, ct) ?? entityId;
                        var overrideState = await itemCanonicalRepo.LoadDisplayOverridesAsync(workId, ct);
                        if (!overrideState.WorkExists)
                        {
                            failedIds.Add(entityId);
                            errors.Add($"{entityId}: no work was found for shared library tags.");
                            continue;
                        }

                        DetailDisplayOverrideCatalog.ApplyChanges(
                            overrideState.Values,
                            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["custom_tags"] = customTags ?? string.Empty,
                            });
                        if (!await itemCanonicalRepo.SaveDisplayOverridesAsync(workId, overrideState.Values, ct))
                        {
                            failedIds.Add(entityId);
                            errors.Add($"{entityId}: shared library tags could not be saved.");
                            continue;
                        }
                    }

                    foreach (var change in canonicalChanges)
                    {
                        if (!targets.TryGetValue(change.Key, out var targetId))
                        {
                            targetId = entityId;
                        }

                        var targetKey = (targetId, change.Key);

                        // Create one user-locked claim per resolved target/key. If ten selected
                        // tracks share the same album parent, album fields are written once.
                        claimsByTargetAndKey[targetKey] = new MetadataClaim
                        {
                            Id = Guid.NewGuid(),
                            EntityId = targetId,
                            ClaimKey = change.Key,
                            ClaimValue = change.Value,
                            ProviderId = WellKnownProviders.UserManual,
                            Confidence = 1.0,
                            IsUserLocked = true,
                            ClaimedAt = now,
                        };

                        canonicalsByTargetAndKey[targetKey] = new CanonicalValue
                        {
                            EntityId = targetId,
                            Key = change.Key,
                            Value = change.Value,
                            LastScoredAt = now,
                            IsConflicted = false,
                            WinningProviderId = WellKnownProviders.UserManual,
                            NeedsReview = false,
                        };
                    }

                    updatedCount++;
                }
                catch (Exception ex)
                {
                    failedIds.Add(entityId);
                    errors.Add($"{entityId}: {ex.Message}");
                }
            }

            if (claimsByTargetAndKey.Count > 0)
            {
                await claimRepo.InsertBatchAsync(claimsByTargetAndKey.Values.ToList(), ct);
            }

            if (canonicalsByTargetAndKey.Count > 0)
            {
                await canonicalRepo.UpsertBatchAsync(canonicalsByTargetAndKey.Values.ToList(), ct);
            }

            return Results.Ok(new LibraryBatchEditResult
            {
                UpdatedCount = updatedCount,
                FailedIds = failedIds,
                Errors = errors,
            });
        })
        .WithName("ApplyBatchEdit")
        .WithSummary("Apply batch field edits to multiple items.")
        .Produces<LibraryBatchEditResult>(StatusCodes.Status200OK)
        .RequireEffectiveAdministrator();

        // ── GET /library/universe-candidates ───────────────────────────────
        group.MapGet("/universe-candidates", async (
            ILibraryCurationReadService curationReadService,
            CancellationToken ct) =>
        {
            var candidates = await curationReadService.GetUniverseCandidatesAsync(ct);
            return Results.Ok(candidates.Select(candidate => new UniverseCandidateDto
            {
                WorkId = candidate.WorkId,
                EntityId = candidate.EntityId,
                Title = candidate.Title,
                MediaType = candidate.MediaType,
                CandidateQid = candidate.CandidateQid,
                CandidateType = candidate.CandidateType,
                CandidateLabel = candidate.CandidateLabel,
            }).ToList());
        })
        .WithName("GetUniverseCandidates")
        .WithSummary("Items with universe-related QIDs but no collection assignment.")
        .Produces<List<UniverseCandidateDto>>(StatusCodes.Status200OK)
        .RequireEffectiveAdministrator();

        // ── POST /library/universe-candidates/{workId}/accept ──────────────
        group.MapPost("/universe-candidates/{workId:guid}/accept", async (
            Guid workId,
            UniverseAcceptRequest request,
            ICollectionRepository collectionRepo,
            CancellationToken ct) =>
        {
            // Find or create the collection for the target QID
            var collection = await collectionRepo.FindByQidAsync(request.TargetCollectionQid, ct);
            if (collection is null)
            {
                // Create a new ContentGroup collection for this QID
                collection = new Collection
                {
                    Id = Guid.NewGuid(),
                    WikidataQid = request.TargetCollectionQid,
                    DisplayName = request.TargetCollectionQid, // Will be enriched later
                    IsEnabled = true,
                };
                collection.RestoreDefinition(
                    CollectionType.ContentGroup,
                    CollectionScope.Library,
                    CollectionResolution.Materialized,
                    CollectionMatchMode.All,
                    CollectionSortDirection.Desc,
                    CollectionUniverseStatus.Unknown);
                await collectionRepo.UpsertAsync(collection, ct);
            }

            await collectionRepo.AssignWorkToCollectionAsync(workId, collection.Id, ct);
            return Results.Ok(new UniverseCandidateAcceptResponse(assigned: true, collection_id: collection.Id));
        })
        .WithName("AcceptUniverseCandidate")
        .WithSummary("Accept a universe assignment for a work.")
        .Produces<UniverseCandidateAcceptResponse>(StatusCodes.Status200OK)
        .RequireEffectiveAdministrator();

        // ── POST /library/universe-candidates/{workId}/reject ──────────────
        group.MapPost("/universe-candidates/{workId:guid}/reject", async (
            Guid workId,
            ICanonicalValueRepository canonicalRepo,
            ILibraryCurationReadService curationReadService,
            CancellationToken ct) =>
        {
            var entityId = await curationReadService.FindOwnedAssetIdForWorkAsync(workId, ct);

            if (entityId is null)
            {
                return ApiErrors.NotFound($"No owned media asset found for work '{workId}'.");
            }

            // Mark as reviewed/rejected so it doesn't reappear in the pending queue
            await canonicalRepo.UpsertBatchAsync([new CanonicalValue
            {
                EntityId = entityId.Value,
                Key = "universe_review_status",
                Value = "rejected",
                LastScoredAt = DateTimeOffset.UtcNow,
                IsConflicted = false,
                WinningProviderId = WellKnownProviders.UserManual,
                NeedsReview = false,
            }], ct);

            return Results.Ok(new UniverseCandidateRejectResponse(rejected: true));
        })
        .WithName("RejectUniverseCandidate")
        .WithSummary("Reject a universe candidate for a work.")
        .Produces<UniverseCandidateRejectResponse>(StatusCodes.Status200OK)
        .RequireEffectiveAdministrator();

        // ── POST /library/universe-candidates/batch-accept ─────────────────
        group.MapPost("/universe-candidates/batch-accept", async (
            UniverseBatchAcceptRequest request,
            ICollectionRepository collectionRepo,
            ILibraryCurationReadService curationReadService,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var accepted = 0;
            var failedIds = new List<Guid>();
            var missingCandidateIds = new List<Guid>();
            var errors = new List<string>();
            var logger = loggerFactory.CreateLogger("MediaEngine.Api.LibraryUniverseCuration");
            var candidateQids = await curationReadService.GetBestUniverseCandidateQidsAsync(
                request.WorkIds,
                ct);

            foreach (var workId in request.WorkIds.Distinct())
            {
                try
                {
                    if (!candidateQids.TryGetValue(workId, out var candidateQid))
                    {
                        missingCandidateIds.Add(workId);
                        continue;
                    }

                    var collection = await collectionRepo.FindByQidAsync(candidateQid, ct);
                    if (collection is null)
                    {
                        collection = new Collection
                        {
                            Id = Guid.NewGuid(),
                            WikidataQid = candidateQid,
                            DisplayName = candidateQid,
                            IsEnabled = true,
                        };
                        collection.RestoreDefinition(
                            CollectionType.ContentGroup,
                            CollectionScope.Library,
                            CollectionResolution.Materialized,
                            CollectionMatchMode.All,
                            CollectionSortDirection.Desc,
                            CollectionUniverseStatus.Unknown);
                        await collectionRepo.UpsertAsync(collection, ct);
                    }

                    await collectionRepo.AssignWorkToCollectionAsync(workId, collection.Id, ct);
                    accepted++;
                }
                catch (Exception ex)
                {
                    failedIds.Add(workId);
                    errors.Add($"{workId}: {ex.Message}");
                    logger.LogError(ex, "Failed to accept universe candidate for work {WorkId}.", workId);
                }
            }

            return Results.Ok(new UniverseBatchAcceptResult
            {
                AcceptedCount = accepted,
                MissingCandidateIds = missingCandidateIds,
                FailedIds = failedIds,
                Errors = errors,
            });
        })
        .WithName("BatchAcceptUniverseCandidates")
        .WithSummary("Batch accept universe assignments.")
        .Produces<UniverseBatchAcceptResult>(StatusCodes.Status200OK)
        .RequireEffectiveAdministrator();

        // ── GET /library/universe-unlinked ─────────────────────────────────
        group.MapGet("/universe-unlinked", async (
            ILibraryCurationReadService curationReadService,
            CancellationToken ct) =>
        {
            return Results.Ok(await curationReadService.GetUniverseUnlinkedAsync(ct));
        })
        .WithName("GetUniverseUnlinked")
        .WithSummary("Works with Wikidata QID but no universe-related properties.")
        .Produces<List<UnlinkedWorkDto>>(StatusCodes.Status200OK)
        .RequireEffectiveAdministrator();

        // ── POST /library/universe-assign ──────────────────────────────────
        group.MapPost("/universe-assign", async (
            UniverseManualAssignRequest request,
            ICollectionRepository collectionRepo,
            CancellationToken ct) =>
        {
            await collectionRepo.AssignWorkToCollectionAsync(request.WorkId, request.CollectionId, ct);
            return Results.Ok(new UniverseManualAssignResponse(assigned: true));
        })
        .WithName("ManualUniverseAssign")
        .WithSummary("Manually assign a work to an existing collection.")
        .Produces<UniverseManualAssignResponse>(StatusCodes.Status200OK)
        .RequireEffectiveAdministrator();

        return app;
    }
}


