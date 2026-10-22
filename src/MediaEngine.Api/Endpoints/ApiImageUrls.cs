using MediaEngine.Domain;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Addresses the Engine hands out for faces. A local picture carries its change marker (<c>?v=</c>) so a
/// replaced photo gets a new address and an unchanged one stays in the browser's keep.
/// </summary>
internal static class ApiImageUrls
{
    public static string? BuildPersonHeadshotUrl(
        Guid personId,
        string? localHeadshotPath,
        string? remoteHeadshotUrl,
        string? size = null)
    {
        if (string.IsNullOrWhiteSpace(localHeadshotPath) && string.IsNullOrWhiteSpace(remoteHeadshotUrl))
        {
            return null;
        }

        var url = string.IsNullOrWhiteSpace(size)
            ? $"/persons/{personId}/headshot"
            : $"/persons/{personId}/headshot?size={size}";
        return ArtworkVersionMarker.WithMarker(url, ArtworkVersionMarker.ForFile(localHeadshotPath));
    }

    public static string? BuildCharacterPortraitUrl(
        Guid? portraitId,
        string? localPortraitPath,
        string? remotePortraitUrl)
        => portraitId.HasValue
           && (!string.IsNullOrWhiteSpace(localPortraitPath) || !string.IsNullOrWhiteSpace(remotePortraitUrl))
            ? ArtworkVersionMarker.WithMarker(
                $"/library/portraits/{portraitId.Value}",
                ArtworkVersionMarker.ForFile(localPortraitPath))
            : null;
}
