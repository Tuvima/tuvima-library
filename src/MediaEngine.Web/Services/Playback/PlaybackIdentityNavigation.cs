using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Playback;

public interface IPlaybackIdentityNavigationSink
{
    Task<ListenPlaybackCommandReplyDto?> NavigateIdentityAsync(ListenPlaybackSnapshot snapshot, string kind, Guid id,
        CancellationToken ct = default);
}

public static class PlaybackIdentityNavigation
{
    public static string? Route(ListenQueueItem? item, string? kind, Guid id)
    {
        if (item is null || id == Guid.Empty) return null;
        return kind switch
        {
            "album" when item.AlbumWorkId == id => ListenPlaybackIdentityRoutes.Album(item),
            "audiobook" when (item.AudiobookWorkId ?? item.AlbumWorkId ?? item.WorkId) == id => ListenPlaybackIdentityRoutes.Audiobook(item),
            "artist" when item.ArtistPersonId == id => ListenPlaybackIdentityRoutes.Artist(item),
            "person" when item.Authors.Concat(item.Narrators).Any(person => person.PersonId == id) => $"/details/person/{id:D}",
            "playlist" when item.PlaylistId == id => ListenPlaybackIdentityRoutes.Playlist(item),
            "collection" when item.CollectionId == id && item.PlaylistId != id => $"/details/collection/{id:D}",
            _ => null,
        };
    }
}

/// <summary>Rechecks both current playback membership and Engine access before a main-window SPA navigation.</summary>
public sealed class PlaybackIdentityNavigationOwner(PlaybackSessionController playback, IEngineApiClient api,
    IUserPlaybackPreferencesAccessor preferences)
{
    public async Task<string?> ResolveAsync(ListenPlaybackCommandDto command, CancellationToken ct)
    {
        var snapshot = playback.CreateSnapshot();
        var item = playback.CurrentItem;
        bool Current() => !playback.IsDismissed && item is not null && command.ProfileId is Guid profile && profile != Guid.Empty
            && preferences.ActiveProfileId == profile && snapshot.ProfileId == profile
            && command.WorkId == item.WorkId && command.ExpectedAssetId == item.AssetId && item.AssetId is not null
            && command.ExpectedPlaybackRequestVersion == snapshot.PlaybackRequestVersion
            && playback.PlaybackRequestVersion == snapshot.PlaybackRequestVersion
            && playback.CurrentItem?.WorkId == item.WorkId && playback.CurrentItem?.AssetId == item.AssetId;
        if (!Current() || command.IdentityId is not Guid id || PlaybackIdentityNavigation.Route(item, command.IdentityKind, id) is not { } route) return null;
        try
        {
            if (command.IdentityKind == "playlist")
            {
                var collection = await api.GetCollectionSummaryAsync(id, command.ProfileId, ct);
                if (collection is null || collection.Id != id || !collection.CollectionType.Equals("Playlist", StringComparison.OrdinalIgnoreCase)) return null;
            }
            else
            {
                var type = command.IdentityKind switch
                {
                    "album" => DetailEntityType.MusicAlbum,
                    "audiobook" => DetailEntityType.Audiobook,
                    "artist" or "person" => DetailEntityType.Person,
                    "collection" => DetailEntityType.Collection,
                    _ => (DetailEntityType?)null,
                };
                if (type is null) return null;
                var detail = await api.GetDetailPageAsync(type.Value, id, DetailPresentationContext.Listen, profileId: command.ProfileId, ct: ct);
                if (detail is null || !Guid.TryParse(detail.Id, out var returnedId) || returnedId != id || detail.EntityType != type) return null;
            }
        }
        catch (Exception) { return null; }
        return Current() && PlaybackIdentityNavigation.Route(playback.CurrentItem, command.IdentityKind, id) == route ? route : null;
    }
}
