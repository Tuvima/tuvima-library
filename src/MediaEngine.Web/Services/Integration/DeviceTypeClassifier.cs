using System.Text.RegularExpressions;

namespace MediaEngine.Web.Services.Integration;

/// <summary>The kind of device a row stands for; it picks the icon on the Devices page.</summary>
public enum DeviceTypeKind
{
    Generic,
    Android,
    Apple,
    Tv,
    Browser,
}

/// <summary>
/// Works out the kind of a paired device from what it reported when it paired: its platform (<c>web</c>, <c>mobile</c>,
/// <c>television</c>, <c>automotive</c>) and its app name (for example "Tuvima for Roku"). Anything it cannot place is
/// <see cref="DeviceTypeKind.Generic"/>.
/// </summary>
public static partial class DeviceTypeClassifier
{
    public static DeviceTypeKind Classify(string? platform, string? clientName)
    {
        var platformKey = platform?.Trim().ToLowerInvariant() ?? string.Empty;
        var client = clientName?.Trim().ToLowerInvariant() ?? string.Empty;

        // Any TV (Roku, Android TV, Apple TV, the TV web app) draws as a TV, whatever else its name says.
        if (platformKey == "television" || client.Contains("roku", StringComparison.Ordinal) || TvWord().IsMatch(client))
        {
            return DeviceTypeKind.Tv;
        }

        if (platformKey == "web" || client.Contains("browser", StringComparison.Ordinal))
        {
            return DeviceTypeKind.Browser;
        }

        if (client.Contains("android", StringComparison.Ordinal))
        {
            return DeviceTypeKind.Android;
        }

        if (AppleWord().IsMatch(client))
        {
            return DeviceTypeKind.Apple;
        }

        return DeviceTypeKind.Generic;
    }

    /// <summary>The words people read beside the icon, for its accessible name.</summary>
    public static string Label(DeviceTypeKind kind) => kind switch
    {
        DeviceTypeKind.Android => "Android device",
        DeviceTypeKind.Apple => "Apple device",
        DeviceTypeKind.Tv => "TV",
        DeviceTypeKind.Browser => "Browser",
        _ => "Device",
    };

    [GeneratedRegex(@"\b(tv|tvos)\b")]
    private static partial Regex TvWord();

    [GeneratedRegex(@"\b(ios|ipados|iphone|ipad|macos|apple)\b")]
    private static partial Regex AppleWord();
}
