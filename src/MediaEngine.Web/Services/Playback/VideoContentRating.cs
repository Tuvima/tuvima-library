using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.Playback;

/// <summary>
/// Finds the age rating (R, PG-13, TV-MA) a video's pause card shows. The detail model's
/// <c>Rating</c> is the audience score (for example 6.21), so it is never used here
/// (unified player D7).
/// </summary>
public static class VideoContentRating
{
    public static string? Of(LibraryItemDetailViewModel detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        foreach (var value in detail.CanonicalValues)
        {
            if (value.Key is "content_rating" or "certification" && !string.IsNullOrWhiteSpace(value.Value))
            {
                return value.Value.Trim();
            }
        }
        return null;
    }
}
