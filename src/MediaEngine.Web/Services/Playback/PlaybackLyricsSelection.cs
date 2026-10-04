using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Playback;

public sealed record PlaybackLyricsIdentity(Guid? ProfileId, Guid WorkId, Guid AssetId, long RequestVersion)
{
    public static PlaybackLyricsIdentity? From(ListenPlaybackSnapshot snapshot) =>
        snapshot.ProfileId is Guid profile && profile != Guid.Empty && !snapshot.IsDismissed && snapshot.CurrentIndex >= 0 && snapshot.CurrentIndex < snapshot.Queue.Count
        && snapshot.Queue[snapshot.CurrentIndex] is { WorkId: var work, AssetId: Guid asset } item
        && work != Guid.Empty && asset != Guid.Empty && item.PlaybackExperience == PlaybackExperience.Music
            ? new(snapshot.ProfileId, work, asset, snapshot.PlaybackRequestVersion) : null;
}

public sealed record PlaybackLyricsSelectionProjection(Guid OwnerId, PlaybackLyricsIdentity Identity, Guid TrackId);

public static class ListenPlaybackPresentationActions
{
    public const string SelectLyrics = "select-lyrics";
    public const string NavigateIdentity = "navigate-identity";
    public const string RegisterPopup = "register-popup";
}

public interface IPlaybackLyricsSelectionSink
{
    Task<ListenPlaybackCommandReplyDto?> SelectLyricsAsync(ListenPlaybackSnapshot snapshot, Guid trackId,
        CancellationToken ct = default);
}

/// <summary>Only the existing command owner can publish a temporary authorized lyric choice.</summary>
public sealed class PlaybackLyricsSelectionOwner(PlaybackSessionController playback, IEngineApiClient api)
{
    public async Task<bool> SelectAsync(Guid ownerId, ListenPlaybackCommandDto command, CancellationToken ct)
    {
        var identity = PlaybackLyricsIdentity.From(playback.CreateSnapshot());
        bool Current() => identity is not null && identity.ProfileId is Guid profile && profile != Guid.Empty
            && command.ProfileId == profile && command.WorkId == identity.WorkId
            && command.ExpectedAssetId == identity.AssetId && command.ExpectedPlaybackRequestVersion == identity.RequestVersion
            && identity == PlaybackLyricsIdentity.From(playback.CreateSnapshot());
        if (!Current() || command.LyricTrackId is not Guid trackId || trackId == Guid.Empty) return false;
        IReadOnlyList<TextTrackDto> tracks;
        try { tracks = await api.GetTextTracksAsync(identity!.AssetId, ct); }
        catch (Exception) { return false; }
        if (!Current()) return false;
        if (!tracks.Any(track => track.Id == trackId && track.Kind.Equals("Lyrics", StringComparison.OrdinalIgnoreCase)))
        {
            playback.SetLyricsSelection(null);
            return false;
        }
        playback.SetLyricsSelection(new(ownerId, identity, trackId));
        return true;
    }
}
