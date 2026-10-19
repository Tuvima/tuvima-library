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
/// <param name="WillBeDeleted">True when every title in the collection is in the request, so deleting them would leave it empty.</param>
public sealed record LibraryItemDeletionCollection(Guid CollectionId, string Name, int ItemsInRequest, bool WillBeDeleted);

/// <summary>What else is tied to the requested titles besides their files.</summary>
/// <param name="ProfilesWithPersonalState">Distinct profiles with progress, history, bookmarks, highlights, saved items, ratings or queue entries for the titles or their files.</param>
/// <param name="ProcessingRunning">True when a queued or running identification or enrichment job targets one of the titles or their files.</param>
public sealed record LibraryItemDeletionRelatedFacts(
    IReadOnlyList<LibraryItemDeletionCollection> CuratedCollections,
    int ProfilesWithPersonalState,
    bool ProcessingRunning);
