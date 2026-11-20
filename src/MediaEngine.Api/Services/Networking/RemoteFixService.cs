using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Networking;

/// <summary>
/// The safe one-click fixes the guided away-from-home flow offers for a failed check (spec remote-access D12). Each one changes
/// a single setting, only ever makes outside visitors easier to tell apart from people at home, and can be repeated.
/// </summary>
public interface IRemoteFixService
{
    /// <summary>
    /// Lets Library trust the proxy or tunnel running on this computer: the Dashboard listens on its dedicated proxy port, where
    /// every visitor counts as outside and forwarded visitor details are believed (from this computer only).
    /// </summary>
    RemoteFixResultDto TrustProxy();
}

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
