using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.MediaTiles;

/// <summary>
/// The shape of a quadrant group tile (spec details-and-tile-fixes D1). A group is the standard poster with portrait cells; a group
/// whose covers are all albums is a square with square cells, so no cover is cropped.
/// </summary>
public static class GroupTileShape
{
    /// <summary>Square when the group shows covers and every one of them is square; a poster otherwise.</summary>
    public static MediaTileShape For(IReadOnlyList<ArtworkStackItem> covers)
    {
        ArgumentNullException.ThrowIfNull(covers);
        var shown = covers.Where(cover => !string.IsNullOrWhiteSpace(cover.ImageUrl)).Take(4).ToList();
        return shown.Count > 0 && shown.All(cover => cover.Shape == ArtworkShape.Square)
            ? MediaTileShape.Square
            : MediaTileShape.Portrait;
    }

    /// <summary>The cover surface that goes with a group shape.</summary>
    public static MediaTileSurfaceKind SurfaceFor(MediaTileShape shape) =>
        shape == MediaTileShape.Square ? MediaTileSurfaceKind.CoverSquare : MediaTileSurfaceKind.CoverPortrait;
}
