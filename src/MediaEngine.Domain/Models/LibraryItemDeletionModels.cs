using MediaEngine.Domain.Contracts;

namespace MediaEngine.Domain.Models;

/// <summary>One media file that belongs to a title being considered for deletion.</summary>
public sealed record LibraryItemDeletionAsset(Guid AssetId, string FilePath, string ContentHash);

/// <summary>A title (Work) being considered for deletion, with every media file under its editions. A title without files has an empty asset list.</summary>
public sealed record LibraryItemDeletionWork(
    Guid WorkId,
    string WorkKind,
    bool IsCatalogOnly,
    string MediaType,
    string Title,
    IReadOnlyList<LibraryItemDeletionAsset> Assets);

/// <summary>A hand-picked (Manual) collection that holds at least one of the requested titles.</summary>
/// <param name="ItemsInRequest">How many of the requested titles the collection holds.</param>
/// <param name="WillBeDeleted">True when the collection will be deleted with the titles: a curated (Custom, stored-member) collection with no personal-media source whose every title is in the request. Playlists and other hand-picked groupings are never deleted.</param>
public sealed record LibraryItemDeletionCollection(Guid CollectionId, string Name, int ItemsInRequest, bool WillBeDeleted);

/// <summary>What else is tied to the requested titles besides their files.</summary>
/// <param name="ProfilesWithPersonalState">Distinct profiles with progress, history, bookmarks, highlights, saved items, ratings or queue entries for the titles or their files.</param>
/// <param name="ProcessingRunning">True when a queued or running identification or enrichment job targets one of the titles or their files.</param>
public sealed record LibraryItemDeletionRelatedFacts(
    IReadOnlyList<LibraryItemDeletionCollection> CuratedCollections,
    int ProfilesWithPersonalState,
    bool ProcessingRunning);

/// <summary>What removing the titles' records took with it.</summary>
/// <param name="EditionIds">The editions that were removed with the titles.</param>
/// <param name="AssetIds">The media assets that were removed with the titles.</param>
/// <param name="CollectionsDeleted">Hand-picked collections that were left empty and deleted (the cleanup sweeps every empty one; only collections actually removed are listed).</param>
/// <param name="StillReferencedFiles">The full path of every Tuvima-owned file a remaining row still points at. A folder that holds one of these is not Tuvima's to remove.</param>
/// <param name="TuvimaOwnedFiles">Files Tuvima itself wrote for the titles (pictures only they used, text tracks, cached images) that are now unreferenced. The caller deletes them after the data was removed, and only inside Tuvima's own data folders.</param>
public sealed record LibraryItemDeletionDataResult(
    IReadOnlyList<Guid> EditionIds,
    IReadOnlyList<Guid> AssetIds,
    IReadOnlyList<RemovedCollection> CollectionsDeleted,
    IReadOnlyList<string> TuvimaOwnedFiles,
    IReadOnlyCollection<string> StillReferencedFiles);

/// <summary>One part of a group (a season of a show, a sub-series of a Series) and how much of it the library owns.</summary>
public sealed record LibraryItemDeletionSubGroup(Guid SubGroupId, string Title, int OwnedCount, int TotalCount);

/// <summary>
/// A show, season, album or Series resolved to the titles the library owns inside it (spec Delete a show, season, album or Series, D5, D6).
/// </summary>
/// <param name="Kind">One of <c>show</c>, <c>season</c>, <c>album</c> or <c>series</c>.</param>
/// <param name="OwnedWorkIds">Every owned title (never a group, never a catalog-only row) under the group, in a stable order.</param>
/// <param name="NotOwnedCount">Titles the group lists that the library does not own; they are never deleted or counted.</param>
/// <param name="SubGroups">The parts of the group that hold at least one owned title (the seasons of a show).</param>
/// <param name="ParentTitle">For a season, its show; null otherwise.</param>
/// <param name="ParentOwnedCount">For a season, how many owned titles its show holds in all, so the Engine can tell it is the last season.</param>
public sealed record LibraryItemDeletionGroup(
    Guid GroupId,
    string Kind,
    string Title,
    string MediaType,
    IReadOnlyList<Guid> OwnedWorkIds,
    int NotOwnedCount,
    IReadOnlyList<LibraryItemDeletionSubGroup> SubGroups,
    string? ParentTitle,
    int ParentOwnedCount,
    Guid? ParentGroupId = null);
