using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.ProfileState;
using MediaEngine.Domain.Enums;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.MediaTiles;

/// <summary>
/// The personal pages' lookup from "an id the profile saved, liked or finished" to the tile that shows it. It is built
/// from reads the Engine has already filtered for the active profile (the item lookup, the finished cards, the Collection
/// catalogue and the Playlist list), so an item the profile may not see never gets a tile. For Me and My Library share it.
/// </summary>
public sealed class PersonalTileIndex
{
    private const string DocumentKind = "Document";

    private readonly IReadOnlyList<MediaTileViewModel> _media;
    private readonly IReadOnlyList<DisplayCardDto> _finished;
    private readonly IReadOnlyDictionary<Guid, CollectionManagementCatalogViewModel> _collections;
    private readonly IReadOnlyDictionary<Guid, ManagedCollectionViewModel> _playlists;

    public PersonalTileIndex(
        IEnumerable<DisplayCardDto> cards,
        IEnumerable<DisplayCardDto> finished,
        IEnumerable<CollectionManagementCatalogViewModel> collectionCatalog,
        IEnumerable<ManagedCollectionViewModel> managedCollections)
    {
        _media = cards
            .GroupBy(card => card.Id)
            .Select(group => MediaTileComposerService.FromDisplayCard(group.First()))
            .ToList();
        // Finished items: the Engine marks a card Completed with the time the profile last touched it. Music has no finished state.
        _finished = finished
            .Where(card => card.ContinuationState == DisplayContinuationState.Completed && card.Progress?.LastAccessed is not null)
            .GroupBy(card => card.Id)
            .Select(group => group.First())
            .OrderByDescending(card => card.Progress!.LastAccessed)
            .ToList();
        _collections = collectionCatalog.GroupBy(item => item.Id).ToDictionary(group => group.Key, group => group.First());
        _playlists = managedCollections.GroupBy(item => item.Id).ToDictionary(group => group.Key, group => group.First());
    }

    /// <summary>Every media tile the item lookup returned, one per card.</summary>
    public IReadOnlyList<MediaTileViewModel> Media => _media;

    /// <summary>True for a tile that stands for a Document (a profile that may not see Documents never meets one).</summary>
    public static bool IsDocument(MediaTileViewModel tile) =>
        string.Equals(tile.MediaKind, DocumentKind, StringComparison.OrdinalIgnoreCase);

    /// <summary>The ids My List needs from the item lookup. Collections and Playlists come from their own reads; saved songs belong to Favorites.</summary>
    public static IReadOnlyList<Guid> SavedMediaIds(IEnumerable<ProfileSavedItemDto> saved) =>
        saved
            .Where(item => item.EntityKind is not (ProfileEntityKind.Song or ProfileEntityKind.Collection or ProfileEntityKind.Playlist))
            .Select(item => item.EntityId)
            .Distinct()
            .ToList();

    /// <summary>The ids Liked (and Favorites) need from the item lookup. A liked Collection comes from the Collection catalogue.</summary>
    public static IReadOnlyList<Guid> ReactionMediaIds(IEnumerable<ProfileReactionDto> reactions) =>
        reactions
            .Where(item => item.Reaction is ProfileReactionKind.Like or ProfileReactionKind.Love)
            .Where(item => item.EntityKind != ProfileEntityKind.Collection)
            .Select(item => item.EntityId)
            .Distinct()
            .ToList();

    /// <summary>
    /// Asks the Engine for the cards behind a list of ids (in calls of at most <see cref="DisplayItemsRequest.MaxIds"/>). The Engine leaves out
    /// every id this profile may not see. A failed call throws, so the section shows its quiet notice instead of an empty row.
    /// </summary>
    public static async Task<IReadOnlyList<DisplayCardDto>> LookupAsync(IEngineApiClient api, IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var page = await api.GetDisplayItemsAsync(ids, ct) ?? throw new InvalidOperationException("The Engine could not look up the items.");
        return FlattenCards(page).ToList();
    }

    public MediaTileViewModel? FindSaved(ProfileSavedItemDto saved)
    {
        if (saved.EntityKind == ProfileEntityKind.Collection && _collections.TryGetValue(saved.EntityId, out var collection))
        {
            return CollectionSurfaceTileComposer.FromCollection(collection);
        }

        if (saved.EntityKind == ProfileEntityKind.Playlist && _playlists.TryGetValue(saved.EntityId, out var playlist))
        {
            return MediaTileComposerService.FromPlaylist(playlist);
        }

        return FindMedia(saved.EntityId);
    }

    public MediaTileViewModel? FindReaction(ProfileReactionDto reaction)
    {
        if (reaction.EntityKind == ProfileEntityKind.Collection && _collections.TryGetValue(reaction.EntityId, out var collection))
        {
            return CollectionSurfaceTileComposer.FromCollection(collection);
        }

        return FindMedia(reaction.EntityId);
    }

    public MediaTileViewModel? FindMedia(Guid id) =>
        _media.FirstOrDefault(item => item.Id == id || item.WorkId == id || item.RepresentativeEntityId == id);

    /// <summary>My List: what the profile saved for later, newest first. Saved songs belong to Favorites, so they are left out.</summary>
    public IReadOnlyList<MediaTileViewModel> Saved(IEnumerable<ProfileSavedItemDto> saved, bool documentsAllowed) =>
        Resolve(saved
            .Where(item => item.EntityKind != ProfileEntityKind.Song)
            .OrderByDescending(item => item.SavedAt)
            .Select(FindSaved), documentsAllowed);

    /// <summary>Liked: everything the profile marked Like or Love, newest first.</summary>
    public IReadOnlyList<MediaTileViewModel> Liked(IEnumerable<ProfileReactionDto> reactions, bool documentsAllowed) =>
        Resolve(reactions
            .Where(item => item.Reaction is ProfileReactionKind.Like or ProfileReactionKind.Love)
            .OrderByDescending(item => item.UpdatedAt)
            .Select(FindReaction), documentsAllowed);

    /// <summary>History: items the profile finished watching, reading or listening to, most recent first.</summary>
    public IReadOnlyList<MediaTileViewModel> History(bool documentsAllowed) =>
        Resolve(_finished
            // The finished mark is the point of this row, so the tile carries no progress bar.
            .Select(card => (MediaTileViewModel?)MediaTileComposerService.FromDisplayCard(card with { Progress = null })), documentsAllowed);

    public static IReadOnlyList<MediaTileViewModel> Visible(IEnumerable<MediaTileViewModel> tiles, bool documentsAllowed) =>
        Resolve(tiles.Select(tile => (MediaTileViewModel?)tile), documentsAllowed);

    public static IEnumerable<DisplayCardDto> FlattenCards(DisplayPageDto? page) => page is null
        ? []
        : page.Catalog.Concat(page.Shelves.SelectMany(shelf => shelf.Items)).DistinctBy(card => card.Id);

    private static IReadOnlyList<MediaTileViewModel> Resolve(IEnumerable<MediaTileViewModel?> tiles, bool documentsAllowed) =>
        tiles
            .Where(tile => tile is not null && (documentsAllowed || !IsDocument(tile)))
            .Cast<MediaTileViewModel>()
            .DistinctBy(tile => (tile.Id, tile.CollectionId))
            .ToList();
}
