using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Navigation;

/// <summary>
/// Whether the Engine this Dashboard is connected to is a development build that offers the development-only Settings
/// pages (the Enrichment Tester and Development data). Settings asks once per session and keeps the answer, so the menu,
/// the phone page selector and the pages themselves agree. Anything other than a clear "yes" from the Engine counts as
/// "not available": a release Engine says no, and an Engine that cannot be asked (or refuses) is logged and treated as no.
/// </summary>
public sealed class DevelopmentToolsState(IEngineApiClient api, ILogger<DevelopmentToolsState>? logger = null)
{
    private readonly object _gate = new();
    private Task? _loading;
    private bool _loaded;
    private bool _enrichmentTester;
    private bool _developmentData;

    /// <summary>The Engine offers the debug actions the Enrichment Tester calls.</summary>
    public bool EnrichmentTester
    {
        get
        {
            lock (_gate)
            {
                return _loaded && _enrichmentTester;
            }
        }
    }

    /// <summary>The Engine offers the development actions the Development data page calls.</summary>
    public bool DevelopmentData
    {
        get
        {
            lock (_gate)
            {
                return _loaded && _developmentData;
            }
        }
    }

    /// <summary>
    /// Asks the Engine once. Callers that arrive while a read is running share it, and a kept answer is not asked for
    /// again. If the Engine cannot answer, both pages stay unavailable and the next caller asks again.
    /// </summary>
    public Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_loaded)
            {
                return Task.CompletedTask;
            }

            if (_loading is null || _loading.IsCompleted)
            {
                _loading = LoadAsync(ct);
            }

            return _loading;
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            var tools = await api.GetDevelopmentToolsAsync(ct).ConfigureAwait(false);
            if (tools is null)
            {
                logger?.LogWarning("The Engine could not say whether it offers the development-only Settings pages; they stay unavailable.");
                return;
            }

            lock (_gate)
            {
                _enrichmentTester = tools.EnrichmentTester;
                _developmentData = tools.DevelopmentData;
                _loaded = true;
            }
        }
        catch (OperationCanceledException)
        {
            // Settings closed while the read was running; the next visit asks again.
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Asking the Engine which development-only Settings pages it offers failed; they stay unavailable.");
        }
    }
}
