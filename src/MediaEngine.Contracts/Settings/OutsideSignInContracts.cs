using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Settings;

/// <summary>
/// One recent attempt to sign in from outside the home network (<c>GET /api/v1/network/outside-sign-ins</c>, administrators
/// only). It carries the person's profile name, when, how, and whether Library let them in. It never carries an address.
/// </summary>
public sealed class OutsideSignInDto
{
    [JsonPropertyName("at")] public DateTimeOffset At { get; set; }

    /// <summary>The person's profile name, or null when that profile no longer exists.</summary>
    [JsonPropertyName("profile_name")] public string? ProfileName { get; set; }

    /// <summary>True when the person was let in, false when Library refused them.</summary>
    [JsonPropertyName("succeeded")] public bool Succeeded { get; set; }

    /// <summary>One of <see cref="OutsideSignInMethods"/>.</summary>
    [JsonPropertyName("method")] public string Method { get; set; } = OutsideSignInMethods.Password;

    /// <summary>For a refusal, the reason in plain words; null for a sign-in that worked.</summary>
    [JsonPropertyName("refusal_reason")] public string? RefusalReason { get; set; }
}

/// <summary>How an outside sign-in was tried.</summary>
public static class OutsideSignInMethods
{
    public const string Password = "password";
    public const string Passkey = "passkey";
    public const string LinkedAccount = "linked_account";
    public const string NoPassword = "no_password";
}

/// <summary>
/// What switching away-from-home access off did (<c>POST /api/v1/network/remote-off</c>). Home access is never touched.
/// </summary>
public sealed class RemoteOffResultDto
{
    /// <summary>True when outside access was on before this call; false when it was already off (the call is safe to repeat).</summary>
    [JsonPropertyName("was_on")] public bool WasOn { get; set; }

    /// <summary>The connection mode afterwards: always <c>local-only</c>.</summary>
    [JsonPropertyName("connection_mode")] public string ConnectionMode { get; set; } = "local-only";
}
