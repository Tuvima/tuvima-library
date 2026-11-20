using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Settings;

/// <summary>
/// What the "away from home" check found, in plain sentences, plus the way in Library suggests
/// (<c>GET /network/remote-check</c>). Read-only: asking changes nothing.
/// </summary>
public sealed class RemoteCheckResultDto
{
    [JsonPropertyName("checked_at")] public DateTimeOffset CheckedAt { get; set; }

    /// <summary>One of <see cref="RemoteCheckSuggestions"/>.</summary>
    [JsonPropertyName("suggestion")] public string Suggestion { get; set; } = RemoteCheckSuggestions.Tailscale;

    /// <summary>One plain sentence saying why that way in is suggested.</summary>
    [JsonPropertyName("suggestion_reason")] public string SuggestionReason { get; set; } = string.Empty;

    /// <summary>Only the findings that apply, each with a key from <see cref="RemoteCheckFindingKeys"/>.</summary>
    [JsonPropertyName("findings")] public List<RemoteCheckFindingDto> Findings { get; set; } = [];
}

public sealed class RemoteCheckFindingDto
{
    /// <summary>One of <see cref="RemoteCheckFindingKeys"/>; the public documentation has one troubleshooting entry per key.</summary>
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;

    /// <summary>One of <see cref="RemoteCheckStatuses"/>.</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = RemoteCheckStatuses.Info;

    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;

    /// <summary>Plain English, no jargon.</summary>
    [JsonPropertyName("detail")] public string Detail { get; set; } = string.Empty;

    /// <summary>The single next step as one sentence, or null when there is nothing to do.</summary>
    [JsonPropertyName("next_step")] public string? NextStep { get; set; }

    /// <summary>The safe one-click fix when one exists (see <see cref="RemoteCheckFixActions"/>), otherwise null.</summary>
    [JsonPropertyName("fix_action")] public string? FixAction { get; set; }
}

/// <summary>The three ways in the check can suggest.</summary>
public static class RemoteCheckSuggestions
{
    public const string Tailscale = "tailscale";
    public const string OwnAddress = "own-address";
    public const string NotNow = "not-now";
}

public static class RemoteCheckStatuses
{
    public const string Ok = "ok";
    public const string Info = "info";
    public const string Warn = "warn";
    public const string Fail = "fail";
}

/// <summary>One-click fixes the guided flow can offer for a finding.</summary>
public static class RemoteCheckFixActions
{
    public const string TrustProxy = "trust-proxy";
}

/// <summary>
/// Every finding key the check can return. The public troubleshooting page has one entry per key, and a test compares the two,
/// so add a key here and in the documentation together.
/// </summary>
public static class RemoteCheckFindingKeys
{
    public const string TailscaleNotInstalled = "tailscale_not_installed";
    public const string TailscaleNotSignedIn = "tailscale_not_signed_in";
    public const string TailscaleHttpsOff = "tailscale_https_off";
    public const string TailscaleReady = "tailscale_ready";
    public const string SharedAddress = "shared_address";
    public const string RouterDoorUnavailable = "router_door_unavailable";
    public const string RouterDoorReady = "router_door_ready";
    public const string PublicAddressUnreachable = "public_address_unreachable";
    public const string ProxyNotTrusted = "proxy_not_trusted";
    public const string CertificateInvalid = "certificate_invalid";
    public const string CertificateExpiring = "certificate_expiring";
    public const string NameNotPointingHere = "name_not_pointing_here";
    public const string NoOutsideVisitorsSeen = "no_outside_visitors_seen";
    public const string ProfilesWithoutPassword = "profiles_without_password";
    public const string AdminWithoutPassword = "admin_without_password";

    /// <summary>Every key, in the order the documentation lists them.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        TailscaleNotInstalled,
        TailscaleNotSignedIn,
        TailscaleHttpsOff,
        TailscaleReady,
        SharedAddress,
        RouterDoorUnavailable,
        RouterDoorReady,
        PublicAddressUnreachable,
        ProxyNotTrusted,
        CertificateInvalid,
        CertificateExpiring,
        NameNotPointingHere,
        NoOutsideVisitorsSeen,
        ProfilesWithoutPassword,
        AdminWithoutPassword,
    ];
}
