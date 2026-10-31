using System.Globalization;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Models;
using MediaEngine.Api.Services.Collections;
using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Collections;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.ReadServices;

public sealed record CollectionItemReadResult(bool Found, bool Forbidden, List<CollectionItemDto> Items);

/// <summary>One page of a collection's items for a paired app, with the number of items the profile may see in all.</summary>
public sealed record CollectionItemPageResult(bool Found, bool Forbidden, int TotalCount, List<CollectionItemDto> Items);

public sealed class CollectionCatalogReadService(
    ICollectionRepository collectionRepo,
    ISeriesManifestRepository manifestRepo,
    IPersonRepository personRepo,
    IArtworkPaletteService artworkPaletteService,
    ICollectionMediaLookupReadService mediaLookupReadService,
    IDatabaseConnection db,
    ILogger<CollectionCatalogReadService> logger,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>Today's date on this machine: the day the seasonal windows and the daily random sample are read against.</summary>
    private DateOnly Today => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

    public async Task<List<ManagedCollectionDto>> GetManagedAsync(
        Profile? activeProfile,
        CancellationToken ct = default,
        IReadOnlySet<Guid>? allowedWorkIds = null)
    {
        var collections = (await collectionRepo.GetManagedCollectionsAsync(ct).ConfigureAwait(false))
            .Where(collection => collection.IsEnabled
                && CollectionAccessPolicy.CanAccess(collection, activeProfile))
            .ToList();
        var materializedCounts = await collectionRepo.GetCollectionItemCountsAsync(
            collections
                .Where(collection => collection.Resolution != CollectionResolution.Query)
                .Select(collection => collection.Id),
            ct).ConfigureAwait(false);

        var results = new List<ManagedCollectionDto>(collections.Count);
        foreach (var collection in collections)
        {
            if (!HasSupportedRuleDefinition(collection))
            {
                logger.LogWarning(
                    "Skipping collection {CollectionId} ({CollectionName}) because its rule definition is unsupported.",
                    collection.Id,
                    collection.DisplayName);
                continue;
            }

            var collectionWorkIds = allowedWorkIds is null
                ? []
                : (await GetCollectionWorkIdsAsync(collection, ct).ConfigureAwait(false))
                    .Where(allowedWorkIds.Contains)
                    .Distinct()
                    .ToList();
            var count = allowedWorkIds is not null
                ? collectionWorkIds.Count
                : collection.Resolution == CollectionResolution.Query
                    ? (await GetCollectionWorkIdsAsync(collection, ct).ConfigureAwait(false)).Count
                    : GetManagedCollectionItemCount(collection, materializedCounts, []);
            results.Add(ManagedCollectionMapper.FromDomain(
                collection,
                CollectionOptionsPolicy.CapCount(collection, count),
                activeProfile,
                today: Today));
        }

        return results;
    }

    public Task<IReadOnlyList<Guid>> GetDisplayWorkIdsAsync(
        IEnumerable<Guid> sourceWorkIds,
        CancellationToken ct = default) =>
        GetCollectionCatalogDisplayWorkIdsAsync(sourceWorkIds, ct);

    public async Task<Guid> ResolveMembershipWorkIdAsync(Guid sourceWorkId, CancellationToken ct = default)
    {
        var displayIds = await GetCollectionCatalogDisplayWorkIdsAsync([sourceWorkId], ct).ConfigureAwait(false);
        return displayIds.Count == 0 ? sourceWorkId : displayIds[0];
    }

    public async Task<List<CollectionManagementCatalogDto>> GetCatalogAsync(
        Profile? activeProfile,
        CancellationToken ct = default,
        IReadOnlySet<Guid>? allowedWorkIds = null)
    {
        var collections = await GetAccessibleCollectionsAsync(activeProfile, ct).ConfigureAwait(false);
        var allowedMemberIds = await ExpandAllowedMembersAsync(allowedWorkIds, ct).ConfigureAwait(false);
        var candidates = new List<CollectionManagementCatalogCandidate>();
        var today = Today;
        foreach (var collection in collections)
        {
            // Outside its seasonal window a collection is left out of the Collections page and the lanes; the
            // editor's list (GetManagedAsync) still has it, with a "Hidden until" note.
            if (!CollectionOptionsPolicy.IsInSeason(collection, today))
            {
                continue;
            }

            if (!HasSupportedRuleDefinition(collection))
            {
                logger.LogWarning(
                    "Skipping collection {CollectionId} ({CollectionName}) because its rule definition is unsupported.",
                    collection.Id,
                    collection.DisplayName);
                continue;
            }

            var classification = ClassifyCollectionForCatalog(collection);
            var sourceWorkIds = await GetCollectionCatalogSourceWorkIdsAsync(collection, collections, ct).ConfigureAwait(false);
            // Only members the profile may see count; TV, comic and music members are then collapsed to their display
            // root, so an owned episode keeps its show tile, but a restricted episode never lends its show to the count.
            if (allowedMemberIds is not null)
            {
                sourceWorkIds = sourceWorkIds.Where(allowedMemberIds.Contains).ToList();
            }

            var workIds = await GetOwnedCollectionCatalogDisplayWorkIdsAsync(sourceWorkIds, ct).ConfigureAwait(false);
            if (allowedMemberIds is not null)
            {
                // A series parent expands to every book or film below it; only the ones the profile may see stay.
                workIds = workIds.Where(allowedMemberIds.Contains).ToList();
            }

            // "At most N" and random order decide which members the collection shows today, so the tile's count
            // and artwork agree with the collection's own page; a minimum the person chose then hides a short one.
            workIds = CollectionOptionsPolicy.Arrange(collection, workIds, id => id, today).ToList();
            if (!CollectionOptionsPolicy.MeetsMinimum(collection, workIds.Count, CollectionSurface.CollectionsPage))
            {
                continue;
            }

            var itemCount = workIds.Count;
            var mediaCounts = await GetCollectionMediaCountsAsync(workIds, ct).ConfigureAwait(false);
            var hasKnownSeriesManifest = await HasKnownSeriesManifestAsync(collection, ct).ConfigureAwait(false);
            if (!ShouldIncludeInManagementCatalog(
                    collection,
                    classification,
                    mediaCounts,
                    hasKnownSeriesManifest,
                    collections))
            {
                continue;
            }

            candidates.Add(new CollectionManagementCatalogCandidate(
                collection,
                classification,
                GetCollectionCatalogAggregation(collection, collections),
                workIds,
                itemCount,
                mediaCounts));
        }

        var dtos = new List<CollectionManagementCatalogDto>();
        foreach (var group in candidates.GroupBy(candidate => candidate.Grouping?.Key ?? candidate.Collection.Id.ToString("D"), StringComparer.OrdinalIgnoreCase))
        {
            var entries = group.ToList();
            if (!ShouldIncludeCatalogGroup(entries))
            {
                continue;
            }

            var representative = SelectCatalogRepresentative(entries);
            var workIds = entries
                .SelectMany(entry => entry.WorkIds)
                .Distinct()
                .ToList();
            var mediaCounts = await GetCollectionMediaCountsAsync(workIds, ct).ConfigureAwait(false);
            if (IsGeneratedTvShowContainer(representative.Collection, mediaCounts))
            {
                continue;
            }

            var artworkItems = await GetCollectionArtworkItemsAsync(workIds, 12, allowedMemberIds, ct).ConfigureAwait(false);
            var person = await ResolveCatalogPersonAsync(
                entries.Select(entry => entry.Collection),
                ct).ConfigureAwait(false);
            var artworkPalette = await artworkPaletteService.GeneratePaletteAsync(
                artworkItems
                    .Select(item => new ArtworkPaletteSource
                    {
                        Id = item.WorkId.ToString("D"),
                        ImageUrl = item.CoverUrl ?? string.Empty,
                        LocalPath = item.LocalImagePath,
                        MediaType = TryParseMediaType(item.MediaType),
                        Shape = TryParseArtworkShape(item.ArtworkShape),
                    })
                    .ToList(),
                new ArtworkPaletteOptions
                {
                    StableSeed = representative.Collection.Id.ToString("D"),
                    MaxImagesToAnalyze = 5,
                },
                ct).ConfigureAwait(false);

            dtos.Add(ManagedCollectionMapper.ToCatalog(
                representative.Collection,
                workIds.Count,
                activeProfile,
                representative.Classification,
                mediaCounts,
                artworkItems,
                artworkPalette,
                entries.Count > 1 ? representative.Grouping?.Label : null,
                person,
                groupType: ClassifyGroupType(
                    representative.Collection,
                    representative.Classification,
                    mediaCounts,
                    entries.Count > 1 || IsBroadRollupKey(representative.Grouping?.Key)),
                universeQid: ResolveUniverseQid(representative.Collection)));
        }

        return dtos
            .OrderBy(dto => dto.Family == "System" ? 0 : dto.Family == "Global" ? 1 : dto.Family == "User" ? 2 : 3)
            .ThenByDescending(dto => dto.IsFeatured)
            .ThenBy(dto => dto.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<CollectionManagementCatalogDto?> GetSummaryAsync(
        Guid collectionId,
        Profile? activeProfile,
        CancellationToken ct = default,
        IReadOnlySet<Guid>? allowedWorkIds = null)
    {
        var catalog = await GetCatalogAsync(activeProfile, ct, allowedWorkIds).ConfigureAwait(false);
        return catalog.FirstOrDefault(collection => collection.Id == collectionId);
    }

    public async Task<CollectionItemReadResult> GetItemsAsync(
        Guid collectionId,
        Profile? activeProfile,
        int limit,
        CancellationToken ct = default,
        IReadOnlySet<Guid>? allowedWorkIds = null)
    {
        var collection = await collectionRepo.GetByIdAsync(collectionId, ct).ConfigureAwait(false);
        if (collection is null)
        {
            return new CollectionItemReadResult(false, false, []);
        }

        if (!CollectionAccessPolicy.CanAccess(collection, activeProfile))
        {
            return new CollectionItemReadResult(true, true, []);
        }

        var take = Math.Max(1, limit);
        var allowedMemberIds = await ExpandAllowedMembersAsync(allowedWorkIds, ct).ConfigureAwait(false);
        var dtos = await ResolveRootItemsAsync(collection, activeProfile, allowedMemberIds, take, ct).ConfigureAwait(false);
        dtos = dtos.Take(take).ToList();

        return new CollectionItemReadResult(true, false, dtos);
    }

    /// <summary>The most items of one collection a paged read looks at.</summary>
    public const int MaxPagedItems = 5000;

    /// <summary>
    /// One page of a collection's items for a paired app. The collection must be one the catalogue would list for the
    /// profile (enabled, openable by the profile, a supported rule, not a playlist); anything else is reported as
    /// forbidden so the caller answers it like an unknown collection. The ordered ids are resolved once and only the
    /// page's items are then looked up, except for generated series, whose order comes from the lookup itself and are
    /// resolved whole. Returns up to <paramref name="limit"/> + 1 items so the caller can tell whether more follow.
    /// </summary>
    public async Task<CollectionItemPageResult> GetItemPageAsync(
        Guid collectionId,
        Profile? activeProfile,
        int offset,
        int limit,
        IReadOnlySet<Guid> allowedWorkIds,
        CancellationToken ct = default)
    {
        var collection = await collectionRepo.GetByIdAsync(collectionId, ct).ConfigureAwait(false);
        if (collection is null)
        {
            return new CollectionItemPageResult(false, false, 0, []);
        }

        if (!collection.IsEnabled
            || !CollectionAccessPolicy.CanAccess(collection, activeProfile)
            || !HasSupportedRuleDefinition(collection)
            || IsPlaylistCatalogCollection(collection)
            || !CollectionOptionsPolicy.IsInSeason(collection, Today))
        {
            return new CollectionItemPageResult(true, true, 0, []);
        }

        var take = limit + 1;
        var allowedMemberIds = await ExpandAllowedMembersAsync(allowedWorkIds, ct).ConfigureAwait(false);
        var rootMembers = await GetRootMembersAsync(collection, activeProfile, allowedMemberIds, MaxPagedItems, ct).ConfigureAwait(false);
        if (rootMembers.RootIds is not { } rootIds)
        {
            // Hand-picked: the members are already owned, allowed and unique per root, so only the page is looked up.
            var page = await mediaLookupReadService.ResolveItemsAsync(
                collection.Id, rootMembers.Items.Skip(offset).Take(take).ToList(), ct, allowedMemberIds).ConfigureAwait(false);
            return new CollectionItemPageResult(true, false, rootMembers.Items.Count, page);
        }

        if (!rootMembers.PreserveOrder)
        {
            // The order of a generated series comes from the lookup itself, so the whole set is resolved.
            var all = await ResolveCollectionWorkIdsToItemsAsync(
                collection.Id, rootIds.Take(MaxPagedItems).ToList(), preserveRequestedOrder: false, UsesSeriesOrdering(collection), allowedMemberIds, ct).ConfigureAwait(false);
            return new CollectionItemPageResult(true, false, all.Count, all.Skip(offset).Take(take).ToList());
        }

        var queryIds = rootIds.Take(MaxPagedItems).ToList();
        var resolved = await ResolveCollectionWorkIdsToItemsAsync(
            collection.Id, queryIds.Skip(offset).Take(take).ToList(), preserveRequestedOrder: true, UsesSeriesOrdering(collection), allowedMemberIds, ct).ConfigureAwait(false);
        // The lookup numbers items from zero within the ids it was given; keep the position within the whole list.
        foreach (var item in resolved)
        {
            item.SortOrder += offset;
        }

        return new CollectionItemPageResult(true, false, queryIds.Count, resolved);
    }

    /// <summary>
    /// The one read behind every collection item list: members the caller may see resolve to their display roots (a
    /// show, a comic series or an album rather than each episode, issue or track) and are listed once each in the
    /// collection's order. A restricted member never lends its root to the list. The catalogue count uses the same
    /// member authorisation and root resolution for TV, comic and music members, so those counts and lists agree; a
    /// series of books counts each owned book in the catalogue but is listed as its one series tile, and the
    /// catalogue also counts members of nested child collections that a hand-picked list does not hold.
    /// </summary>
    private async Task<List<CollectionItemDto>> ResolveRootItemsAsync(
        Collection collection,
        Profile? activeProfile,
        IReadOnlySet<Guid>? allowedMemberIds,
        int take,
        CancellationToken ct)
    {
        var rootMembers = await GetRootMembersAsync(collection, activeProfile, allowedMemberIds, take, ct).ConfigureAwait(false);
        return rootMembers.RootIds is { } rootIds
            ? await ResolveCollectionWorkIdsToItemsAsync(
                collection.Id,
                rootMembers.PreserveOrder ? rootIds.Take(take).ToList() : rootIds,
                rootMembers.PreserveOrder,
                UsesSeriesOrdering(collection),
                allowedMemberIds,
                ct).ConfigureAwait(false)
            : await mediaLookupReadService.ResolveItemsAsync(
                collection.Id, rootMembers.Items.Take(take).ToList(), ct, allowedMemberIds).ConfigureAwait(false);
    }

    private sealed record CollectionRootMembers(
        IReadOnlyList<Guid>? RootIds,
        bool PreserveOrder,
        IReadOnlyList<CollectionItem> Items);

    /// <summary>
    /// The members a collection lists, before they are looked up. Generated and rule-built collections give their
    /// display root ids (owned, unique, in member order). A hand-picked collection gives one item row per root (the
    /// first), so each keeps its own id and position and a page can be looked up without the rest. In every case a
    /// member is checked for ownership and for the caller's authorisation as itself, before it is collapsed to a root.
    /// </summary>
    private async Task<CollectionRootMembers> GetRootMembersAsync(
        Collection collection,
        Profile? activeProfile,
        IReadOnlySet<Guid>? allowedMemberIds,
        int take,
        CancellationToken ct)
    {
        if (IsGeneratedSeriesCollection(collection))
        {
            var sourceIds = await GetAggregatedCollectionWorkIdsAsync(collection.Id, activeProfile, ct).ConfigureAwait(false);
            var rootIds = await CollapseToOwnedRootsAsync(sourceIds, allowedMemberIds, ct).ConfigureAwait(false);
            return new CollectionRootMembers(rootIds, PreserveOrder: false, []);
        }

        if (collection.Resolution == CollectionResolution.Materialized)
        {
            IReadOnlyList<CollectionItem> items = await collectionRepo.GetCollectionItemsAsync(
                collection.Id,
                Math.Max(take, MaxPagedItems),
                ct).ConfigureAwait(false);
            var members = items
                .Where(item => allowedMemberIds is null || allowedMemberIds.Contains(item.WorkId))
                .ToList();
            var ownedIds = await GetOwnedSourceWorkIdsAsync(members.Select(item => item.WorkId), ct).ConfigureAwait(false);
            members = members.Where(item => ownedIds.Contains(item.WorkId)).ToList();
            var foldedRoots = await GetFoldedItemRootsAsync(members.Select(item => item.WorkId), ct).ConfigureAwait(false);
            var seenRoots = new HashSet<Guid>();
            var onePerRoot = members
                .Where(item => seenRoots.Add(foldedRoots.GetValueOrDefault(item.WorkId, item.WorkId)))
                .ToList();
            return new CollectionRootMembers(
                null,
                PreserveOrder: true,
                CollectionOptionsPolicy.Arrange(collection, onePerRoot, item => item.WorkId, Today));
        }

        var queryIds = await GetCollectionWorkIdsAsync(collection, ct).ConfigureAwait(false);
        var queryRootIds = await CollapseToOwnedRootsAsync(queryIds, allowedMemberIds, ct).ConfigureAwait(false);
        return new CollectionRootMembers(
            CollectionOptionsPolicy.Arrange(collection, queryRootIds, id => id, Today),
            PreserveOrder: true,
            []);
    }

    /// <summary>Keeps the members the caller may see, then collapses them to owned, unique display roots in order.</summary>
    private async Task<List<Guid>> CollapseToOwnedRootsAsync(
        IEnumerable<Guid> sourceWorkIds,
        IReadOnlySet<Guid>? allowedMemberIds,
        CancellationToken ct)
    {
        var members = sourceWorkIds
            .Distinct()
            .Where(workId => allowedMemberIds is null || allowedMemberIds.Contains(workId))
            .ToList();
        return (await GetOwnedCollectionCatalogDisplayWorkIdsAsync(members, ct).ConfigureAwait(false))
            .Distinct()
            .Where(workId => allowedMemberIds is null || allowedMemberIds.Contains(workId))
            .ToList();
    }

    /// <summary>
    /// A member is authorised when the work is itself in the caller's projection, or is a show, season, comic series,
    /// album or book series that holds a work in it. The projection lists episodes, issues, tracks and books but never
    /// their parents, and a restricted sibling does not become authorised just because another child of the same parent
    /// is. The same set also limits the display ids a member expands to, because a book or movie series parent expands
    /// to every owned work below it, not to a root.
    /// </summary>
    private async Task<IReadOnlySet<Guid>?> ExpandAllowedMembersAsync(
        IReadOnlySet<Guid>? allowedWorkIds,
        CancellationToken ct)
    {
        if (allowedWorkIds is null)
        {
            return null;
        }

        var expanded = new HashSet<Guid>(allowedWorkIds);
        if (allowedWorkIds.Count == 0)
        {
            return expanded;
        }

        using var conn = db.CreateConnection();
        foreach (var chunk in allowedWorkIds.Chunk(MaxSqlIdsPerQuery))
        {
            var rows = await conn.QueryAsync<CollectionDisplayWorkRow>(new CommandDefinition(
                """
                -- readiness: ancestors of works whose ids come from Ready-gated reads
                WITH RECURSIVE ancestors(WorkId, ParentId) AS (
                    SELECT w.id, w.parent_work_id
                    FROM works w
                    WHERE w.id IN @WorkIds
                    UNION
                    SELECT parent.id, parent.parent_work_id
                    FROM works parent
                    INNER JOIN ancestors ON parent.id = ancestors.ParentId
                )
                SELECT WorkId FROM ancestors;
                """,
                new { WorkIds = chunk.Select(GuidSql.ToBlob).ToArray() },
                cancellationToken: ct)).ConfigureAwait(false);
            expanded.UnionWith(rows.Select(row => row.WorkId));
        }

        return expanded;
    }

    /// <summary>
    /// Maps hand-picked members to the root the media lookup folds them into: any child to its parent or grandparent,
    /// and a nested parent to its top. Keep in step with the <c>display_work</c> rule in
    /// <c>CollectionMediaLookupReadService.ResolveItemsAsync</c>.
    /// </summary>
    private async Task<Dictionary<Guid, Guid>> GetFoldedItemRootsAsync(
        IEnumerable<Guid> workIds,
        CancellationToken ct)
    {
        var ids = workIds.Distinct().ToList();
        var roots = new Dictionary<Guid, Guid>();
        if (ids.Count == 0)
        {
            return roots;
        }

        using var conn = db.CreateConnection();
        foreach (var chunk in ids.Chunk(MaxSqlIdsPerQuery))
        {
            var rows = await conn.QueryAsync<CollectionDisplayWorkRow>(new CommandDefinition(
                """
                -- readiness: turns work ids into their display roots; the ids come from Ready-gated reads
                SELECT w.id AS SourceWorkId,
                       CASE
                           WHEN w.work_kind = 'child' THEN COALESCE(gp.id, p.id, w.id)
                           WHEN w.work_kind = 'parent' AND p.id IS NOT NULL THEN COALESCE(gp.id, p.id, w.id)
                           ELSE w.id
                       END AS WorkId
                FROM works w
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                WHERE w.id IN @WorkIds;
                """,
                new { WorkIds = chunk.Select(GuidSql.ToBlob).ToArray() },
                cancellationToken: ct)).ConfigureAwait(false);
            foreach (var row in rows)
            {
                roots[row.SourceWorkId ?? row.WorkId] = row.WorkId;
            }
        }

        return roots;
    }

    /// <summary>
    /// The members that are owned: the work itself or something below it has a visible file, including a file that
    /// covers it together with other works (the combined episode file), exactly as the display projection counts it.
    /// </summary>
    private async Task<HashSet<Guid>> GetOwnedSourceWorkIdsAsync(
        IEnumerable<Guid> workIds,
        CancellationToken ct)
    {
        var ids = workIds.Distinct().ToList();
        var owned = new HashSet<Guid>();
        if (ids.Count == 0)
        {
            return owned;
        }

        using var conn = db.CreateConnection();
        var visibleAssetPredicate = HomeVisibilitySql.VisibleAssetPathPredicate("ma.file_path_root");
        foreach (var chunk in ids.Chunk(MaxSqlIdsPerQuery))
        {
            var rows = await conn.QueryAsync<CollectionDisplayWorkRow>(new CommandDefinition(
                $"""
                WITH RECURSIVE work_tree(RootWorkId, WorkId) AS (
                    SELECT w.id AS RootWorkId,
                           w.id AS WorkId
                    FROM works w
                    WHERE w.id IN @WorkIds
                    UNION
                    SELECT work_tree.RootWorkId,
                           child.id AS WorkId
                    FROM works child
                    INNER JOIN work_tree ON child.parent_work_id = work_tree.WorkId
                )
                SELECT DISTINCT work_tree.RootWorkId AS WorkId
                FROM work_tree
                INNER JOIN works leaf_w ON leaf_w.id = work_tree.WorkId
                INNER JOIN work_owned_assets woa ON woa.work_id = work_tree.WorkId
                INNER JOIN media_assets ma ON ma.id = woa.asset_id
                WHERE {visibleAssetPredicate}
                  AND {VaultSql.NotVaultAsset("ma.id")}
                  AND {WorkReadinessSql.ReadyWorkPredicate("leaf_w")};
                """,
                new { WorkIds = chunk.Select(GuidSql.ToBlob).ToArray() },
                cancellationToken: ct)).ConfigureAwait(false);
            owned.UnionWith(rows.Select(row => row.WorkId));
        }

        return owned;
    }

    /// <summary>The most ids sent to the data store in one statement, well under its parameter limit.</summary>
    private const int MaxSqlIdsPerQuery = 5000;

    private async Task<List<Collection>> GetAccessibleCollectionsAsync(Profile? activeProfile, CancellationToken ct)
    {
        var collections = await collectionRepo.GetAllAsync(ct).ConfigureAwait(false);
        return collections
            .Where(collection => collection.IsEnabled
                && CollectionAccessPolicy.CanAccess(collection, activeProfile))
            .ToList();
    }

    private static int GetManagedCollectionItemCount(
        Collection collection,
        IReadOnlyDictionary<Guid, int> curatedCountByCollection,
        IReadOnlyList<Guid> workIds)
    {
        if (collection.Resolution != CollectionResolution.Query
            && curatedCountByCollection.TryGetValue(collection.Id, out var count))
        {
            return Math.Max(count, workIds.Count);
        }

        return workIds.Count;
    }

    private static CollectionCatalogClassification ClassifyCollectionForCatalog(Collection collection)
    {
        var systemKey = GetSystemCollectionKey(collection);
        if (systemKey is not null)
        {
            return new CollectionCatalogClassification("System", CollectionTypeNames.System, systemKey, true, SystemLaneForKey(systemKey));
        }

        var family = collection.Scope == CollectionScope.Library
            ? "Global"
            : "User";
        return new CollectionCatalogClassification(
            family,
            collection.CollectionType.ToStorageValue(),
            null,
            false);
    }

    private static string? GetSystemCollectionKey(Collection collection)
    {
        var normalizedName = (collection.DisplayName ?? string.Empty).Trim();
        if (normalizedName.Length == 0)
        {
            return null;
        }

        return collection.CollectionType == CollectionType.System
            ? normalizedName.ToLowerInvariant().Replace(' ', '-')
            : null;
    }

    private static string? SystemLaneForKey(string systemKey) => systemKey switch
    {
        "favorites" or "listening-queue" => "Listen",
        "watchlist" or "currently-watching" => "Watch",
        "reading-list" => "Read",
        _ => null,
    };

    private async Task<bool> HasKnownSeriesManifestAsync(Collection collection, CancellationToken ct)
    {
        if (!IsGeneratedSeriesCollection(collection))
        {
            return false;
        }

        var manifest = await manifestRepo.GetViewByCollectionIdAsync(collection.Id, ct).ConfigureAwait(false);
        return manifest?.TotalCount > 1;
    }

    private static bool ShouldIncludeInManagementCatalog(
        Collection collection,
        CollectionCatalogClassification classification,
        CollectionMediaCounts mediaCounts,
        bool hasKnownSeriesManifest,
        IReadOnlyList<Collection> accessibleCollections)
    {
        if (IsPlaylistCatalogCollection(collection))
        {
            return false;
        }

        if (classification.IsSystem || string.Equals(classification.Family, "User", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (CollectionAccessPolicy.IsManagedCollectionType(collection.CollectionType))
        {
            return true;
        }

        var generatedAggregation = IsGeneratedSeriesCollection(collection)
            ? GetCollectionCatalogAggregation(collection, accessibleCollections)
            : null;
        if (IsGeneratedSeriesCollection(collection) && generatedAggregation is null)
        {
            return false;
        }

        if (generatedAggregation?.Key.StartsWith("parent:", StringComparison.OrdinalIgnoreCase) == true)
        {
            return mediaCounts.TotalCount > 0;
        }

        return mediaCounts.TotalCount >= 2 || hasKnownSeriesManifest;
    }

    private static bool IsPlaylistCatalogCollection(Collection collection)
        => collection.CollectionType is CollectionType.Playlist
            or CollectionType.PlaylistFolder
            or CollectionType.Smart;

    private static CollectionManagementCatalogCandidate SelectCatalogRepresentative(
        IReadOnlyList<CollectionManagementCatalogCandidate> entries)
        => entries
            .OrderByDescending(entry => entry.MediaCounts.TotalCount)
            .ThenByDescending(entry => entry.ItemCount)
            .ThenBy(entry => entry.Collection.DisplayName, StringComparer.OrdinalIgnoreCase)
            .First();

    private async Task<CollectionCatalogPersonDto?> ResolveCatalogPersonAsync(
        IEnumerable<Collection> collections,
        CancellationToken ct)
    {
        var references = collections
            .SelectMany(GetCatalogPersonReferences)
            .DistinctBy(reference => $"{reference.LookupKind}:{reference.LookupValue}", StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (references.Count == 0)
        {
            return null;
        }

        var peopleByQid = (await personRepo.FindByQidsAsync(
                references
                    .Where(reference => string.Equals(reference.LookupKind, "qid", StringComparison.OrdinalIgnoreCase))
                    .Select(reference => reference.LookupValue),
                ct).ConfigureAwait(false))
            .Where(person => !string.IsNullOrWhiteSpace(person.WikidataQid))
            .ToDictionary(person => person.WikidataQid!, StringComparer.OrdinalIgnoreCase);
        var peopleByName = (await personRepo.FindByNamesAsync(
                references
                    .Where(reference => !string.Equals(reference.LookupKind, "qid", StringComparison.OrdinalIgnoreCase))
                    .Select(reference => reference.LookupValue),
                ct).ConfigureAwait(false))
            .ToDictionary(person => person.Name, StringComparer.OrdinalIgnoreCase);

        var resolved = new List<(Person Person, CatalogPersonReference Reference)>();
        foreach (var reference in references)
        {
            var person = string.Equals(reference.LookupKind, "qid", StringComparison.OrdinalIgnoreCase)
                ? peopleByQid.GetValueOrDefault(reference.LookupValue)
                : peopleByName.GetValueOrDefault(reference.LookupValue);
            if (person is null)
            {
                return null;
            }

            resolved.Add((person, reference));
        }

        var people = resolved
            .Select(result => result.Person)
            .DistinctBy(person => person.Id)
            .ToList();
        if (people.Count != 1)
        {
            return null;
        }

        var selected = people[0];
        var roles = resolved
            .Select(result => result.Reference.Role)
            .Concat(selected.Roles)
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Select(role => NormalizePersonRole(role!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new CollectionCatalogPersonDto
        {
            Id = selected.Id,
            Name = selected.Name,
            HeadshotUrl = ApiImageUrls.BuildPersonHeadshotUrl(
                selected.Id,
                selected.LocalHeadshotPath,
                selected.HeadshotUrl),
            Roles = roles,
        };
    }

    private static IEnumerable<CatalogPersonReference> GetCatalogPersonReferences(Collection collection)
    {
        var foundExplicitReference = false;
        foreach (var rule in CollectionRuleEvaluator.ParseDefinition(collection.RuleJson).AllConditions)
        {
            var field = rule.Field.Trim().ToLowerInvariant();
            if (!IsExactPersonRule(rule, field))
            {
                continue;
            }

            var values = rule.GetEffectiveValues()
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (values.Count != 1)
            {
                continue;
            }

            foundExplicitReference = true;
            yield return field == "person_qid"
                ? new CatalogPersonReference("qid", values[0], null)
                : new CatalogPersonReference("name", values[0], PersonRoleForRuleField(field));
        }

        if (!foundExplicitReference
            && !string.IsNullOrWhiteSpace(collection.DisplayName)
            && TryGetPersonCollectionRole(collection.CollectionType.ToStorageValue(), out var collectionRole))
        {
            var personName = RemoveCollectionSuffix(collection.DisplayName);
            if (!string.IsNullOrWhiteSpace(personName))
            {
                yield return new CatalogPersonReference("name", personName, collectionRole);
            }
        }
    }

    private static bool IsExactPersonRule(CollectionRulePredicate rule, string field)
        => (string.Equals(rule.Op, "eq", StringComparison.OrdinalIgnoreCase)
            || string.Equals(rule.Op, "in", StringComparison.OrdinalIgnoreCase))
            && (field == "person_qid" || PersonRoleForRuleField(field) is not null);

    private static string? PersonRoleForRuleField(string field) => field switch
    {
        "author" => "Author",
        "artist" => "Artist",
        "director" => "Director",
        "actor" => "Actor",
        "narrator" => "Narrator",
        "composer" => "Composer",
        "illustrator" => "Illustrator",
        "performer" => "Performer",
        "creator" => "Creator",
        "writer" => "Writer",
        _ => null,
    };

    private static bool TryGetPersonCollectionRole(string collectionType, out string? role)
    {
        role = collectionType.Trim().ToLowerInvariant() switch
        {
            "author" => "Author",
            "artist" => "Artist",
            "director" => "Director",
            "actor" => "Actor",
            "narrator" => "Narrator",
            "composer" => "Composer",
            "illustrator" => "Illustrator",
            "performer" => "Performer",
            "creator" => "Creator",
            "writer" => "Writer",
            "person" => null,
            _ => string.Empty,
        };
        return role != string.Empty;
    }

    private static string RemoveCollectionSuffix(string name)
    {
        const string suffix = " collection";
        var trimmed = name.Trim();
        return trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^suffix.Length].Trim()
            : trimmed;
    }

    private static string NormalizePersonRole(string role)
    {
        var trimmed = role.Trim();
        return trimmed.Length == 0
            ? trimmed
            : char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }

    private sealed record CatalogPersonReference(string LookupKind, string LookupValue, string? Role);

    private static CollectionCatalogAggregation? GetCollectionCatalogAggregation(
        Collection collection,
        IReadOnlyList<Collection>? accessibleCollections = null)
    {
        if (!IsGeneratedSeriesCollection(collection))
        {
            return null;
        }

        // Broad, trusted relationships must win over a lane-local parent or series.
        // For example, Batman comics and Batman films commonly meet through a
        // shared based_on identity even though each lane also has its own series
        // hierarchy. Choosing the hierarchy first split that cross-media rollup.
        if (TryGetRelationshipAggregation(collection, "fictional_universe", out var aggregation)
            || TryGetRelationshipAggregation(collection, "franchise", out aggregation)
            || TryGetRelationshipAggregation(collection, "based_on", out aggregation))
        {
            return aggregation;
        }

        if (accessibleCollections is not null
            && TryGetStructuralParentAggregation(collection, accessibleCollections, out var structuralAggregation))
        {
            return structuralAggregation;
        }

        return TryGetRelationshipAggregation(collection, "series", out aggregation)
            ? aggregation
            : null;
    }

    private static bool TryGetStructuralParentAggregation(
        Collection collection,
        IReadOnlyList<Collection> accessibleCollections,
        out CollectionCatalogAggregation aggregation)
    {
        var byId = accessibleCollections.ToDictionary(candidate => candidate.Id);
        var root = collection;
        var visited = new HashSet<Guid>();
        while (root.ParentCollectionId is Guid parentId
               && visited.Add(root.Id)
               && byId.TryGetValue(parentId, out var parent)
               && IsGeneratedSeriesCollection(parent))
        {
            root = parent;
        }

        var hasGeneratedChildren = accessibleCollections.Any(candidate =>
            candidate.ParentCollectionId == root.Id
            && IsGeneratedSeriesCollection(candidate));
        if (!hasGeneratedChildren)
        {
            aggregation = default!;
            return false;
        }

        aggregation = new CollectionCatalogAggregation(
            $"parent:{root.Id:D}",
            root.DisplayName);
        return true;
    }

    private static bool ShouldIncludeCatalogGroup(IReadOnlyList<CollectionManagementCatalogCandidate> entries)
    {
        var generatedEntries = entries
            .Where(entry => IsGeneratedSeriesCollection(entry.Collection))
            .ToList();

        if (generatedEntries.Count == 0)
        {
            return true;
        }

        var generatedCollectionCount = generatedEntries
            .Where(entry => entry.Collection.Works.Count > 0)
            .Select(entry => entry.Collection.Id)
            .Distinct()
            .Count();
        if (generatedCollectionCount >= 2)
        {
            return true;
        }

        // A single structural series can still be a truthful cross-media
        // collection when the library owns at least two distinct titles across
        // lanes (for example, Harry Potter books plus an audiobook edition).
        return generatedEntries.Any(entry =>
            entry.MediaCounts.IsCrossMedia
            && CountDistinctOwnedTitles(entry) >= 2);
    }

    private static int CountDistinctOwnedTitles(CollectionManagementCatalogCandidate entry)
    {
        var ownedWorkIds = entry.WorkIds.ToHashSet();
        return CollectionResponseFormatting.CountDistinctWorkTitles(
            entry.Collection.Works.Where(work => ownedWorkIds.Contains(work.Id)));
    }

    private static bool TryGetRelationshipAggregation(
        Collection collection,
        string relationshipType,
        out CollectionCatalogAggregation aggregation)
    {
        var relationship = collection.Relationships
            .FirstOrDefault(candidate => string.Equals(candidate.RelType, relationshipType, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(candidate.RelQid));
        if (relationship is null)
        {
            aggregation = default!;
            return false;
        }

        aggregation = new CollectionCatalogAggregation(
            $"{relationshipType}:{NormalizeCatalogQid(relationship.RelQid)}",
            StringHelpers.FirstNonBlank(relationship.RelLabel, collection.DisplayName));
        return true;
    }

    private static string NormalizeCatalogQid(string qid)
    {
        var value = qid.Contains('/') ? qid.Split('/')[^1] : qid;
        if (value.Contains("::", StringComparison.Ordinal))
        {
            value = value.Split("::", 2)[0];
        }

        return value.Trim();
    }

    private static string? ToNullableText(object? value) =>
        value switch
        {
            null => null,
            string text when string.IsNullOrWhiteSpace(text) => null,
            string text => text,
            byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        };

    // A key such as "parent:...", "franchise:Q..." or "fictional_universe:Q..." joins several shelves; a bare "series:" key does not.
    private static bool IsBroadRollupKey(string? key) =>
        key is not null && !key.StartsWith("series:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Names the group the way people see it: a TV show is a show, a book or movie sequence is a series, a rollup
    /// across shelves or a rule-built group is a collection, and a hand-picked or system list is a list.
    /// </summary>
    private static string ClassifyGroupType(
        Collection collection,
        CollectionCatalogClassification classification,
        CollectionMediaCounts mediaCounts,
        bool isBroadRollup)
    {
        if (classification.IsSystem)
        {
            return CollectionGroupTypes.List;
        }

        if (CollectionAccessPolicy.IsManagedCollectionType(collection.CollectionType))
        {
            return collection.CollectionType is CollectionType.Playlist or CollectionType.PlaylistFolder
                   || collection.MembershipMode == ContainerMembershipMode.Manual
                ? CollectionGroupTypes.List
                : CollectionGroupTypes.Collection;
        }

        if (!IsGeneratedSeriesCollection(collection))
        {
            return CollectionGroupTypes.Collection;
        }

        if (isBroadRollup || collection.CollectionType == CollectionType.Universe)
        {
            return CollectionGroupTypes.Collection;
        }

        return mediaCounts.TvCount > 0 && mediaCounts.TvCount == mediaCounts.TotalCount
            ? CollectionGroupTypes.Show
            : CollectionGroupTypes.Series;
    }

    // A root Universe with a well-formed Wikidata QID: the same identity /api/v1/universe/{qid} resolves. Everything
    // else (series, shows, lists, malformed QIDs) has none. The native routes confirm the profile can open it.
    private static string? ResolveUniverseQid(Collection collection) =>
        collection.CollectionType == CollectionType.Universe && collection.ParentCollectionId is null
            ? UniverseClientReadService.NormalizeQid(collection.WikidataQid)
            : null;

    private static bool IsGeneratedSeriesCollection(Collection collection)
        => collection.CollectionType is CollectionType.Universe
            or CollectionType.Series
            or CollectionType.ContentGroup;

    private static bool IsGeneratedTvShowContainer(Collection collection, CollectionMediaCounts mediaCounts)
        => IsGeneratedSeriesCollection(collection)
            && mediaCounts.TvCount > 0
            && mediaCounts.WatchCount == mediaCounts.TvCount
            && mediaCounts.ListenCount == 0
            && mediaCounts.ReadCount == 0
            && mediaCounts.OtherCount == 0;

    private async Task<CollectionMediaCounts> GetCollectionMediaCountsAsync(
        IReadOnlyList<Guid> workIds,
        CancellationToken ct)
    {
        if (workIds.Count == 0)
        {
            return new CollectionMediaCounts(0, 0, 0, 0);
        }

        using var conn = db.CreateConnection();
        var rows = new List<CollectionMediaMetadataRow>();
        foreach (var chunk in workIds.Chunk(MaxSqlIdsPerQuery))
        {
            rows.AddRange(await conn.QueryAsync<CollectionMediaMetadataRow>(new CommandDefinition(
            """
            -- readiness: type and year of works whose ids come from Ready-gated reads
            SELECT w.media_type AS MediaType,
                   COALESCE(
                       (SELECT NULLIF(cv.value, '')
                        FROM canonical_values cv
                        WHERE cv.entity_id = w.id AND cv.key = 'release_year'
                        LIMIT 1),
                       (SELECT NULLIF(cv.value, '')
                        FROM canonical_values cv
                        WHERE cv.entity_id = w.id AND cv.key = 'year'
                        LIMIT 1)) AS Year
            FROM works w
            WHERE w.id IN @WorkIds
            """,
            new { WorkIds = chunk.Select(GuidSql.ToBlob).ToArray() },
            cancellationToken: ct)).ConfigureAwait(false));
        }

        var watch = 0;
        var listen = 0;
        var read = 0;
        var other = 0;
        var tv = 0;
        var movies = 0;
        var books = 0;
        var comics = 0;
        var music = 0;
        var audiobooks = 0;
        var years = new List<int>();
        foreach (var row in rows)
        {
            if (string.Equals(row.MediaType, "TV", StringComparison.OrdinalIgnoreCase))
            {
                tv++;
            }
            else if (string.Equals(row.MediaType, "Movies", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(row.MediaType, "Movie", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(row.MediaType, "Video", StringComparison.OrdinalIgnoreCase))
            {
                movies++;
            }
            else if (string.Equals(row.MediaType, "Audiobooks", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(row.MediaType, "Audiobook", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(row.MediaType, "M4B", StringComparison.OrdinalIgnoreCase))
            {
                audiobooks++;
            }
            else if (string.Equals(row.MediaType, "Music", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(row.MediaType, "Song", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(row.MediaType, "Album", StringComparison.OrdinalIgnoreCase))
            {
                music++;
            }
            else if (string.Equals(row.MediaType, "Comics", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(row.MediaType, "Comic", StringComparison.OrdinalIgnoreCase))
            {
                comics++;
            }
            else if (string.Equals(row.MediaType, "Books", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(row.MediaType, "Book", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(row.MediaType, "EPUB", StringComparison.OrdinalIgnoreCase))
            {
                books++;
            }

            if (IsWatchMediaType(row.MediaType))
            {
                watch++;
            }
            else if (IsListenMediaType(row.MediaType))
            {
                listen++;
            }
            else if (IsReadMediaType(row.MediaType))
            {
                read++;
            }
            else
            {
                other++;
            }

            if (ParseCatalogYear(row.Year) is { } year)
            {
                years.Add(year);
            }
        }

        return new CollectionMediaCounts(
            watch,
            listen,
            read,
            other,
            tv,
            movies,
            books,
            comics,
            music,
            audiobooks,
            years.Count > 0 ? years.Min() : null,
            years.Count > 0 ? years.Max() : null);
    }

    private static int? ParseCatalogYear(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var prefixLength = Math.Min(4, trimmed.Length);
        return int.TryParse(trimmed.AsSpan(0, prefixLength), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
               && year is >= 1000 and <= 9999
            ? year
            : null;
    }

    private async Task<IReadOnlyList<Guid>> GetCollectionWorkIdsAsync(Collection collection, CancellationToken ct)
    {
        if (collection.Resolution == CollectionResolution.Query
            && !string.IsNullOrWhiteSpace(collection.RuleJson))
        {
            var definition = CollectionRuleEvaluator.ParseDefinition(collection.RuleJson);
            if (definition.AllConditions.Count == 0)
            {
                return [];
            }

            var evaluator = new CollectionRuleEvaluator(db);
            return evaluator.Evaluate(
                definition,
                collection.SortField,
                collection.SortDirection.ToStorageValue(),
                0,
                secondarySortField: collection.SecondarySortField,
                secondarySortDirection: collection.SecondarySortDirection?.ToStorageValue());
        }

        var items = await collectionRepo.GetCollectionItemsAsync(collection.Id, 5000, ct).ConfigureAwait(false);
        if (items.Count > 0)
        {
            return items.Select(item => item.WorkId).Distinct().ToList();
        }

        var collectionWithWorks = await collectionRepo.GetCollectionWithWorksAsync(collection.Id, ct).ConfigureAwait(false);
        return collectionWithWorks?.Works.Select(work => work.Id).Distinct().ToList() ?? [];
    }

    private async Task<IReadOnlyList<Guid>> GetAggregatedCollectionWorkIdsAsync(
        Guid collectionId,
        Profile? activeProfile,
        CancellationToken ct)
    {
        var accessibleCollections = await GetAccessibleCollectionsAsync(activeProfile, ct).ConfigureAwait(false);
        var target = accessibleCollections.FirstOrDefault(collection => collection.Id == collectionId);
        if (target is null)
        {
            return [];
        }

        var targetGrouping = GetCollectionCatalogAggregation(target, accessibleCollections);
        var siblingCollections = targetGrouping is null
            ? new List<Collection> { target }
            : accessibleCollections
                .Where(collection => IsGeneratedSeriesCollection(collection)
                    && string.Equals(
                        GetCollectionCatalogAggregation(collection, accessibleCollections)?.Key,
                        targetGrouping.Key,
                        StringComparison.OrdinalIgnoreCase))
                .ToList();

        var workIds = new List<Guid>();
        foreach (var collection in ExpandWithChildCollections(siblingCollections, accessibleCollections))
        {
            workIds.AddRange(await GetCollectionWorkIdsAsync(collection, ct).ConfigureAwait(false));
        }

        return workIds;
    }

    private async Task<IReadOnlyList<Guid>> GetCollectionCatalogSourceWorkIdsAsync(
        Collection collection,
        IReadOnlyList<Collection> accessibleCollections,
        CancellationToken ct)
    {
        var workIds = new List<Guid>();
        foreach (var sourceCollection in ExpandWithChildCollections([collection], accessibleCollections))
        {
            workIds.AddRange(await GetCollectionWorkIdsAsync(sourceCollection, ct).ConfigureAwait(false));
        }

        return workIds.Distinct().ToList();
    }

    private static IReadOnlyList<Collection> ExpandWithChildCollections(
        IReadOnlyList<Collection> collections,
        IReadOnlyList<Collection> accessibleCollections)
    {
        var result = new List<Collection>();
        var queue = new Queue<Collection>(collections);
        var seen = new HashSet<Guid>();
        while (queue.Count > 0)
        {
            var collection = queue.Dequeue();
            if (!seen.Add(collection.Id))
            {
                continue;
            }

            result.Add(collection);
            foreach (var child in accessibleCollections.Where(candidate => candidate.ParentCollectionId == collection.Id))
            {
                queue.Enqueue(child);
            }
        }

        return result;
    }

    private async Task<IReadOnlyList<Guid>> GetCollectionCatalogDisplayWorkIdsAsync(
        IEnumerable<Guid> sourceWorkIds,
        CancellationToken ct)
    {
        var workIds = sourceWorkIds.Distinct().ToList();
        if (workIds.Count == 0)
        {
            return [];
        }

        // The ids go in as one JSON list. A detail page of a show, album or series passes every work the profile may see,
        // and binding them one parameter each took about 0.4 s at 4,000 works (B29).
        using var conn = db.CreateConnection();
        var rows = (await conn.QueryAsync<CollectionDisplayWorkRow>(new CommandDefinition(
            $"""
            -- readiness: turns work ids into their display roots; the ids come from Ready-gated reads
            WITH RECURSIVE source_roots(SourceWorkId, RootWorkId, RootMediaType) AS (
                SELECT DISTINCT
                       w.id AS SourceWorkId,
                       CASE
                           WHEN w.work_kind = 'child' THEN COALESCE(gp.id, p.id, w.id)
                           WHEN w.work_kind = 'parent' AND p.id IS NOT NULL THEN COALESCE(gp.id, p.id, w.id)
                           ELSE w.id
                       END AS RootWorkId,
                       COALESCE(gp.media_type, p.media_type, w.media_type) AS RootMediaType
                FROM works w
                LEFT JOIN works p ON p.id = w.parent_work_id
                LEFT JOIN works gp ON gp.id = p.parent_work_id
                WHERE w.id IN (SELECT unhex(value) FROM json_each(@WorkIds))
            ),
            work_tree(SourceWorkId, RootWorkId, RootMediaType, WorkId) AS (
                SELECT SourceWorkId,
                       RootWorkId,
                       RootMediaType,
                       SourceWorkId
                FROM source_roots
                UNION ALL
                SELECT work_tree.SourceWorkId,
                       work_tree.RootWorkId,
                       work_tree.RootMediaType,
                       child.id
                FROM works child
                INNER JOIN work_tree ON child.parent_work_id = work_tree.WorkId
            )
            SELECT DISTINCT
                   SourceWorkId,
                   CASE
                       WHEN LOWER(RootMediaType) LIKE '%tv%'
                         OR LOWER(RootMediaType) LIKE '%comic%'
                         OR LOWER(RootMediaType) LIKE '%music%'
                       THEN RootWorkId
                       ELSE WorkId
                   END AS WorkId
            FROM work_tree
            WHERE LOWER(RootMediaType) LIKE '%tv%'
               OR LOWER(RootMediaType) LIKE '%comic%'
               OR LOWER(RootMediaType) LIKE '%music%'
               OR EXISTS (
                   SELECT 1
                   FROM work_owned_assets woa
                   WHERE woa.work_id = work_tree.WorkId
                     AND {VaultSql.NotVaultAsset("woa.asset_id")}
               );
            """,
            new { WorkIds = GuidSql.ToJsonHexArray(workIds) },
            cancellationToken: ct)).ConfigureAwait(false)).ToList();

        var rowsBySource = rows
            .GroupBy(row => row.SourceWorkId ?? row.WorkId)
            .ToDictionary(group => group.Key, group => group.Select(row => row.WorkId).ToList());
        return workIds
            .SelectMany(workId => rowsBySource.GetValueOrDefault(workId) ?? [])
            .Distinct()
            .ToList();
    }

    private async Task<IReadOnlyList<Guid>> GetOwnedCollectionCatalogDisplayWorkIdsAsync(
        IEnumerable<Guid> sourceWorkIds,
        CancellationToken ct)
    {
        var displayWorkIds = await GetCollectionCatalogDisplayWorkIdsAsync(sourceWorkIds, ct).ConfigureAwait(false);
        if (displayWorkIds.Count == 0)
        {
            return [];
        }

        var ownedIds = await GetOwnedSourceWorkIdsAsync(displayWorkIds, ct).ConfigureAwait(false);
        return displayWorkIds.Where(ownedIds.Contains).ToList();
    }

    /// <summary>
    /// The Series ordering (manifest ordinal, the work's own decimal position, date, title, with "Also in this world"
    /// last) applies to a Series shelf only. A generated Universe list keeps the order it always had.
    /// </summary>
    private static bool UsesSeriesOrdering(Collection collection)
        => collection.CollectionType is CollectionType.ContentGroup or CollectionType.Series;

    /// <param name="workIds">Display root ids that are already owned and allowed; they are not expanded again.</param>
    /// <param name="seriesOrdering">True for a Series shelf; false keeps a generated Universe list in its previous order.</param>
    /// <param name="allowedMemberIds">Limits the file that stands in for each root; null leaves every work eligible.</param>
    private async Task<List<CollectionItemDto>> ResolveCollectionWorkIdsToItemsAsync(
        Guid collectionId,
        IReadOnlyList<Guid> workIds,
        bool preserveRequestedOrder,
        bool seriesOrdering,
        IReadOnlySet<Guid>? allowedMemberIds,
        CancellationToken ct)
    {
        var displayWorkIds = workIds.Distinct().ToList();
        if (displayWorkIds.Count == 0)
        {
            return [];
        }

        using var conn = db.CreateConnection();
        var visibleWorkPredicate = WorkReadinessSql.ReadyWorkPredicate("w");
        var visibleAssetPredicate = HomeVisibilitySql.VisibleAssetPathPredicate("ma.file_path_root");
        var rows = new List<GeneratedCollectionItemRow>();
        foreach (var chunk in displayWorkIds.Chunk(MaxSqlIdsPerQuery))
        {
            rows.AddRange(await conn.QueryAsync<GeneratedCollectionItemRow>(new CommandDefinition(
            $"""
            WITH RECURSIVE work_tree(RootWorkId, WorkId) AS (
                SELECT w.id AS RootWorkId,
                       w.id AS WorkId
                FROM works w
                WHERE w.id IN @WorkIds
                UNION ALL
                SELECT work_tree.RootWorkId,
                       child.id AS WorkId
                FROM works child
                INNER JOIN work_tree ON child.parent_work_id = work_tree.WorkId
            ),
            representative_assets AS (
                SELECT work_tree.RootWorkId AS WorkId,
                       MIN(ma.id) AS AssetId
                FROM work_tree
                INNER JOIN editions e ON e.work_id = work_tree.WorkId
                INNER JOIN media_assets ma ON ma.edition_id = e.id
                WHERE {visibleAssetPredicate}
                  AND {VaultSql.NotVaultAsset("ma.id")}
                  {CollectionAssetAllowList.Clause("work_tree.WorkId", allowedMemberIds)}
                GROUP BY work_tree.RootWorkId
            )
            SELECT w.id AS WorkId,
                   w.work_kind AS WorkKind,
                   COALESCE(
                       NULLIF(title_work.value, ''),
                       NULLIF(title_asset.value, ''),
                       NULLIF(episode_title.value, ''),
                       NULLIF(show_name.value, ''),
                       NULLIF(series_item.item_label, ''),
                       'Untitled'
                   ) AS Title,
                   COALESCE(
                       (SELECT NULLIF(cva.value, '')
                        FROM canonical_value_arrays cva
                        WHERE cva.entity_id = w.id
                          AND cva.key IN ('author', 'album_artist', 'artist', 'director')
                        ORDER BY CASE cva.key
                                     WHEN 'author' THEN 1
                                     WHEN 'album_artist' THEN 2
                                     WHEN 'artist' THEN 3
                                     ELSE 4
                                 END,
                                 cva.ordinal
                        LIMIT 1),
                       (SELECT NULLIF(cva.value, '')
                        FROM canonical_value_arrays cva
                        WHERE cva.entity_id = ra.AssetId
                          AND cva.key IN ('author', 'album_artist', 'artist', 'director')
                        ORDER BY CASE cva.key
                                     WHEN 'author' THEN 1
                                     WHEN 'album_artist' THEN 2
                                     WHEN 'artist' THEN 3
                                     ELSE 4
                                 END,
                                 cva.ordinal
                        LIMIT 1)) AS Creator,
                   w.media_type AS MediaType,
                   ra.AssetId AS MediaAssetId,
                   CASE
                       WHEN LOWER(COALESCE(w.work_kind, '')) = 'parent'
                            AND LOWER(COALESCE(w.media_type, '')) LIKE '%tv%'
                           THEN NULLIF(cover_work.value, '')
                       ELSE COALESCE(
                           NULLIF(cover_work.value, ''),
                           NULLIF(cover_asset.value, '')
                       )
                   END AS CoverUrl,
                   (SELECT c.id
                    FROM work_tree member_tree
                    INNER JOIN collection_items member_item
                        ON member_item.work_id = member_tree.WorkId
                    INNER JOIN collections c
                        ON c.id = member_item.collection_id
                    WHERE member_tree.RootWorkId = w.id
                      AND c.id <> @CollectionId
                      AND LOWER(COALESCE(c.group_by_field, '')) IN ('series', 'show_name')
                    ORDER BY CASE WHEN c.parent_collection_id = @CollectionId THEN 0 ELSE 1 END,
                             c.created_at,
                             c.id
                    LIMIT 1) AS StructuralCollectionId,
                   CAST(COALESCE(w.ordinal, series_item.sort_order, 999999) AS INTEGER) AS LegacySortOrder,
                   CAST(COALESCE(w.ordinal_sort, w.ordinal) AS REAL) AS WorkOrdinalSort,
                   series_item.parsed_ordinal AS ManifestOrdinal,
                   series_item.ordinal_scope_qid AS ManifestOrdinalScopeQid,
                   series_item.series_qid AS ManifestSeriesQid,
                   series_item.raw_ordinal AS ManifestRawOrdinal,
                   series_item.membership_scope AS MembershipScope,
                   COALESCE(series_item.is_expanded_from_collection, 0) AS IsExpandedFromCollection,
                   COALESCE(
                       NULLIF(series_item.publication_date, ''),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '')
                        FROM canonical_values cv
                        WHERE cv.entity_id IN (w.id, ra.AssetId)
                          AND cv.key IN ('publication_date', 'release_date', 'year')
                        ORDER BY CASE cv.key WHEN 'publication_date' THEN 0 WHEN 'release_date' THEN 1 ELSE 2 END, cv.entity_id
                        LIMIT 1)) AS PublicationDate,
                   COALESCE(
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = ra.AssetId AND cv.key = 'series_position' LIMIT 1),
                       (SELECT NULLIF(CAST(cv.value AS TEXT), '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'series_position' LIMIT 1)) AS RetailSeriesPosition,
                   COALESCE(
                       (SELECT NULLIF(cva.value, '') FROM canonical_value_arrays cva WHERE cva.entity_id = ra.AssetId AND cva.key = 'series_placement' AND cva.value_qid = (SELECT c.wikidata_qid FROM collections c WHERE c.id = @CollectionId) COLLATE NOCASE ORDER BY cva.ordinal LIMIT 1),
                       (SELECT NULLIF(cva.value, '') FROM canonical_value_arrays cva WHERE cva.entity_id = w.id AND cva.key = 'series_placement' AND cva.value_qid = (SELECT c.wikidata_qid FROM collections c WHERE c.id = @CollectionId) COLLATE NOCASE ORDER BY cva.ordinal LIMIT 1)) AS SeriesPlacementPosition
            FROM works w
            LEFT JOIN representative_assets ra ON ra.WorkId = w.id
            LEFT JOIN canonical_values title_work ON title_work.entity_id = w.id AND title_work.key = 'title'
            LEFT JOIN canonical_values title_asset ON title_asset.entity_id = ra.AssetId AND title_asset.key = 'title'
            LEFT JOIN canonical_values episode_title ON episode_title.entity_id = w.id AND episode_title.key = 'episode_title'
            LEFT JOIN canonical_values show_name ON show_name.entity_id = w.id AND show_name.key = 'show_name'
            LEFT JOIN canonical_values cover_work ON cover_work.entity_id = w.id AND cover_work.key IN ('cover_url', 'cover', 'poster_url', 'poster', 'episode_still_url', 'episode_still', 'still_url', 'still')
            LEFT JOIN canonical_values cover_asset ON cover_asset.entity_id = ra.AssetId AND cover_asset.key IN ('cover_url', 'cover', 'poster_url', 'poster', 'episode_still_url', 'episode_still', 'still_url', 'still')
            LEFT JOIN series_manifest_items series_item ON series_item.linked_work_id = w.id AND series_item.collection_id = @CollectionId
            WHERE w.id IN @WorkIds
              AND ({visibleWorkPredicate} OR ra.AssetId IS NOT NULL)
            ORDER BY w.id;
            """,
            new
            {
                CollectionId = collectionId,
                AllowedHex = CollectionAssetAllowList.ToJson(allowedMemberIds),
                WorkIds = chunk.Select(GuidSql.ToBlob).ToArray(),
            },
            cancellationToken: ct)).ConfigureAwait(false));
        }

        var requestedPosition = displayWorkIds
            .Select((workId, index) => (workId, index))
            .ToDictionary(entry => entry.workId, entry => entry.index);
        List<GeneratedCollectionItemRow> selectedRows;
        if (seriesOrdering)
        {
            // One row per work: the manifest row that is not an expansion of a collection, then the strongest scope.
            selectedRows = rows
                .GroupBy(row => row.WorkId)
                .Select(group => group
                    .OrderBy(row => row.IsExpandedFromCollection)
                    .ThenBy(row => SeriesPlacementRule.ScopeRank(row.MembershipScope))
                    .First())
                .OrderBy(row => requestedPosition.GetValueOrDefault(row.WorkId, int.MaxValue))
                .ToList();

            // A generated Series list uses the one Series ordering (manifest ordinal, the work's own decimal position,
            // publication date, title) with "Also in this world" members after the Series run; an explicit list keeps
            // the order it was given. The sort is stable, so equal members keep the order they were requested in.
            if (!preserveRequestedOrder)
            {
                selectedRows = SeriesOrdering.Order(selectedRows, row => row.ToOrderInput());
            }
        }
        else
        {
            // A generated Universe list is outside the Series ordering: whole-number position, then the order the
            // collection holds its members in, exactly as before.
            var legacyRows = rows
                .GroupBy(row => row.WorkId)
                .Select(group => group.OrderBy(row => row.LegacySortOrder).ThenBy(row => row.Title, StringComparer.OrdinalIgnoreCase).First());
            selectedRows = (preserveRequestedOrder
                    ? legacyRows.OrderBy(row => requestedPosition.GetValueOrDefault(row.WorkId, int.MaxValue))
                    : legacyRows.OrderBy(row => row.LegacySortOrder).ThenBy(row => requestedPosition.GetValueOrDefault(row.WorkId, int.MaxValue)))
                .ToList();
        }

        return selectedRows
            .Select((row, index) => new CollectionItemDto
            {
                Id = DeterministicCollectionItemId(collectionId, row.WorkId),
                WorkId = row.WorkId,
                Title = row.Title,
                Creator = ToNullableText(row.Creator),
                MediaType = row.MediaType,
                CoverUrl = ResolveCollectionItemCoverUrl(row),
                SortOrder = seriesOrdering || preserveRequestedOrder ? index : row.LegacySortOrder,
                DetailRoute = BuildCollectionItemDetailRoute(row),
                MembershipScope = string.IsNullOrWhiteSpace(row.MembershipScope)
                    ? SeriesMembershipScopeNames.MainSequence
                    : row.MembershipScope,
                PositionLabel = seriesOrdering ? row.ToPositionLabel() : null,
            }).ToList();
    }

    private static string? ResolveCollectionItemCoverUrl(GeneratedCollectionItemRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.CoverUrl))
        {
            return row.CoverUrl;
        }

        var isTvParent = string.Equals(row.WorkKind, "parent", StringComparison.OrdinalIgnoreCase)
            && row.MediaType.Contains("TV", StringComparison.OrdinalIgnoreCase);
        return !isTvParent && row.MediaAssetId is { } assetId
            ? $"/stream/{assetId:D}/cover"
            : null;
    }

    private static bool HasSupportedRuleDefinition(Collection collection)
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
        catch (FormatException)
        {
            return false;
        }
    }

    private static string BuildCollectionItemDetailRoute(GeneratedCollectionItemRow row)
    {
        var mediaType = row.MediaType.Trim();
        var isParent = string.Equals(row.WorkKind, "parent", StringComparison.OrdinalIgnoreCase);
        if (mediaType.Contains("TV", StringComparison.OrdinalIgnoreCase))
        {
            return $"/details/tvshow/{row.WorkId:D}?context=watch";
        }

        if (isParent
            && row.StructuralCollectionId.HasValue
            && mediaType.Contains("comic", StringComparison.OrdinalIgnoreCase))
        {
            return $"/details/comicseries/{row.StructuralCollectionId.Value:D}?context=comics";
        }

        var context = IsWatchMediaType(mediaType)
            ? "watch"
            : IsListenMediaType(mediaType)
                ? "listen"
                : "read";
        return $"/details/work/{row.WorkId:D}?context={context}";
    }

    /// <summary>
    /// Intentionally NOT migrated to <see cref="MediaEngine.Domain.Services.Hashing.DeterministicGuid(string)"/>.
    /// That helper hashes a UTF-8 encoded string; this method hashes the raw concatenated
    /// GUID bytes of <paramref name="collectionId"/> and <paramref name="workId"/> directly.
    /// The two are different algorithms that produce different output for the same logical
    /// inputs, and this method's output is persisted/compared as a stored identifier, so
    /// switching it would change previously generated IDs.
    /// </summary>
    private static Guid DeterministicCollectionItemId(Guid collectionId, Guid workId)
    {
        var bytes = collectionId.ToByteArray().Concat(workId.ToByteArray()).ToArray();
        return new Guid(System.Security.Cryptography.MD5.HashData(bytes));
    }

    private async Task<IReadOnlyList<CollectionArtworkItemDto>> GetCollectionArtworkItemsAsync(
        IReadOnlyList<Guid> displayWorkIds,
        int limit,
        IReadOnlySet<Guid>? allowedMemberIds,
        CancellationToken ct)
    {
        // The ids are already owned and allowed display roots; expanding them again would bring back restricted children.
        var workIds = displayWorkIds
            .Distinct()
            .Take(Math.Clamp(limit, 1, 8))
            .ToList();
        if (workIds.Count == 0)
        {
            return [];
        }

        using var conn = db.CreateConnection();
        var visibleWorkPredicate = WorkReadinessSql.ReadyWorkPredicate("w");
        var visibleAssetPredicate = HomeVisibilitySql.VisibleAssetPathPredicate("ma.file_path_root");
        var rows = (await conn.QueryAsync<CollectionArtworkItemRow>(new CommandDefinition(
            $"""
            WITH RECURSIVE work_tree(RootWorkId, WorkId) AS (
                SELECT w.id AS RootWorkId,
                       w.id AS WorkId
                FROM works w
                WHERE w.id IN @WorkIds
                UNION ALL
                SELECT work_tree.RootWorkId,
                       child.id AS WorkId
                FROM works child
                INNER JOIN work_tree ON child.parent_work_id = work_tree.WorkId
            ),
            representative_assets AS (
                SELECT work_tree.RootWorkId AS WorkId,
                       MIN(ma.id) AS AssetId
                FROM work_tree
                INNER JOIN editions e ON e.work_id = work_tree.WorkId
                INNER JOIN media_assets ma ON ma.edition_id = e.id
                WHERE {visibleAssetPredicate}
                  AND {VaultSql.NotVaultAsset("ma.id")}
                  {CollectionAssetAllowList.Clause("work_tree.WorkId", allowedMemberIds)}
                GROUP BY work_tree.RootWorkId
            )
            SELECT w.id AS WorkId,
                   COALESCE(NULLIF(title_work.value, ''), NULLIF(episode_title.value, ''), NULLIF(show_name.value, ''), NULLIF(series_item.item_label, ''), 'Untitled') AS Title,
                   w.media_type AS MediaType,
                   ra.AssetId AS MediaAssetId,
                   preferred_cover.id AS CoverAssetId,
                   preferred_cover.asset_type AS ArtworkType,
                   COALESCE(NULLIF(cover_work.value, ''), NULLIF(cover_asset.value, '')) AS CoverUrl,
                   COALESCE(
                       (SELECT NULLIF(cv.value, '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'short_description' LIMIT 1),
                       (SELECT NULLIF(cv.value, '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'description' LIMIT 1)) AS Description,
                   COALESCE(
                       (SELECT NULLIF(cv.value, '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'release_year' LIMIT 1),
                       (SELECT NULLIF(cv.value, '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'year' LIMIT 1)) AS Year,
                   (SELECT NULLIF(cva.value, '') FROM canonical_value_arrays cva WHERE cva.entity_id = w.id AND cva.key = 'author' ORDER BY cva.ordinal LIMIT 1) AS Author,
                   (SELECT NULLIF(cva.value, '') FROM canonical_value_arrays cva WHERE cva.entity_id = w.id AND cva.key IN ('album_artist', 'artist') ORDER BY CASE cva.key WHEN 'album_artist' THEN 1 ELSE 2 END, cva.ordinal LIMIT 1) AS Artist,
                   COALESCE(
                       (SELECT NULLIF(cv.value, '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'content_rating' LIMIT 1),
                       (SELECT NULLIF(cv.value, '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'certification' LIMIT 1)) AS ContentRating,
                   (SELECT NULLIF(cv.value, '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'runtime' LIMIT 1) AS Runtime,
                   (SELECT NULLIF(cv.value, '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'duration' LIMIT 1) AS Duration,
                   (SELECT NULLIF(cv.value, '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'page_count' LIMIT 1) AS PageCount,
                   COALESCE(
                       (SELECT NULLIF(cv.value, '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'rating' LIMIT 1),
                       (SELECT NULLIF(cv.value, '') FROM canonical_values cv WHERE cv.entity_id = w.id AND cv.key = 'star_rating' LIMIT 1)) AS Rating,
                   COALESCE(NULLIF(primary_work.value, ''), NULLIF(cover_primary_work.value, ''), NULLIF(preferred_cover.primary_hex, '')) AS PrimaryColor,
                   COALESCE(NULLIF(secondary_work.value, ''), NULLIF(cover_secondary_work.value, ''), NULLIF(preferred_cover.secondary_hex, '')) AS SecondaryColor,
                   COALESCE(NULLIF(accent_work.value, ''), NULLIF(dominant_work.value, ''), NULLIF(cover_accent_work.value, ''), NULLIF(preferred_cover.accent_hex, '')) AS AccentColor,
                   COALESCE(NULLIF(preferred_cover.local_image_path_s, ''), NULLIF(preferred_cover.local_image_path_m, ''), NULLIF(preferred_cover.local_image_path, '')) AS LocalImagePath
            FROM works w
            LEFT JOIN representative_assets ra ON ra.WorkId = w.id
            LEFT JOIN entity_assets preferred_cover ON preferred_cover.id = (
                SELECT ea.id
                FROM entity_assets ea
                WHERE ea.entity_id = w.id
                  AND ea.entity_type = 'Work'
                  AND ea.asset_type IN ('CoverArt', 'Background', 'Banner')
                ORDER BY CASE ea.asset_type
                             WHEN 'CoverArt' THEN 0
                             WHEN 'Background' THEN 1
                             ELSE 2
                         END,
                         ea.is_preferred DESC,
                         ea.created_at DESC,
                         ea.id
                LIMIT 1
            )
            LEFT JOIN canonical_values title_work ON title_work.entity_id = w.id AND title_work.key = 'title'
            LEFT JOIN canonical_values episode_title ON episode_title.entity_id = w.id AND episode_title.key = 'episode_title'
            LEFT JOIN canonical_values show_name ON show_name.entity_id = w.id AND show_name.key = 'show_name'
            LEFT JOIN canonical_values cover_asset ON cover_asset.entity_id = ra.AssetId AND cover_asset.key IN ('cover_url', 'cover', 'poster_url', 'poster', 'episode_still_url', 'episode_still', 'still_url', 'still')
            LEFT JOIN canonical_values cover_work ON cover_work.entity_id = w.id AND cover_work.key IN ('cover_url', 'cover', 'poster_url', 'poster', 'episode_still_url', 'episode_still', 'still_url', 'still')
            LEFT JOIN canonical_values primary_work ON primary_work.entity_id = w.id AND primary_work.key = 'artwork_primary_hex'
            LEFT JOIN canonical_values secondary_work ON secondary_work.entity_id = w.id AND secondary_work.key = 'artwork_secondary_hex'
            LEFT JOIN canonical_values accent_work ON accent_work.entity_id = w.id AND accent_work.key = 'artwork_accent_hex'
            LEFT JOIN canonical_values dominant_work ON dominant_work.entity_id = w.id AND dominant_work.key = 'dominant_color'
            LEFT JOIN canonical_values cover_primary_work ON cover_primary_work.entity_id = w.id AND cover_primary_work.key = 'cover_primary_hex'
            LEFT JOIN canonical_values cover_secondary_work ON cover_secondary_work.entity_id = w.id AND cover_secondary_work.key = 'cover_secondary_hex'
            LEFT JOIN canonical_values cover_accent_work ON cover_accent_work.entity_id = w.id AND cover_accent_work.key = 'cover_accent_hex'
            LEFT JOIN series_manifest_items series_item ON series_item.linked_work_id = w.id
            WHERE w.id IN @WorkIds
              AND ({visibleWorkPredicate} OR ra.AssetId IS NOT NULL)
            """,
            new
            {
                WorkIds = workIds.Select(GuidSql.ToBlob).ToArray(),
                AllowedHex = CollectionAssetAllowList.ToJson(allowedMemberIds),
            },
            cancellationToken: ct))).ToList();

        var rowById = rows
            .GroupBy(row => row.WorkId)
            .ToDictionary(grouping => grouping.Key, grouping => grouping.First());

        var artworkItems = workIds
            .Where(rowById.ContainsKey)
            .Select(id =>
            {
                var row = rowById[id];
                return new CollectionArtworkItemDto
                {
                    WorkId = id,
                    Title = string.IsNullOrWhiteSpace(row.Title) ? "Untitled" : row.Title,
                    MediaType = row.MediaType ?? "Unknown",
                    CoverUrl = row.CoverAssetId is { } coverAssetId
                        ? $"/stream/artwork/{coverAssetId:D}"
                        : !string.IsNullOrWhiteSpace(row.CoverUrl)
                            ? row.CoverUrl
                            : row.MediaAssetId is { } mediaAssetId
                                ? $"/stream/{mediaAssetId:D}/cover"
                                : null,
                    Description = row.Description,
                    Facts = DisplayFactBuilder.Build(
                        DisplayMediaRules.NormalizeDisplayKind(row.MediaType ?? string.Empty),
                        row.Title ?? string.Empty,
                        year: row.Year,
                        author: row.Author,
                        artist: row.Artist,
                        contentRating: row.ContentRating,
                        runtime: row.Runtime,
                        duration: row.Duration,
                        pageCount: row.PageCount,
                        starRating: row.Rating),
                    PrimaryColor = row.PrimaryColor,
                    SecondaryColor = row.SecondaryColor,
                    AccentColor = row.AccentColor,
                    ArtworkShape = ArtworkShapeFor(row.ArtworkType, row.MediaType),
                    LocalImagePath = row.LocalImagePath,
                };
            })
            .ToList();

        return SelectCollectionArtworkItems(artworkItems, maximum: 4);
    }

    private static bool IsWatchMediaType(string? mediaType) =>
        string.Equals(mediaType, "Movies", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mediaType, "Movie", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mediaType, "TV", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mediaType, "Video", StringComparison.OrdinalIgnoreCase);

    private static bool IsListenMediaType(string? mediaType) =>
        string.Equals(mediaType, "Music", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mediaType, "Audio", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mediaType, "Audiobooks", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mediaType, "Audiobook", StringComparison.OrdinalIgnoreCase);

    private static bool IsReadMediaType(string? mediaType) =>
        string.Equals(mediaType, "Books", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mediaType, "Book", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mediaType, "Comics", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mediaType, "Comic", StringComparison.OrdinalIgnoreCase);

    private static string ArtworkShapeFor(string? artworkType, string? mediaType)
    {
        if (artworkType?.Contains("Square", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "square";
        }

        if (artworkType?.Contains("Background", StringComparison.OrdinalIgnoreCase) == true
            || artworkType?.Contains("Banner", StringComparison.OrdinalIgnoreCase) == true
            || artworkType?.Contains("Still", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "wide";
        }

        if (IsReadMediaType(mediaType) || IsWatchMediaType(mediaType))
        {
            return "portrait";
        }

        return "square";
    }

    private static IReadOnlyList<CollectionArtworkItemDto> SelectCollectionArtworkItems(
        IReadOnlyList<CollectionArtworkItemDto> items,
        int maximum)
    {
        var available = items
            .Where(item => !string.IsNullOrWhiteSpace(item.CoverUrl))
            .DistinctBy(item => item.WorkId)
            .DistinctBy(item => item.CoverUrl, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (available.Count <= maximum)
        {
            return available;
        }

        // Collections first communicate their cross-media breadth, then fill any
        // remaining slots in the collection's stable display order.
        return available
            .GroupBy(item => DisplayMediaRules.NormalizeDisplayKind(item.MediaType), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Concat(available)
            .DistinctBy(item => item.WorkId)
            .Take(maximum)
            .ToList();
    }

    // Widens slightly versus the old hand-rolled alias set: MediaTypeParser's shared alias
    // table also recognizes "ebook"/"epub" (-> Books) and "tv show"/"tv shows" (-> TV), which
    // this method's previous local switch did not. Nullability is preserved deliberately (via
    // TryParse rather than Parse) since callers here rely on null meaning "unparseable".
    private static MediaType? TryParseMediaType(string? mediaType) =>
        MediaTypeParser.TryParse(mediaType, out var parsed) ? parsed : null;

    private static ArtworkShape? TryParseArtworkShape(string? shape) => shape?.Trim().ToLowerInvariant() switch
    {
        "square" => ArtworkShape.Square,
        "portrait" => ArtworkShape.Portrait,
        "wide" or "landscape" => ArtworkShape.Wide,
        _ => null,
    };

    private sealed class CollectionArtworkItemRow
    {
        public Guid WorkId { get; init; }
        public string? Title { get; init; }
        public string? MediaType { get; init; }
        public Guid? MediaAssetId { get; init; }
        public Guid? CoverAssetId { get; init; }
        public string? ArtworkType { get; init; }
        public string? CoverUrl { get; init; }
        public string? Description { get; init; }
        public string? Year { get; init; }
        public string? Author { get; init; }
        public string? Artist { get; init; }
        public string? ContentRating { get; init; }
        public string? Runtime { get; init; }
        public string? Duration { get; init; }
        public string? PageCount { get; init; }
        public string? Rating { get; init; }
        public string? PrimaryColor { get; init; }
        public string? SecondaryColor { get; init; }
        public string? AccentColor { get; init; }
        public string? LocalImagePath { get; init; }
    }

    private sealed class CollectionMediaMetadataRow
    {
        public string MediaType { get; init; } = string.Empty;
        public string? Year { get; init; }
    }

    private sealed class GeneratedCollectionItemRow
    {
        public Guid WorkId { get; init; }
        public string Title { get; init; } = string.Empty;
        public object? Creator { get; init; }
        public string MediaType { get; init; } = string.Empty;
        public string WorkKind { get; init; } = string.Empty;
        public Guid? MediaAssetId { get; init; }
        public string? CoverUrl { get; init; }
        public Guid? StructuralCollectionId { get; init; }
        public int LegacySortOrder { get; init; }
        public double? WorkOrdinalSort { get; init; }
        public double? ManifestOrdinal { get; init; }
        public string? ManifestOrdinalScopeQid { get; init; }
        public string? ManifestSeriesQid { get; init; }
        public string? ManifestRawOrdinal { get; init; }
        public string? MembershipScope { get; init; }
        public int IsExpandedFromCollection { get; init; }
        public string? PublicationDate { get; init; }
        public string? RetailSeriesPosition { get; init; }

        /// <summary>The Work's stored position in this shelf's Series (its <c>series_placement</c> pair), when it has one.</summary>
        public string? SeriesPlacementPosition { get; init; }

        // The Work's own number in this Series (its stored pair) first, then today's single-number chain.
        private double? OwnOrdinalSort => SeriesPositionPairs.SortValueOf(SeriesPlacementPosition) ?? WorkOrdinalSort;

        public SeriesOrderInput ToOrderInput() => SeriesOrdering.BuildInput(
            MembershipScope,
            ManifestOrdinal,
            ManifestOrdinalScopeQid,
            ManifestSeriesQid,
            OwnOrdinalSort,
            PublicationDate,
            Title);

        public string? ToPositionLabel() => SeriesOrdering.DisplayNumber(
            MembershipScope,
            SeriesOrdering.OrdinalBelongsToSeries(ManifestOrdinalScopeQid, ManifestSeriesQid) ? ManifestRawOrdinal : null,
            string.IsNullOrWhiteSpace(SeriesPlacementPosition) ? RetailSeriesPosition : SeriesPlacementPosition,
            OwnOrdinalSort);
    }

    private sealed class CollectionDisplayWorkRow
    {
        public Guid WorkId { get; init; }
        public Guid? SourceWorkId { get; init; }
    }

    private sealed record CollectionCatalogAggregation(string Key, string? Label);

    private sealed record CollectionManagementCatalogCandidate(
        Collection Collection,
        CollectionCatalogClassification Classification,
        CollectionCatalogAggregation? Grouping,
        IReadOnlyList<Guid> WorkIds,
        int ItemCount,
        CollectionMediaCounts MediaCounts);
}
