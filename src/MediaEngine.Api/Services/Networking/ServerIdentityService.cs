using MediaEngine.Contracts.Setup;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Networking;

/// <summary>
/// Reads and writes how this server introduces itself: the friendly Library name (<c>core.json</c>) and the short
/// network name (<c>network.json</c>). Setup and Settings &gt; General share it, so the rule that the network name
/// follows the friendly name until someone sets it by hand lives in one place.
/// </summary>
public static class ServerIdentityService
{
    public const int MaxServerNameLength = 64;

    public static SetupServerIdentityDto Read(IConfigurationLoader configuration)
    {
        var serverName = configuration.LoadCore().ServerName;
        var hostName = configuration.LoadNetwork().Local.PreferredServerName;
        return new SetupServerIdentityDto(serverName, hostName, NetworkNameRules.IsDerived(serverName, hostName));
    }

    /// <summary>Saves the identity, or returns the one-sentence reason it was refused.</summary>
    public static (SetupServerIdentityDto? Saved, string? Error) Save(IConfigurationLoader configuration, SetupServerIdentityDto request)
    {
        var serverName = (request.ServerName ?? string.Empty).Trim();
        if (serverName.Length is 0 or > MaxServerNameLength)
        {
            return (null, "Give the server a name of 1 to 64 characters.");
        }

        // While the network name just follows the friendly name the Engine derives it; a hand-set one is kept as typed.
        var hostName = request.HostNameDerived ? string.Empty : NetworkNameRules.ToLabel(request.HostName);
        if (hostName.Length == 0)
        {
            hostName = NetworkNameRules.ToLabel(serverName);
        }
        if (hostName.Length == 0)
        {
            return (null, "The network name needs at least one letter or number.");
        }

        var core = configuration.LoadCore();
        core.ServerName = serverName;
        configuration.SaveCore(core);
        var network = configuration.LoadNetwork();
        network.Local.PreferredServerName = hostName;
        configuration.SaveNetwork(network);
        return (new SetupServerIdentityDto(serverName, hostName, NetworkNameRules.IsDerived(serverName, hostName)), null);
    }
}
