using System.Net;
using MediaEngine.Web.Services.Configuration;

namespace MediaEngine.Web.Services.Settings;

/// <summary>
/// The "Open in the Library app" part of the invitation page (spec remote-access D13, WP5b). A friend who has the app opens it with the
/// away-from-home address and the one-time code, and the app pairs through the existing device pairing. A friend without it is sent to
/// the store for their phone and comes back to this same link. The browser form under it always works, so nobody needs the app.
/// The code appears only in this page's own links; it is never logged.
/// </summary>
public static class InviteAppHandoff
{
    /// <summary>The address the native app registers for invitations.</summary>
    public const string AppScheme = "tuvima";

    public static string AppLink(string awayAddress, string code) =>
        $"{AppScheme}://invite?address={Uri.EscapeDataString(awayAddress.Trim().TrimEnd('/'))}&code={Uri.EscapeDataString(code)}";

    /// <summary>The store for the visitor's phone, or null when it is not an iPhone, iPad or Android phone, or that store is not published yet.</summary>
    public static (string Name, string Address)? StoreFor(string? userAgent, AppStoreLinks links)
    {
        var agent = userAgent ?? string.Empty;
        if (agent.Contains("iPhone", StringComparison.OrdinalIgnoreCase) || agent.Contains("iPad", StringComparison.OrdinalIgnoreCase))
        {
            return IsWebAddress(links.AppStore) ? ("App Store", links.AppStore) : null;
        }

        if (agent.Contains("Android", StringComparison.OrdinalIgnoreCase))
        {
            return IsWebAddress(links.GooglePlay) ? ("Google Play", links.GooglePlay) : null;
        }

        return null;
    }

    /// <summary>The block shown above the browser form: open the app, or get it first.</summary>
    public static string Render(string awayAddress, string code, string? userAgent, AppStoreLinks links)
    {
        var html = new System.Text.StringBuilder();
        html.Append("<div class=\"app-handoff\" data-invite-handoff>");
        html.Append("<p class=\"supporting\">Have the Library app? Open it to finish there. No app? Choose a password below and carry on in this browser.</p>");
        html.Append($"<p><a class=\"button\" href=\"{WebUtility.HtmlEncode(AppLink(awayAddress, code))}\" data-invite-open-app>Open in the Library app</a></p>");
        if (StoreFor(userAgent, links) is { } store)
        {
            html.Append($"<p class=\"supporting\"><a href=\"{WebUtility.HtmlEncode(store.Address)}\" rel=\"noreferrer\" data-invite-store>Get the Library app from {WebUtility.HtmlEncode(store.Name)}</a>, then open this link again.</p>");
        }

        html.Append("</div>");
        return html.ToString();
    }

    private static bool IsWebAddress(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
