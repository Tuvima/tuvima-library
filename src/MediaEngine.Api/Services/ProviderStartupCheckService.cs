using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage;

namespace MediaEngine.Api.Services;

/// <summary>
/// One-shot background service that runs a connection check for every enabled provider once per
/// Engine start, after first-run setup is complete. Results are cached in
/// <c>provider_connection_checks</c> by <see cref="ProviderCredentialService.TestConfiguredAsync"/>,
/// so provider status in Settings is known without anyone pressing "Test".
/// A successful check also clears a stale Down/Degraded health status for that provider.
/// </summary>
public sealed class ProviderStartupCheckService : BackgroundService
{
    private readonly ProviderCredentialService _credentials;
    private readonly IConfigurationLoader _configuration;
    private readonly IProviderHealthMonitor _health;
    private readonly ILogger<ProviderStartupCheckService> _logger;
    private readonly OnboardingActivationGate? _onboardingGate;

    public ProviderStartupCheckService(
        ProviderCredentialService credentials,
        IConfigurationLoader configuration,
        IProviderHealthMonitor health,
        ILogger<ProviderStartupCheckService> logger,
        OnboardingActivationGate? onboardingGate = null)
    {
        _credentials = credentials;
        _configuration = configuration;
        _health = health;
        _logger = logger;
        _onboardingGate = onboardingGate;
    }

    /// <summary>Pause between provider checks so the checks never burst against external services.</summary>
    internal TimeSpan InterProviderDelay { get; init; } = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Return control to the host immediately; the checks must never delay Engine startup.
        await Task.Yield();

        try
        {
            if (_onboardingGate is not null && !_onboardingGate.IsComplete)
            {
                _logger.LogInformation("Provider connection checks are waiting for first-run setup to complete");
                await _onboardingGate.WaitAsync(stoppingToken).ConfigureAwait(false);
            }

            await RunChecksAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown before the checks finished — nothing to clean up.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Provider startup connection checks failed unexpectedly");
        }

        // Service is done — runs once per Engine start.
    }

    internal async Task RunChecksAsync(CancellationToken ct)
    {
        var providers = _configuration.LoadAllProviders()
            .Where(provider => provider.Enabled)
            .OrderBy(provider => provider.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var first = true;
        foreach (var provider in providers)
        {
            ct.ThrowIfCancellationRequested();
            if (!first && InterProviderDelay > TimeSpan.Zero)
            {
                await Task.Delay(InterProviderDelay, ct).ConfigureAwait(false);
            }

            first = false;

            try
            {
                var result = await _credentials.TestConfiguredAsync(provider.Name, ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "Provider {Provider} startup connection check: {Status}", provider.Name, result.Status);

                // "local_ready" proves nothing about a remote service, so it must not clear a Down status.
                if (result.Success
                    && !string.Equals(result.Status, "local_ready", StringComparison.Ordinal)
                    && _health.GetStatus(provider.Name) != ProviderHealthStatus.Healthy)
                {
                    await _health.ReportSuccessAsync(provider.Name, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Provider {Provider} startup connection check failed", provider.Name);
            }
        }
    }
}
