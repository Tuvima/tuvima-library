using MediaEngine.Domain.Services;

namespace MediaEngine.Web.Services.Playback;

/// <summary>One piece of the bar's second line, optionally a link to a page of the library.</summary>
/// <param name="Text">What is shown.</param>
/// <param name="LinkKind">The kind <see cref="PlaybackIdentityNavigation"/> understands ("album", "artist", "audiobook", "person"), or null for plain text.</param>
/// <param name="LinkId">The id that goes with <paramref name="LinkKind"/>.</param>
/// <param name="Separator">What goes before the part (" · " between parts, "; " between several authors).</param>
public sealed record PlaybackIdentityPart(string Text, string? LinkKind = null, Guid? LinkId = null, string Separator = PlaybackBarIdentity.PartSeparator);

/// <summary>What the playback shell asks its host to draw as a link: the text, the kind and the id.</summary>
public sealed record PlaybackIdentityLinkRequest(string Text, string Kind, Guid Id);

/// <summary>
/// What the identity zone of the persistent bar says (unified player v2 D5): a title and a secondary line. The lock screen and system controls
/// (<see cref="PlaybackSessionMetadata"/>) are built from the same parts so the two never disagree.
/// </summary>
public sealed record PlaybackBarIdentity(
    string Title,
    string? TitleLinkKind,
    Guid? TitleLinkId,
    IReadOnlyList<PlaybackIdentityPart> Secondary)
{
    public const string PartSeparator = " · ";

    /// <summary>The secondary line as one string.</summary>
    public string SecondaryText => string.Concat(Secondary.Select((part, index) => (index == 0 ? string.Empty : part.Separator) + part.Text));

    /// <summary>
    /// Builds the identity for the item that is playing.
    /// </summary>
    /// <param name="media">Which of the four layouts to follow.</param>
    /// <param name="item">The playing item.</param>
    /// <param name="showTitle">The show's name for a TV episode when the presentation context has loaded; the queue's show name is used until then.</param>
    /// <param name="chapterTitle">The audiobook's current chapter name, when it has chapters.</param>
    /// <param name="hideEpisodeTitle">True when the profile's Spoilers setting hides the episode title: it is then left out of the line.</param>
    /// <param name="includeAlbum">True adds the album after the artist for music (the lock screen's second line); the bar leaves it out.</param>
    /// <param name="showWorkId">The show of a TV episode once its presentation context has loaded; the show name and the episode title link only then.</param>
    public static PlaybackBarIdentity Build(PlaybackBarMedia media, ListenQueueItem item, string? showTitle = null, string? chapterTitle = null, bool hideEpisodeTitle = false, Guid? showWorkId = null, bool includeAlbum = false)
    {
        var parts = new List<PlaybackIdentityPart>();
        switch (media)
        {
            case PlaybackBarMedia.Music:
                AddIfText(parts, item.Subtitle, item.ArtistPersonId is not null ? "artist" : null, item.ArtistPersonId);
                // The bar shows track · artist (D5, mockup screen 1); the lock screen adds the album (D27).
                if (includeAlbum)
                {
                    AddIfText(parts, item.Album, item.AlbumWorkId is not null ? "album" : null, item.AlbumWorkId);
                }
                // There is no page of its own for one song, so the title opens the album it is on (D5: title and names are links).
                return new(item.Title, item.AlbumWorkId is not null ? "album" : null, item.AlbumWorkId, parts);

            case PlaybackBarMedia.Audiobook:
                var bookId = item.AudiobookWorkId ?? item.WorkId;
                if (item.Authors.Count > 0)
                {
                    foreach (var author in item.Authors)
                    {
                        parts.Add(new(author.Name, author.PersonId is not null ? "person" : null, author.PersonId, parts.Count == 0 ? PartSeparator : "; "));
                    }
                }
                else
                {
                    AddIfText(parts, item.AuthorName, null, null);
                }
                AddIfText(parts, chapterTitle, null, null);
                return new(item.Title, "audiobook", bookId, parts);

            case PlaybackBarMedia.Tv:
                // A video from a Videos or Other videos library says its number and title ("3 · Ball handling"), never a season or S1 E3 (issue 703).
                if (VideoPlayerWording.IsFolderVideo(item))
                {
                    AddIfText(parts, VideoPlayerWording.TitleLine(item), null, null);
                    return new(StringHelpers.FirstNonBlankOr(item.Title, showTitle, item.Album), null, null, parts);
                }

                AddIfText(parts, EpisodeLabel(item), null, null);
                var hasShow = showWorkId is { } known && known != Guid.Empty;
                if (!hideEpisodeTitle)
                {
                    AddIfText(parts, StringHelpers.FirstNonBlank(item.EpisodeTitle, item.Title), hasShow ? "episode" : null, hasShow ? item.WorkId : null);
                }
                // The bar names the show (the queue's show name until the presentation context has loaded); the episode name is on the second line.
                return new(StringHelpers.FirstNonBlankOr(item.Title, showTitle, item.Album), hasShow ? "show" : null, hasShow ? showWorkId : null, parts);

            default:
                AddIfText(parts, item.Year, null, null);
                parts.Add(new("Movie"));
                return new(item.Title, item.WorkId != Guid.Empty ? "movie" : null, item.WorkId != Guid.Empty ? item.WorkId : null, parts);
        }
    }

    /// <summary>"S1 E1" for a TV episode, from whichever of the season and episode numbers are known.</summary>
    public static string? EpisodeLabel(ListenQueueItem item)
    {
        if (VideoPlayerWording.IsFolderVideo(item))
        {
            return null;
        }

        var season = string.IsNullOrWhiteSpace(item.SeasonNumber) ? null : $"S{item.SeasonNumber.Trim()}";
        var episode = string.IsNullOrWhiteSpace(item.EpisodeNumber) ? null : $"E{item.EpisodeNumber.Trim()}";
        var label = string.Join(' ', new[] { season, episode }.Where(part => part is not null));
        return label.Length == 0 ? null : label;
    }

    private static void AddIfText(List<PlaybackIdentityPart> parts, string? text, string? kind, Guid? id)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            parts.Add(new(text.Trim(), kind, id));
        }
    }
}
