using MediaEngine.Domain.Services;

namespace MediaEngine.Web.Services.Playback;

/// <summary>
/// The words the video player uses for a video from a Videos or Other videos library (issue 703): the video's number and title
/// ("3 · Ball handling"), a list called Videos with named sections, never a season or an "S1 E3" code. A TV episode keeps its own wording.
/// The decision is the item's media type, in one place, so no surface guesses.
/// </summary>
public static class VideoPlayerWording
{
    /// <summary>What the player's list of the folder's videos is called.</summary>
    public const string ListTitle = "Videos";

    /// <summary>The label over the control that picks a section of the folder.</summary>
    public const string SectionControl = VideoFolderWording.SectionLabel;

    /// <summary>True when the playing item is a video from a Videos or Other videos library.</summary>
    public static bool IsFolderVideo(ListenQueueItem? item) => item is not null && VideoFolderWording.IsVideoItemMediaType(item.MediaType);

    /// <summary>The player's title line for a video: "3 · Ball handling", or the title alone when it has no number.</summary>
    public static string TitleLine(ListenQueueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return VideoFolderWording.NumberedTitle(item.EpisodeNumber, item.EpisodeTitle ?? item.Title);
    }
}
