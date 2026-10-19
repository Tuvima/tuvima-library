using System.Text.Json;

namespace MediaEngine.Api.Services.Networking;

/// <summary>
/// Observes an independently installed Tailscale client. Tuvima never embeds a
/// VPN implementation and never reads or stores an auth key.
/// </summary>
public sealed class TailscaleRemoteConnectivityProvider : IRemoteConnectivityProvider
{
    private readonly ICommandRunner _commands;
    private readonly HttpClient _http;
    private readonly ILogger<TailscaleRemoteConnectivityProvider> _logger;

    public TailscaleRemoteConnectivityProvider(
        ICommandRunner commands,
        HttpClient http,
        ILogger<TailscaleRemoteConnectivityProvider> logger)
    {
        _commands = commands;
        _http = http;
        _logger = logger;
    }

    public string Key => "tailscale";
    public string DisplayName => "Tailscale";

    public Task<RemoteProviderSnapshot> GetStateAsync(CancellationToken ct) => ProbeAsync(ct);
    public Task<RemoteProviderSnapshot> TestAsync(CancellationToken ct) => ProbeAsync(ct);

    private async Task<RemoteProviderSnapshot> ProbeAsync(CancellationToken ct)
    {
        var configuredHealth = Environment.GetEnvironmentVariable("TUVIMA_TAILSCALE_HEALTH_URL");
        var configuredAddress = NormalizeHttpsAddress(Environment.GetEnvironmentVariable("TUVIMA_TAILSCALE_URL"));
        if (!string.IsNullOrWhiteSpace(configuredHealth))
        {
            try
            {
                using var response = await _http.GetAsync(configuredHealth, ct);
                if (response.IsSuccessStatusCode)
                {
                    var serveVerified = configuredAddress is not null
                        && await VerifyServeAsync(configuredAddress, ct).ConfigureAwait(false);
                    return new RemoteProviderSnapshot(
                        Key, DisplayName,
                        serveVerified ? RemoteProviderState.Connected : RemoteProviderState.Degraded,
                        configuredAddress,
                        configuredAddress is null
                            ? "Tailscale is connected, but the tailnet HTTPS address is not configured for this deployment preset."
                            : serveVerified
                                ? "Tailscale is connected and Serve HTTPS securely reaches this Dashboard."
                                : "Tailscale is connected, but Serve HTTPS could not be verified through the configured tailnet address.",
                        SecureHttps: serveVerified);
                }

                return new RemoteProviderSnapshot(
                    Key, DisplayName, RemoteProviderState.Degraded, configuredAddress,
                    "The Tailscale sidecar is present but not connected to a tailnet.");
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
            {
                if (ex is OperationCanceledException && ct.IsCancellationRequested)
                {
                    throw;
                }

                _logger.LogDebug(ex, "Tailscale sidecar health probe failed");
                return new RemoteProviderSnapshot(
                    Key, DisplayName, RemoteProviderState.Error, configuredAddress,
                    "The Tailscale sidecar health endpoint did not respond.");
            }
        }

        var executable = Environment.GetEnvironmentVariable("TUVIMA_TAILSCALE_CLI") ?? "tailscale";
        try
        {
            var status = await _commands.RunAsync(executable, ["status", "--json"], TimeSpan.FromSeconds(5), ct);
            if (status.ExitCode != 0)
            {
                return new RemoteProviderSnapshot(
                    Key, DisplayName, RemoteProviderState.Error, null,
                    "Tailscale is installed but did not return a usable status.");
            }

            using var document = JsonDocument.Parse(status.StandardOutput);
            var root = document.RootElement;
            var backendState = GetString(root, "BackendState");
            var dnsName = root.TryGetProperty("Self", out var self) ? GetString(self, "DNSName") : null;
            var address = NormalizeHttpsAddress(dnsName?.TrimEnd('.'));
            if (!string.Equals(backendState, "Running", StringComparison.OrdinalIgnoreCase))
            {
                return new RemoteProviderSnapshot(
                    Key, DisplayName,
                    string.Equals(backendState, "Starting", StringComparison.OrdinalIgnoreCase)
                        ? RemoteProviderState.Connecting
                        : RemoteProviderState.Unconfigured,
                    address,
                    $"Tailscale is {backendState?.ToLowerInvariant() ?? "not signed in"}.");
            }

            var serve = await _commands.RunAsync(executable, ["serve", "status", "--json"], TimeSpan.FromSeconds(5), ct);
            var serveHttps = serve.ExitCode == 0
                && serve.StandardOutput.Contains("https", StringComparison.OrdinalIgnoreCase);
            return new RemoteProviderSnapshot(
                Key, DisplayName, serveHttps ? RemoteProviderState.Connected : RemoteProviderState.Degraded,
                address,
                serveHttps
                    ? "Tailscale is connected and Serve HTTPS is active."
                    : "Tailscale is connected, but Serve HTTPS is not active for Library.",
                SecureHttps: serveHttps && address is not null);
        }
        catch (FileNotFoundException)
        {
            return new RemoteProviderSnapshot(
                Key, DisplayName, RemoteProviderState.Unconfigured, null,
                "Tailscale is not installed. Use the supported deployment preset or install it on this host.");
        }
        catch (Exception ex) when (ex is JsonException or TimeoutException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Tailscale status could not be read");
            return new RemoteProviderSnapshot(
                Key, DisplayName, RemoteProviderState.Error, null,
                "Tailscale status could not be verified.");
        }
    }

    private static string? NormalizeHttpsAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            return absolute.Scheme == Uri.UriSchemeHttps ? absolute.GetLeftPart(UriPartial.Authority) : null;
        }

        return Uri.TryCreate($"https://{value}", UriKind.Absolute, out var hostname)
            ? hostname.GetLeftPart(UriPartial.Authority)
            : null;
    }

    private async Task<bool> VerifyServeAsync(string address, CancellationToken ct)
    {
        var nonce = Guid.NewGuid().ToString("N");
        using var response = await _http.GetAsync($"{address}/_tuvima/remote-probe?nonce={nonce}", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = document.RootElement;
        return string.Equals(GetString(root, "product"), "Tuvima Library", StringComparison.Ordinal)
            && string.Equals(GetString(root, "nonce"), nonce, StringComparison.Ordinal)
            && root.TryGetProperty("secure", out var secure)
            && secure.ValueKind == JsonValueKind.True;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
