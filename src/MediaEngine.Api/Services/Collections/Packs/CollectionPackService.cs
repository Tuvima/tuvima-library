using System.Globalization;
using MediaEngine.Api.Models;
using MediaEngine.Contracts.Collections;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Api.Services.Collections.Packs;

/// <summary>
/// Lists, creates, exports and removes packs (Collections and lists spec D14). A pack becomes ordinary rule collections;
/// a pack of several sits under a set, a parent collection of type Set whose children the person edits like any other.
/// </summary>
public sealed class CollectionPackService(
    CollectionPackPlanner planner,
    ICollectionRepository collections,
    ICollectionPlacementRepository placements,
    ILogger<CollectionPackService> logger) : IPackFileImporter
{
    public string Kind => CollectionPackFile.FileId;

    public bool Recognises(string content) => CollectionPackFile.Recognises(content);

    // ── Listing and previewing ───────────────────────────────────────────────────────────────────

    /// <summary>Every built-in pack with what it would create from this library.</summary>
    public async Task<CollectionPackListResponse> ListAsync(IEnumerable<string>? mediaTypes, Guid? viewerProfileId, CancellationToken ct)
    {
        var plans = await planner.PlanStartersAsync(PackScope.From(mediaTypes), viewerProfileId, ct).ConfigureAwait(false);
        return new CollectionPackListResponse { Packs = plans.Select(ToSummary).ToList() };
    }

    public async Task<CollectionPackSummaryDto> PreviewAsync(string content, Guid? viewerProfileId, CancellationToken ct)
    {
        var plan = await planner.PlanBlueprintAsync(CollectionPackFile.Parse(content), viewerProfileId, ct).ConfigureAwait(false);
        return ToSummary(plan);
    }

    private static CollectionPackSummaryDto ToSummary(PackPlan plan) => new()
    {
        Id = plan.Id,
        Name = plan.Name,
        Description = plan.Description ?? string.Empty,
        CreatesSet = plan.CreatesSet,
        WouldCreateCount = plan.Collections.Count,
        UsesPerProfileFields = plan.UsesPerProfileFields,
        Collections = plan.Collections.Select(planned => new CollectionPackEntryDto
        {
            Name = planned.Entry.Name,
            Description = planned.Entry.Description,
            ItemCount = planned.ItemCount,
            MediaTypeCounts = planned.MediaTypeCounts.ToDictionary(pair => pair.Key, pair => pair.Value),
            SeasonText = SeasonText(planned.Entry),
        }).ToList(),
    };

    private static string? SeasonText(PackEntry entry) =>
        CollectionSeasonWindow.TryCreate(entry.SeasonFrom, entry.SeasonTo, out var window) && window is not null
            ? $"{DayText(window.From)} to {DayText(window.To)}"
            : null;

    private static string DayText(DayMonth day) =>
        new DateOnly(2000, day.Month, day.Day).ToString("d MMM", CultureInfo.InvariantCulture);

    // ── Creating ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Creates the built-in packs named in the request and the pack file in it, if any.</summary>
    public async Task<CollectionPackCreateResponse> CreateAsync(
        CollectionPackCreateRequest request,
        Profile? activeProfile,
        Guid? viewerProfileId,
        CancellationToken ct)
    {
        var packIds = request.PackIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var hasFile = !string.IsNullOrWhiteSpace(request.FileContent);
        if (packIds.Count == 0 && !hasFile)
        {
            throw new CollectionPackException(CollectionPackFailure.BadRequest, "Choose at least one pack.");
        }

        if (!CollectionLanePolicy.TryParseShowOn(request.ShowOn, out var showOnLocations))
        {
            throw new CollectionPackException(CollectionPackFailure.BadRequest, "Show on can be: home, watch, read, listen.");
        }

        var ownership = Ownership.From(request, activeProfile);
        var scope = PackScope.From(request.MediaTypes);
        var plans = new List<PackPlan>();
        foreach (var packId in packIds)
        {
            plans.Add(await planner.PlanStarterAsync(packId, scope, viewerProfileId, ct).ConfigureAwait(false)
                ?? throw new CollectionPackException(CollectionPackFailure.BadRequest, $"There is no pack called '{packId}'."));
        }

        if (hasFile)
        {
            plans.Add(await planner.PlanBlueprintAsync(CollectionPackFile.Parse(request.FileContent), viewerProfileId, ct).ConfigureAwait(false));
        }

        var response = new CollectionPackCreateResponse();
        foreach (var plan in plans)
        {
            foreach (var name in plan.LeftOut ?? [])
            {
                response.Skipped.Add(new CollectionPackSkippedDto { Name = name, Reason = "Your library doesn't have enough titles for it." });
            }

            if (plan.Collections.Count == 0)
            {
                response.Skipped.Add(new CollectionPackSkippedDto { Name = plan.Name, Reason = "Your library doesn't have enough for this pack yet." });
                continue;
            }

            await CreatePackAsync(plan, ownership, showOnLocations, response, ct).ConfigureAwait(false);
        }

        logger.LogInformation(
            "Packs created {Collections} collections in {Sets} sets, skipped {Skipped}.",
            response.Collections.Count, response.Sets.Count, response.Skipped.Count);
        return response;
    }

    public Task<CollectionPackCreateResponse> ImportAsync(
        string content,
        CollectionPackCreateRequest ownership,
        Profile? activeProfile,
        Guid? viewerProfileId,
        CancellationToken ct) =>
        CreateAsync(new CollectionPackCreateRequest
        {
            FileContent = content,
            OwnerKind = ownership.OwnerKind,
            Audience = ownership.Audience,
            SelectedProfileIds = ownership.SelectedProfileIds,
            ShowOn = ownership.ShowOn,
        }, activeProfile, viewerProfileId, ct);

    private async Task CreatePackAsync(
        PackPlan plan,
        Ownership ownership,
        IReadOnlyList<string> showOnLocations,
        CollectionPackCreateResponse response,
        CancellationToken ct)
    {
        // A collection with the same rules that already exists is not made twice.
        var toCreate = new List<(PlannedCollection Planned, string RuleHash)>();
        foreach (var planned in plan.Collections)
        {
            var hash = CollectionRuleEvaluator.ComputeRuleHash(planned.Entry.Rules);
            var duplicate = await collections.FindByRuleHashAsync(hash, ct).ConfigureAwait(false);
            if (duplicate is { IsEnabled: true })
            {
                response.Skipped.Add(new CollectionPackSkippedDto
                {
                    Name = planned.Entry.Name,
                    Reason = $"You already have '{duplicate.DisplayName ?? "a collection"}' with the same rules.",
                });
                continue;
            }

            toCreate.Add((planned, hash));
        }

        if (toCreate.Count == 0)
        {
            return;
        }

        Collection? set = null;
        if (plan.CreatesSet)
        {
            set = await FindReusableSetAsync(plan.Name, ownership, ct).ConfigureAwait(false);
            if (set is null)
            {
                set = NewSet(plan, ownership);
                await collections.CreateManagedCollectionAsync(set, [], ct).ConfigureAwait(false);
                await collections.ReplaceAudienceProfileIdsAsync(set.Id, ownership.SelectedProfileIds, ct).ConfigureAwait(false);
                response.Sets.Add(new CollectionPackCreatedDto { Id = set.Id, Name = set.DisplayName ?? plan.Name });
            }
        }

        foreach (var (planned, hash) in toCreate)
        {
            var collection = NewRuleCollection(planned.Entry, hash, set?.Id, ownership);
            await collections.CreateManagedCollectionAsync(collection, [], ct).ConfigureAwait(false);
            await collections.ReplaceAudienceProfileIdsAsync(collection.Id, ownership.SelectedProfileIds, ct).ConfigureAwait(false);
            if (showOnLocations.Count > 0)
            {
                await placements.SyncSharedLocationsAsync(
                    collection.Id, showOnLocations, CollectionLanePolicy.Locations, CollectionLanePolicy.LaneItemLimit, ct).ConfigureAwait(false);
            }

            response.Collections.Add(new CollectionPackCreatedDto { Id = collection.Id, Name = collection.DisplayName ?? planned.Entry.Name, SetId = set?.Id });
        }

        if (showOnLocations.Count > 0)
        {
            CollectionLanePolicy.MarkChanged();
        }
    }

    // Running a pack again adds only what is new to the set it made before, rather than a second set of the same name.
    private async Task<Collection?> FindReusableSetAsync(string name, Ownership ownership, CancellationToken ct)
    {
        var existing = await collections.FindByDisplayNameAsync(name, ct).ConfigureAwait(false);
        return existing is { CollectionType: CollectionType.Set, IsEnabled: true }
            && CollectionAccessPolicy.CanEdit(existing, ownership.ActiveProfile, hasCollectionsWrite: true)
                ? existing
                : null;
    }

    private static Collection NewSet(PackPlan plan, Ownership ownership)
    {
        var set = new Collection
        {
            Id = Guid.NewGuid(),
            DisplayName = plan.Name,
            Description = plan.Description,
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            MembershipMode = ContainerMembershipMode.Manual,
            PrimaryArea = CollectionPrimaryArea.Mixed,
            OwnerKind = ownership.OwnerKind,
            Audience = ownership.Audience,
        };
        set.RestoreDefinition(
            CollectionType.Set, CollectionScope.Library, CollectionResolution.Materialized,
            CollectionMatchMode.All, CollectionSortDirection.Desc, CollectionUniverseStatus.Unknown);
        CollectionAccessPolicy.ApplyOwnership(set, ownership.ActiveProfile?.Id);
        set.ReplaceAudienceProfiles(ownership.SelectedProfileIds);
        return set;
    }

    private static Collection NewRuleCollection(PackEntry entry, string ruleHash, Guid? setId, Ownership ownership)
    {
        var collection = new Collection
        {
            Id = Guid.NewGuid(),
            DisplayName = entry.Name,
            Description = entry.Description,
            IconName = entry.IconName,
            IsEnabled = true,
            RuleJson = CollectionPackFile.RuleJson(entry.Rules),
            RuleHash = ruleHash,
            SortField = entry.SortField,
            SecondarySortField = entry.SecondarySortField,
            SecondarySortDirection = string.IsNullOrWhiteSpace(entry.SecondarySortField) || string.IsNullOrWhiteSpace(entry.SecondarySortDirection)
                ? null
                : AggregateStateSerializer.ParseCollectionSortDirection(entry.SecondarySortDirection),
            CreatedAt = DateTimeOffset.UtcNow,
            MembershipMode = ContainerMembershipMode.Smart,
            PrimaryArea = AggregateStateSerializer.ParseCollectionPrimaryArea(entry.PrimaryArea),
            OwnerKind = ownership.OwnerKind,
            Audience = ownership.Audience,
            ParentCollectionId = setId,
        };
        collection.RestoreDefinition(
            CollectionType.Custom, CollectionScope.Library, CollectionResolution.Query, CollectionMatchMode.All,
            AggregateStateSerializer.ParseCollectionSortDirection(entry.SortDirection), CollectionUniverseStatus.Unknown);
        if (CollectionOptionsPolicy.TryApplyRequested(
                collection, entry.MinItems, entry.MaxItems, entry.SeasonFrom, entry.SeasonTo) is { } optionsError)
        {
            throw new CollectionPackException(CollectionPackFailure.BadRequest, optionsError);
        }

        CollectionAccessPolicy.ApplyOwnership(collection, ownership.ActiveProfile?.Id);
        collection.ReplaceAudienceProfiles(ownership.SelectedProfileIds);
        return collection;
    }

    /// <summary>Who the created collections belong to and who may see them, settled the way a single new collection settles it.</summary>
    private sealed record Ownership(
        Profile? ActiveProfile,
        ContainerOwnerKind OwnerKind,
        ContainerAudience Audience,
        IReadOnlyList<Guid> SelectedProfileIds)
    {
        public static Ownership From(CollectionPackCreateRequest request, Profile? activeProfile)
        {
            var ownerKind = AggregateStateSerializer.ParseContainerOwnerKind(request.OwnerKind);
            var audience = AggregateStateSerializer.ParseContainerAudience(request.Audience);
            if (activeProfile is null && ownerKind == ContainerOwnerKind.Profile)
            {
                throw new CollectionPackException(CollectionPackFailure.BadRequest, "An active profile is required to create a profile-owned collection.");
            }

            if (!CollectionAccessPolicy.CanManageCuratedCollections(hasCollectionsWrite: true)
                || !CollectionAccessPolicy.CanManageSharedCollections(hasCollectionsWrite: true))
            {
                throw new CollectionPackException(CollectionPackFailure.Forbidden, "You can't create collections.");
            }

            var selected = ownerKind == ContainerOwnerKind.Profile && audience == ContainerAudience.SelectedProfiles
                ? request.SelectedProfileIds
                    .Where(id => id != Guid.Empty && (activeProfile is null || id != activeProfile.Id))
                    .Distinct()
                    .ToList()
                : [];
            if (ownerKind == ContainerOwnerKind.Profile && audience == ContainerAudience.SelectedProfiles && selected.Count == 0)
            {
                throw new CollectionPackException(CollectionPackFailure.BadRequest, "Choose at least one other profile for the selected-profile audience.");
            }

            return new Ownership(activeProfile, ownerKind, audience, selected);
        }
    }

    // ── Exporting and removing a set ─────────────────────────────────────────────────────────────

    /// <summary>The set written out as a pack file, so it can be imported on another Library.</summary>
    public async Task<CollectionPackExportResponse> ExportSetAsync(Guid setId, Profile? activeProfile, CancellationToken ct)
    {
        var set = await LoadEditableSetAsync(setId, activeProfile, ct).ConfigureAwait(false);
        var children = (await collections.GetChildCollectionsAsync(set.Id, ct).ConfigureAwait(false)).Where(child => child.IsEnabled);
        var omitted = new List<string>();
        var file = CollectionPackFile.ToFile(set, children, omitted);
        if (file.Collections.Count == 0)
        {
            throw new CollectionPackException(CollectionPackFailure.BadRequest, "None of the collections in this set can be written to a pack file.");
        }

        return new CollectionPackExportResponse { File = file, Omitted = omitted };
    }

    /// <summary>Removes a set: its collections go with it, and so does everywhere they were shown as a lane.</summary>
    public async Task<CollectionSetRemovedResponse> RemoveSetAsync(Guid setId, Profile? activeProfile, CancellationToken ct)
    {
        var set = await LoadEditableSetAsync(setId, activeProfile, ct).ConfigureAwait(false);
        var children = (await collections.GetChildCollectionsAsync(set.Id, ct).ConfigureAwait(false)).Where(child => child.IsEnabled).ToList();
        foreach (var child in children)
        {
            await collections.UpdateCollectionEnabledAsync(child.Id, false, ct).ConfigureAwait(false);
            await placements.DeleteByCollectionIdAsync(child.Id, ct).ConfigureAwait(false);
        }

        await collections.UpdateCollectionEnabledAsync(set.Id, false, ct).ConfigureAwait(false);
        await placements.DeleteByCollectionIdAsync(set.Id, ct).ConfigureAwait(false);
        CollectionLanePolicy.MarkChanged();
        logger.LogInformation("Removed set {SetId} with {Children} collections.", set.Id, children.Count);
        return new CollectionSetRemovedResponse { RemovedCollections = children.Count };
    }

    private async Task<Collection> LoadEditableSetAsync(Guid setId, Profile? activeProfile, CancellationToken ct)
    {
        var set = await collections.GetByIdAsync(setId, ct).ConfigureAwait(false);
        if (set is null)
        {
            throw new CollectionPackException(CollectionPackFailure.NotFound, $"Set '{setId}' not found.");
        }

        if (set.CollectionType != CollectionType.Set || !set.IsEnabled)
        {
            throw new CollectionPackException(CollectionPackFailure.BadRequest, "This collection is not a set.");
        }

        if (!CollectionAccessPolicy.CanEdit(set, activeProfile, hasCollectionsWrite: true))
        {
            throw new CollectionPackException(CollectionPackFailure.Forbidden, "You can't change this set.");
        }

        return set;
    }
}
