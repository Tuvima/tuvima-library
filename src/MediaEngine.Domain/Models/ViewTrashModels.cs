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
