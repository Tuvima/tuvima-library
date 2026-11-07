using System.Text.Json.Serialization;
using MediaEngine.Domain.Services;

namespace MediaEngine.Web.Services.Playback;

/// <summary>Everything <see cref="PlaybackSessionMetadata"/> is built from.</summary>
/// <param name="Media">Which of the four layouts the item follows.</param>
/// <param name="Item">The item that is playing.</param>
/// <param name="BaseUri">The Dashboard's address, so artwork addresses are absolute for the operating system.</param>
/// <param name="ShowTitle">The show's name for a TV episode once the presentation context has loaded.</param>
/// <param name="ChapterTitle">An audiobook's current chapter name.</param>
/// <param name="SkipBackSeconds">The profile's skip back length (10 s unless the profile chose another).</param>
/// <param name="SkipForwardSeconds">The profile's skip forward length (30 s unless the profile chose another).</param>
/// <param name="HasNextEpisode">True when the queue holds an episode after this one; only then does a TV episode offer Next.</param>
/// <param name="VaultLocked">True while Vault mode is locked on this device.</param>
/// <param name="HideEpisodeTitle">True when the profile's Spoilers setting hides the episode title.</param>
public sealed record PlaybackSessionContext(
    PlaybackBarMedia Media,
    ListenQueueItem Item,
    Uri BaseUri,
    string? ShowTitle = null,
    string? ChapterTitle = null,
    int SkipBackSeconds = ListenPlaybackClientSettings.FallbackSkipBackSeconds,
    int SkipForwardSeconds = ListenPlaybackClientSettings.FallbackSkipForwardSeconds,
    bool HasNextEpisode = false,
    bool VaultLocked = false,
    bool HideEpisodeTitle = false);

/// <summary>
/// What the lock screen, notification shade, headsets and car stereos show and can do for the item that is playing (unified player v2 D27).
/// One builder serves the audio bar and the video player, so every media gets the same title, second line, artwork and action set as the bar.
/// The operating system draws the card; Tuvima only supplies the fields and the buttons that work.
/// </summary>
public sealed record PlaybackSessionMetadata(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("artist")] string Artist,
    [property: JsonPropertyName("album")] string Album,
    [property: JsonPropertyName("artwork")] IReadOnlyList<PlaybackSessionArtwork> Artwork,
    [property: JsonPropertyName("actions")] IReadOnlyList<string> Actions,
    [property: JsonPropertyName("skipBackSeconds")] int SkipBackSeconds,
    [property: JsonPropertyName("skipForwardSeconds")] int SkipForwardSeconds)
{
    /// <summary>The two artwork sizes every client receives.</summary>
    public static readonly IReadOnlyList<int> ArtworkSizes = [512, 1024];

    /// <summary>
    /// Builds the metadata, or returns null when nothing may be shown: a Vault item while Vault mode is locked sends no metadata at all, so the
    /// card shows nothing rather than a title. A Vault item while Vault mode is open shows only the generic "Video" (Videos spec D33).
    /// </summary>
    public static PlaybackSessionMetadata? Create(PlaybackSessionContext context)
    {
        var item = context.Item;
        if (item.IsVault)
        {
            return context.VaultLocked
                ? null
                : new(VaultPrivacy.GenericTitle, string.Empty, string.Empty, [], ActionsFor(context), context.SkipBackSeconds, context.SkipForwardSeconds);
        }

        var (title, second) = Lines(context);
        var artwork = ArtworkSizes
            .Select(size => (size, source: PlaybackArtworkUrl.ForDrawnSize(item, size)))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.source))
            .Select(entry => new PlaybackSessionArtwork(new Uri(context.BaseUri, entry.source!).ToString(), $"{entry.size}x{entry.size}"))
            .ToList();
        return new(title, second, string.Empty, artwork, ActionsFor(context), context.SkipBackSeconds, context.SkipForwardSeconds);
    }

    /// <summary>
    /// Line 1 and line 2. Music: song, artist · album. Audiobook: book, author · chapter. TV: episode, show · S1 E1 (the episode title is left
    /// out when Spoilers hides it). Movie: title, year · Movie.
    /// </summary>
    private static (string Title, string Second) Lines(PlaybackSessionContext context)
    {
        var item = context.Item;
        switch (context.Media)
        {
            case PlaybackBarMedia.Tv:
                var show = StringHelpers.FirstNonBlankOr(string.Empty, context.ShowTitle, item.Album);
                // A folder video says "3 · Ball handling" and never a season (issue 703); EpisodeLabel is empty for it.
                var episodeTitle = VideoPlayerWording.IsFolderVideo(item) ? VideoPlayerWording.TitleLine(item)
                    : context.HideEpisodeTitle ? null : StringHelpers.FirstNonBlank(item.EpisodeTitle, item.Title);
                var title = episodeTitle ?? (string.IsNullOrWhiteSpace(show) ? "Episode" : show);
                var second = string.Join(PlaybackBarIdentity.PartSeparator, new[] { string.IsNullOrWhiteSpace(show) ? null : show, PlaybackBarIdentity.EpisodeLabel(item) }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));
                return (title, second);
            default:
                var identity = PlaybackBarIdentity.Build(context.Media, item, context.ShowTitle, context.ChapterTitle, context.HideEpisodeTitle);
                return (identity.Title, identity.SecondaryText);
        }
    }

    private static IReadOnlyList<string> ActionsFor(PlaybackSessionContext context) => context.Media switch
    {
        PlaybackBarMedia.Music => ["play", "pause", "previoustrack", "nexttrack", "seekto"],
        PlaybackBarMedia.Audiobook => ["play", "pause", "seekbackward", "seekforward", "previoustrack", "nexttrack", "seekto"],
        PlaybackBarMedia.Tv when context.HasNextEpisode => ["play", "pause", "seekbackward", "seekforward", "seekto", "nexttrack"],
        _ => ["play", "pause", "seekbackward", "seekforward", "seekto"],
    };
}

/// <summary>One artwork rendition handed to the browser's media session.</summary>
public sealed record PlaybackSessionArtwork(
    [property: JsonPropertyName("src")] string Src,
    [property: JsonPropertyName("sizes")] string Sizes);
