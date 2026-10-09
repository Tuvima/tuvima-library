using MediaEngine.Domain.Configuration;
using MediaEngine.Web.Services.Configuration;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// Passkeys are bound to the one public address, so they work only for a visitor who reached the Dashboard
/// at that exact origin. Anyone on localhost or a LAN address is shown password sign-in only.
/// </summary>
public static class PasskeyOriginGate
{
    /// <summary>True when a public address is set and this request arrived at exactly that origin.</summary>
    public static bool IsPublicOrigin(HttpRequest request, NetworkSettings network)
    {
        return PublicAddress.IsSameOrigin(network.Remote.PublicHostname, $"{request.Scheme}://{request.Host}");
    }

    /// <summary>Reads the current network settings on every call so a changed address applies without a restart.</summary>
    public static bool IsPublicOrigin(HttpContext context) =>
        context.RequestServices.GetService<DashboardConfigurationReader>() is { } configuration
        && IsPublicOrigin(context.Request, configuration.LoadNetwork());
}
