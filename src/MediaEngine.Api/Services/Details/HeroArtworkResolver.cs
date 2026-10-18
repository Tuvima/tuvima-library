using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.Details;

public static class HeroArtworkResolver
{
    public static HeroArtworkViewModel Resolve(
        DetailEntityType entityType,
        string? backdropUrl,
        string? bannerUrl,
        string? coverUrl,
        string? posterUrl,
        string? portraitUrl,
        IReadOnlyList<string> relatedArtworkUrls,
        string? logoUrl = null,
        string? smallUrl = null,
        string? mediumUrl = null,
        string? largeUrl = null)
    {
        var background = StringHelpers.FirstNonBlank(backdropUrl, bannerUrl);
        if (!string.IsNullOrWhiteSpace(background))
        {
            return new HeroArtworkViewModel
            {
                Url = StringHelpers.FirstNonBlank(largeUrl, background),
                SmallUrl = smallUrl,
                MediumUrl = mediumUrl,
                LargeUrl = StringHelpers.FirstNonBlank(largeUrl, background),
                Mode = string.IsNullOrWhiteSpace(logoUrl)
                    ? HeroArtworkMode.BackdropWithRenderedTitle
                    : HeroArtworkMode.BackdropWithLogo,
                HasImage = true,
                AspectRatio = 16d / 9d,
                BackgroundPosition = "center right",
                MobilePosition = "center top",
            };
        }

        var cover = StringHelpers.FirstNonBlank(
            posterUrl,
            coverUrl,
            portraitUrl,
            relatedArtworkUrls.FirstOrDefault());

        if (!string.IsNullOrWhiteSpace(cover))
        {
            return new HeroArtworkViewModel
            {
                Url = StringHelpers.FirstNonBlank(largeUrl, cover),
                SmallUrl = smallUrl,
                MediumUrl = mediumUrl,
                LargeUrl = StringHelpers.FirstNonBlank(largeUrl, cover),
                Mode = HeroArtworkMode.ArtworkFallback,
                HasImage = true,
                AspectRatio = ResolveCoverAspectRatio(entityType),
                BackgroundPosition = "center",
                MobilePosition = "center top",
            };
        }

        return new HeroArtworkViewModel
        {
            Mode = HeroArtworkMode.Placeholder,
            HasImage = false,
            BackgroundPosition = "center",
            MobilePosition = "center",
        };
    }

    private static double ResolveCoverAspectRatio(DetailEntityType entityType) => entityType switch
    {
        DetailEntityType.MusicAlbum => 1d,
        DetailEntityType.Movie or DetailEntityType.MovieSeries or DetailEntityType.TvShow or DetailEntityType.TvSeason or DetailEntityType.TvEpisode => 2d / 3d,
        DetailEntityType.Person => 3d / 4d,
        _ => 2d / 3d,
    };

}
