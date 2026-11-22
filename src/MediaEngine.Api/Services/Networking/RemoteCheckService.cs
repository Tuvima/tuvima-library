using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Networking;

/// <summary>
/// Ties together what the Engine already detects (Tailscale, the router mapping, shared-address providers, the person's own
/// address and who has a password) into plain findings and one suggested way in. It reads only: nothing is saved or changed,
/// and the only outside contact is one lookup and one secure handshake to the address the person has already chosen.
/// </summary>
public sealed class RemoteCheckService(
    IConfigurationLoader configuration,
    NetworkStatusService status,
    INetworkTopologyService topology,
    NetworkRuntimeState runtime,
    IEnumerable<IRemoteConnectivityProvider> providers,
    IRemoteAddressProbe addressProbe,
    IRemoteSignInReadinessReader signIn,
    IOutsideSignInHistory outsideSignIns,
    TimeProvider time) : IRemoteCheckService
{
    private static readonly TimeSpan CertificateWarningWindow = TimeSpan.FromDays(14);

    public async Task<RemoteCheckResultDto> CheckAsync(bool skipRequested, CancellationToken ct)
    {
        var settings = configuration.LoadNetwork();
        var tailscale = await ReadTailscaleAsync(ct).ConfigureAwait(false);
        var networkStatus = status.GetStatus();
        var routerSnapshot = topology.GetSnapshot();
        var sharedAddress = networkStatus.CgnatSuspected;

        var findings = new List<RemoteCheckFindingDto>();
        AddTailscale(findings, tailscale);
        if (sharedAddress)
        {
            findings.Add(Finding(RemoteCheckFindingKeys.SharedAddress, RemoteCheckStatuses.Warn,
                "Your internet provider shares one address between many homes",
                "Opening a door in your router will not work, because the address your router gets is not yours alone.",
                "Use Tailscale, or a tunnel or proxy that does not need your router."));
        }

        await AddOwnAddressAsync(findings, settings, networkStatus, ct).ConfigureAwait(false);
        // The router door comes last of the ways in: it is the hardest to get right and the first to fail.
        AddRouterDoor(findings, networkStatus, routerSnapshot, sharedAddress);
        await AddPasswordsAsync(findings, ct).ConfigureAwait(false);
        await AddOutsideVisitorsAsync(findings, settings, ct).ConfigureAwait(false);

        var (suggestion, reason) = Suggest(settings, tailscale, sharedAddress, skipRequested);
        return new RemoteCheckResultDto
        {
            CheckedAt = time.GetUtcNow(),
            Suggestion = suggestion,
            SuggestionReason = reason,
            Findings = findings,
        };
    }

    // A fresh look, so "Check again" after installing or signing in to Tailscale shows the change straight away.
    private async Task<RemoteProviderSnapshot?> ReadTailscaleAsync(CancellationToken ct)
    {
        var provider = providers.FirstOrDefault(candidate => string.Equals(candidate.Key, "tailscale", StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            return runtime.GetRemoteProvider("tailscale");
        }

        var snapshot = await provider.GetStateAsync(ct).ConfigureAwait(false);
        runtime.RecordRemoteProvider(snapshot);
        return snapshot;
    }

    private static bool TailscaleSignedIn(RemoteProviderSnapshot? tailscale) =>
        tailscale is { State: RemoteProviderState.Connected or RemoteProviderState.Degraded };

    private static void AddTailscale(List<RemoteCheckFindingDto> findings, RemoteProviderSnapshot? tailscale)
    {
        if (tailscale is null || tailscale.NotInstalled)
        {
            findings.Add(Finding(RemoteCheckFindingKeys.TailscaleNotInstalled, RemoteCheckStatuses.Info,
                "Tailscale is not installed on this computer",
                "Tailscale is a free app that gives you a private, secure way to reach Library from your phone or laptop.",
                "Install Tailscale on this computer and on the phone you will use away from home."));
            return;
        }

        switch (tailscale.State)
        {
            case RemoteProviderState.Connected when tailscale.SecureHttps:
                findings.Add(Finding(RemoteCheckFindingKeys.TailscaleReady, RemoteCheckStatuses.Ok,
                    "Tailscale is ready",
                    tailscale.PublicAddress is null
                        ? "Tailscale is signed in and its secure address is on."
                        : $"Tailscale is signed in and Library is reachable at {tailscale.PublicAddress}.",
                    null));
                break;
            case RemoteProviderState.Connected:
            case RemoteProviderState.Degraded:
                findings.Add(Finding(RemoteCheckFindingKeys.TailscaleHttpsOff, RemoteCheckStatuses.Warn,
                    "Tailscale is signed in, but its secure address is off",
                    "Library needs Tailscale's secure (HTTPS) address so that sign-in is protected on the way.",
                    "Turn on HTTPS in your Tailscale account settings, then check again."));
                break;
            case RemoteProviderState.Connecting:
                findings.Add(Finding(RemoteCheckFindingKeys.TailscaleNotSignedIn, RemoteCheckStatuses.Info,
                    "Tailscale is still starting",
                    "Tailscale is installed and is connecting now.",
                    "Wait a moment, then check again."));
                break;
            case RemoteProviderState.Error:
                findings.Add(Finding(RemoteCheckFindingKeys.TailscaleNotSignedIn, RemoteCheckStatuses.Warn,
                    "Tailscale is installed but not answering",
                    "Library could not read Tailscale's status, so it cannot tell whether you are signed in.",
                    "Open the Tailscale app on this computer and make sure you are signed in, then check again."));
                break;
            default:
                findings.Add(Finding(RemoteCheckFindingKeys.TailscaleNotSignedIn, RemoteCheckStatuses.Warn,
                    "Tailscale is installed but you are not signed in",
                    "Tailscale needs you to sign in before it can connect this computer.",
                    "Open the Tailscale app on this computer and sign in, then check again."));
                break;
        }
    }

    private async Task AddOwnAddressAsync(
        List<RemoteCheckFindingDto> findings, NetworkSettings settings, NetworkRuntimeStatusDto networkStatus, CancellationToken ct)
    {
        var mode = settings.Remote.ConnectionMode?.Trim();
        if (string.Equals(mode, NetworkConnectionModes.Custom, StringComparison.OrdinalIgnoreCase)
            && settings.Remote.ProxyPort is null)
        {
            findings.Add(Finding(RemoteCheckFindingKeys.ProxyNotTrusted, RemoteCheckStatuses.Warn,
                "Library does not trust your proxy yet",
                "Visitors who arrive through a proxy or tunnel look like they are on this computer, so Library cannot tell them from you.",
                "Let Library trust the proxy running on this computer.",
                RemoteCheckFixActions.TrustProxy));
        }

        if (mode is null
            || !(string.Equals(mode, NetworkConnectionModes.Custom, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(mode, NetworkConnectionModes.DirectOnly, StringComparison.OrdinalIgnoreCase))
            || !Uri.TryCreate(settings.Remote.PublicHostname, UriKind.Absolute, out var address)
            || address.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        var probe = await addressProbe.ProbeAsync(address, ct).ConfigureAwait(false);
        if (!probe.NameResolves)
        {
            findings.Add(Finding(RemoteCheckFindingKeys.NameNotPointingHere, RemoteCheckStatuses.Fail,
                $"{address.Host} does not point anywhere yet",
                "The name has no address behind it, so nobody can find Library with it.",
                "Add a record for this name at the company where you bought it, pointing to your home address or your tunnel."));
            return;
        }

        if (string.Equals(mode, NetworkConnectionModes.DirectOnly, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(networkStatus.PublicIp)
            && !probe.Addresses.Contains(networkStatus.PublicIp, StringComparer.OrdinalIgnoreCase))
        {
            findings.Add(Finding(RemoteCheckFindingKeys.NameNotPointingHere, RemoteCheckStatuses.Fail,
                $"{address.Host} points somewhere else",
                $"The name points to {string.Join(", ", probe.Addresses)}, but your home's public address is {networkStatus.PublicIp}.",
                "Change the record for this name to your home's public address."));
            return;
        }

        if (!probe.Reachable)
        {
            findings.Add(Finding(RemoteCheckFindingKeys.PublicAddressUnreachable, RemoteCheckStatuses.Fail,
                $"Library could not reach {address.Host}",
                "The name was found, but nothing answered on it from this computer. Some routers cannot loop back to their own address, so this can also be a false alarm.",
                "Check the address from your phone off the home Wi-Fi, and check that your tunnel, proxy or router door is running."));
            return;
        }

        if (probe.CertificateTrusted == false)
        {
            findings.Add(Finding(RemoteCheckFindingKeys.CertificateInvalid, RemoteCheckStatuses.Fail,
                "The security certificate is not valid",
                "Browsers and apps will warn people away, or refuse to connect, because the certificate does not match this name or has run out.",
                "Renew or replace the certificate for this name in your proxy or tunnel."));
        }
        else if (probe.CertificateExpiresAt is { } expiresAt && expiresAt - time.GetUtcNow() <= CertificateWarningWindow)
        {
            findings.Add(Finding(RemoteCheckFindingKeys.CertificateExpiring, RemoteCheckStatuses.Warn,
                "The security certificate runs out soon",
                $"It is valid until {expiresAt:yyyy-MM-dd}. After that, apps and browsers will refuse to connect.",
                "Check that your proxy or tunnel renews its certificate by itself."));
        }
    }

    private static void AddRouterDoor(
        List<RemoteCheckFindingDto> findings, NetworkRuntimeStatusDto networkStatus, NetworkTopologySnapshot router, bool sharedAddress)
    {
        if (sharedAddress)
        {
            return;
        }

        if (networkStatus.MappingStatus == "active")
        {
            findings.Add(Finding(RemoteCheckFindingKeys.RouterDoorReady, RemoteCheckStatuses.Ok,
                "Your router opened a door for Library",
                networkStatus.RouterMethod is null
                    ? "The router accepted Library's request."
                    : $"The router accepted Library's request ({networkStatus.RouterMethod}).",
                null));
            return;
        }

        var failed = networkStatus.MappingStatus is "router-refused" or "failed" or "protocol-unavailable" or "unsupported-topology" or "expired";
        if (failed || !router.SupportsRouterDiscovery)
        {
            findings.Add(Finding(RemoteCheckFindingKeys.RouterDoorUnavailable,
                failed ? RemoteCheckStatuses.Warn : RemoteCheckStatuses.Info,
                "Library cannot open a door in your router by itself",
                failed
                    ? "The router did not accept Library's request, or it does not allow this."
                    : "Library cannot see your router from here, for example because it runs inside a container.",
                "Use Tailscale, or open the door by hand in your router's settings."));
        }
    }

    private async Task AddPasswordsAsync(List<RemoteCheckFindingDto> findings, CancellationToken ct)
    {
        var readiness = await signIn.ReadAsync(ct).ConfigureAwait(false);
        if (readiness.NoAdministratorHasPassword)
        {
            findings.Add(Finding(RemoteCheckFindingKeys.AdminWithoutPassword, RemoteCheckStatuses.Fail,
                "The server administrator has no password",
                "Away from home everyone signs in with a password or passkey, and nobody who looks after Library has one yet.",
                "Add a password or passkey to the server administrator's sign-in."));
        }

        if (readiness.HomeOnlyProfileNames.Count > 0)
        {
            findings.Add(Finding(RemoteCheckFindingKeys.ProfilesWithoutPassword, RemoteCheckStatuses.Warn,
                readiness.HomeOnlyProfileNames.Count == 1
                    ? "One person can only sign in at home"
                    : $"{readiness.HomeOnlyProfileNames.Count} people can only sign in at home",
                $"{NameList(readiness.HomeOnlyProfileNames)} no password or passkey, so Library will not let them in from outside.",
                "Set a password for each of them if they should be able to sign in away from home."));
        }
    }

    // Said only once away-from-home access is on and nobody has yet signed in from outside: a way to tell "set up" from "proven".
    private async Task AddOutsideVisitorsAsync(List<RemoteCheckFindingDto> findings, NetworkSettings settings, CancellationToken ct)
    {
        if (!settings.Remote.HasOutsidePath || (await outsideSignIns.GetRecentAsync(1, ct).ConfigureAwait(false)).Count > 0)
        {
            return;
        }

        findings.Add(Finding(RemoteCheckFindingKeys.NoOutsideVisitorsSeen, RemoteCheckStatuses.Info,
            "Nobody has signed in from outside yet",
            "Library has not seen anyone sign in from outside your home network, so it cannot tell yet whether the way in works.",
            "Open Library on a phone that is off your home Wi-Fi and sign in, then check again."));
    }

    private static string NameList(IReadOnlyList<string> names)
    {
        var shown = names.Take(3).ToList();
        var rest = names.Count - shown.Count;
        var list = shown.Count == 1
            ? shown[0]
            : string.Join(", ", shown.Take(shown.Count - 1)) + " and " + shown[^1];
        if (rest > 0)
        {
            list = string.Join(", ", shown) + $" and {rest} more";
        }

        return names.Count == 1 ? $"{list} has" : $"{list} have";
    }

    private static (string Suggestion, string Reason) Suggest(
        NetworkSettings settings, RemoteProviderSnapshot? tailscale, bool sharedAddress, bool skipRequested)
    {
        var mode = settings.Remote.ConnectionMode?.Trim();
        if (skipRequested && string.Equals(mode, NetworkConnectionModes.LocalOnly, StringComparison.OrdinalIgnoreCase))
        {
            return (RemoteCheckSuggestions.NotNow, "You chose to leave away-from-home access for later. Library keeps working at home.");
        }

        if (!string.IsNullOrWhiteSpace(settings.Remote.PublicHostname)
            && !string.Equals(mode, NetworkConnectionModes.Tailscale, StringComparison.OrdinalIgnoreCase))
        {
            return (RemoteCheckSuggestions.OwnAddress, "You already have your own address set up, so Library will check that first.");
        }

        if (TailscaleSignedIn(tailscale))
        {
            return (RemoteCheckSuggestions.Tailscale, "Tailscale is already installed and signed in on this computer, so it is the easiest way in.");
        }

        if (sharedAddress)
        {
            return (RemoteCheckSuggestions.Tailscale,
                "Your internet provider shares one address between many homes, so opening your router will not work. Tailscale does not need your router.");
        }

        if (tailscale is { NotInstalled: false })
        {
            return (RemoteCheckSuggestions.Tailscale, "Tailscale is installed on this computer, so signing in to it is the only step left.");
        }

        return (RemoteCheckSuggestions.Tailscale,
            "Tailscale is the easiest way to reach Library away from home. It is free and takes about five minutes to set up.");
    }

    private static RemoteCheckFindingDto Finding(string key, string statusValue, string title, string detail, string? nextStep, string? fixAction = null) => new()
    {
        Key = key,
        Status = statusValue,
        Title = title,
        Detail = detail,
        NextStep = nextStep,
        FixAction = fixAction,
    };
}
