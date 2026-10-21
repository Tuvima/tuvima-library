using System.Text;
using System.Text.Json;

namespace MediaEngine.Api.Services.Plugins.Service;

internal enum ServiceCallStatus
{
    Succeeded,

    /// <summary>The plugin has no program running and healthy right now.</summary>
    NotRunning,

    /// <summary>The program answered with an error, too slowly, or with something that is not JSON.</summary>
    Failed,
}

internal sealed record ServiceLookupResult(ServiceCallStatus Status, string Message, string? Json = null);

/// <summary>
/// The Library asking a plugin program to look something up (<c>GET {lookup_path}?title=...</c>). The program's answer is
/// kept to a size limit and must be JSON; anything else is reported as a plain failure and never passed on.
/// </summary>
internal sealed class PluginServiceClient
{
    private const int MaxAnswerBytes = 256 * 1024;

    /// <summary>The header the program can check to know the Library is the caller (it equals the key it was started with).</summary>
    public const string KeyHeader = "X-Tuvima-Plugin-Key";

    private readonly PluginServiceHost _host;
    private readonly PluginCatalog _catalog;
    private readonly HttpClient _http;

    public PluginServiceClient(PluginServiceHost host, PluginCatalog catalog, PluginHostLimits limits)
    {
        _host = host;
        _catalog = catalog;
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = limits.ServiceRequestTimeout };
    }

    public async Task<ServiceLookupResult> LookupAsync(
        string pluginId,
        IReadOnlyDictionary<string, string> query,
        CancellationToken cancellationToken = default)
    {
        var definition = _catalog.Get(pluginId)?.Capabilities.OfType<ServiceLookupCapability>().FirstOrDefault()?.Definition;
        var endpoint = _host.GetEndpoint(pluginId);
        if (definition is null || endpoint is null)
        {
            return new ServiceLookupResult(ServiceCallStatus.NotRunning, "The plugin's program is not running right now.");
        }

        var builder = new UriBuilder(new Uri(endpoint.BaseAddress, definition.LookupPath))
        {
            Query = string.Join('&', query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")),
        };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
            if (endpoint.Key is not null)
            {
                request.Headers.TryAddWithoutValidation(KeyHeader, endpoint.Key);
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new ServiceLookupResult(ServiceCallStatus.Failed, $"The program answered {(int)response.StatusCode} to the lookup.");
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxAnswerBytes)
                {
                    return new ServiceLookupResult(ServiceCallStatus.Failed, "The program's answer was larger than 256 KB, so it was ignored.");
                }
            }

            var text = Encoding.UTF8.GetString(buffer.ToArray());
            try
            {
                using var _ = JsonDocument.Parse(text);
            }
            catch (JsonException)
            {
                return new ServiceLookupResult(ServiceCallStatus.Failed, "The program's answer was not JSON, so it was ignored.");
            }

            return new ServiceLookupResult(ServiceCallStatus.Succeeded, "The program answered.", text);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new ServiceLookupResult(ServiceCallStatus.Failed, "The program did not answer in time.");
        }
    }
}
