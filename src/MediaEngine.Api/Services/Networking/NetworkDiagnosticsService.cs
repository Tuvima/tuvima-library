using System.Net;
using System.Net.Sockets;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Networking;

public sealed class NetworkDiagnosticsService : INetworkDiagnosticsService
{
    private readonly IConfigurationLoader _configuration;
    private readonly INetworkEnvironmentService _environment;
    private readonly NetworkRuntimeState _runtime;
    private readonly RouterPortMappingCoordinator _routerMappings;
    private readonly IReadOnlyDictionary<string, IRemoteConnectivityProvider> _remoteProviders;
    private readonly HttpClient _http;
    private readonly ILogger<NetworkDiagnosticsService> _logger;

    public NetworkDiagnosticsService(
        IConfigurationLoader configuration,
        INetworkEnvironmentService environment,
        NetworkRuntimeState runtime,
        RouterPortMappingCoordinator routerMappings,
        IEnumerable<IRemoteConnectivityProvider> remoteProviders,
        HttpClient http,
        ILogger<NetworkDiagnosticsService> logger)
    {
        _configuration = configuration;
        _environment = environment;
        _runtime = runtime;
        _routerMappings = routerMappings;
        _remoteProviders = remoteProviders.ToDictionary(provider => provider.Key, StringComparer.OrdinalIgnoreCase);
        _http = http;
        _logger = logger;
    }

    public async Task<NetworkTestResultDto> TestLocalAsync(CancellationToken ct)
    {
        var settings = _configuration.LoadNetwork();
        var addresses = _environment.GetUsableAddresses(settings.Local.Ipv6Enabled);
        var check = new NetworkTestCheckDto
        {
            Key = "local-server",
            Label = "Local server",
            Status = "failed",
            Detail = "Tuvima could not find an active local network address.",
        };

        foreach (var candidate in addresses)
        {
            if (await CanConnectAsync(candidate.Address, settings.Local.Port, ct))
            {
                check.Status = "passed";
                check.Detail = $"Tuvima responded at {FormatAddress(candidate.Address, settings.Local.Port)}.";
                break;
            }
        }

        var passed = check.Status == "passed";
        var result = new NetworkTestResultDto
        {
            Kind = "local",
            Status = passed ? "passed" : "failed",
            Headline = passed ? "Local access is working" : "Local access needs attention",
            Detail = passed
                ? "Devices on this network can reach Tuvima."
                : "The configured address did not respond. The Dashboard may still be starting or the selected port may be blocked.",
            TestedAt = DateTimeOffset.UtcNow,
            Checks = [check],
        };
        _runtime.RecordLocalTest(result);
        return result;
    }

    public async Task<NetworkTestResultDto> TestRemoteAsync(CancellationToken ct)
    {
        var settings = _configuration.LoadNetwork();
        var local = await TestLocalAsync(ct);
        var checks = new List<NetworkTestCheckDto>
        {
            new()
            {
                Key = "local-server",
                Label = "Local server",
                Status = local.Status,
                Detail = local.Checks.FirstOrDefault()?.Detail ?? local.Detail,
            },
        };

        if (!settings.AllowsInternet)
        {
            checks.Add(new NetworkTestCheckDto
            {
                Key = "remote-access",
                Label = "Remote access",
                Status = "skipped",
                Detail = "Remote access is turned off.",
            });
            var disabled = BuildRemoteResult("disabled", "Remote access is disabled", "Tuvima remains available on your local network.", checks);
            _runtime.RecordRemoteTest(disabled);
            return disabled;
        }

        if (settings.Remote.AutomaticRouterConfiguration
            && settings.Remote.ConnectionMode == NetworkConnectionModes.DirectOnly)
        {
            var mapping = await _routerMappings.EnsureMappingAsync(ct);
            checks.Add(new NetworkTestCheckDto
            {
                Key = "router-mapping",
                Label = "Automatic router configuration",
                Status = mapping.State == RouterMappingState.Active ? "passed"
                    : mapping.State is RouterMappingState.RouterRefused or RouterMappingState.Failed ? "failed" : "unknown",
                Detail = mapping.Message,
            });
        }

        if (settings.Remote.ConnectionMode == NetworkConnectionModes.Tailscale)
        {
            if (!_remoteProviders.TryGetValue("tailscale", out var provider))
            {
                checks.Add(new NetworkTestCheckDto
                {
                    Key = "tailscale",
                    Label = "Tailscale Serve",
                    Status = "failed",
                    Detail = "The Tailscale deployment provider is unavailable.",
                });
            }
            else
            {
                var snapshot = await provider.TestAsync(ct);
                _runtime.RecordRemoteProvider(snapshot);
                checks.Add(new NetworkTestCheckDto
                {
                    Key = "tailscale",
                    Label = "Tailscale Serve",
                    Status = snapshot.State == RemoteProviderState.Connected && snapshot.SecureHttps ? "passed" : "failed",
                    Detail = snapshot.Message,
                });
            }
        }
        else if (settings.Remote.ConnectionMode is NetworkConnectionModes.Custom or NetworkConnectionModes.DirectOnly
            && Uri.TryCreate(settings.Remote.PublicHostname, UriKind.Absolute, out var endpoint))
        {
            checks.Add(await TestCustomEndpointAsync(endpoint, ct));
        }
        else if (settings.Remote.ConnectionMode != NetworkConnectionModes.Tailscale)
        {
            checks.Add(new NetworkTestCheckDto
            {
                Key = "external-port",
                Label = "External reachability",
                Status = "unknown",
                Detail = "A trusted external connection checker is not configured, so Tuvima cannot verify this path from inside your home network.",
            });
        }

        var failed = checks.Any(check => check.Status == "failed");
        var passed = checks.All(check => check.Status is "passed" or "skipped");
        var result = BuildRemoteResult(
            failed ? "failed" : passed ? "passed" : "unknown",
            failed ? "Remote access needs attention" : passed ? "Remote access is working" : "Remote access could not be fully verified",
            failed
                ? "One or more connection checks failed. Review the failed step below."
                : passed
                    ? "Tuvima is reachable through the configured secure endpoint."
                    : "Local checks passed, but an external service is required to prove internet reachability.",
            checks);
        _runtime.RecordRemoteTest(result);
        return result;
    }

