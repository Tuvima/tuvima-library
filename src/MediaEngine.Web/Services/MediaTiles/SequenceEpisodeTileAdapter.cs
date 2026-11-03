using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.MediaTiles;

/// <summary>The sequence owns placement; the shared tile owns episode artwork, state, and detail navigation.</summary>
public static class SequenceEpisodeTileAdapter
{
    public static MediaTileViewModel FromItem(SequenceItemViewModel item, string? standInBackdropUrl = null)
    {
        var context = item.EpisodeContext ?? throw new ArgumentException("An owned episode identity is required.", nameof(item));
        // A real picture always wins; the stand-in is drawn only when the Engine says TheTVDB has none.
        var standIn = item.PictureIsStandIn && string.IsNullOrWhiteSpace(item.EpisodeStillUrl);
        var artworkUrl = standIn ? standInBackdropUrl : item.ArtworkUrl;
        var small = MediaTileArtworkUrl.Sized(artworkUrl, "s") ?? artworkUrl;
        var medium = MediaTileArtworkUrl.Sized(artworkUrl, "m");
        var route = TvEpisodeDetailRoute.Build(context.ShowWorkId, context.EpisodeWorkId, "watch");
        return new MediaTileViewModel
        {
            Id = context.EpisodeWorkId,
            WorkId = context.EpisodeWorkId,
            AssetId = context.EpisodeAssetId,
            Subject = DisplaySubjectKind.TvEpisode,
            EpisodeContext = context,
            ContinuationState = context.State,
            Title = item.Title,
            Subtitle = $"S{context.SeasonNumber} E{context.EpisodeNumber}",
            IsStandInPicture = standIn,
            StandInLabel = standIn ? StandInLabelFor(item, context) : null,
            StandInRetryAfter = standIn ? item.StandInRetryAfter : null,
            MediaKind = "TV",
            Description = item.Description,
            HoverFacts = string.IsNullOrWhiteSpace(item.Duration) ? [] : [item.Duration],
            Shape = MediaTileShape.Landscape,
            HoverArtworkShape = MediaTileShape.Landscape,
            SurfaceKind = MediaTileSurfaceKind.BannerLandscape,
            TileImageUrl = small,
            TileImageSrcSet = MediaTileArtworkUrl.SrcSet(small, medium),
            TileImageSizes = "(max-width: 720px) 72vw, 256px",
            HoverImageUrl = medium ?? small,
            BackgroundUrl = medium ?? small,
            HoverImageSrcSet = MediaTileArtworkUrl.SrcSet(small, medium),
            TileImageFitMode = MediaTileImageFitMode.Fill,
            HoverImageFitMode = MediaTileImageFitMode.Fill,
            HoverLayout = MediaTileHoverLayout.BannerPopover,
            HoverMode = MediaTileHoverMode.Expanded,
            TileTextMode = MediaTileTextMode.Caption,
            NavigationUrl = route,
            DetailsNavigationUrl = route,
            ProgressPct = item.ProgressPercent,
            ProgressLabel = item.ProgressLabel,
            RemainingSeconds = item.RemainingSeconds,
            PositionSeconds = item.PositionSeconds,
            DurationSeconds = item.DurationSeconds,
        };
    }

    /// <summary>"E5" for an episode; a special shows its own label (for example "S1"), as the sequence names it.</summary>
    private static string StandInLabelFor(SequenceItemViewModel item, DisplayEpisodeContextDto context)
    {
        if (context.SeasonNumber == 0 && !string.IsNullOrWhiteSpace(item.PositionLabel))
        {
            return $"S{item.PositionLabel.Trim().TrimStart('S', 's')}";
        }
        return context.EpisodeNumber is { } number ? $"E{number}" : item.PositionText ?? string.Empty;
    }
}
