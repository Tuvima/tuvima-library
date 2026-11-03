using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.MediaTiles;

/// <summary>No tile grows or changes shape on hover (Home, detail and tiles spec D1). Every tile gets a thick white border with a purple ring; Home Continue may keep its fixed overlay.</summary>
public static class MediaTileHoverPolicy
{
    public static MediaTileHoverMode ForShelf(MediaTileViewModel item) =>
        MediaTileHoverMode.GlowOnly;

    public static MediaTileHoverMode ForContinue(MediaTileViewModel item) =>
        item.MediaKind is "Movie" or "TV" ? MediaTileHoverMode.Overlay : MediaTileHoverMode.GlowOnly;

    public static MediaTileHoverMode Resolve(MediaTileViewModel item, MediaTileHoverMode? requested, bool isHomeSurface = false)
    {
        if (requested == MediaTileHoverMode.None)
        {
            return MediaTileHoverMode.None;
        }
        if (requested == MediaTileHoverMode.Overlay && item.MediaKind is "Movie" or "TV")
        {
            return MediaTileHoverMode.Overlay;
        }
        return MediaTileHoverMode.GlowOnly;
    }
}
