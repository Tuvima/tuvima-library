using MediaEngine.Api.Services.Plugins.Sources;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Domain.Contracts;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>
/// Check now (Plugin architecture spec G13): runs a plugin's own health checks, or reaches out to a metadata source once, and reports in
/// plain words. It never changes what is switched on and never touches the person's media.
/// </summary>
public sealed class PluginCheckNowService(
    PluginCatalog catalog,
    ProviderCapabilityCatalogue sources,
    IConfigurationLoader configuration,
    IPluginExecutionContextFactory contexts,
    IHttpClientFactory httpClients,
    TimeProvider clock,
    ILogger<PluginCheckNowService> logger)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Null when the id is neither an installed plugin nor a metadata source.</summary>
    public async Task<PluginCheckNowResponse?> CheckAsync(string id, CancellationToken ct)
    {
        if (catalog.Get(id) is { } plugin)
        {
            return await CheckPluginAsync(plugin, ct).ConfigureAwait(false);
        }

        if (sources.Sources().FirstOrDefault(source => string.Equals(source.Name, id, StringComparison.OrdinalIgnoreCase)) is { } source)
        {
            return await CheckSourceAsync(source.Name, source.Enabled, ct).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<PluginCheckNowResponse> CheckPluginAsync(PluginRegistration plugin, CancellationToken ct)
    {
        var response = Start(plugin.Manifest.Id);
        if (plugin.LoadError is not null)
        {
            response.Steps.Add(new PluginCheckStepDto { Label = "Loads", Ok = false, Detail = "It could not be loaded. Reinstall it or ask its author for an update." });
            return Finish(response, "It could not be loaded, so it cannot be checked.", cannotCheck: true);
        }

        if (!plugin.Enabled)
        {
            response.Steps.Add(new PluginCheckStepDto { Label = "Switched on", Ok = false, Detail = "Switch it on to check it." });
            return Finish(response, "It is switched off, so there is nothing to check.", cannotCheck: true);
        }

        response.Steps.Add(new PluginCheckStepDto { Label = "Switched on", Ok = true });
        var waiting = catalog.WaitingFor(plugin.Manifest.Id);
        if (waiting is not null)
        {
            response.Steps.Add(new PluginCheckStepDto { Label = "Has what it needs", Ok = false, Detail = waiting });
        }

        var checks = plugin.Capabilities.OfType<IPluginHealthCheck>().ToList();
        if (checks.Count == 0 && waiting is null)
        {
            response.Steps.Add(new PluginCheckStepDto { Label = "Health check", Ok = true, Detail = "It has no test of its own; it is switched on and loaded." });
        }

        await using var context = contexts.Create(plugin.Manifest.Id, "check-now");
        foreach (var check in checks)
        {
            try
            {
                var result = await check.GetHealthAsync(context, ct).ConfigureAwait(false);
                var healthy = string.Equals(result.Status, "healthy", StringComparison.OrdinalIgnoreCase);
                var detail = string.Join(" ", new[] { result.Message }.Concat(result.Warnings).Where(text => !string.IsNullOrWhiteSpace(text)));
                response.Steps.Add(new PluginCheckStepDto { Label = "Health check", Ok = healthy, Detail = detail.Length == 0 ? null : detail });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Check now for plugin {PluginId} failed ({ErrorType})", plugin.Manifest.Id, ex.GetType().Name);
                response.Steps.Add(new PluginCheckStepDto { Label = "Health check", Ok = false, Detail = "The check itself failed to run." });
            }
        }

        return Finish(response);
    }

    private async Task<PluginCheckNowResponse> CheckSourceAsync(string name, bool enabled, CancellationToken ct)
    {
        var response = Start(name);
        if (!enabled)
        {
            response.Steps.Add(new PluginCheckStepDto { Label = "Switched on", Ok = false, Detail = "Switch it on to check it." });
            return Finish(response, "It is switched off, so there is nothing to check.", cannotCheck: true);
        }

        response.Steps.Add(new PluginCheckStepDto { Label = "Switched on", Ok = true });
        var address = ProbeAddress(name);
        if (address is null)
        {
            response.Steps.Add(new PluginCheckStepDto { Label = "Reachable", Ok = true, Detail = "It works from files on this machine, so it needs no internet." });
            return Finish(response);
        }

        try
        {
            using var client = httpClients.CreateClient(name);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeTimeout);
            using var reply = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

            // Any answer below 500 other than "slow down" proves the service is up, even if the test request was not a valid lookup.
            var up = (int)reply.StatusCode < 500 && reply.StatusCode != System.Net.HttpStatusCode.TooManyRequests;
            response.Steps.Add(new PluginCheckStepDto
            {
                Label = "Reachable",
                Ok = up,
                Detail = up ? $"{address.Host} answered." : $"{address.Host} answered with an error ({(int)reply.StatusCode}). It may be down or limiting requests.",
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            response.Steps.Add(new PluginCheckStepDto
            {
                Label = "Reachable",
                Ok = false,
                Detail = $"{address.Host} could not be reached. Check your internet connection or try again later.",
            });
        }

        return Finish(response);
    }

    private Uri? ProbeAddress(string name)
    {
        var endpoints = configuration.LoadProvider(name)?.Endpoints;
        if (endpoints is not { Count: > 0 })
        {
            return null;
        }

        if (endpoints.TryGetValue("api", out var api) && TryHttp(api, out var apiUri))
        {
            return apiUri;
        }

        return endpoints.Values.Select(value => TryHttp(value, out var uri) ? uri : null).FirstOrDefault(uri => uri is not null);
    }

    private static bool TryHttp(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https")
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    private PluginCheckNowResponse Start(string id) => new() { PluginId = id, CheckedAt = clock.GetUtcNow() };

    private static PluginCheckNowResponse Finish(PluginCheckNowResponse response, string? summary = null, bool cannotCheck = false)
    {
        var allOk = response.Steps.All(step => step.Ok);
        response.Result = cannotCheck ? "cannot_check" : allOk ? "working" : "needs_attention";
        response.Summary = summary ?? (allOk ? "It is working." : "It needs attention. " + response.Steps.First(step => !step.Ok).Detail);
        return response;
    }
}
