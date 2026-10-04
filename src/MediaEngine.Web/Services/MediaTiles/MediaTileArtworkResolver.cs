using MediaEngine.Domain;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.MediaTiles;

public enum ArtworkRole
{
    Cover,
    Square,
    Background,
    Banner,
    Hero,
    ArtistPhoto,
}

public sealed record MediaTileArtworkVariant(
    ArtworkRole Role,
    string? SmallUrl = null,
    string? MediumUrl = null,
    string? LargeUrl = null,
    int? WidthPx = null,
    int? HeightPx = null,
    string? AspectClass = null)
{
    public bool HasUrl => !string.IsNullOrWhiteSpace(SmallUrl)
        || !string.IsNullOrWhiteSpace(MediumUrl)
        || !string.IsNullOrWhiteSpace(LargeUrl);

    public string? TileUrl => StringHelpers.FirstNonBlank(SmallUrl, MediumUrl);

    public string? HoverUrl => StringHelpers.FirstNonBlank(MediumUrl, LargeUrl, SmallUrl);

    public string? HeroUrl => StringHelpers.FirstNonBlank(LargeUrl, MediumUrl, SmallUrl);

    public MediaTileShape Shape => MediaTileArtworkResolver.ShapeFor(this);

}

public sealed record MediaTileSurfaceSelection(
    MediaTileSurfaceKind SurfaceKind,
    MediaTileHoverLayout HoverLayout,
    string? TileImageUrl,
    string? TileImageSrcSet,
    string? HoverImageUrl,
    string? HoverImageSrcSet,
    string? HeroBackgroundImageUrl,
    string? PreviewImageUrl,
    MediaTileImageFitMode TileImageFitMode,
    MediaTileImageFitMode HoverImageFitMode,
    MediaTileShape Shape,
    MediaTileShape HoverArtworkShape);

public static class MediaTileArtworkResolver
{
    public static MediaTileSurfaceSelection Resolve(
        MediaTileBucket bucket,
        MediaTilePresentation presentation,
        IReadOnlyList<MediaTileArtworkVariant> variants,
        bool preferLandscapeTile = false)
    {
        var tileVariant = SelectTileVariant(bucket, presentation, variants, preferLandscapeTile);
        var hoverVariant = SelectHoverVariant(variants) ?? tileVariant;
        var shape = presentation is MediaTilePresentation.BookSeries or MediaTilePresentation.ComicSeries or MediaTilePresentation.MovieSeries or MediaTilePresentation.AudiobookSeries
            ? MediaTileShape.Landscape
            : preferLandscapeTile ? MediaTileShape.Landscape
            : bucket == MediaTileBucket.Audiobook || presentation == MediaTilePresentation.Album
                || (bucket == MediaTileBucket.Music && variants.Any(variant => variant.HasUrl && variant.Shape == MediaTileShape.Square))
            ? MediaTileShape.Square
            : MediaTileShape.Portrait;
        var surfaceKind = shape switch {
            MediaTileShape.Landscape => MediaTileSurfaceKind.BannerLandscape,
            MediaTileShape.Square => MediaTileSurfaceKind.CoverSquare,
            _ => MediaTileSurfaceKind.CoverPortrait,
        };
        var hoverLayout = hoverVariant is not null && IsCinematic(hoverVariant)
            ? MediaTileHoverLayout.BannerPopover
            : MediaTileHoverLayout.ArtOnlyPopover;
        var tileFit = (shape == MediaTileShape.Portrait && tileVariant?.Shape == MediaTileShape.Portrait)
            || (preferLandscapeTile && tileVariant?.Shape == MediaTileShape.Landscape)
            ? MediaTileImageFitMode.Fill
            : MediaTileImageFitMode.Contain;

        return new MediaTileSurfaceSelection(
            surfaceKind,
            hoverLayout,
            TileImageUrl: tileVariant?.TileUrl,
            TileImageSrcSet: MediaTileArtworkUrl.SrcSet(tileVariant?.SmallUrl, tileVariant?.MediumUrl, null, tileVariant?.WidthPx, tileVariant?.HeightPx),
            HoverImageUrl: hoverVariant?.HoverUrl,
            HoverImageSrcSet: MediaTileArtworkUrl.SrcSet(null, hoverVariant?.MediumUrl, hoverVariant?.LargeUrl, hoverVariant?.WidthPx, hoverVariant?.HeightPx),
            HeroBackgroundImageUrl: hoverVariant?.HeroUrl,
            PreviewImageUrl: tileVariant?.HoverUrl,
            TileImageFitMode: tileFit,
            HoverImageFitMode: hoverVariant is not null && IsCinematic(hoverVariant)
                ? MediaTileImageFitMode.Fill
                : MediaTileImageFitMode.Contain,
            Shape: shape,
            HoverArtworkShape: hoverVariant?.Shape ?? shape);
    }

