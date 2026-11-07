using MediaEngine.Domain.Services;
using MediaEngine.Web.Resources;
using Microsoft.Extensions.Localization;

namespace MediaEngine.Web.Services.Playback;

/// <summary>What the paused video says about itself, bottom-left above the bar (unified player v2 D25; mockup screen 14).</summary>
/// <param name="Title">The show, course or film title.</param>
/// <param name="Line">"S1 E1 · The First Wish · 22 min" for an episode, "3 · Ball handling · 12 min" for a folder video, "2019 · 1h 52m" for a film.</param>
/// <param name="Description">The short description of the episode; a film has none here.</param>
public sealed record VideoPausedDetailsContent(string Title, string? Line, string? Description);

/// <summary>
/// Builds the paused details for each kind of video. A video from a Videos or Other videos library says its number and title and never a
/// season or an "S1 E3" code (issue 703); a TV episode keeps the season and episode code; a film says its year and length.
/// </summary>
public static class VideoPausedDetailsPresenter
{
    private const string Separator = " · ";

    public static VideoPausedDetailsContent? Build(
        ListenQueueItem? item, string? showTitle, string? synopsis, double durationSeconds, IStringLocalizer<SharedStrings> L)
    {
        ArgumentNullException.ThrowIfNull(L);
        if (item is null)
        {
            return null;
        }

        var length = Length(L, durationSeconds);
        if (VideoPlayerWording.IsFolderVideo(item))
        {
            var folderTitle = StringHelpers.FirstNonBlankOr(item.Title, showTitle, item.Album);
            return new(folderTitle, Join(VideoPlayerWording.TitleLine(item), length), Clean(synopsis));
        }

        if (VideoPresentationResolver.IsTvEpisode(item))
        {
            var title = StringHelpers.FirstNonBlankOr(item.Title, showTitle, item.Album);
            var episodeTitle = StringHelpers.FirstNonBlank(item.EpisodeTitle, item.Title);
            // The show's own title is not repeated on the line when the item carries no separate episode name.
            if (string.Equals(episodeTitle, title, StringComparison.Ordinal))
            {
                episodeTitle = null;
            }
            return new(title, Join(PlaybackBarIdentity.EpisodeLabel(item), episodeTitle, length), Clean(synopsis));
        }

        return new(item.Title, Join(item.Year, length), null);
    }

    /// <summary>"22 min" under an hour, "1h 52m" from an hour; nothing while the length is not known.</summary>
    public static string? Length(IStringLocalizer<SharedStrings> L, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 30)
        {
            return null;
        }

        var minutes = (int)Math.Round(seconds / 60d);
        return minutes < 60
            ? L["VideoPausedDetails_Minutes", Math.Max(1, minutes)].Value
            : L["VideoPausedDetails_HoursMinutes", minutes / 60, minutes % 60].Value;
    }

    private static string? Join(params string?[] parts)
    {
        var text = string.Join(Separator, parts.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim()));
        return text.Length == 0 ? null : text;
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
