using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.MediaTiles;

/// <summary>
/// The shape of a group tile. A group of titles (collection, Series, Universe) is the landscape cover collage (issue 1175,
/// <see cref="GroupCollageLayout"/>): covers keep their own shapes inside one wide tile, so the tile no longer takes a poster or square
/// shape from what it holds. A person's group (an artist's albums, a director's films) keeps the quadrant tile of spec
/// details-and-tile-fixes D1 and D2: a poster, or a square when it shows only albums, so no cover is cropped.
/// </summary>
public static class GroupTileShape
{
    /// <summary>The shape of a group of titles: always landscape. The covers are read only by the collage, which lays them out itself.</summary>
    public static MediaTileShape For(IReadOnlyList<ArtworkStackItem> covers)
    {
        ArgumentNullException.ThrowIfNull(covers);
        return MediaTileShape.Landscape;
    }

    /// <summary>The shape of a person's quadrant tile: square when it shows covers and every one of them is square; a poster otherwise.</summary>
    public static MediaTileShape ForPerson(IReadOnlyList<ArtworkStackItem> covers)
    {
        ArgumentNullException.ThrowIfNull(covers);
        var shown = covers.Where(cover => !string.IsNullOrWhiteSpace(cover.ImageUrl)).Take(4).ToList();
        return shown.Count > 0 && shown.All(cover => cover.Shape == ArtworkShape.Square)
            ? MediaTileShape.Square
            : MediaTileShape.Portrait;
    }

    /// <summary>The cover surface that goes with a group shape: a square group (a person's albums) is square, every other group a cover surface.</summary>
    public static MediaTileSurfaceKind SurfaceFor(MediaTileShape shape) =>
        shape == MediaTileShape.Square ? MediaTileSurfaceKind.CoverSquare : MediaTileSurfaceKind.CoverPortrait;
}