    public static MediaTileShape ShapeFor(MediaTileArtworkVariant variant)
    {
        if (variant.WidthPx is > 0 && variant.HeightPx is > 0)
        {
            var ratio = (double)variant.WidthPx.Value / variant.HeightPx.Value;
            if (ratio >= 1.32)
            {
                return MediaTileShape.Landscape;
            }

            if (ratio >= 0.86)
            {
                return MediaTileShape.Square;
            }

            return MediaTileShape.Portrait;
        }

        if (string.Equals(variant.AspectClass, ArtworkAspectClasses.LandscapeWide, StringComparison.OrdinalIgnoreCase)
            || string.Equals(variant.AspectClass, ArtworkAspectClasses.BannerStrip, StringComparison.OrdinalIgnoreCase))
        {
            return MediaTileShape.Landscape;
        }

        if (string.Equals(variant.AspectClass, ArtworkAspectClasses.Square, StringComparison.OrdinalIgnoreCase))
        {
            return MediaTileShape.Square;
        }

        return variant.Role switch
        {
            ArtworkRole.Background or ArtworkRole.Banner or ArtworkRole.Hero => MediaTileShape.Landscape,
            ArtworkRole.Square or ArtworkRole.ArtistPhoto => MediaTileShape.Square,
            _ => MediaTileShape.Portrait,
        };
    }

    private static MediaTileArtworkVariant? SelectTileVariant(
        MediaTileBucket bucket,
        MediaTilePresentation presentation,
        IReadOnlyList<MediaTileArtworkVariant> variants,
        bool preferLandscapeTile)
    {
        if (preferLandscapeTile)
        {
            return variants.FirstOrDefault(v => v.HasUrl && v.Shape == MediaTileShape.Landscape && v.Role == ArtworkRole.Background)
                ?? variants.FirstOrDefault(v => v.HasUrl && v.Shape == MediaTileShape.Landscape && v.Role == ArtworkRole.Banner)
                ?? First(variants, ArtworkRole.Cover, ArtworkRole.Square);
        }

        if (presentation == MediaTilePresentation.Artist)
        {
            return First(variants, ArtworkRole.ArtistPhoto, ArtworkRole.Cover, ArtworkRole.Square);
        }

        return bucket switch
        {
            MediaTileBucket.Movie or MediaTileBucket.Tv => First(variants, ArtworkRole.Cover, ArtworkRole.Square),
            MediaTileBucket.Music => First(variants, ArtworkRole.Square, ArtworkRole.Cover),
            MediaTileBucket.Audiobook => First(variants, ArtworkRole.Cover, ArtworkRole.Square),
            MediaTileBucket.Book or MediaTileBucket.Comic =>
                First(variants, ArtworkRole.Cover, ArtworkRole.Square),
            _ => First(variants, ArtworkRole.Cover, ArtworkRole.Square, ArtworkRole.ArtistPhoto),
        };
    }

    private static MediaTileArtworkVariant? SelectHoverVariant(IReadOnlyList<MediaTileArtworkVariant> variants)
    {
        return variants.FirstOrDefault(variant => variant.Role == ArtworkRole.Background && variant.HasUrl && IsCinematic(variant))
               ?? variants.FirstOrDefault(variant => variant.Role == ArtworkRole.Banner && variant.HasUrl && IsCinematic(variant));
    }

    private static bool IsCinematic(MediaTileArtworkVariant variant)
    {
        if (variant.WidthPx is > 0 && variant.HeightPx is > 0)
        {
            var ratio = variant.WidthPx.Value / (double)variant.HeightPx.Value;
            return ratio is >= 1.45 and <= 2.2;
        }

        return variant.Role == ArtworkRole.Background;
    }

    private static MediaTileArtworkVariant? First(IReadOnlyList<MediaTileArtworkVariant> variants, params ArtworkRole[] roles)
    {
        foreach (var role in roles)
        {
            var match = variants.FirstOrDefault(variant => variant.Role == role && variant.HasUrl);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static string? BuildSrcSet(params (string? Url, int Width)[] candidates)
    {
        var parts = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Url))
            .DistinctBy(candidate => candidate.Url, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => $"{candidate.Url} {candidate.Width}w")
            .ToList();

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }
}
