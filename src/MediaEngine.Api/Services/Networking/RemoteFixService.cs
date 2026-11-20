using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Networking;

public sealed class RemoteFixService(IConfigurationLoader configuration) : IRemoteFixService
{
    /// <summary>The port the bundled proxy preset (Tailscale Serve, the Caddy file) sends visitors to.</summary>
    public const int PresetProxyPort = 5017;

    public RemoteFixResultDto TrustProxy()
    {
        var settings = configuration.LoadNetwork();
        if (settings.Remote.ProxyPort is int existing)
        {
            // Already trusted: nothing to change, and nothing to restart for.
            return new RemoteFixResultDto { Action = RemoteCheckFixActions.TrustProxy, Changed = false, ProxyPort = existing };
        }

        // A proxy port must differ from the Dashboard's own port; step aside rather than collide.
        var port = settings.Local.Port == PresetProxyPort ? PresetProxyPort + 1 : PresetProxyPort;
        settings.Remote.ProxyPort = port;
        configuration.SaveNetwork(settings);
        return new RemoteFixResultDto { Action = RemoteCheckFixActions.TrustProxy, Changed = true, ProxyPort = port, RestartRequired = true };
    }
}
