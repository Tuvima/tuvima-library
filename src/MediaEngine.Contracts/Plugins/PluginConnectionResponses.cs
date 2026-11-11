using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Plugins;

/// <summary>What the signed-in profile has linked for one plugin (Plugin architecture spec C13). Never carries a value.</summary>
public sealed class PluginConnectionsResponse
{
    [JsonPropertyName("plugin_id")]
    public string PluginId { get; set; } = "";

    [JsonPropertyName("plugin_name")]
    public string PluginName { get; set; } = "";

    /// <summary>One line on what linking shares, shown beside the consent tick.</summary>
    [JsonPropertyName("consent")]
    public string Consent { get; set; } = "";

    [JsonPropertyName("connections")]
    public List<PluginConnectionDto> Connections { get; set; } = [];
}

/// <summary>Every switched-on plugin that asks the signed-in profile to link an account, with what that profile has linked. Never carries a value.</summary>
public sealed class PluginConnectionsListResponse
{
    [JsonPropertyName("plugins")]
    public List<PluginConnectionsResponse> Plugins { get; set; } = [];
}

/// <summary>One personal credential a plugin asks each profile to link.</summary>
public sealed class PluginConnectionDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("linked")]
    public bool Linked { get; set; }

    [JsonPropertyName("linked_at")]
    public DateTimeOffset? LinkedAt { get; set; }

    /// <summary>True when the credential is linked by approving a code on another device, so the Dashboard offers Sign in rather than a box to type in.</summary>
    [JsonPropertyName("can_sign_in")]
    public bool CanSignIn { get; set; }

    /// <summary>True when the app's own client id is not set yet, so Sign in cannot start. An administrator enters it in the plugin's settings.</summary>
    [JsonPropertyName("sign_in_needs_setup")]
    public bool SignInNeedsSetup { get; set; }
}

/// <summary>Body for linking a personal credential. <c>agreed</c> must be true: linking is a consent step.</summary>
public sealed class PluginConnectionLinkRequest
{
    [JsonPropertyName("value")]
    public string Value { get; set; } = "";

    [JsonPropertyName("agreed")]
    public bool Agreed { get; set; }
}

/// <summary>Body for starting a sign-in on another device. <c>agreed</c> must be true: signing in is a consent step.</summary>
public sealed class PluginDeviceSignInStartRequest
{
    [JsonPropertyName("agreed")]
    public bool Agreed { get; set; }
}

/// <summary>The code to approve and where to approve it (Plugin architecture spec C13).</summary>
public sealed class PluginDeviceSignInStartResponse
{
    /// <summary>The short code the person types on the other device.</summary>
    [JsonPropertyName("user_code")]
    public string UserCode { get; set; } = "";

    /// <summary>The address to open on the other device.</summary>
    [JsonPropertyName("verification_url")]
    public string VerificationUrl { get; set; } = "";

    /// <summary>How many seconds the code stays valid.</summary>
    [JsonPropertyName("expires_in_seconds")]
    public int ExpiresInSeconds { get; set; }

    /// <summary>How many seconds to wait between checks.</summary>
    [JsonPropertyName("interval_seconds")]
    public int IntervalSeconds { get; set; }
}

/// <summary>Where a sign-in on another device stands: <c>waiting</c>, <c>signed_in</c>, <c>expired</c>, <c>denied</c> or <c>failed</c>.</summary>
public sealed class PluginDeviceSignInCheckResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "waiting";

    /// <summary>A plain sentence for the person when the sign-in did not finish. Empty otherwise.</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    /// <summary>When waiting: how many seconds to wait before the next check.</summary>
    [JsonPropertyName("interval_seconds")]
    public int IntervalSeconds { get; set; }
}

/// <summary>The values of <see cref="PluginDeviceSignInCheckResponse.Status"/>.</summary>
public static class PluginDeviceSignInStatuses
{
    public const string Waiting = "waiting";
    public const string SignedIn = "signed_in";
    public const string Expired = "expired";
    public const string Denied = "denied";
    public const string Failed = "failed";
}
