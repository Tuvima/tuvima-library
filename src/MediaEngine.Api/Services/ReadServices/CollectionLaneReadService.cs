using MediaEngine.Api.Models;
using MediaEngine.Api.Services.Collections;
using MediaEngine.Contracts.Collections;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>One collection shown as a lane: its name and the items it holds for the profile looking, up to the lane limit.</summary>
public sealed record CollectionLane(
    Guid CollectionId,
    string Name,
    string? Description,
    bool IsShared,
    IReadOnlyList<CollectionItemDto> Items);

/// <summary>One row of the Lanes settings page. No items are read for it.</summary>
public sealed record CollectionLaneListing(
    Guid CollectionId,
    string Name,
    string CollectionType,
    string? IconName,
    bool Visible);

/// <summary>What the Lanes settings page shows one profile for one location.</summary>
public sealed record CollectionLaneListings(
    IReadOnlyList<CollectionLaneListing> Shared,
    IReadOnlyList<CollectionLaneListing> Mine,
    IReadOnlyList<CollectionLaneListing> HiddenByMe);

/// <summary>
/// Reads the collections shown as lanes on Home and the Watch, Read and Listen pages (Collections and lists spec D9 to
/// D11). A lane is a collection with a shared "Show on" placement at the location. It reaches a profile only when the
/// collection is enabled, the profile may open it, it is inside its seasonal window, the profile did not hide it and it
/// holds at least its minimum (3 by default) of items the profile may see. Shared lanes come first in the
/// administrator's order; the collections that belong to profiles (private or shared with selected profiles) follow in
/// the order their owner set.
/// </summary>
public sealed class CollectionLaneReadService(
    ICollectionPlacementRepository placements,
    ICollectionRepository collectionRepo,
    CollectionCatalogReadService catalog,
    ILogger<CollectionLaneReadService> logger,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    private DateOnly Today => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

    public async Task<IReadOnlyList<CollectionLane>> GetLanesAsync(
        Profile? profile,
        string location,
        IReadOnlySet<Guid>? allowedWorkIds,
        CancellationToken ct = default)
    {
        var shown = await placements.GetByLocationAsync(location, ct).ConfigureAwait(false);
        if (shown.Count == 0)
        {
            return [];
        }

        var hidden = profile is null
            ? new HashSet<Guid>()
            : (await placements.GetHiddenCollectionIdsAsync(profile.Id, location, ct).ConfigureAwait(false)).ToHashSet();
        var byId = (await collectionRepo.GetByIdsAsync(shown.Select(placement => placement.CollectionId), ct).ConfigureAwait(false))
            .ToDictionary(collection => collection.Id);

        var today = Today;
        var ordered = new List<Collection>();
        foreach (var placement in shown)
        {
            if (!byId.TryGetValue(placement.CollectionId, out var collection)
                || hidden.Contains(collection.Id)
                || !collection.IsEnabled
                || !CollectionAccessPolicy.CanAccess(collection, profile)
                || !CollectionOptionsPolicy.IsInSeason(collection, today)
                || !HasSupportedRules(collection))
            {
                continue;
            }

            ordered.Add(collection);
        }

        // Shared lanes keep the administrator's order; a profile's own collections follow, in the order their owner set.
        // OrderBy is stable, so the position order of the placements survives inside each group.
        ordered = ordered.OrderBy(collection => CollectionLanePolicy.IsShared(collection) ? 0 : 1).ToList();

        var lanes = new List<CollectionLane>(ordered.Count);
        foreach (var collection in ordered)
        {
            var take = Math.Max(CollectionLanePolicy.LaneItemLimit, CollectionOptionsPolicy.EffectiveMinimum(collection, CollectionSurface.Lane));
            var read = await catalog.GetItemsAsync(collection.Id, profile, take, ct, allowedWorkIds).ConfigureAwait(false);
            if (!read.Found || read.Forbidden
                || !CollectionOptionsPolicy.MeetsMinimum(collection, read.Items.Count, CollectionSurface.Lane))
            {
                continue;
            }

            lanes.Add(new CollectionLane(
                collection.Id,
                collection.DisplayName ?? $"Collection {collection.Id.ToString("N")[..8]}",
                collection.Description,
                CollectionLanePolicy.IsShared(collection),
                read.Items.Take(CollectionLanePolicy.LaneItemLimit).ToList()));
        }

        return lanes;
    }

    /// <summary>
    /// The lanes the Lanes settings page lists. <paramref name="includeHiddenShared"/> is true for an administrator, who
    /// also sees the shared lanes switched off for everyone so they can be switched back on.
    /// </summary>
    public async Task<CollectionLaneListings> GetListingsAsync(
        Profile? profile,
        string location,
        bool includeHiddenShared,
        CancellationToken ct = default)
    {
        var all = await placements.GetSharedByLocationAsync(location, ct).ConfigureAwait(false);
        var hidden = profile is null
            ? new HashSet<Guid>()
            : (await placements.GetHiddenCollectionIdsAsync(profile.Id, location, ct).ConfigureAwait(false)).ToHashSet();
        var byId = (await collectionRepo.GetByIdsAsync(all.Select(placement => placement.CollectionId), ct).ConfigureAwait(false))
            .ToDictionary(collection => collection.Id);

        var shared = new List<CollectionLaneListing>();
        var mine = new List<CollectionLaneListing>();
        var hiddenByMe = new List<CollectionLaneListing>();
        foreach (var placement in all)
        {
            if (!byId.TryGetValue(placement.CollectionId, out var collection)
                || !collection.IsEnabled
                || !CollectionAccessPolicy.CanAccess(collection, profile))
            {
                continue;
            }

            var listing = new CollectionLaneListing(
                collection.Id,
                collection.DisplayName ?? $"Collection {collection.Id.ToString("N")[..8]}",
                collection.CollectionType.ToStorageValue(),
                collection.IconName,
                placement.IsVisible);

            if (!CollectionLanePolicy.IsShared(collection))
            {
                if (profile is not null && collection.ProfileId == profile.Id)
                {
                    mine.Add(listing);
                }
            }
            else if (includeHiddenShared || placement.IsVisible)
            {
                shared.Add(listing);
            }

            if (hidden.Contains(collection.Id) && placement.IsVisible)
            {
                hiddenByMe.Add(listing);
            }
        }

        return new CollectionLaneListings(shared, mine, hiddenByMe);
    }

    private bool HasSupportedRules(Collection collection)
    {
        if (string.IsNullOrWhiteSpace(collection.RuleJson))
        {
            return true;
        }

        try
        {
            _ = CollectionRuleEvaluator.ParseDefinition(collection.RuleJson);
            return true;
        }
        catch (FormatException ex)
        {
            logger.LogWarning(
                ex,
                "Skipping lane {CollectionId} ({CollectionName}) because its rule definition is unsupported.",
                collection.Id,
                collection.DisplayName);
            return false;
        }
    }
}
