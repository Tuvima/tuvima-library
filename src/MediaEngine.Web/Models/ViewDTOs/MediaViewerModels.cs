namespace MediaEngine.Web.Models.ViewDTOs;

public enum MediaViewerKind
{
    Image,
    Video,
    Audio,
    Document,
}

public sealed record MediaViewerItem(
    string Key,
    MediaViewerKind Kind,
    string Title,
    string? Subtitle,
    string PreviewUrl,
    string OriginalUrl,
    string? PosterUrl = null,
    string? PositionLabel = null,
    string? AltText = null);

public sealed record MediaViewerCapabilities(
    bool CanFavorite = false,
    bool IsFavorite = false,
    bool CanAddToGallery = false,
    bool CanDownload = true,
    bool CanArchive = false,
    bool CanRestore = false,
    bool CanTrash = false,
    bool CanHide = false,
    bool CanShowInfo = true,
    bool CanZoom = true,
    bool CanPlay = false,
    bool CanSlideshow = false);
