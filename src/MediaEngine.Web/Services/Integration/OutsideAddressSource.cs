using System.Net.Http.Json;
using MediaEngine.Contracts.Settings;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// Where the Dashboard's anonymous discovery answer gets the outside address from. Never throws: when the address
/// cannot be read the last good answer is kept (null only if there has never been one) and discovery goes on.
/// </summary>
public interface IOutsideAddressSource
{
    Task<string?> GetAsync(CancellationToken ct = default);
}

/// <summary>
/// Reads the outside address from the Engine with the Dashboard's own service credential and remembers the answer
/// briefly, so anonymous discovery traffic cannot turn into Engine traffic. A short Engine outage must not make apps
/// forget their outside address, so a failed read keeps serving the last good value.
/// </summary>
public sealed class EngineOutsideAddressSource(
    IHttpClientFactory clients,
    ILogger<EngineOutsideAddressSource> logger,
    TimeProvider? timeProvider = null) : IOutsideAddressSource
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public static readonly TimeSpan AnswerLifetime = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan FailureLifetime = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan EngineTimeout = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _address;
    private DateTimeOffset _validUntil = DateTimeOffset.MinValue;

    public async Task<string?> GetAsync(CancellationToken ct = default)
    {
        if (clock.GetUtcNow() < _validUntil)
        {
            return _address;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Another caller may have refreshed it while this one waited.
            if (clock.GetUtcNow() < _validUntil)
            {
                return _address;
            }

            var (address, succeeded) = await ReadAsync(ct).ConfigureAwait(false);
            if (succeeded)
            {
                _address = address;
            }

            // On a failure the last good value stays (null when there never was one) and the next try comes sooner.
            _validUntil = clock.GetUtcNow() + (succeeded ? AnswerLifetime : FailureLifetime);
            return _address;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(string? Address, bool Succeeded)> ReadAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(EngineTimeout);
        try
        {
            // The Dashboard's own credential only: there is no signed-in person on an anonymous discovery request.
            using var request = new HttpRequestMessage(HttpMethod.Get, "/network/outside-address");
            request.Options.Set(DashboardEngineAuthenticationHandler.SuppressSessionToken, true);
            using var response = await clients.CreateClient("EngineIdentity")
                .SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("The Engine answered {Status} when asked for the outside address; discovery goes on without a fresh one.", (int)response.StatusCode);
                return (null, false);
            }

            var body = await response.Content.ReadFromJsonAsync<NetworkOutsideAddressDto>(timeout.Token).ConfigureAwait(false);
            return (string.IsNullOrWhiteSpace(body?.OutsideAddress) ? null : body.OutsideAddress.Trim(), true);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Unreachable, timed out, or an answer that is not the expected JSON (a wrong content type or a broken body):
            // every one of them is a failed read, never a failed discovery.
            logger.LogWarning(ex, "The Engine could not be asked for the outside address; discovery goes on without a fresh one.");
            return (null, false);
        }
    }
}
