using MediaEngine.Contracts.ProfileState;
using MediaEngine.Domain.Enums;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Playback;

public sealed record SavedItemMembership(ProfileEntityKind EntityKind, Guid EntityId, bool IsSaved);

/// <summary>What a bulk remove or restore really did: the titles it changed, and whether any call to the Engine failed along the way.</summary>
public sealed record SavedItemsBatchResult(IReadOnlyList<ProfileSavedItemDto> Done, bool Failed);

public sealed class SavedItemService(IEngineApiClient apiClient, DashboardSessionAccessor? session = null)
{
    private readonly object _cacheGate = new();
    private Guid? _cachedProfileId;
    private Task<IReadOnlyList<ProfileSavedItemDto>>? _cachedList;

    public event Action? Changed;

    /// <summary>
    /// Raised when a save or removal made from a cover or button could not be done, so a page that shows My List can say so in place
    /// (spec my-list: "My List could not be changed. Try again."). Listeners must not treat it as a change to the list.
    /// </summary>
    public event Action? ChangeFailed;

    /// <summary>Tells listeners that a save or removal failed (see <see cref="ChangeFailed"/>).</summary>
    public void ReportChangeFailed() => ChangeFailed?.Invoke();

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

    /// <summary>
    /// A fresh read of the saved list straight from the Engine, or null when the call failed. <see cref="GetListAsync"/> cannot tell a failed
    /// read from an empty list, so a page that compares lists (My List's Undo for a cover's own removal) must use this one.
    /// </summary>
    public async Task<IReadOnlyList<ProfileSavedItemDto>?> TryGetListAsync(CancellationToken ct = default)
    {
        var result = await apiClient.GetSavedItemsAsync(ct);
        return ReadFailed(SavedItemsRead) ? null : result;
    }

    /// <summary>The saved titles this profile has finished (what "Remove finished" would take), or null when the Engine call failed.</summary>
    public async Task<IReadOnlyList<ProfileSavedItemDto>?> TryGetFinishedAsync(CancellationToken ct = default)
    {
        var result = await apiClient.GetFinishedSavedItemsAsync(ct);
        return ReadFailed(FinishedSavedItemsRead) ? null : result;
    }

    // The client remembers the last call that failed anywhere in this session, so "did this read fail" must ask about this read by name:
    // an older failure of some other call (a missing cover, a failed Continue read) says nothing about the saved list.
    private const string SavedItemsRead = "Saved profile items";
    private const string FinishedSavedItemsRead = "Finished saved profile items";

    private bool ReadFailed(string read) =>
        apiClient.LastError is not null && string.Equals(apiClient.LastFailedEndpoint, read, StringComparison.Ordinal);

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
    /// Takes several titles off My List at once (Select, Remove finished), 500 to a call. <see cref="SavedItemsBatchResult.Done"/> is what was
    /// really removed, with each saved date, so <see cref="RestoreManyAsync"/> can put them back in the same order; <see cref="SavedItemsBatchResult.Failed"/>
    /// is true when any call failed, including a later one after earlier ones went through. The cached list is dropped and <see cref="Changed"/>
    /// raised whenever something changed, so every Add to My List button shows the right state.
    /// </summary>
    public async Task<SavedItemsBatchResult> RemoveManyAsync(IReadOnlyList<ProfileSavedItemKeyDto> items, CancellationToken ct = default)
    {
        var removed = new List<ProfileSavedItemDto>();
        var failed = false;
        foreach (var batch in items.Chunk(RemoveSavedItemsRequest.MaxItems))
        {
            if (await apiClient.RemoveSavedItemsAsync(batch, ct) is not { } done)
            {
                failed = true;
                break;
            }

            removed.AddRange(done);
        }

        if (removed.Count > 0)
        {
            InvalidateList();
            Changed?.Invoke();
        }

        return new(removed, failed);
    }

    /// <summary>Puts titles back on My List with their original saved dates (Undo); reports what was restored and whether any call failed, like <see cref="RemoveManyAsync"/>.</summary>
    public async Task<SavedItemsBatchResult> RestoreManyAsync(IReadOnlyList<ProfileSavedItemDto> items, CancellationToken ct = default)
    {
        var restored = new List<ProfileSavedItemDto>();
        var failed = false;
        foreach (var batch in items.Chunk(RestoreSavedItemsRequest.MaxItems))
        {
            if (await apiClient.RestoreSavedItemsAsync(batch, ct) is not { } done)
            {
                failed = true;
                break;
            }

            restored.AddRange(done);
        }

        if (restored.Count > 0)
        {
            InvalidateList();
            Changed?.Invoke();
        }

        return new(restored, failed);
    }
}
