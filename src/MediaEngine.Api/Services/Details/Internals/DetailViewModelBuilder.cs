using MediaEngine.Contracts.Details;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.Details.Internals;

/// <summary>
/// Builds reusable detail view-model fragments from already-loaded projection data.
/// </summary>
internal static class DetailViewModelBuilder
{
    internal static ArtworkSet BuildArtwork(
        DetailEntityType entityType,
        string? backdropUrl,
        string? bannerUrl,
        string? coverUrl,
        string? posterUrl,
        string? portraitUrl,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyList<string> relatedArtwork,
        int ownedFormatCount,
        string? artworkSource,
        string? logoUrl = null)
    {
        var primary = StringHelpers.FirstNonBlankOr(
            string.Empty,
            GetValue(values, MetadataFieldConstants.ArtworkPrimaryHex),
            GetValue(values, "primary_color"),
            "#C9922E");
        var secondary = StringHelpers.FirstNonBlankOr(
            string.Empty,
            GetValue(values, MetadataFieldConstants.ArtworkSecondaryHex),
            GetValue(values, "secondary_color"),
            "#271A3A");
        var accent = StringHelpers.FirstNonBlankOr(
            string.Empty,
            GetValue(values, MetadataFieldConstants.ArtworkAccentHex),
            GetValue(values, "accent_color"),
            "#4F7DBA");
        var backdropTop = !string.IsNullOrWhiteSpace(backdropUrl)
            ? GetValue(values, "background_primary_hex")
            : null;
        var backdropMiddle = !string.IsNullOrWhiteSpace(backdropUrl)
            ? GetValue(values, "background_secondary_hex")
            : null;
        var backdropBottom = !string.IsNullOrWhiteSpace(backdropUrl)
            ? GetValue(values, "background_accent_hex")
            : null;
        var mode = DetailPresentationPolicy.ResolveArtworkPresentationMode(
            entityType,
            backdropUrl,
            bannerUrl,
            coverUrl,
            posterUrl,
            portraitUrl,
            relatedArtwork.Count,
            ownedFormatCount);
        var characterImageUrl = entityType == DetailEntityType.Character ? portraitUrl : null;
        var resolvedLogoUrl = StringHelpers.FirstNonBlankOr(
            string.Empty,
            logoUrl,
            GetValue(values, "clear_logo_url"),
            GetValue(values, "clear_logo"),
            GetValue(values, "logo_url"),
            GetValue(values, "logo"));
        // Renditions must belong to the selected asset, never to another scope's
        // generic background. Otherwise largeUrl/srcset can replace an episode still.
        var selectedImage = StringHelpers.FirstNonBlank(backdropUrl, bannerUrl, coverUrl, posterUrl, portraitUrl);
        string? Variant(string size)
        {
            if (string.IsNullOrWhiteSpace(selectedImage))
            {
                return null;
            }

            var baseUrl = selectedImage.Split('?')[0];
            return baseUrl.StartsWith("/stream/artwork/", StringComparison.OrdinalIgnoreCase)
                ? $"{baseUrl}?size={size}"
                : selectedImage;
        }
        var heroSmallUrl = Variant("s");
        var heroMediumUrl = Variant("m");
        var heroLargeUrl = Variant("l");
        var heroArtwork = HeroArtworkResolver.Resolve(
            entityType,
            backdropUrl,
            bannerUrl,
            coverUrl,
            posterUrl,
            portraitUrl,
            characterImageUrl,
            relatedArtwork,
            resolvedLogoUrl,
            heroSmallUrl,
            heroMediumUrl,
            heroLargeUrl);

        return new ArtworkSet
        {
            BackdropUrl = backdropUrl,
            BannerUrl = bannerUrl,
            PosterUrl = posterUrl,
            CoverUrl = coverUrl,
            LogoUrl = resolvedLogoUrl,
            PortraitUrl = portraitUrl,
            CharacterImageUrl = characterImageUrl,
            RelatedArtworkUrls = relatedArtwork,
            DominantColors = [primary, secondary, accent],
            PrimaryColor = primary,
            SecondaryColor = secondary,
            AccentColor = accent,
            BackdropLeftTopColor = backdropTop,
            BackdropLeftMiddleColor = backdropMiddle,
            BackdropLeftBottomColor = backdropBottom,
            HeroArtwork = heroArtwork,
            PresentationMode = mode,
            Source = DetailPresentationPolicy.ResolveArtworkSource(artworkSource),
        };
    }

    private static string? GetValue(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
}
