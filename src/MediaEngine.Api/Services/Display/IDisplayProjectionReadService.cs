namespace MediaEngine.Api.Services.Display;

public interface IDisplayProjectionReadService
{
    Task<IReadOnlyList<DisplayWorkRow>> LoadWorksAsync(CancellationToken ct);
    Task<IReadOnlyList<DisplayWorkRow>> LoadDetailWorksAsync(Guid id, CancellationToken ct) => LoadWorksAsync(ct);

    Task<IReadOnlyList<DisplayWorkRow>> LoadHomeWorksAsync(CancellationToken ct) => LoadWorksAsync(ct);

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
}

/// <summary>The libraries a request may see: every library, or exactly the granted ones.</summary>
public sealed record DisplayLibraryScope(bool AllLibraries, IReadOnlySet<Guid> Libraries)
{
    public static readonly DisplayLibraryScope All = new(true, new HashSet<Guid>());

    public bool Allows(Guid libraryId) => AllLibraries || Libraries.Contains(libraryId);
}

internal interface IRawDisplayProjectionReadService : IDisplayProjectionReadService
{
    Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(
        Guid? profileId,
        IReadOnlySet<Guid> allowedWorkIds,
        CancellationToken ct) => LoadHomeCollectionsAsync(profileId, ct);
}
