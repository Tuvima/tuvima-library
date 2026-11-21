using System.Net;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Authorization;
using MediaEngine.Identity.Contracts;

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

    /// <summary>
    /// The place to judge the request by: forwarded, missing, blank or unknown all count as outside (never local). A forwarded
    /// request, or one the Dashboard marked clearly outside, comes back as <see cref="ClientIngress.Outside"/> so the
    /// "outside access is off" refusal can tell it from a place that is only "not known to be home".
    /// </summary>
    public static string Current(HttpRequest request) =>
        WasForwarded(request)
            ? ClientIngress.Outside
            : ClientIngress.ParseCurrentKeepingEvidence(request.Headers[ClientIngressValues.ValidateHeader].ToString());

    /// <summary>
    /// Like <see cref="Current"/>, but null when the Dashboard said nothing about the place at all and the request was not
    /// forwarded. Callers pass that on so a session of a person with no password still fails closed further in.
    /// </summary>
    public static string? CurrentOrNull(HttpRequest request) =>
        WasForwarded(request) || (request.Headers.TryGetValue(ClientIngressValues.ValidateHeader, out var values)
            && !string.IsNullOrWhiteSpace(values.ToString()))
            ? Current(request)
            : null;

    /// <summary>
    /// The place to judge a request made straight with a device token. The Dashboard's edge puts its own reading in the header (it
    /// sees the visitor's real address); this also looks at the Engine's own connection and forwarding headers. Evidence only
    /// ever adds: a header that says "home" never clears a public address or a forwarding header.
    /// </summary>
    public static string CurrentForDevice(HttpRequest request, IPAddress? connectionAddress) =>
        ClientIngress.IsClearlyOutside(Current(request))
        || OutsideRequestEvidence.IsPubliclyRoutable(connectionAddress)
            ? ClientIngress.Outside
            : Current(request);

    /// <summary>
    /// True when a home-network-only token (one from a no-password account's approved device) is presented from anywhere but this
    /// computer or the home network. The Dashboard's front door refuses these too; the Engine checks again because it is the
    /// one that issues and validates the token, so a call that skips the front door cannot use such a token from outside.
    /// The place is the Dashboard's reading (the header is read from any caller, so this is a second line of defence: public addresses and forwarding headers are still caught here), and a missing or unknown place counts as outside (fail closed).
    /// </summary>
    public static bool HomeOnlyTokenRefused(HttpContext context, string? token) =>
        ClientIngress.IsLanOnlyToken(token)
        && !ClientIngress.TokenMayBeUsedFrom(token, CurrentForDevice(context.Request, context.Connection.RemoteIpAddress));

    /// <summary>
    /// True when outside access is off and this device-token request is clearly from outside. A paired app is still fine at home:
    /// the same token keeps working there, and switching outside access off revokes nothing.
    /// </summary>
    public static bool DeviceRefusedWhileOutsideAccessOff(HttpContext context, IOutsideAccessPolicy? policy) =>
        policy is not null
        && ClientIngress.IsClearlyOutside(CurrentForDevice(context.Request, context.Connection.RemoteIpAddress))
        && !policy.IsOutsideAccessOn();
}
