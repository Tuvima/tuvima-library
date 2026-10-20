using MediaEngine.Contracts.Library;
using MediaEngine.Domain.Authorization;

namespace MediaEngine.Api.Services.Libraries.Moves;

/// <summary>Who asked for a move. A preview works only for the person (and credential) who asked for it.</summary>
public sealed record LibraryMoveActor(RequestAuthority Authority, Guid? CredentialId, string DisplayName);

/// <summary>Whether the caller may edit one item or destination (resource scope; the route's policy has already admitted the person).</summary>
public delegate ValueTask<bool> LibraryMoveAccess(Guid id, CancellationToken ct);

public enum LibraryMoveRefusalKind
{
    /// <summary>An item or the destination does not exist (any more).</summary>
    NotFound,

    /// <summary>The request is not one the Engine allows: wrong kind, a taken entry matched, a missing choice.</summary>
    Invalid,

    /// <summary>The caller may not edit one of the items or the destination.</summary>
    Unavailable,

    /// <summary>The review is unknown, expired, or belongs to another person.</summary>
    Expired,

    /// <summary>Something changed since the preview. Nothing was moved; <see cref="LibraryMoveRefusal.FreshPreview"/> shows what is true now.</summary>
    Changed,
}

public sealed record LibraryMoveRefusal(
    LibraryMoveRefusalKind Kind,
    string Message,
    IReadOnlyList<Guid> Ids,
    LibraryMovePreview? FreshPreview);

public sealed class LibraryMoveResult<T> where T : class
{
    private LibraryMoveResult(T? value, LibraryMoveRefusal? refusal)
    {
        Value = value;
        Refusal = refusal;
    }

    public T? Value { get; }

    public LibraryMoveRefusal? Refusal { get; }

    public bool IsSuccess => Refusal is null;

    public static LibraryMoveResult<T> Ok(T value) => new(value, null);

    public static LibraryMoveResult<T> Refused(LibraryMoveRefusal refusal) => new(null, refusal);

    public static LibraryMoveResult<T> Refused(LibraryMoveRefusalKind kind, string message, IReadOnlyList<Guid>? ids = null) =>
        new(null, new LibraryMoveRefusal(kind, message, ids ?? [], null));
}

/// <summary>Moves items into another album, season, series, audiobook or hand-picked collection through a Match step.</summary>
public interface ILibraryMoveService
{
    /// <summary>The places this item can go. Only places that accept its kind are offered (Details overlay D15).</summary>
    Task<LibraryMoveResult<LibraryMoveDestinationsResponse>> GetDestinationsAsync(
        Guid itemId, string? query, LibraryMoveAccess canEdit, CancellationToken ct = default);

    /// <summary>Works out the Match step and what a move would do. Changes nothing.</summary>
    Task<LibraryMoveResult<LibraryMovePreview>> PreviewAsync(
        LibraryMovePreviewRequest request, LibraryMoveActor actor, LibraryMoveAccess canEdit, CancellationToken ct = default);

    /// <summary>Moves the items as the person chose, if the preview they confirmed is still true. Otherwise changes nothing and returns a fresh preview.</summary>
    Task<LibraryMoveResult<LibraryMoveCommitResult>> CommitAsync(
        LibraryMoveCommitRequest request, LibraryMoveActor actor, LibraryMoveAccess canEdit, CancellationToken ct = default);
}
