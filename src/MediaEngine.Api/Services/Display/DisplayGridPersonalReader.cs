using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Collections;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Api.Services.Display;

/// <summary>One thing a profile saved or liked, and when.</summary>
public sealed record DisplayGridPersonalEntry(ProfileEntityKind Kind, Guid EntityId, DateTimeOffset At);

/// <summary>
/// The active profile's own My List and Liked lists for the "View all" grids. It only ever reads the profile it is given
/// (the caller passes the signed-in profile from the claim) and returns the rows in the order the grid shows them.
/// </summary>
public sealed class DisplayGridPersonalReader(
    IProfileStateRepository state,
    IProfileRepository profiles,
    CollectionCatalogReadService collections)
{
    /// <summary>My List: everything the profile saved except songs (those belong to Favorites), newest first.</summary>
    public async Task<IReadOnlyList<DisplayGridPersonalEntry>> LoadSavedAsync(Guid profileId, CancellationToken ct) =>
        (await state.GetSavedItemsAsync(profileId, ct).ConfigureAwait(false))
            .Where(item => item.ProfileId == profileId && item.EntityKind != ProfileEntityKind.Song)
            .OrderByDescending(item => item.SavedAt)
            .Select(item => new DisplayGridPersonalEntry(item.EntityKind, item.EntityId, item.SavedAt))
            .ToList();

    /// <summary>Liked: everything the profile marked Like or Love, newest first.</summary>
    public async Task<IReadOnlyList<DisplayGridPersonalEntry>> LoadLikedAsync(Guid profileId, CancellationToken ct) =>
        (await state.GetReactionsAsync(profileId, ct).ConfigureAwait(false))
            .Where(item => item.ProfileId == profileId && item.Reaction is ProfileReactionKind.Like or ProfileReactionKind.Love)
            .OrderByDescending(item => item.UpdatedAt)
            .Select(item => new DisplayGridPersonalEntry(item.EntityKind, item.EntityId, item.UpdatedAt))
            .ToList();

    /// <summary>The managed collections and playlists this profile may open, by id; item counts only include what it may see.</summary>
    public async Task<IReadOnlyDictionary<Guid, ManagedCollectionDto>> LoadPlaylistsAsync(
        Guid profileId,
        IReadOnlySet<Guid> allowedWorkIds,
        CancellationToken ct)
    {
        var profile = await profiles.GetByIdAsync(profileId, ct).ConfigureAwait(false);
        if (profile is null)
        {
            return new Dictionary<Guid, ManagedCollectionDto>();
        }

        return (await collections.GetManagedAsync(profile, ct, allowedWorkIds).ConfigureAwait(false))
            .GroupBy(item => item.Id)
            .ToDictionary(group => group.Key, group => group.First());
    }
}
