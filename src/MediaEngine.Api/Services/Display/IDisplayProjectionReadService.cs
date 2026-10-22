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

    /// <summary>Which libraries the current request may see files from. Unscoped sources allow every library.</summary>
    Task<DisplayLibraryScope> LoadLibraryScopeAsync(CancellationToken ct) => Task.FromResult(DisplayLibraryScope.All);

    /// <summary>
    /// Which libraries the current request may browse: the same rule that decides which works it sees (library grants, the lane's feature,
    /// the content limit's video rule, the Vault), asked of a whole library instead of one work. Unscoped sources allow every library.
    /// </summary>
    Task<DisplayLibraryAccess> LoadLibraryAccessAsync(CancellationToken ct) => Task.FromResult(DisplayLibraryAccess.Everything);
}

/// <summary>Whether a request may browse a library, given the library's id and the media type it holds.</summary>
public sealed class DisplayLibraryAccess(Func<string, string, bool> allows)
{
    public static readonly DisplayLibraryAccess Everything = new((_, _) => true);

    public static readonly DisplayLibraryAccess Nothing = new((_, _) => false);

    public bool Allows(string libraryId, string mediaType) => allows(libraryId, mediaType);
}

/// <summary>The libraries a request may see: every library, or exactly the granted ones.</summary>
public sealed record DisplayLibraryScope(bool AllLibraries, IReadOnlySet<Guid> Libraries)
{
    public static readonly DisplayLibraryScope All = new(true, new HashSet<Guid>());

    public bool Allows(Guid libraryId) => AllLibraries || Libraries.Contains(libraryId);
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
