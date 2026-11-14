using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Services.Playback;

/// <summary>
/// Measures what this browser can play and tells the Engine, once per session and person (playback-path spec D12). The Dashboard is not a
/// paired app, so without this the Engine would assume a fixed "web" list. The measuring is the browser's own media capability checks in
/// <c>wwwroot/js/playback-capabilities.js</c>; a browser without them reports nothing and keeps the fixed list.
/// </summary>
public sealed class BrowserPlaybackCapabilityReporter(IJSRuntime js, IEngineApiClient api, ILogger<BrowserPlaybackCapabilityReporter> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _measured;
    private ClientCapabilitiesDto? _report;
    private Guid? _reportedFor;

    /// <summary>The measured report, or null before it is measured or when this browser cannot measure.</summary>
    public ClientCapabilitiesDto? Report => _report;

    /// <summary>
    /// Measures (the first time only) and reports for <paramref name="profileId"/> unless that person's report already reached the Engine.
    /// The Engine keeps reports per person and browser session, so a person switch reports again.
    /// </summary>
    public async Task EnsureReportedAsync(Guid profileId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_reportedFor == profileId)
            {
                return;
            }

            if (!_measured)
            {
                var (conclusive, report) = await MeasureAsync(ct).ConfigureAwait(false);
                _report = report;
                _measured = conclusive;
            }

            if (_report is null)
            {
                return;
            }

            if (await api.ReportBrowserPlaybackCapabilitiesAsync(_report, ct).ConfigureAwait(false))
            {
                _reportedFor = profileId;
            }
            else
            {
                logger.LogWarning("The Engine did not accept what this browser can play; it keeps using the fixed web list until the next try.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Tells the Engine this browser started a title in the manifest's rung and could not play it, so the next start of that kind of file
    /// uses a heavier rung (playback-path spec D13). Nothing is sent for a manifest that carries no playback path.
    /// </summary>
    public async Task<bool> ReportFailureAsync(PlaybackManifestDto? manifest, string? detail, CancellationToken ct = default)
    {
        if (manifest?.PlaybackPath is not { } path)
        {
            return false;
        }

        var sent = await api.ReportPlaybackFailureAsync(manifest.AssetId, path.Rung, detail, ct).ConfigureAwait(false);
        if (!sent)
        {
            logger.LogWarning("The Engine did not take the playback failure for asset {AssetId}; it will not be remembered.", manifest.AssetId);
        }

        return sent;
    }

    /// <summary>The report (null when the browser cannot measure) and whether that answer is final; an unavailable interop is worth another try.</summary>
    private async Task<(bool Conclusive, ClientCapabilitiesDto? Report)> MeasureAsync(CancellationToken ct)
    {
        try
        {
            await using var module = await js.InvokeAsync<IJSObjectReference>("import", ct, "./js/playback-capabilities.js").ConfigureAwait(false);
            return (true, await module.InvokeAsync<ClientCapabilitiesDto?>("measure", ct).ConfigureAwait(false));
        }
        catch (JSDisconnectedException)
        {
            logger.LogDebug("The browser went away before it could say what it can play.");
            return (true, null);
        }
        catch (JSException ex)
        {
            logger.LogWarning(ex, "This browser could not say what video it can play; the Engine keeps the fixed web list.");
            return (true, null);
        }
        catch (InvalidOperationException ex)
        {
            // Interop is not available yet (the circuit is still starting); the next call tries again.
            logger.LogDebug(ex, "Browser measuring is not available yet.");
            return (false, null);
        }
    }
}
