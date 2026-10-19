using System.Text.RegularExpressions;
using MediaEngine.Contracts.Activity;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// The small pictures kept with personal activity are private to the person they belong to, so the browser cannot ask the Engine for
/// them directly. They are served by the Dashboard at <see cref="BrowserPrefix"/>, which asks the Engine as the signed-in person.
/// </summary>
public static partial class PersonalActivityArtworkPath
{
    /// <summary>Where the Dashboard serves an activity picture; the key follows.</summary>
    public const string BrowserPrefix = "/engine-activity-artwork/";

    private const string EnginePrefix = "/api/v1/activity/artwork/";

    /// <summary>The Engine path of a picture, which the Dashboard route asks for.</summary>
    public static string EnginePath(string key) => EnginePrefix + key;

    /// <summary>A picture key is the 64-character lower-case hexadecimal name the Engine gave it.</summary>
    public static bool IsValidKey(string? key) => !string.IsNullOrEmpty(key) && KeyPattern().IsMatch(key);

    /// <summary>
    /// Turns an Engine picture address into the Dashboard address a browser can load. Anything that is not an activity picture
    /// address (including no address) comes back as null so it is never shown.
    /// </summary>
    public static string? ToBrowserUrl(string? engineUrl)
    {
        if (string.IsNullOrWhiteSpace(engineUrl) || !engineUrl.StartsWith(EnginePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = engineUrl[EnginePrefix.Length..];
        var query = rest.IndexOf('?', StringComparison.Ordinal);
        var key = query < 0 ? rest : rest[..query];
        return IsValidKey(key) ? BrowserPrefix + rest : null;
    }

    /// <summary>The same tile with every picture address turned into a Dashboard address.</summary>
    public static PersonalActivityRecentTileDto ForBrowser(PersonalActivityRecentTileDto tile) => tile with
    {
        ArtworkUrl = ToBrowserUrl(tile.ArtworkUrl),
        CoverUrls = tile.CoverUrls is null
            ? null
            : [.. tile.CoverUrls.Select(ToBrowserUrl).OfType<string>()],
    };

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
