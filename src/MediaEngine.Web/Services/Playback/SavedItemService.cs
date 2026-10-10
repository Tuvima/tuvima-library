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
                _cachedList = apiClient.GetSavedItemsAsync(CancellationToken.None);
            }

            list = _cachedList;
        }

        return list.WaitAsync(ct);
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
}
