using MediaEngine.Api.Services.Collections;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

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
    private readonly SharedListCache _shared;

    public DisplayProjectionReadService(
        DisplayWorkProjectionReader works,
        DisplayVisibleWorkReader visibleWorks,
        DisplayJourneyProjectionReader journey,
        DisplayFavoriteProjectionReader favorites,
        DisplayHomeCollectionProjectionReader homeCollections,
        IMemoryCache cache,
        ILogger<DisplayProjectionReadService>? logger = null,
        TimeProvider? time = null)
    {
        _works = works;
        _visibleWorks = visibleWorks;
        _journey = journey;
        _favorites = favorites;
        _homeCollections = homeCollections;
        _cache = cache;
        _shared = new SharedListCache(cache, logger, time);
    }

    public async Task<IReadOnlyList<DisplayWorkRow>> LoadWorksAsync(CancellationToken ct)
    {
        return await _shared.GetAsync("display:works:all", () => _works.LoadAsync(CancellationToken.None), ct);
    }


    public async Task<IReadOnlyList<DisplayVisibleWorkRow>> LoadVisibleWorkRowsAsync(CancellationToken ct)
    {
        return (await LoadVisibleSnapshotAsync(ct)).Rows;
    }

    // The rows and the ids built from them are one entry, so they are always the same age and can never disagree (the access
    // checks compare their counts).
    private Task<VisibleSnapshot> LoadVisibleSnapshotAsync(CancellationToken ct) =>
        _shared.GetAsync(
            "display:visible-work-snapshot",
            async () =>
            {
                var rows = await _visibleWorks.LoadAsync(CancellationToken.None);
                return new VisibleSnapshot(rows, rows.Select(row => row.WorkId).ToHashSet());
            },
            ct);

    private sealed record VisibleSnapshot(IReadOnlyList<DisplayVisibleWorkRow> Rows, IReadOnlySet<Guid> Ids);

    public async Task<IReadOnlySet<Guid>> LoadVisibleWorkIdsAsync(CancellationToken ct)
    {
        return (await LoadVisibleSnapshotAsync(ct)).Ids;
    }

    public Task<IReadOnlyList<DisplayWorkRow>> LoadDetailWorksAsync(Guid id, CancellationToken ct) =>
        _works.LoadAsync(ct, detailId: id);

    public async Task<IReadOnlyList<DisplayWorkRow>> LoadHomeWorksAsync(CancellationToken ct)
    {
        return await _shared.GetAsync("display:works:home", () => _works.LoadAsync(CancellationToken.None, HomeProjectionLimit), ct);
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


