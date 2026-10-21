using MediaEngine.Domain.Configuration;
using MediaEngine.Plugins;
using MediaEngine.Providers.Services;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>
/// Holds each plugin to the requests a minute it declared (or the Library's default) for each place it contacts. It uses
/// the same limiter as the metadata providers but its own instance, so plugin traffic never shows up in the provider
/// activity the Dashboard displays. A request that would have to wait longer than the limit allows is refused instead of
/// being held for ever.
/// </summary>
internal sealed class PluginHttpRateLimiter(PluginHostLimits limits)
{
    private readonly ProviderRateLimiterCoordinator _coordinator = new();

    /// <summary>The most requests a minute <paramref name="destination"/> allows this plugin.</summary>
    public int RequestsPerMinute(PluginNetworkDestination destination) =>
        destination.MaxRequestsPerMinute ?? limits.HttpDefaultRequestsPerMinute;

    /// <summary>
    /// Waits for the plugin's turn to contact <paramref name="destination"/>, runs <paramref name="send"/>, and returns its
    /// result. Throws <see cref="PluginLimitExceededException"/> when the turn would take longer than
    /// <see cref="PluginHostLimits.HttpMaxRateLimitWait"/>.
    /// </summary>
    public async Task<T> RunAsync<T>(
        string pluginId,
        PluginNetworkDestination destination,
        Func<CancellationToken, Task<T>> send,
        CancellationToken cancellationToken)
    {
        var perMinute = RequestsPerMinute(destination);
        var configuration = new ProviderRateLimitConfiguration
        {
            RequestsPerMinute = perMinute,
            Burst = Math.Clamp(perMinute, 1, 5),
            MaxConcurrency = 4,
        };

        // The wait for a turn is timed; the request itself is not (the HTTP client has its own timeout), so the timer is
        // switched off as soon as the turn arrives.
        using var waitLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        waitLimit.CancelAfter(limits.HttpMaxRateLimitWait);
        try
        {
            // The number is part of the key: a limit the plugin's manifest changes later starts a fresh limiter at once
            // instead of waiting for a restart (the old one only ever held the first number it was given).
            return await _coordinator.ExecuteAsync(
                $"plugin:{pluginId}:{destination.Host}:{perMinute}",
                configuration,
                _ =>
                {
                    waitLimit.CancelAfter(Timeout.InfiniteTimeSpan);
                    return send(cancellationToken);
                },
                waitLimit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (waitLimit.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new PluginLimitExceededException(PluginHostMessages.RateLimited(perMinute, destination.Host));
        }
    }
}
