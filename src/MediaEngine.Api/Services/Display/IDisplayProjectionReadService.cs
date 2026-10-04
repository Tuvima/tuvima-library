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

}

internal interface IRawDisplayProjectionReadService : IDisplayProjectionReadService
{
    Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(
        Guid? profileId,
        IReadOnlySet<Guid> allowedWorkIds,
        CancellationToken ct) => LoadHomeCollectionsAsync(profileId, ct);
}
