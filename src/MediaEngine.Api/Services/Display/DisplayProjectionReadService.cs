using MediaEngine.Api.Services.Collections;
using Microsoft.Extensions.Caching.Memory;

namespace MediaEngine.Api.Services.Display;

public sealed class DisplayProjectionReadService : IRawDisplayProjectionReadService
{
    private const int HomeProjectionLimit = 1_000;
    private static readonly TimeSpan ProjectionCacheDuration = TimeSpan.FromSeconds(30);
    private readonly DisplayWorkProjectionReader _works;
    private readonly DisplayVisibleWorkReader _visibleWorks;
    private readonly DisplayJourneyProjectionReader _journey;
    private readonly DisplayFavoriteProjectionReader _favorites;
    private readonly DisplayHomeCollectionProjectionReader _homeCollections;
    private readonly IMemoryCache _cache;

    public DisplayProjectionReadService(
        DisplayWorkProjectionReader works,
        DisplayVisibleWorkReader visibleWorks,
        DisplayJourneyProjectionReader journey,
        DisplayFavoriteProjectionReader favorites,
        DisplayHomeCollectionProjectionReader homeCollections,
        IMemoryCache cache)
    {
        _works = works;
        _visibleWorks = visibleWorks;
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

    public async Task<IReadOnlyList<DisplayVisibleWorkRow>> LoadVisibleWorkRowsAsync(CancellationToken ct)
    {
        const string cacheKey = "display:visible-work-rows";
        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<DisplayVisibleWorkRow>? cached) && cached is not null)
        {
            return cached;
        }

        var rows = await _visibleWorks.LoadAsync(ct);
        _cache.Set(cacheKey, rows, ProjectionCacheDuration);
        return rows;
    }

    public async Task<IReadOnlySet<Guid>> LoadVisibleWorkIdsAsync(CancellationToken ct)
    {
        const string cacheKey = "display:visible-work-ids";
        if (_cache.TryGetValue(cacheKey, out IReadOnlySet<Guid>? cached) && cached is not null)
        {
            return cached;
        }

        var ids = (await LoadVisibleWorkRowsAsync(ct)).Select(row => row.WorkId).ToHashSet();
        _cache.Set(cacheKey, ids, ProjectionCacheDuration);
        return ids;
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

    // Cached per profile and location for the same 30 seconds as the other projections; saving a "Show on" choice or a
    // profile's hide changes CollectionLanePolicy.Version, which is part of the key, so a change is never held back.
    public async Task<IReadOnlyList<DisplayCollectionLaneRow>> LoadCollectionLanesAsync(
        Guid? profileId,
        string location,
        CancellationToken ct)
    {
        var cacheKey = $"display:collection-lanes:{CollectionLanePolicy.Version}:{location}:{profileId?.ToString("N") ?? "shared"}";
        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<DisplayCollectionLaneRow>? cached) && cached is not null)
        {
            return cached;
        }

        var rows = await _homeCollections.LoadLanesAsync(profileId, location, ct);
        _cache.Set(cacheKey, rows, ProjectionCacheDuration);
        return rows;
    }

    public Task<IReadOnlyList<DisplayCollectionLaneRow>> LoadCollectionLanesAsync(
        Guid? profileId,
        string location,
        IReadOnlySet<Guid> allowedWorkIds,
        CancellationToken ct) => _homeCollections.LoadLanesAsync(profileId, location, ct, allowedWorkIds);
}


