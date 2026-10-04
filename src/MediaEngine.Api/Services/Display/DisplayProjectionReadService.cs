using Microsoft.Extensions.Caching.Memory;

namespace MediaEngine.Api.Services.Display;

public sealed class DisplayProjectionReadService : IRawDisplayProjectionReadService
{
    private const int HomeProjectionLimit = 1_000;
    private static readonly TimeSpan ProjectionCacheDuration = TimeSpan.FromSeconds(30);
    private readonly DisplayWorkProjectionReader _works;
    private readonly DisplayJourneyProjectionReader _journey;
    private readonly DisplayFavoriteProjectionReader _favorites;
    private readonly DisplayHomeCollectionProjectionReader _homeCollections;
    private readonly IMemoryCache _cache;

    public DisplayProjectionReadService(
        DisplayWorkProjectionReader works,
        DisplayJourneyProjectionReader journey,
        DisplayFavoriteProjectionReader favorites,
        DisplayHomeCollectionProjectionReader homeCollections,
        IMemoryCache cache)
    {
        _works = works;
        _journey = journey;
        _favorites = favorites;
        _homeCollections = homeCollections;
        _cache = cache;
    }

    public async Task<IReadOnlyList<DisplayWorkRow>> LoadWorksAsync(CancellationToken ct)
    {
        const string cacheKey = "display:works:all";
        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<DisplayWorkRow>? cached) && cached is not null)
        {
            return cached;
        }

        var rows = await _works.LoadAsync(ct);
        _cache.Set(cacheKey, rows, ProjectionCacheDuration);
        return rows;
    }

    public Task<IReadOnlyList<DisplayWorkRow>> LoadDetailWorksAsync(Guid id, CancellationToken ct) =>
        _works.LoadAsync(ct, detailId: id);

    public async Task<IReadOnlyList<DisplayWorkRow>> LoadHomeWorksAsync(CancellationToken ct)
    {
        const string cacheKey = "display:works:home";
        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<DisplayWorkRow>? cached) && cached is not null)
        {
            return cached;
        }

        var rows = await _works.LoadAsync(ct, HomeProjectionLimit);
        _cache.Set(cacheKey, rows, ProjectionCacheDuration);
        return rows;
    }

    public Task<IReadOnlyList<DisplayJourneyRow>> LoadJourneyAsync(Guid? profileId, string? lane, CancellationToken ct) =>
        _journey.LoadAsync(profileId, lane, ct);

    public Task<IReadOnlyList<DisplayJourneyRow>> LoadStatesAsync(Guid? profileId, string? lane, CancellationToken ct) =>
        _journey.LoadAsync(profileId, lane, ct, includeCompleted: true);

    public async Task<IReadOnlySet<Guid>> LoadFavoriteWorkIdsAsync(Guid? profileId, CancellationToken ct)
    {
        var cacheKey = $"display:favorites:{profileId?.ToString("N") ?? "shared"}";
        if (_cache.TryGetValue(cacheKey, out IReadOnlySet<Guid>? cached) && cached is not null)
        {
            return cached;
        }

        var rows = await _favorites.LoadAsync(profileId, ct);
        _cache.Set(cacheKey, rows, ProjectionCacheDuration);
        return rows;
    }

    public async Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(Guid? profileId, CancellationToken ct)
    {
        var cacheKey = $"display:home-collections:{profileId?.ToString("N") ?? "shared"}";
        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<DisplayHomeCollectionRow>? cached) && cached is not null)
        {
            return cached;
        }

        var rows = await _homeCollections.LoadAsync(profileId, ct);
        _cache.Set(cacheKey, rows, ProjectionCacheDuration);
        return rows;
    }

    public Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(
        Guid? profileId,
        IReadOnlySet<Guid> allowedWorkIds,
        CancellationToken ct) => _homeCollections.LoadAsync(profileId, ct, allowedWorkIds);
}


