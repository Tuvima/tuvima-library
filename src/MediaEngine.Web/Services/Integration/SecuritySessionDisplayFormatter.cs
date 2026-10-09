using MediaEngine.Contracts.Authentication;

namespace MediaEngine.Web.Services.Integration;

public sealed record SecuritySessionDisplay(string DeviceName, string ClientDescription);

public static class SecuritySessionDisplayFormatter
{
    public static SecuritySessionDisplay Format(DeviceSessionResponse session)
    {
        var source = session.DeviceName ?? string.Empty;
        var browser = Browser(source);
        var platform = Platform(source);
        var looksLikeUserAgent = source.Contains("Mozilla/", StringComparison.OrdinalIgnoreCase)
                                 || browser is not null;

        var deviceName = Device(platform);
        if (deviceName is null && !looksLikeUserAgent && IsUseful(session.DeviceName))
        {
            deviceName = session.DeviceName!.Trim();
        }

        deviceName ??= "Browser";

        var client = browser is not null && platform is not null
            ? $"{browser} on {platform}"
            : browser
              ?? (IsUsefulClient(session.Client) ? session.Client.Trim() : "Browser");
        var method = FormatAuthenticationMethod(session.AuthenticationMethod);
        var description = string.Equals(client, method, StringComparison.OrdinalIgnoreCase)
            ? client
            : $"{client} · {method}";

        return new SecuritySessionDisplay(deviceName, description);
    }

    public static string FormatAuthenticationMethod(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "Unknown method"
            : System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(
                value.Replace('-', ' ').Replace('_', ' '));

    private static string? Browser(string value)
    {
        if (value.Contains("Edg/", StringComparison.OrdinalIgnoreCase))
        {
            return "Edge";
        }
        if (value.Contains("OPR/", StringComparison.OrdinalIgnoreCase))
        {
            return "Opera";
        }
        if (value.Contains("Firefox/", StringComparison.OrdinalIgnoreCase))
        {
            return "Firefox";
        }
        if (value.Contains("CriOS/", StringComparison.OrdinalIgnoreCase))
        {
            return "Chrome";
        }
        if (value.Contains("Chrome/", StringComparison.OrdinalIgnoreCase))
        {
            return "Chrome";
        }
        if (value.Contains("FxiOS/", StringComparison.OrdinalIgnoreCase))
        {
            return "Firefox";
        }
        if (value.Contains("Safari/", StringComparison.OrdinalIgnoreCase))
        {
            return "Safari";
        }
        return null;
    }

    private static string? Platform(string value)
    {
        if (value.Contains("iPhone", StringComparison.OrdinalIgnoreCase))
        {
            return "iPhone";
        }
        if (value.Contains("iPad", StringComparison.OrdinalIgnoreCase))
        {
            return "iPad";
        }
        if (value.Contains("Android", StringComparison.OrdinalIgnoreCase))
        {
            return "Android";
        }
        if (value.Contains("Windows", StringComparison.OrdinalIgnoreCase))
        {
            return "Windows";
        }
        if (value.Contains("Macintosh", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Mac OS X", StringComparison.OrdinalIgnoreCase))
        {
            return "macOS";
        }
        if (value.Contains("Linux", StringComparison.OrdinalIgnoreCase))
        {
            return "Linux";
        }
        return null;
    }

    private static string? Device(string? platform) => platform switch
    {
        "iPhone" => "iPhone",
        "iPad" => "iPad",
        "Android" => "Android device",
        "Windows" => "Windows PC",
        "macOS" => "Mac",
        "Linux" => "Linux PC",
        _ => null,
    };

    private static bool IsUseful(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Equals("Unknown device", StringComparison.OrdinalIgnoreCase);

    private static bool IsUsefulClient(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Equals("Dashboard", StringComparison.OrdinalIgnoreCase)
        && !value.Equals("Tuvima Dashboard", StringComparison.OrdinalIgnoreCase)
        && !value.Equals("Tuvima Library Dashboard", StringComparison.OrdinalIgnoreCase);
}
