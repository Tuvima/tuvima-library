using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.MediaTiles;

/// <summary>Only Home Watch discovery cards expand. Other surfaces highlight; Home Continue uses a fixed overlay.</summary>
public static class MediaTileHoverPolicy
{
    public static MediaTileHoverMode ForShelf(MediaTileViewModel item) =>
        item.MediaKind is "Movie" or "TV" ? MediaTileHoverMode.Expanded : MediaTileHoverMode.GlowOnly;

    public static MediaTileHoverMode ForContinue(MediaTileViewModel item) =>
        item.MediaKind is "Movie" or "TV" ? MediaTileHoverMode.Overlay : MediaTileHoverMode.GlowOnly;

    public static MediaTileHoverMode Resolve(MediaTileViewModel item, MediaTileHoverMode? requested, bool isHomeSurface = false)
    {
        if (requested == MediaTileHoverMode.None) return MediaTileHoverMode.None;
        if (requested == MediaTileHoverMode.Overlay && item.MediaKind is "Movie" or "TV") return MediaTileHoverMode.Overlay;
        if (!isHomeSurface || requested == MediaTileHoverMode.GlowOnly || item.MediaKind is not ("Movie" or "TV")) return MediaTileHoverMode.GlowOnly;
        return requested == MediaTileHoverMode.Overlay ? MediaTileHoverMode.Overlay : MediaTileHoverMode.Expanded;
    }
}
