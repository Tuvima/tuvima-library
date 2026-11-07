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
            if (value.Key is "content_rating" or "certification" && !string.IsNullOrWhiteSpace(value.Value) && !LooksLikeAScore(value.Value))
            {
                return value.Value.Trim();
            }
        }
        return null;
    }

    /// <summary>
    /// A decimal such as 6.21 is an audience score, never an age rating (issue 707), even when a provider files it under the rating key.
    /// Whole numbers stay: UK and German certificates are "12", "15" and "18".
    /// </summary>
    private static bool LooksLikeAScore(string value) =>
        System.Text.RegularExpressions.Regex.IsMatch(value.Trim(), @"^\d+[.,]\d+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
}
