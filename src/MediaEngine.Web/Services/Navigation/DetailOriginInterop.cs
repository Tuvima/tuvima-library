using MediaEngine.Contracts.Details;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Services.Navigation;

/// <summary>
/// Leaving a detail page the way Back would: to wherever the person came from, or to a browse page when that is unknown.
/// Wraps <c>window.detailOrigin.back</c> (wwwroot/app.js) so no page carries the call as an inline string.
/// </summary>
public static class DetailOriginInterop
{
    /// <summary>
    /// Goes back to where the detail page was opened from (<paramref name="fallbackRoute"/> when nothing was remembered).
    /// False when the browser connection was gone or the call failed, so the caller can navigate to the fallback itself.
    /// </summary>
    public static async Task<bool> TryBackAsync(this IJSRuntime js, string fallbackRoute, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(js);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            await js.InvokeVoidAsync("detailOrigin.back", fallbackRoute);
            return true;
        }
        catch (JSDisconnectedException)
        {
            logger.LogDebug("The browser connection closed before the detail page could go back.");
            return false;
        }
        catch (JSException exception)
        {
            logger.LogWarning(exception, "The browser could not take the detail page back to where it was opened from.");
            return false;
        }
    }

    /// <summary>The browse page for a title's kind, used when there is no remembered origin.</summary>
    public static string FallbackRoute(DetailEntityType entityType, string? mediaKind) => entityType switch
    {
        DetailEntityType.Movie or DetailEntityType.TvShow or DetailEntityType.TvSeason or DetailEntityType.TvEpisode => "/watch",
        DetailEntityType.Book or DetailEntityType.BookSeries or DetailEntityType.ComicIssue or DetailEntityType.ComicSeries => "/read",
        DetailEntityType.Audiobook => "/listen/audiobooks",
        DetailEntityType.MusicAlbum => "/listen/music",
        _ when mediaKind is not null && mediaKind.Contains("audiobook", StringComparison.OrdinalIgnoreCase) => "/listen/audiobooks",
        _ when mediaKind is not null && (mediaKind.Contains("movie", StringComparison.OrdinalIgnoreCase) || mediaKind.Contains("tv", StringComparison.OrdinalIgnoreCase)) => "/watch",
        _ when mediaKind is not null && (mediaKind.Contains("book", StringComparison.OrdinalIgnoreCase) || mediaKind.Contains("comic", StringComparison.OrdinalIgnoreCase)) => "/read",
        _ when mediaKind is not null && mediaKind.Contains("music", StringComparison.OrdinalIgnoreCase) => "/listen/music",
        _ => "/",
    };
}
