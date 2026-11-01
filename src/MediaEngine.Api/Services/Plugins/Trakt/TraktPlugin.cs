using MediaEngine.Domain.Configuration;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins.Trakt;

/// <summary>
/// Trakt, as a plugin (Collections and lists spec D7, WP6e; Plugin architecture spec C13). It reads a Trakt list or watchlist from
/// its address so the list can become a collection. A public list needs only the app's client id. A private list, a person's own
/// lists (<c>users/me</c>) and their own watchlist need that person to sign in to Trakt: each profile approves a short code on
/// another device, and its token is kept as that profile's own credential. The app's client id and client secret are the person's
/// own, from a free Trakt app they create; Library ships none.
/// </summary>
public sealed class TraktPlugin : ITuvimaPlugin
{
    public const string PluginId = "tuvima.trakt";
    public const string Host = "api.trakt.tv";

    /// <summary>The household secret that holds the Trakt app's client id.</summary>
    public const string ClientIdName = "client_id";

    /// <summary>The household secret that holds the Trakt app's client secret.</summary>
    public const string ClientSecretName = "client_secret";

    /// <summary>The personal credential each profile signs in to: its Trakt token.</summary>
    public const string AccountName = "account";

    /// <summary>Where each profile signs in to Trakt in the Dashboard (Settings → Profile → Accounts).</summary>
    public const string SignInPagePath = "/settings/overview/accounts";

    /// <summary>The most requests a minute the plugin asks to make to Trakt.</summary>
    public const int RequestsPerMinute = 120;

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "Trakt",
        Version = "0.1.0",
        Kind = PluginKind.Official,
        MinimumLibraryVersion = "1.0.0",
        Description = "Reads Trakt lists and watchlists so they can become collections. Public lists need only your Trakt app's client ID. To read your own lists, your watchlist or a private list, each profile signs in to its own Trakt account by approving a code on another device.",
        Category = "services",
        MediaTypes = ["movies", "tv"],
        Capabilities =
        [
            new PluginCapabilityDescriptor
            {
                Kind = "list-source",
                Name = "Trakt lists",
                Description = "Reads a Trakt list or watchlist from its address, for a collection and its refresh.",
            },
        ],
        Permissions =
        [
            PluginPermissionIds.NetworkHttp,
            PluginPermissionIds.PluginSecrets,
        ],
        Network =
        [
            new PluginNetworkDestination
            {
                Host = Host,
                Sends = "The list's address and identifiers, your Trakt app's client ID and, for your own or a private list, the token of the profile that signed in",
                MaxRequestsPerMinute = RequestsPerMinute,
            },
        ],
        Secrets =
        [
            new PluginSecretDeclaration
            {
                Name = ClientIdName,
                Label = "Trakt app client ID",
                Description = "The Client ID of a free Trakt app you create at trakt.tv/oauth/applications. Library does not include one.",
            },
            new PluginSecretDeclaration
            {
                Name = ClientSecretName,
                Label = "Trakt app client secret",
                Description = "The Client Secret of the same Trakt app. It is needed to sign in to Trakt and stays on this machine.",
            },
            new PluginSecretDeclaration
            {
                Name = AccountName,
                Label = "Your Trakt account",
                Description = "Your own Trakt sign-in, approved on another device. It stays encrypted on this machine, only this profile can use it, and signing out deletes it.",
                SignIn = new PluginDeviceSignIn
                {
                    Method = PluginDeviceSignInMethods.DeviceCode,
                    Host = Host,
                    CodePath = "/oauth/device/code",
                    TokenPath = "/oauth/device/token",
                    RefreshPath = "/oauth/token",
                    ClientIdSecret = ClientIdName,
                    ClientSecretSecret = ClientSecretName,
                    ClientIdHeader = "trakt-api-key",
                    Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["trakt-api-version"] = "2" },
                },
            },
        ],
        SupportedPlatforms = [],
        Declaration = new PluginDeclaration
        {
            Audience = "adults",
            Vault = "never",
            WeightClass = "light",
            OnFailure = "skip_and_retry",
            Notify = "tell_me",
            Stores = new PluginStores { Records = true, OnRemoval = "keep" },
            CredentialScopes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [ClientIdName] = "household",
                [ClientSecretName] = "household",
                [AccountName] = "personal",
            },
            Changelog = "Trakt is now a plugin: each profile can sign in to read its own and private lists. Public lists work as before.",
        },
    };

    public IReadOnlyList<IPluginCapability> CreateCapabilities() => [];
}
