using MediaEngine.Domain.Configuration;

namespace MediaEngine.Plugins;

/// <summary>
/// One key or login a plugin needs, in the single shape both file formats map to (Plugins and Integrations screens v5 D48): the
/// manifest's <c>secrets</c> and a provider source file's <c>onboarding.credentials</c>.
/// </summary>
public sealed record PluginCredential(
    string Name,
    string Label,
    string Help,
    bool Required,
    int? MinimumLength,
    int? MaximumLength,
    string? Pattern,
    string Scope,
    string? SignInMethod);

/// <summary>The signup, help, terms and privacy addresses a plugin gives (empty when it gives none).</summary>
public sealed record PluginLinks(string? SignupUrl, string? HelpUrl, string? TermsUrl, string? PrivacyUrl)
{
    public static PluginLinks None { get; } = new(null, null, null, null);
}

/// <summary>The credentials list and links of one plugin.</summary>
public sealed record PluginCredentialSet(IReadOnlyList<PluginCredential> Credentials, PluginLinks Links)
{
    public static PluginCredentialSet Empty { get; } = new([], PluginLinks.None);
}

/// <summary>Maps the two plugin file formats onto the one credentials list.</summary>
public static class PluginCredentialMapper
{
    /// <summary>From a plugin manifest: <c>secrets</c>, with each secret's scope taken from the declaration and the links from <c>links</c>.</summary>
    public static PluginCredentialSet FromManifest(PluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var credentials = manifest.Secrets.Select(secret => new PluginCredential(
            secret.Name,
            string.IsNullOrWhiteSpace(secret.Label) ? secret.Name : secret.Label,
            secret.Description,
            Required: true,
            MinimumLength: null,
            MaximumLength: null,
            Pattern: null,
            Scope: PluginDeclarationRules.IsPersonalCredential(manifest.Declaration, secret.Name) ? PluginSettingScopes.Profile : PluginSettingScopes.Household,
            SignInMethod: secret.SignIn?.Method)).ToList();
        var links = manifest.Links is { } declared
            ? new PluginLinks(Clean(declared.SignupUrl), Clean(declared.HelpUrl), Clean(declared.TermsUrl), Clean(declared.PrivacyUrl))
            : PluginLinks.None;
        return new PluginCredentialSet(credentials, links);
    }

    /// <summary>From a provider source file: <c>onboarding.credentials</c> and the <c>onboarding</c> links. A key the person supplies is shared by the household.</summary>
    public static PluginCredentialSet FromSourceFile(ProviderConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Onboarding is not { } onboarding)
        {
            return PluginCredentialSet.Empty;
        }

        var credentials = onboarding.Credentials
            .Where(credential => string.Equals(credential.Ownership, "user_supplied", StringComparison.OrdinalIgnoreCase))
            .Select(credential => new PluginCredential(
                credential.Key,
                string.IsNullOrWhiteSpace(credential.Label) ? credential.Key : credential.Label,
                credential.FormatHint ?? "",
                credential.Required,
                credential.MinimumLength,
                credential.MaximumLength,
                credential.ValidationPattern,
                PluginSettingScopes.Household,
                SignInMethod: null)).ToList();
        return new PluginCredentialSet(
            credentials,
            new PluginLinks(Clean(onboarding.SignupUrl), Clean(onboarding.HelpUrl), Clean(onboarding.TermsUrl), Clean(onboarding.PrivacyUrl)));
    }

    private static string? Clean(string? url) => string.IsNullOrWhiteSpace(url) ? null : url.Trim();
}
