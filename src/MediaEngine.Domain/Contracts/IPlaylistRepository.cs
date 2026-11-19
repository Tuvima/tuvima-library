namespace MediaEngine.Domain.Contracts;

/// <summary>What one batch add did: how many songs joined the playlist and how many were already in it.</summary>
public sealed record PlaylistAddResult(int Added, int AlreadyThere);

/// <summary>
/// Playlist actions that must work at any playlist size (spec listen-playlists WP1): moving one item to a new place,
/// adding a batch of works with an exact "already there" count, the per-profile pin and the total length. Each reads and
/// writes in SQL, never through a size-capped read of the items.
/// </summary>
public interface IPlaylistRepository
{
    /// <summary>
    /// Moves one item of a playlist to <paramref name="position"/> (1 is the top; a number past the end means the bottom) and
    /// renumbers <c>sort_order</c> to 1..N in one transaction. Returns <c>false</c> when the item is not in the playlist.
    /// </summary>
    Task<bool> MoveItemAsync(Guid collectionId, Guid itemId, int position, CancellationToken ct = default);

    /// <summary>
    /// Appends each work that is not already in the playlist, in the given order, after the last item. A work that is
    /// listed twice in <paramref name="workIds"/> counts once. A work the data store refuses (a Vault work) is neither
    /// added nor counted as already there.
    /// </summary>
    Task<PlaylistAddResult> AddWorksAsync(Guid collectionId, IReadOnlyList<Guid> workIds, CancellationToken ct = default);

    /// <summary>Pins or unpins a playlist for one profile only.</summary>
    Task SetPinnedAsync(Guid profileId, Guid collectionId, bool pinned, CancellationToken ct = default);

    /// <summary>The playlists one profile has pinned.</summary>
    Task<IReadOnlySet<Guid>> GetPinnedCollectionIdsAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>
    /// The summed length in seconds of the songs in each playlist whose length is known, by collection id. A playlist with no
    /// known song length is absent; a song with no stored length adds nothing to its playlist's total.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, double>> GetTotalDurationSecondsAsync(
        IReadOnlyCollection<Guid> collectionIds,
        CancellationToken ct = default);
}
