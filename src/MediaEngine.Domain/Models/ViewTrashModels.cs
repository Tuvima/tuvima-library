namespace MediaEngine.Domain.Models;

/// <summary>
/// One place on disk a file record is indexed at, inside the item's own library. <see cref="SourceId"/> is null when the
/// View source it came from has since been removed.
/// </summary>
public sealed record ViewTrashFileSource(Guid SourceRowId, Guid? SourceId, string FilePath);

/// <summary>One file record an item uses (in <see cref="Role"/>), with every place it is indexed at inside the item's library.</summary>
public sealed record ViewTrashFile(
    Guid FileId,
    string Role,
    long ByteSize,
    IReadOnlyList<ViewTrashFileSource> Sources);

/// <summary>A personal item in its owner's Trash with every file it uses.</summary>
public sealed record ViewTrashItem(
    Guid ItemId,
    string MediaKind,
    Guid LibraryId,
    IReadOnlyList<ViewTrashFile> Files);

/// <summary>A file path of a trashed item that another item also uses (the same path, in the other item's own library).</summary>
public sealed record ViewTrashPathUse(Guid ItemId, string FilePath, Guid OtherItemId);

/// <summary>A Manual Gallery that holds a trashed item.</summary>
public sealed record ViewTrashGalleryMembership(Guid GalleryId, string Name, Guid ItemId);

/// <summary>How many of a person's trashed items are of each kind, and the size of the files they use.</summary>
public sealed record ViewTrashTotals(
    int ItemCount,
    int ImageCount,
    int VideoCount,
    int DocumentCount,
    int OtherCount,
    long Bytes);

/// <summary>
/// What Empty Trash asks the data store to remove for one item. <see cref="PlannedSourceRowIds"/> is every indexed place
/// (<c>local_file_sources</c> row) the plan saw under the item's files, whatever was decided for it; <see cref="RemovedSourceRowIds"/>
/// is the subset whose files were removed from disk or were already gone. Places for files kept on disk are never in the second list.
/// </summary>
public sealed record ViewTrashPurgeRequest(
    Guid ItemId,
    Guid PersonalSpaceId,
    Guid OwnerProfileId,
    IReadOnlyCollection<Guid> PlannedSourceRowIds,
    IReadOnlyCollection<Guid> RemovedSourceRowIds);

/// <summary>What happened when an item's records were removed.</summary>
public enum ViewTrashPurgeOutcome
{
    /// <summary>The item and everything tied to it is gone from the data store.</summary>
    Removed,

    /// <summary>The item is not in this person's Trash any more (restored, or gone), so nothing was changed.</summary>
    NotInTrash,

    /// <summary>The item now has indexed places the plan did not see, so nothing was changed.</summary>
    ChangedSincePlanned,

    /// <summary>Shared Library work on the item started since the plan was made, so nothing was changed.</summary>
    SharedWorkInFlight,
}

/// <summary>The outcome of removing a group of items; <see cref="ItemId"/> names the item that stopped it, when one did.</summary>
public sealed record ViewTrashPurgeResult(ViewTrashPurgeOutcome Outcome, Guid? ItemId = null);
