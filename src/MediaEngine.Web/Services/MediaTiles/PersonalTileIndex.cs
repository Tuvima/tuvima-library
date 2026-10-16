using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.ProfileState;
using MediaEngine.Domain.Enums;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.MediaTiles;

/// <summary>
/// The personal pages' lookup from "an id the profile saved, liked or finished" to the tile that shows it. It is built
/// from reads the Engine has already filtered for the active profile (the lane browse pages, the Collection catalogue
/// and the Playlist list), so an item the profile may not see never gets a tile. For Me and My Library share it.
/// </summary>
public sealed class PersonalTileIndex
{
    private const string DocumentKind = "Document";

    private readonly IReadOnlyList<MediaTileViewModel> _media;
    private readonly IReadOnlyList<DisplayCardDto> _finished;
    private readonly IReadOnlyDictionary<Guid, CollectionManagementCatalogViewModel> _collections;
    private readonly IReadOnlyDictionary<Guid, ManagedCollectionViewModel> _playlists;

    public PersonalTileIndex(
        IEnumerable<DisplayPageDto?> pages,
        IEnumerable<CollectionManagementCatalogViewModel> collectionCatalog,
        IEnumerable<ManagedCollectionViewModel> managedCollections)
    {
        var cards = pages
            .Where(page => page is not null)
            .SelectMany(page => FlattenCards(page))
            .GroupBy(card => card.Id)
            .Select(group => group.First())
            .ToList();

        _media = cards.Select(MediaTileComposerService.FromDisplayCard).ToList();
        // Finished items: the Engine marks a card Completed with the time the profile last touched it. Music has no finished state.
        _finished = cards
            .Where(card => card.ContinuationState == DisplayContinuationState.Completed && card.Progress?.LastAccessed is not null)
            .OrderByDescending(card => card.Progress!.LastAccessed)
            .ToList();
        _collections = collectionCatalog.GroupBy(item => item.Id).ToDictionary(group => group.Key, group => group.First());
        _playlists = managedCollections.GroupBy(item => item.Id).ToDictionary(group => group.Key, group => group.First());
    }

    /// <summary>Every media tile in the lane reads, one per card.</summary>
    public IReadOnlyList<MediaTileViewModel> Media => _media;

    /// <summary>True for a tile that stands for a Document (a profile that may not see Documents never meets one).</summary>
    public static bool IsDocument(MediaTileViewModel tile) =>
        string.Equals(tile.MediaKind, DocumentKind, StringComparison.OrdinalIgnoreCase);

    public MediaTileViewModel? FindSaved(ProfileSavedItemDto saved)
    {
        if (saved.EntityKind == ProfileEntityKind.Collection && _collections.TryGetValue(saved.EntityId, out var collection))
            return CollectionSurfaceTileComposer.FromCollection(collection);
        if (saved.EntityKind == ProfileEntityKind.Playlist && _playlists.TryGetValue(saved.EntityId, out var playlist))
            return MediaTileComposerService.FromPlaylist(playlist);
        return FindMedia(saved.EntityId);
    }

    public MediaTileViewModel? FindReaction(ProfileReactionDto reaction)
    {
        if (reaction.EntityKind == ProfileEntityKind.Collection && _collections.TryGetValue(reaction.EntityId, out var collection))
            return CollectionSurfaceTileComposer.FromCollection(collection);
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
