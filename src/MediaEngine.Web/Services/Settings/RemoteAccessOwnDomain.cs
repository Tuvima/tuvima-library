using System.Text;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Navigation;

namespace MediaEngine.Web.Services.Settings;

/// <summary>The four screens of the own-domain wizard (spec remote-access D11): Address, Point it here, Secure it, Check.</summary>
public enum OwnDomainScreen
{
    Address,
    Point,
    Secure,
    Check,
}

/// <summary>The ways the internet can reach this server under the person's own name (spec remote-access D11).</summary>
public enum OwnDomainRoute
{
    /// <summary>A tunnel (Cloudflare Tunnel): no router changes, hides the home address.</summary>
    Tunnel,

    /// <summary>A proxy the person runs (Caddy, nginx, Traefik), with the ready-made Caddy file.</summary>
    Caddy,

    /// <summary>A door opened in the router. Last of the three, because a provider that shares one address often blocks it.</summary>
    RouterDoor,
}

/// <summary>Builds and reads the person's own web address from its two typed parts (the front name and the domain).</summary>
public static class OwnDomainAddress
{
    /// <summary>The front name suggested when the person has not chosen one.</summary>
    public const string SuggestedFrontName = "library";

    /// <summary>The https address for a front name and a domain, or null when either part cannot be part of a web address.</summary>
    public static string? Build(string? frontName, string? domain)
    {
        var front = frontName?.Trim().Trim('.').ToLowerInvariant();
        var root = domain?.Trim().Trim('.').ToLowerInvariant();
        if (!IsLabel(front) || string.IsNullOrEmpty(root))
        {
            return null;
        }

        var labels = root.Split('.');
        return labels.Length >= 2 && labels.All(IsLabel) && labels[^1].Length >= 2 && !labels[^1].All(char.IsDigit)
            ? $"https://{front}.{root}"
            : null;
    }

    /// <summary>Splits a saved address into its front name and domain; both empty when it is not a name with a front part.</summary>
    public static (string Front, string Domain) Split(string? address)
    {
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return (string.Empty, string.Empty);
        }

        var host = uri.IdnHost;
        var dot = host.IndexOf('.');
        return dot > 0 && host.IndexOf('.', dot + 1) > 0 ? (host[..dot], host[(dot + 1)..]) : (string.Empty, string.Empty);
    }

    private static bool IsLabel(string? label) =>
        !string.IsNullOrEmpty(label) && label.Length <= 63 && label[0] != '-' && label[^1] != '-'
        && label.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');
}

/// <summary>The ready-made Caddy file: the person's address filled in, nothing installed or started by Library (spec remote-access D14).</summary>
public static class CaddyFile
{
    public const string FileName = "Caddyfile";

    /// <summary>The Caddy file for <paramref name="address"/> (an https address), sending visitors to the Dashboard's proxy port on this computer.</summary>
    public static string Render(string address, int proxyPort)
    {
        var host = Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.IdnHost : address.Trim();
        var text = new StringBuilder();
        text.AppendLine("# Caddy file for Library, made by Library. Save it as \"Caddyfile\" and start Caddy next to it.");
        text.AppendLine("# Caddy gets and renews the secure certificate for this name by itself; ports 80 and 443 must reach this computer.");
        text.AppendLine($"{host} {{");
        text.AppendLine($"\treverse_proxy localhost:{proxyPort}");
        text.AppendLine("}");
        return text.ToString();
    }

    /// <summary>A link that downloads <paramref name="text"/> as the file, with no round trip to the server.</summary>
    public static string DownloadAddress(string text) => "data:text/plain;charset=utf-8," + Uri.EscapeDataString(text);
}

/// <summary>The two commands the person runs themselves on their server; Library only shows them (spec remote-access D14).</summary>
public static class OwnDomainCommands
{
    /// <summary>The Cloudflare connector command, with the person's own token to be pasted in.</summary>
    public const string TunnelConnector = "cloudflared service install YOUR-TOKEN";

    public const string CaddyRun = "caddy run";
}

/// <summary>Where each help or check link goes: the public away-from-home guides (spec remote-access WP8). The address comes from <see cref="ProjectLinks.DocsSite"/>.</summary>
public static class RemoteAccessGuideLinks
{
    private const string GuideFolder = ProjectLinks.DocsSite + "guides/away-from-home/";

    /// <summary>Owning or buying a domain, and which registrars work.</summary>
    public const string DomainNames = GuideFolder + "domain-names/";

    /// <summary>The troubleshooting entry for a check's finding key (one anchor per key, so each failed check lands on its own entry).</summary>
    public static string Troubleshooting(string findingKey) => GuideFolder + "testing-and-troubleshooting/#" + Uri.EscapeDataString(findingKey);
}

/// <summary>One line of the wizard's Secure and Check screens: what was looked at, how it went, and the one next step.</summary>
/// <param name="Key">Tells the lines apart for tests and the browser checks.</param>
/// <param name="Tone">Green done, amber needs a look, red not working, grey not known yet.</param>
/// <param name="Title">What was looked at, in plain words.</param>
/// <param name="Detail">The plain reason when it is not fine, otherwise a short confirmation.</param>
/// <param name="NextStep">The single next step, or null when there is nothing to do.</param>
/// <param name="FixAction">The Engine's one-click fix for this line, or null.</param>
/// <param name="HelpKey">The finding key whose troubleshooting entry explains this line, or null.</param>
public sealed record OwnDomainCheckRow(
    string Key,
    AppStatusDotTone Tone,
    string Title,
    string? Detail,
    string? NextStep,
    string? FixAction,
    string? HelpKey);

