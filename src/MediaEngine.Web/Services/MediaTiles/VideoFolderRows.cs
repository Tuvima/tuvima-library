using MediaEngine.Domain.Services;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.MediaTiles;

/// <summary>
/// The Videos tab lists folders of videos and loose videos in two headed rows so the two never mix on one row (issue 1327). The rows
/// show only when the list holds both; a list of only one kind stays a single grid with no heading.
/// </summary>
public static class VideoFolderRows
{
    /// <summary>A folder of videos (a group) as opposed to a single loose video.</summary>
    public static bool IsFolder(MediaTileViewModel card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return card.IsCollection && VideoFolderWording.IsVideoFolderMediaType(card.MediaKind);
    }

    /// <summary>True when the cards hold at least one folder and at least one loose video.</summary>
    public static bool HoldsBoth(IReadOnlyList<MediaTileViewModel> cards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        return cards.Any(IsFolder) && cards.Any(card => !IsFolder(card));
    }
}
