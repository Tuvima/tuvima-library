using MediaEngine.Contracts.Details;

namespace MediaEngine.Api.Services.Details.Internals;

internal static class DetailPresentationPolicy
{
    internal static bool TryParseEntityType(string value, out DetailEntityType entityType)
    {
        entityType = default;
        if (value.Contains("podcast", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalized = value.Replace("-", string.Empty).Replace("_", string.Empty);
        return Enum.TryParse(normalized, ignoreCase: true, out entityType);
    }

    internal static DetailPresentationContext ParseContext(string? value)
        => Enum.TryParse<DetailPresentationContext>(value, ignoreCase: true, out var parsed)
            ? parsed
            : DetailPresentationContext.Default;

    // Shared by the detail composer and the media editor's Match & Identity facts so both
    // report the same identity status and artwork source for an item.
    internal static CanonicalIdentityStatus ResolveIdentityStatus(string? qid, string? status, double? confidence)
    {
        if (!string.IsNullOrWhiteSpace(qid))
        {
            return CanonicalIdentityStatus.WikidataLinked;
        }

        if (status?.Contains("review", StringComparison.OrdinalIgnoreCase) == true || confidence is < 0.7)
        {
            return CanonicalIdentityStatus.NeedsReview;
        }

        return CanonicalIdentityStatus.ProviderMatched;
    }

    internal static ArtworkSource ResolveArtworkSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return ArtworkSource.Generated;
        }

        return source.Contains("user", StringComparison.OrdinalIgnoreCase)
               || source.Contains("manual", StringComparison.OrdinalIgnoreCase)
            ? ArtworkSource.User
            : ArtworkSource.Provider;
    }

    internal static ArtworkPresentationMode ResolveArtworkPresentationMode(
        DetailEntityType entityType,
        string? backdropUrl,
        string? bannerUrl,
        string? coverUrl,
        string? posterUrl,
        string? portraitUrl,
        int relatedArtworkCount,
        int ownedFormatCount)
    {
        if (ownedFormatCount > 1
            && entityType is DetailEntityType.Work or DetailEntityType.Book or DetailEntityType.Audiobook)
        {
            return ArtworkPresentationMode.PairedEditionGradient;
        }

        if (!string.IsNullOrWhiteSpace(backdropUrl) || !string.IsNullOrWhiteSpace(bannerUrl))
        {
            return ArtworkPresentationMode.CinematicBackdrop;
        }

        if (entityType == DetailEntityType.Person && !string.IsNullOrWhiteSpace(portraitUrl))
        {
            return ArtworkPresentationMode.PortraitEcho;
        }

        if (!string.IsNullOrWhiteSpace(coverUrl) || !string.IsNullOrWhiteSpace(posterUrl))
        {
            return ArtworkPresentationMode.ColorGradientFromArtwork;
        }

        return relatedArtworkCount > 1
            ? ArtworkPresentationMode.CollageGradient
            : ArtworkPresentationMode.GeneratedIdentity;
    }
}