/// <summary>Reads the Engine's findings into the wizard's lines. Pure: the words for the good case live here, the reasons come from the Engine.</summary>
public static class OwnDomainChecks
{
    public const string Resolves = "resolves";
    public const string Certificate = "certificate";
    public const string Visitors = "visitors";
    public const string Unreachable = "unreachable";

    /// <summary>The certificate line alone (the Secure it screen).</summary>
    public static OwnDomainCheckRow CertificateRow(RemoteCheckResultDto? result, bool checking)
    {
        const string title = "The secure certificate is valid";
        if (checking || result is null)
        {
            return Pending(Certificate, title, checking);
        }

        if (Find(result, RemoteCheckFindingKeys.CertificateInvalid) is { } invalid)
        {
            return FromFinding(Certificate, AppStatusDotTone.Broken, title, invalid);
        }

        if (Find(result, RemoteCheckFindingKeys.CertificateExpiring) is { } expiring)
        {
            return FromFinding(Certificate, AppStatusDotTone.Attention, title, expiring);
        }

        // Nothing can be said about a certificate until the name resolves and answers.
        if (Find(result, RemoteCheckFindingKeys.NameNotPointingHere) is not null || Find(result, RemoteCheckFindingKeys.PublicAddressUnreachable) is not null)
        {
            return new OwnDomainCheckRow(Certificate, AppStatusDotTone.Off, title, "Library can check it once the name points here and answers.", null, null, null);
        }

        return new OwnDomainCheckRow(Certificate, AppStatusDotTone.Working, title, "The certificate is valid and your proxy or tunnel renews it by itself.", null, null, null);
    }

    /// <summary>The lines of the Check screen: resolves, certificate, and whether Library recognises outside visitors; plus one for an address nothing answers on.</summary>
    public static IReadOnlyList<OwnDomainCheckRow> CheckRows(RemoteCheckResultDto? result, bool checking)
    {
        var rows = new List<OwnDomainCheckRow>();
        const string resolvesTitle = "The name points to your home or your tunnel";
        if (checking || result is null)
        {
            rows.Add(Pending(Resolves, resolvesTitle, checking));
        }
        else if (Find(result, RemoteCheckFindingKeys.NameNotPointingHere) is { } notPointing)
        {
            rows.Add(FromFinding(Resolves, AppStatusDotTone.Broken, resolvesTitle, notPointing));
        }
        else
        {
            rows.Add(new OwnDomainCheckRow(Resolves, AppStatusDotTone.Working, resolvesTitle, null, null, null, null));
        }

        rows.Add(CertificateRow(result, checking));

        const string visitorsTitle = "Library recognises outside visitors";
        if (checking || result is null)
        {
            rows.Add(Pending(Visitors, visitorsTitle, checking));
        }
        else if (Find(result, RemoteCheckFindingKeys.ProxyNotTrusted) is { } proxy)
        {
            // Visitors through a proxy would look like they are on this computer: always shown as not working, whatever the Engine's status word.
            rows.Add(FromFinding(Visitors, AppStatusDotTone.Broken, visitorsTitle, proxy));
        }
        else if (Find(result, RemoteCheckFindingKeys.NoOutsideVisitorsSeen) is { } none)
        {
            rows.Add(FromFinding(Visitors, AppStatusDotTone.Off, visitorsTitle, none));
        }
        else
        {
            rows.Add(new OwnDomainCheckRow(Visitors, AppStatusDotTone.Working, visitorsTitle, "Visitors from outside are told apart from people at home.", null, null, null));
        }

        if (!checking && result is not null && Find(result, RemoteCheckFindingKeys.PublicAddressUnreachable) is { } unreachable)
        {
            rows.Add(FromFinding(Unreachable, AppStatusDotTone.Broken, unreachable.Title, unreachable));
        }

        return rows;
    }

    /// <summary>True when every line is green: the Check is done.</summary>
    public static bool AllGreen(IReadOnlyList<OwnDomainCheckRow> rows) => rows.All(row => row.Tone == AppStatusDotTone.Working);

    private static RemoteCheckFindingDto? Find(RemoteCheckResultDto result, string key) =>
        result.Findings.FirstOrDefault(finding => string.Equals(finding.Key, key, StringComparison.Ordinal));

    private static OwnDomainCheckRow Pending(string key, string title, bool checking) =>
        new(key, AppStatusDotTone.Off, title, checking ? "Checking..." : "Not checked yet.", null, null, null);

    private static OwnDomainCheckRow FromFinding(string key, AppStatusDotTone tone, string title, RemoteCheckFindingDto finding) =>
        new(key, tone, title, string.IsNullOrWhiteSpace(finding.Detail) ? finding.Title : finding.Detail,
            string.IsNullOrWhiteSpace(finding.NextStep) ? null : finding.NextStep,
            string.IsNullOrWhiteSpace(finding.FixAction) ? null : finding.FixAction, finding.Key);
}
