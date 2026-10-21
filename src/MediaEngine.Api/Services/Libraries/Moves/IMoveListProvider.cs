using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.Libraries.Moves;

/// <summary>
/// One entry in a destination's own list (a track, an episode, a position, a part), with what committing a choice of it needs.
/// </summary>
/// <param name="Key">Names the entry in a commit. Stable while the list is unchanged.</param>
/// <param name="Number">What the person reads: "2", "1.5", "S0 E3", "D2 T5".</param>
/// <param name="IsTaken">The entry already has a file.</param>
/// <param name="TakenByItemId">The item holding the entry, when known.</param>
/// <param name="CanAddVersion">The entry is taken and a file can be added to it as another version. Not so for an entry owned elsewhere.</param>
/// <param name="Target">Where an item matched to this entry is placed.</param>
public sealed record MoveListEntry(
    string Key,
    string Number,
    string Title,
    double? LengthSeconds,
    bool IsTaken,
    Guid? TakenByItemId,
    string? TakenByTitle,
    bool CanAddVersion,
    LibraryMoveTarget Target);

/// <summary>
/// What an item can be matched against in one kind of destination. Each media type's destination registers one, or declares it has
/// no list (<see cref="NoListMoveProvider"/>): a list means the Match step (Details overlay D12), no list means free order (D14). A
/// Series is the exception: it has no list to match against and takes only Add anyway (D20).
/// No type needs its own move screen.
/// </summary>
public interface IMoveListProvider
{
    /// <summary>Which kind of list this is on the wire (a <c>LibraryMoveListKinds</c> value).</summary>
    string ListKind { get; }

    /// <summary>True when this provider serves the destination.</summary>
    bool Supports(LibraryMoveDestinationInfo destination);

    /// <summary>The destination's own list, owned and known-missing entries, in the order a person reads them.</summary>
    Task<IReadOnlyList<MoveListEntry>> GetEntriesAsync(LibraryMoveDestinationInfo destination, CancellationToken ct = default);

    /// <summary>
    /// True when the destination has no list and takes no free move either: the only way in is Add anyway (Details overlay D20). A
    /// Series is built from each title's match, never by hand, so the person can only force a title into one, unnumbered.
    /// </summary>
    bool AddAnywayOnly => false;

    /// <summary>A plain line for the person about a destination that takes only Add anyway. Null otherwise.</summary>
    string? Note => null;
}

/// <summary>Finds the provider for a destination. A destination no provider names has no list: moving into it is free.</summary>
public sealed class MoveListRegistry(IEnumerable<IMoveListProvider> providers)
{
    private readonly IReadOnlyList<IMoveListProvider> _providers = [.. providers];

    private static readonly NoListMoveProvider NoList = new();

    public IMoveListProvider Resolve(LibraryMoveDestinationInfo destination) =>
        _providers.FirstOrDefault(provider => provider.Supports(destination)) ?? NoList;
}

/// <summary>Nothing known to match against (hand-picked collections, and any kind of place that declares no list): the move is free.</summary>
public sealed class NoListMoveProvider : IMoveListProvider
{
    public string ListKind => MediaEngine.Contracts.Library.LibraryMoveListKinds.None;

    public bool Supports(LibraryMoveDestinationInfo destination) => destination.Kind == LibraryMoveKinds.Collection;

    public Task<IReadOnlyList<MoveListEntry>> GetEntriesAsync(LibraryMoveDestinationInfo destination, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MoveListEntry>>([]);
}
