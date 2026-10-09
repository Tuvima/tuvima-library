using MediaEngine.Contracts.ProfileState;
using MediaEngine.Domain.Enums;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Playback;

public sealed record SavedItemMembership(ProfileEntityKind EntityKind, Guid EntityId, bool IsSaved);

public sealed class SavedItemService(IEngineApiClient apiClient)
{
    public event Action? Changed;

    public Task<IReadOnlyList<ProfileSavedItemDto>> GetListAsync(CancellationToken ct = default) =>
        apiClient.GetSavedItemsAsync(ct);

    public async Task<SavedItemMembership> GetMembershipAsync(
        ProfileEntityKind entityKind,
        Guid entityId,
        CancellationToken ct = default)
    {
        var item = await apiClient.GetSavedItemAsync(entityKind, entityId, ct);
        return new SavedItemMembership(entityKind, entityId, item is not null);
    }

    public async Task<SavedItemMembership?> ToggleAsync(
        ProfileEntityKind entityKind,
        Guid entityId,
        CancellationToken ct = default)
    {
        var current = await GetMembershipAsync(entityKind, entityId, ct);
        var succeeded = current.IsSaved
            ? await apiClient.RemoveSavedItemAsync(entityKind, entityId, ct)
            : await apiClient.SaveItemAsync(entityKind, entityId, ct) is not null;
        if (!succeeded)
        {
            return null;
        }

        Changed?.Invoke();
        return current with { IsSaved = !current.IsSaved };
    }
}
