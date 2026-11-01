using MediaEngine.Domain.Configuration;

namespace MediaEngine.Plugins;

/// <summary>The one set of rules for a credential's <c>sign_in</c> block (Plugin architecture spec C13).</summary>
public static class PluginDeviceSignInRules
{
    /// <summary>Returns a plain load-error message, or null when the credential's sign-in is fine.</summary>
    public static string? Validate(PluginManifest manifest, PluginSecretDeclaration secret)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.SignIn is not { } signIn)
        {
            return null;
        }

        var name = secret.Name;
        if (!PluginDeclarationRules.IsPersonalCredential(manifest.Declaration, name))
        {
            return $"Plugin manifest gives the secret '{name}' a sign-in, but only a personal credential can be signed in to.";
        }

        if (!string.Equals(signIn.Method, PluginDeviceSignInMethods.DeviceCode, StringComparison.OrdinalIgnoreCase))
        {
            return $"Plugin manifest sign-in for '{name}' uses an unknown method '{signIn.Method}'. Use {PluginDeviceSignInMethods.DeviceCode}.";
        }

        if (!manifest.Network.Any(destination => string.Equals(destination.Host, signIn.Host, StringComparison.OrdinalIgnoreCase)))
        {
            return $"Plugin manifest sign-in for '{name}' contacts '{signIn.Host}', which is not one of its network hosts.";
        }

        if (signIn.Host.StartsWith("*.", StringComparison.Ordinal))
        {
            return $"Plugin manifest sign-in for '{name}' needs one exact host, not a wildcard.";
        }

        foreach (var (what, path) in new[] { ("code_path", signIn.CodePath), ("token_path", signIn.TokenPath), ("refresh_path", signIn.RefreshPath ?? "/") })
        {
            if (!IsPath(path))
            {
                return $"Plugin manifest sign-in for '{name}' has an invalid {what}. Use a path that starts with '/'.";
            }
        }

        foreach (var clientSecret in new[] { signIn.ClientIdSecret, signIn.ClientSecretSecret })
        {
            if (clientSecret is null)
            {
                continue;
            }

            var declared = manifest.Secrets.FirstOrDefault(other => string.Equals(other.Name, clientSecret, StringComparison.OrdinalIgnoreCase));
            if (declared is null || PluginDeclarationRules.IsPersonalCredential(manifest.Declaration, clientSecret))
            {
                return $"Plugin manifest sign-in for '{name}' names '{clientSecret}', which must be a household secret the plugin declares.";
            }
        }

        if (string.IsNullOrWhiteSpace(signIn.ClientIdSecret))
        {
            return $"Plugin manifest sign-in for '{name}' needs a client_id_secret.";
        }

        return null;
    }

    private static bool IsPath(string? path) =>
        !string.IsNullOrEmpty(path) && path[0] == '/' && !path.Contains("//", StringComparison.Ordinal) && !path.Contains("..", StringComparison.Ordinal)
        && !path.Any(char.IsControl) && !path.Contains('?', StringComparison.Ordinal) && !path.Contains('#', StringComparison.Ordinal);
}
