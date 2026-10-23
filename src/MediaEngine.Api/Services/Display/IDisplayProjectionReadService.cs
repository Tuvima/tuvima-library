namespace MediaEngine.Api.Services.Display;

public interface IDisplayProjectionReadService
{
    Task<IReadOnlyList<DisplayWorkRow>> LoadWorksAsync(CancellationToken ct);
    Task<IReadOnlyList<DisplayWorkRow>> LoadDetailWorksAsync(Guid id, CancellationToken ct) => LoadWorksAsync(ct);

    Task<IReadOnlyList<DisplayWorkRow>> LoadHomeWorksAsync(CancellationToken ct) => LoadWorksAsync(ct);

    /// <summary>
    /// The ids of every work the current request may see: exactly the work ids <see cref="LoadWorksAsync"/> returns, without
    /// reading any display value. For callers that only check membership.
    /// </summary>
    async Task<IReadOnlySet<Guid>> LoadVisibleWorkIdsAsync(CancellationToken ct) =>
        (await LoadWorksAsync(ct).ConfigureAwait(false)).Select(work => work.WorkId).ToHashSet();

    Task<IReadOnlyList<DisplayJourneyRow>> LoadJourneyAsync(
        Guid? profileId,
        string? lane,
        CancellationToken ct);

    Task<IReadOnlyList<DisplayJourneyRow>> LoadStatesAsync(Guid? profileId, string? lane, CancellationToken ct);

    Task<IReadOnlySet<Guid>> LoadFavoriteWorkIdsAsync(
        Guid? profileId,
        CancellationToken ct);

    Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(
        Guid? profileId,
        CancellationToken ct);

    /// <summary>Which libraries the current request may see files from (every one except those hidden from the account). Unscoped sources allow every library.</summary>
    Task<DisplayLibraryScope> LoadLibraryScopeAsync(CancellationToken ct) => Task.FromResult(DisplayLibraryScope.All);

    /// <summary>
    /// Which libraries the current request may browse: the same rule that decides which works it sees (libraries hidden from the account, the lane's feature,
    /// the content limit's video rule, the Vault), asked of a whole library instead of one work. A source that does not answer allows
    /// nothing, so a stand-in can never open a library by accident; a wrapper must forward it.
    /// </summary>
    Task<DisplayLibraryAccess> LoadLibraryAccessAsync(CancellationToken ct) => Task.FromResult(DisplayLibraryAccess.Nothing);

    /// <summary>
    /// Every (work, library) row the request may see, before <see cref="LoadWorksAsync"/> keeps one row per work. Narrowing by library or by
    /// Unmatched state looks here, so a work whose first row is in another library is not lost. A source that does not answer gives the
    /// one-row-per-work list, which never shows more than <see cref="LoadWorksAsync"/> does.
    /// </summary>
    Task<IReadOnlyList<DisplayWorkRow>> LoadWorkLibraryRowsAsync(CancellationToken ct) => LoadWorksAsync(ct);

    // Unmatched files live only under the Unmatched filter (Library setup spec D64): the reads above leave out a work whose files in a
    // library are all Unmatched, kept or not. These three are the filter's own, which keep it; nothing else asks for them. A source
    // that does not answer gives the plain read, which never shows more.

    /// <summary><see cref="LoadWorksAsync"/> with the works whose files are all Unmatched kept in, for the Unmatched filter.</summary>
    Task<IReadOnlyList<DisplayWorkRow>> LoadWorksIncludingUnmatchedAsync(CancellationToken ct) => LoadWorksAsync(ct);

    /// <summary><see cref="LoadVisibleWorkIdsAsync"/> with the works whose files are all Unmatched kept in, for the Unmatched filter.</summary>
    Task<IReadOnlySet<Guid>> LoadVisibleWorkIdsIncludingUnmatchedAsync(CancellationToken ct) => LoadVisibleWorkIdsAsync(ct);

    /// <summary><see cref="LoadWorkLibraryRowsAsync"/> with the works whose files are all Unmatched kept in, for the Unmatched filter.</summary>
    Task<IReadOnlyList<DisplayWorkRow>> LoadWorkLibraryRowsIncludingUnmatchedAsync(CancellationToken ct) => LoadWorkLibraryRowsAsync(ct);
}

/// <summary>Whether a request may browse a library, given the library's id and the media type it holds.</summary>
public sealed class DisplayLibraryAccess(Func<string, string, bool> allows)
{
    public static readonly DisplayLibraryAccess Nothing = new((_, _) => false);

    public bool Allows(string libraryId, string mediaType) => allows(libraryId, mediaType);
}

/// <summary>The libraries a request may see: every library except the ones hidden from the account (simpler access D2). The areas are checked by the work lists themselves.</summary>
public sealed record DisplayLibraryScope(IReadOnlySet<Guid> HiddenLibraries, bool DeniesEverything = false)
{
    public static readonly DisplayLibraryScope All = new(new HashSet<Guid>());

    /// <summary>A request with no usable authority: no library at all.</summary>
    public static readonly DisplayLibraryScope Nothing = new(new HashSet<Guid>(), true);

    public bool Allows(Guid libraryId) => !DeniesEverything && !HiddenLibraries.Contains(libraryId);
}

internal interface IRawDisplayProjectionReadService : IDisplayProjectionReadService
{
    /// <summary>
    /// Every visible (work, library) pair with the fields the visibility rules need, before any profile filter.
    /// The same pairs <see cref="IDisplayProjectionReadService.LoadWorksAsync"/> returns.
    /// </summary>
    async Task<IReadOnlyList<DisplayVisibleWorkRow>> LoadVisibleWorkRowsAsync(CancellationToken ct) =>
        (await LoadWorksAsync(ct).ConfigureAwait(false))
        .Select(work => new DisplayVisibleWorkRow
        {
            WorkId = work.WorkId,
            LibraryId = work.LibraryId,
            MediaType = work.MediaType,
            ContentRating = work.ContentRating,
        })
        .ToList();

    Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(
        Guid? profileId,
        IReadOnlySet<Guid> allowedWorkIds,
        CancellationToken ct) => LoadHomeCollectionsAsync(profileId, ct);
}
