using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// Remembers which external sign-in providers were registered when the Dashboard started.
/// Providers are only registered at startup, so the Settings page compares this snapshot
/// with the saved configuration to tell the administrator when a restart is needed.
/// Secrets are never part of the fingerprint.
/// </summary>
public sealed class RegisteredExternalProvidersSnapshot
{
    private readonly IReadOnlyDictionary<string, string> _fingerprints;
    private volatile bool _secretChanged;

    public RegisteredExternalProvidersSnapshot(IEnumerable<ExternalAuthProviderSettings> registered)
    {
        _fingerprints = registered
            .Where(provider => provider.Enabled)
            .ToDictionary(provider => provider.Id, Fingerprint, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Records that a provider secret was replaced or cleared since startup. The Engine never
    /// returns secrets, so this is the only way to know a restart is needed for a secret-only change.
    /// </summary>
    public void MarkSecretChanged() => _secretChanged = true;

    /// <summary>
    /// True when the saved providers differ from what is registered right now. Providers are
    /// only registered while the sign-in mode allows external sign-in, so a mode that does not
    /// is treated as "no providers".
    /// </summary>
    public bool DiffersFrom(IEnumerable<ExternalAuthProviderDto> saved, string mode)
    {
        if (_secretChanged)
        {
            return true;
        }

        var externalAllowed =
            !string.Equals(mode, "Local", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mode, "DisabledLocalOnly", StringComparison.OrdinalIgnoreCase);
        var current = saved
            .Where(provider => externalAllowed && provider.Enabled)
            .GroupBy(provider => provider.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => Fingerprint(group.Last()), StringComparer.OrdinalIgnoreCase);
        if (current.Count != _fingerprints.Count)
        {
            return true;
        }

        return current.Any(pair =>
            !_fingerprints.TryGetValue(pair.Key, out var registered)
            || !string.Equals(registered, pair.Value, StringComparison.Ordinal));
    }

    private static string Fingerprint(ExternalAuthProviderSettings p) => Join(
        p.Id, p.Kind, p.DisplayName, p.Issuer, p.Authority, p.ClientId, p.Scopes, p.UsePkce,
        p.AuthorizationEndpoint, p.TokenEndpoint, p.UserInformationEndpoint, p.IdClaim, p.NameClaim, p.EmailClaim);

    private static string Fingerprint(ExternalAuthProviderDto p) => Join(
        p.Id, p.Kind, p.DisplayName, p.Issuer, p.Authority, p.ClientId, p.Scopes, p.UsePkce,
        p.AuthorizationEndpoint, p.TokenEndpoint, p.UserInformationEndpoint, p.IdClaim, p.NameClaim, p.EmailClaim);

    private static string Join(
        string id, string kind, string displayName, string issuer, string authority, string clientId,
        IEnumerable<string> scopes, bool usePkce, string authorizationEndpoint, string tokenEndpoint,
        string userInformationEndpoint, string idClaim, string nameClaim, string emailClaim) =>
        string.Join('\u001f', new[]
        {
            id.Trim().ToLowerInvariant(), kind, displayName.Trim(), issuer.Trim(), authority.Trim(), clientId.Trim(),
            string.Join(' ', scopes.OrderBy(scope => scope, StringComparer.Ordinal)), usePkce.ToString(),
            authorizationEndpoint.Trim(), tokenEndpoint.Trim(), userInformationEndpoint.Trim(),
            idClaim.Trim(), nameClaim.Trim(), emailClaim.Trim(),
        });
}
