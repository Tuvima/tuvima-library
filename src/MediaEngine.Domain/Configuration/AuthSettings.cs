using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Configuration;

public sealed class AuthSettings
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "Local";

    [JsonPropertyName("localhost_bypass")]
    public bool LocalhostBypass { get; set; }

    [JsonPropertyName("password_sign_in_enabled")]
    public bool PasswordSignInEnabled { get; set; } = true;

    [JsonPropertyName("passkey_sign_in_enabled")]
    public bool PasskeySignInEnabled { get; set; } = true;

    [JsonPropertyName("external_sign_in_enabled")]
    public bool ExternalSignInEnabled { get; set; } = true;

    [JsonPropertyName("allow_local_only_accounts")]
    public bool AllowLocalOnlyAccounts { get; set; } = true;

    [JsonPropertyName("trusted_local_networks")]
    public List<string> TrustedLocalNetworks { get; set; } = [];

    [JsonPropertyName("invitation_lifetime_hours")]
    public int InvitationLifetimeHours { get; set; } = 168;

    [JsonPropertyName("session_lifetime_hours")]
    public int SessionLifetimeHours { get; set; } = 336;

    [JsonPropertyName("maximum_active_sessions")]
    public int MaximumActiveSessions { get; set; } = 20;

    [JsonPropertyName("external_providers")]
    public List<ExternalAuthProviderSettings> ExternalProviders { get; set; } = [];

    [JsonPropertyName("password_reset")]
    public PasswordResetDeliverySettings PasswordReset { get; set; } = new();
}

public sealed class PasswordResetDeliverySettings
{
    [JsonPropertyName("mode")] public string Mode { get; set; } = "Disabled";
    [JsonPropertyName("smtp_host")] public string SmtpHost { get; set; } = string.Empty;
    [JsonPropertyName("smtp_port")] public int SmtpPort { get; set; } = 587;
    [JsonPropertyName("use_start_tls")] public bool UseStartTls { get; set; } = true;
    [JsonPropertyName("from_address")] public string FromAddress { get; set; } = string.Empty;
    [JsonPropertyName("from_name")] public string FromName { get; set; } = "Tuvima Library";
    [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;
    [JsonIgnore] public string Password { get; set; } = string.Empty;
}

public static class ExternalAuthProviderKinds
{
    public const string OpenIdConnect = "oidc";
    public const string OAuth = "oauth";
}

public sealed class ExternalAuthProviderSettings
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = ExternalAuthProviderKinds.OpenIdConnect;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = "OpenID Connect";

    [JsonPropertyName("issuer")]
    public string Issuer { get; set; } = string.Empty;

    [JsonPropertyName("authority")]
    public string Authority { get; set; } = string.Empty;

    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = string.Empty;

    [JsonIgnore]
    public string ClientSecret { get; set; } = string.Empty;

    [JsonPropertyName("scopes")]
    public List<string> Scopes { get; set; } = ["openid", "profile", "email"];

    [JsonPropertyName("use_pkce")]
    public bool UsePkce { get; set; } = true;

    [JsonPropertyName("authorization_endpoint")]
    public string AuthorizationEndpoint { get; set; } = string.Empty;

    [JsonPropertyName("token_endpoint")]
    public string TokenEndpoint { get; set; } = string.Empty;

    [JsonPropertyName("user_information_endpoint")]
    public string UserInformationEndpoint { get; set; } = string.Empty;

    [JsonPropertyName("id_claim")]
    public string IdClaim { get; set; } = "id";

    [JsonPropertyName("name_claim")]
    public string NameClaim { get; set; } = "name";

    [JsonPropertyName("email_claim")]
    public string EmailClaim { get; set; } = "email";
}
