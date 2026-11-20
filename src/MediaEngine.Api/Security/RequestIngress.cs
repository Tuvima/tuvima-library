using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Authorization;

namespace MediaEngine.Api.Security;

/// <summary>
/// Where the Dashboard says a visitor is, as the Engine reads it. Only the Dashboard's own service credential may say so
/// (callers check that first). A request that was forwarded, by a tunnel or proxy that may be relaying someone else, is
/// never trusted as local, whatever place the Dashboard named.
/// </summary>
internal static class RequestIngress
{
    /// <summary>
    /// A tunnel such as cloudflared or ngrok on this computer looks like loopback, so a visitor whose request carried
    /// forwarding headers (seen by the Dashboard, which says so in a header, or by the Engine itself) is not trusted.
    /// </summary>
    public static bool WasForwarded(HttpRequest request) =>
        string.Equals(request.Headers[ClientIngressValues.ForwardedHeader].ToString(), "true", StringComparison.OrdinalIgnoreCase)
        || request.Headers.ContainsKey("X-Forwarded-For")
        || request.Headers.ContainsKey("Forwarded");

    /// <summary>The place to judge the request by: forwarded, missing, blank or unknown all count as outside.</summary>
    public static string Current(HttpRequest request) =>
        WasForwarded(request)
            ? ClientIngress.Remote
            : ClientIngress.ParseCurrent(request.Headers[ClientIngressValues.ValidateHeader].ToString());

    /// <summary>
    /// Like <see cref="Current"/>, but null when the Dashboard said nothing about the place at all and the request was not
    /// forwarded. Callers pass that on so a session of a person with no password still fails closed further in.
    /// </summary>
    public static string? CurrentOrNull(HttpRequest request) =>
        WasForwarded(request) || (request.Headers.TryGetValue(ClientIngressValues.ValidateHeader, out var values)
            && !string.IsNullOrWhiteSpace(values.ToString()))
            ? Current(request)
            : null;
}
