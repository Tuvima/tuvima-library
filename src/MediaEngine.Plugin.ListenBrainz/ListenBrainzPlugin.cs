using MediaEngine.Domain.Configuration;
using MediaEngine.Plugins;

namespace MediaEngine.Plugin.ListenBrainz;

/// <summary>
/// Sends the music a person finishes playing to their own ListenBrainz account (Plugin architecture spec PA6). Each profile links its
/// own token on its profile page; a profile that has not linked sends nothing. Plays are queued in the Library's retry queue, so being
/// offline never loses or delays one. A Vault play is never sent: the Library answers "nothing to share" for it.
/// </summary>
public sealed class ListenBrainzPlugin : ITuvimaPlugin
{
    public const string PluginId = "tuvima.listenbrainz";
    public const string TokenName = "user_token";
    public const string Host = "api.listenbrainz.org";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginId,
        Name = "ListenBrainz",
        Version = "0.1.0",
        Kind = PluginKind.Official,
        MinimumLibraryVersion = "1.0.0",
        Description = "Sends the music you finish playing to your own ListenBrainz account. Each profile links its own token; a profile that has not linked sends nothing, and nothing from the Vault is ever sent.",
        Category = "services",
        MediaTypes = ["music"],
        Capabilities =
        [
            new PluginCapabilityDescriptor
            {
                Kind = PluginCapabilityKinds.EventSubscriberKind,
                Name = "Finished plays",
                Description = "Notices when a profile finishes playing a song.",
            },
            new PluginCapabilityDescriptor
            {
                Kind = "outbox-delivery",
                Name = "ListenBrainz sender",
                Description = "Sends each queued play to ListenBrainz, trying again later if it is offline.",
            },
        ],
        Permissions =
        [
            PluginPermissionIds.WatchActivity,
            PluginPermissionIds.PlayDetails,
            PluginPermissionIds.PluginOutbox,
            PluginPermissionIds.NetworkHttp,
            PluginPermissionIds.PluginSecrets,
        ],
        Network =
        [
            new PluginNetworkDestination
            {
                Host = Host,
                Sends = "The title, artist, album and MusicBrainz ids of each song a linked profile finishes playing, and when. Nothing from the Vault, and nothing for a profile that has not linked its own token",
                MaxRequestsPerMinute = 30,
            },
        ],
        Secrets =
        [
            new PluginSecretDeclaration
            {
                Name = TokenName,
                Label = "ListenBrainz user token",
                Description = "Your own token from your ListenBrainz profile settings (listenbrainz.org/settings). It stays encrypted on this machine, only this profile can use it, and unlinking deletes it.",
            },
        ],
        SupportedPlatforms = [],
        Declaration = new PluginDeclaration
        {
            Audience = "everyone",
            Vault = "never",
            WeightClass = "light",
            OnFailure = "skip_and_retry",
            Notify = "tell_me",
            Stores = new PluginStores { Records = true, OnRemoval = "keep" },
            CredentialScopes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [TokenName] = "personal" },
            Changelog = "First version: sends finished plays to ListenBrainz for each profile that links its own token.",
        },
    };

    public IReadOnlyList<IPluginCapability> CreateCapabilities() =>
    [
        new ListenBrainzFinishedPlays(),
        new ListenBrainzDelivery(),
    ];
}
