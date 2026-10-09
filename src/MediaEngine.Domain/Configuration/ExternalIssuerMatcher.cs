namespace MediaEngine.Domain.Configuration;

/// <summary>
/// Single source of truth for comparing a token issuer against a configured
/// external sign-in provider. The Dashboard and the Engine both call this so
/// the two sides can never disagree.
/// </summary>
public static class ExternalIssuerMatcher
{
    public const string MicrosoftTenantError =
        "Use your Microsoft tenant ID instead of 'common', 'organizations' or 'consumers'.";

    /// <summary>
    /// When <paramref name="configuredIssuer"/> is set it must match exactly (ordinal, trimmed).
    /// When only <paramref name="configuredAuthority"/> is set (OIDC), a single
    /// trailing-slash difference is tolerated.
    /// </summary>
    public static bool Matches(string? configuredIssuer, string? configuredAuthority, string? tokenIssuer)
    {
        var token = tokenIssuer?.Trim();
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var issuer = configuredIssuer?.Trim();
        if (!string.IsNullOrEmpty(issuer))
        {
            return string.Equals(issuer, token, StringComparison.Ordinal);
        }

        var authority = configuredAuthority?.Trim();
        if (string.IsNullOrEmpty(authority))
        {
            return false;
        }

        return string.Equals(authority, token, StringComparison.Ordinal)
            || string.Equals(authority + "/", token, StringComparison.Ordinal)
            || string.Equals(authority, token + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns an error message when <paramref name="authority"/> points at a Microsoft
    /// multi-tenant endpoint whose token issuer is tenant-specific, otherwise null.
    /// </summary>
    public static string? ValidateMicrosoftAuthority(string? authority)
    {
        if (!Uri.TryCreate(authority?.Trim(), UriKind.Absolute, out var uri)
            || !uri.Host.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var tenant = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return tenant is not null
            && (tenant.Equals("common", StringComparison.OrdinalIgnoreCase)
                || tenant.Equals("organizations", StringComparison.OrdinalIgnoreCase)
                || tenant.Equals("consumers", StringComparison.OrdinalIgnoreCase))
            ? MicrosoftTenantError
            : null;
    }
}
