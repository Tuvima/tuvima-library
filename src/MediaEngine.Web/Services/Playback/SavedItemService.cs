using MediaEngine.Contracts.ProfileState;
using MediaEngine.Domain.Enums;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Playback;

public sealed record SavedItemMembership(ProfileEntityKind EntityKind, Guid EntityId, bool IsSaved);

public sealed class SavedItemService(IEngineApiClient apiClient, DashboardSessionAccessor? session = null)
{
    private readonly object _cacheGate = new();
    private Guid? _cachedProfileId;
    private Task<IReadOnlyList<ProfileSavedItemDto>>? _cachedList;

    public event Action? Changed;

    /// <summary>
    /// One Engine call per profile per circuit: Home, hero slides and detail pages all ask "is this saved?",
    /// so the list is fetched once, shared while in flight, and dropped when a save changes or the profile switches.
    /// </summary>
    public Task<IReadOnlyList<ProfileSavedItemDto>> GetListAsync(CancellationToken ct = default)
    {
        Task<IReadOnlyList<ProfileSavedItemDto>> list;
        lock (_cacheGate)
        {
            var profileId = session?.ActiveProfileId;
            if (_cachedList is null || _cachedList.IsFaulted || _cachedList.IsCanceled || _cachedProfileId != profileId)
            {
                _cachedProfileId = profileId;
                _cachedList = LoadListAsync();
            }

            list = _cachedList;
        }

        return list.WaitAsync(ct);
    }

    // The API client turns a failed or timed-out call into an empty list. Caching that would show
    // "nothing saved" for the whole circuit (and make Add to My List remove a saved item), so a result
    // that came back with an error is dropped from the cache and refetched on the next ask.
    private async Task<IReadOnlyList<ProfileSavedItemDto>> LoadListAsync()
    {
        var result = await apiClient.GetSavedItemsAsync(CancellationToken.None);
        if (apiClient.LastError is not null)
        {
            InvalidateList();
        }

        return result;
    }

    private void InvalidateList()
    {
        lock (_cacheGate)
        {
            _cachedList = null;
        }
    }

    public async Task<SavedItemMembership> GetMembershipAsync(
        ProfileEntityKind entityKind,
        Guid entityId,
        CancellationToken ct = default)
    {
        var list = await GetListAsync(ct);
        return new SavedItemMembership(entityKind, entityId, list.Any(item => item.EntityKind == entityKind && item.EntityId == entityId));
    }

    public async Task<SavedItemMembership?> ToggleAsync(
        ProfileEntityKind entityKind,
        Guid entityId,
        CancellationToken ct = default)
    {
        // Toggle checks the Engine directly so a stale cache can never flip the wrong way.
        var existing = await apiClient.GetSavedItemAsync(entityKind, entityId, ct);
        var current = new SavedItemMembership(entityKind, entityId, existing is not null);
        var succeeded = current.IsSaved
            ? await apiClient.RemoveSavedItemAsync(entityKind, entityId, ct)
            : await apiClient.SaveItemAsync(entityKind, entityId, ct) is not null;
        if (!succeeded)
        {
            return null;
        }

        InvalidateList();
        Changed?.Invoke();
        return current with { IsSaved = !current.IsSaved };
    }

    /// <summary>
    /// Takes several titles off My List at once (Select, Remove finished). Returns what was really removed, with each saved date, so
    /// <see cref="RestoreManyAsync"/> can put them back in the same order; null when nothing could be removed. The cached list is dropped
    /// and <see cref="Changed"/> raised, so every Add to My List button shows the right state.
    /// </summary>
    public async Task<IReadOnlyList<ProfileSavedItemDto>?> RemoveManyAsync(IReadOnlyList<ProfileSavedItemKeyDto> items, CancellationToken ct = default)
    {
        var removed = new List<ProfileSavedItemDto>();
        foreach (var batch in items.Chunk(RemoveSavedItemsRequest.MaxItems))
        {
            if (await apiClient.RemoveSavedItemsAsync(batch, ct) is not { } done)
            {
                break;
            }

            removed.AddRange(done);
        }

        if (removed.Count == 0)
        {
            return null;
        }

        InvalidateList();
        Changed?.Invoke();
        return removed;
    }

    /// <summary>Puts titles back on My List with their original saved dates (Undo). Returns what was restored; null when nothing could be.</summary>
    public async Task<IReadOnlyList<ProfileSavedItemDto>?> RestoreManyAsync(IReadOnlyList<ProfileSavedItemDto> items, CancellationToken ct = default)
    {
        var restored = new List<ProfileSavedItemDto>();
        foreach (var batch in items.Chunk(RestoreSavedItemsRequest.MaxItems))
        {
            if (await apiClient.RestoreSavedItemsAsync(batch, ct) is not { } done)
            {
                break;
            }

            restored.AddRange(done);
        }

        if (restored.Count == 0)
        {
            return null;
        }

        InvalidateList();
        Changed?.Invoke();
        return restored;
    }
}
