using MediaEngine.Domain.Models;

namespace MediaEngine.Domain.Contracts;

/// <summary>The facts the Engine needs to preview moving items into another album, season, series, audiobook or collection, and the one write that moves them.</summary>
public interface ILibraryMoveRepository
{
    /// <summary>
    /// Loads the requested items that exist: each id is a title (a Work) or an audiobook part (an Edition). Ids that are
    /// neither, and personal View items, are simply absent from the answer.
    /// </summary>
    Task<IReadOnlyList<LibraryMoveItem>> GetItemsAsync(IReadOnlyList<Guid> itemIds, CancellationToken ct = default);

    /// <summary>
    /// Places that accept the item's kind (never one it already sits in), whose name or parent's name contains
    /// <paramref name="query"/> when given, A to Z, at most <paramref name="limit"/>.
    /// </summary>
    Task<IReadOnlyList<LibraryMoveDestinationRow>> FindDestinationsAsync(
        LibraryMoveItem item,
        string? query,
        int limit,
        CancellationToken ct = default);

    /// <summary>One destination by id (an album, season, series or audiobook Work, or a hand-picked collection), or null.</summary>
    Task<LibraryMoveDestinationInfo?> GetDestinationAsync(Guid destinationId, CancellationToken ct = default);


    /// <summary>
    /// True when the destination is one of the Works, or sits anywhere beneath one of them (a season is beneath its show). Moving a
    /// container into itself or into what it holds would make a loop.
    /// </summary>
    Task<bool> IsSameOrBeneathAsync(Guid destinationId, IReadOnlyList<Guid> workIds, CancellationToken ct = default);

    /// <summary>The Works under an album, season, series or audiobook (its tracks, episodes, issues, volumes), with whether each has a file.</summary>
    Task<IReadOnlyList<LibraryMoveChild>> GetChildrenAsync(Guid parentWorkId, CancellationToken ct = default);

    /// <summary>The files (Editions) of an audiobook with the part number each carries.</summary>
    Task<IReadOnlyList<LibraryMovePart>> GetPartsAsync(Guid audiobookWorkId, CancellationToken ct = default);

    /// <summary>
    /// The places leaving would empty. For each item's source place (and the place above it), whether nothing else would be
    /// left in it once the given items are gone, with the name to show. Items sitting in no place contribute nothing.
    /// </summary>
    Task<IReadOnlyList<LibraryMoveSourceFacts>> GetSourcePlacesAsync(
        IReadOnlyList<LibraryMoveItem> items,
        Guid? sourceCollectionId,
        CancellationToken ct = default);

    /// <summary>
    /// A hand-picked collection a move leaves: its name, and whether it would be left with nothing in it once the items are
    /// gone. Null when it is not a hand-picked collection that holds any of them.
    /// </summary>
    Task<LibraryMoveSourceFacts?> GetSourceCollectionAsync(
        Guid collectionId,
        IReadOnlyList<LibraryMoveItem> items,
        CancellationToken ct = default);

    /// <summary>
    /// In one transaction, checks every row again (the item still where the preview saw it with the same identity revision, nothing
    /// running for it, the entry still free or still taken, no details the person set would be lost) and then places the items.
    /// Any row that fails the check stops the whole move and changes nothing. Places left empty are removed in the same transaction.
    /// </summary>
    Task<LibraryMoveCommitOutcome> CommitAsync(
        LibraryMovePlan plan,
        Guid? sourceCollectionId,
        CancellationToken ct = default);
}
