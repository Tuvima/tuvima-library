using MediaEngine.Api.Services.Collections.ListImport.Sources;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Plugins.Trakt;

/// <summary>
/// The Trakt plugin's switch and its app's client ID, read fresh on every use so a change on the Plugins page applies at once.
/// The client ID is the household secret an administrator entered for the plugin; Library ships none.
/// </summary>
public sealed class PluginTraktSettings(PluginCatalog catalog, IConfigSecretsStore secrets) : ITraktSettings
{
    public async Task<TraktSetup> LoadAsync(CancellationToken ct)
    {
        if (catalog.Get(TraktPlugin.PluginId) is not { Enabled: true })
        {
            return new TraktSetup(PluginOn: false, ClientId: null);
        }

        var clientId = await secrets.GetAsync(SecretOwner.Plugin(TraktPlugin.PluginId), TraktPlugin.ClientIdName, ct).ConfigureAwait(false);
        return new TraktSetup(PluginOn: true, string.IsNullOrWhiteSpace(clientId) ? null : clientId.Trim());
    }
}

/// <summary>
/// A profile's own Trakt token, from the Trakt plugin's per-profile connection. It asks for the profile it is given and no other,
/// and the token goes nowhere except to Trakt in a request's header.
/// </summary>
public sealed class PluginTraktAccessTokens(PluginDeviceSignInService signIn, PluginCatalog catalog) : ITraktAccessTokens
{
    public async Task<string?> TokenAsync(Guid? profileId, CancellationToken ct)
    {
        if (profileId is not { } profile || catalog.Get(TraktPlugin.PluginId) is not { Enabled: true })
        {
            return null;
        }

        return await signIn.AccessTokenAsync(TraktPlugin.PluginId, profile, TraktPlugin.AccountName, ct).ConfigureAwait(false);
    }

    public Task<bool> HasAccountAsync(Guid profileId, CancellationToken ct) =>
        signIn.HasTokenAsync(TraktPlugin.PluginId, profileId, TraktPlugin.AccountName, ct);
}
