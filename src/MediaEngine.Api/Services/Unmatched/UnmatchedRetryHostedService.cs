using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Unmatched;

/// <summary>
/// Looks up again the files the Engine could not match, without anyone doing anything (Library setup spec D34): every few days (weekly by
/// default, <c>unmatched_retry_days</c> in <c>config/maintenance.json</c>, set per file when it becomes Unmatched), and straight away after a
/// provider or plugin is switched on or updated. A file kept as it is is never retried, and a library that looks nothing up is left alone.
/// A long list is worked through in small batches so the lookups do not all land at once.
/// </summary>
public sealed class UnmatchedRetryHostedService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan BusyDelay = TimeSpan.FromSeconds(30);

    private readonly UnmatchedAssetService _service;
    private readonly ScheduledTaskRunner _runner;
    private readonly IConfigurationLoader _configuration;
    private readonly PluginCatalog? _plugins;
    private readonly ILogger<UnmatchedRetryHostedService> _logger;
    private readonly UnmatchedRetryWake _wake;
    private readonly bool _ownsWake;
    private int _changePending;

    public UnmatchedRetryHostedService(
        UnmatchedAssetService service,
        ScheduledTaskRunner runner,
        IConfigurationLoader configuration,
        ILogger<UnmatchedRetryHostedService> logger,
        PluginCatalog? plugins = null,
        UnmatchedRetryWake? wake = null)
    {
        _ownsWake = wake is null;
        _wake = wake ?? new UnmatchedRetryWake();
        _service = service;
        _runner = runner;
        _configuration = configuration;
        _logger = logger;
        _plugins = plugins;
        _configuration.ConfigurationChanged += OnConfigurationChanged;
        if (_plugins is not null)
        {
            _plugins.Changed += NotifyProviderOrPluginChanged;
        }
    }

    /// <summary>
    /// A provider or plugin was switched on or updated: the next pass makes every Unmatched file that is not kept due, and wakes the loop.
    /// Cheap and safe to call from an event handler.
    /// </summary>
    public void NotifyProviderOrPluginChanged()
    {
        Interlocked.Exchange(ref _changePending, 1);
        _wake.Signal();
    }

    /// <summary>One pass: applies a pending provider or plugin change, then looks up again one batch of due files. Returns how many were handled.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _changePending, 0) == 1)
        {
            var due = await _service.MakeEveryItemDueAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("A provider or plugin changed: {Count} Unmatched file(s) are due to be looked up again", due);
        }

        var handled = 0;
        await _runner.RunAsync(ScheduledTaskKeys.UnmatchedRetry, async token =>
        {
            handled = await _service.RetryDueAsync(token).ConfigureAwait(false);
            return TaskRunResult.Succeeded(handled);
        }, ct).ConfigureAwait(false);
        return handled;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("UnmatchedRetryHostedService started - first pass in {Minutes} minutes", InitialDelay.TotalMinutes);
        try
        {
            await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                var handled = 0;
                try
                {
                    handled = await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "UnmatchedRetryHostedService: a pass failed; the next pass tries again");
                }

                // A full batch means more files are due: carry on soon. Otherwise wait, but wake at once for a provider or plugin change.
                var delay = handled >= UnmatchedAssetService.BatchSize ? BusyDelay : IdleDelay;
                try
                {
                    await _wake.WaitAsync(delay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The Engine is stopping.
        }
    }

    public override void Dispose()
    {
        _configuration.ConfigurationChanged -= OnConfigurationChanged;
        if (_plugins is not null)
        {
            _plugins.Changed -= NotifyProviderOrPluginChanged;
        }

        if (_ownsWake)
        {
            _wake.Dispose();
        }

        base.Dispose();
    }

    // A provider's own settings file was reloaded (a provider switched on, its key or its endpoint changed): a lookup that found nothing
    // before may find something now. Other settings files are not a reason to look up again.
    private void OnConfigurationChanged(object? sender, ConfigurationFileChangedEventArgs change)
    {
        if (change.Applied && IsProviderFile(change.RelativePath))
        {
            NotifyProviderOrPluginChanged();
        }
    }

    private static bool IsProviderFile(string relativePath) =>
        relativePath.Replace('\\', '/').StartsWith("providers/", StringComparison.OrdinalIgnoreCase);
}