    public Task<PortAvailabilityResultDto> CheckPortAvailabilityAsync(int port, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (port is < 1 or > 65535)
        {
            return Task.FromResult(new PortAvailabilityResultDto
            {
                Port = port,
                Available = false,
                Message = "Choose a port between 1 and 65535.",
            });
        }

        if (_configuration.LoadNetwork().Local.Port == port)
        {
            return Task.FromResult(new PortAvailabilityResultDto
            {
                Port = port,
                Available = true,
                Message = "This is Tuvima's current configured port.",
            });
        }

        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            return Task.FromResult(new PortAvailabilityResultDto
            {
                Port = port,
                Available = true,
                Message = $"Port {port} is available. Restarting the Dashboard will be required after applying the change.",
            });
        }
        catch (SocketException ex)
        {
            _logger.LogInformation(ex, "Network port availability probe failed for {Port}", port);
            return Task.FromResult(new PortAvailabilityResultDto
            {
                Port = port,
                Available = false,
                Message = $"Port {port} is already being used. Choose another port.",
            });
        }
        finally
        {
            listener?.Stop();
        }
    }

    public Task<NetworkBandwidthStatusDto> TestBandwidthAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var settings = _configuration.LoadNetwork();
        var result = new NetworkBandwidthStatusDto
        {
            ReservedMbps = settings.Streaming.ReservedUploadMbps,
            Status = "unavailable",
        };
        _runtime.RecordBandwidth(result);
        return Task.FromResult(result);
    }

    private async Task<NetworkTestCheckDto> TestCustomEndpointAsync(Uri endpoint, CancellationToken ct)
    {
        if (endpoint.Scheme != Uri.UriSchemeHttps)
        {
            return new NetworkTestCheckDto
            {
                Key = "secure-connection",
                Label = "Secure connection",
                Status = "failed",
                Detail = "The custom address must use HTTPS.",
            };
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return new NetworkTestCheckDto
            {
                Key = "custom-endpoint",
                Label = "Custom HTTPS endpoint",
                Status = response.IsSuccessStatusCode || (int)response.StatusCode is >= 300 and < 400 ? "passed" : "failed",
                Detail = response.IsSuccessStatusCode || (int)response.StatusCode is >= 300 and < 400
                    ? "The configured HTTPS address responded."
                    : "The configured address responded, but it did not return a successful result.",
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            _logger.LogInformation(ex, "Custom remote endpoint test failed for {Host}", endpoint.Host);
            return new NetworkTestCheckDto
            {
                Key = "custom-endpoint",
                Label = "Custom HTTPS endpoint",
                Status = "failed",
                Detail = "Tuvima could not reach the configured HTTPS address.",
            };
        }
    }

    private static NetworkTestResultDto BuildRemoteResult(string status, string headline, string detail, List<NetworkTestCheckDto> checks) => new()
    {
        Kind = "remote",
        Status = status,
        Headline = headline,
        Detail = detail,
        TestedAt = DateTimeOffset.UtcNow,
        Checks = checks,
    };

    private static async Task<bool> CanConnectAsync(string address, int port, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var client = new TcpClient();
            await client.ConnectAsync(address, port, timeout.Token);
            return client.Connected;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    internal static string FormatAddress(string address, int port) =>
        address.Contains(':', StringComparison.Ordinal) ? $"http://[{address}]:{port}" : $"http://{address}:{port}";
}
