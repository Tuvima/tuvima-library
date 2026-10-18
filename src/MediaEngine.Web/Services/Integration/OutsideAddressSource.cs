using System.Net.Http.Json;
using MediaEngine.Contracts.Settings;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// Where the Dashboard's anonymous discovery answer gets the outside address from. Never throws: when the address
/// cannot be read the answer is simply null and discovery goes on without it.
/// </summary>
public interface IOutsideAddressSource
{
    Task<string?> GetAsync(CancellationToken ct = default);
}

/// <summary>
/// Reads the outside address from the Engine with the Dashboard's own service credential and remembers the answer
/// briefly, so anonymous discovery traffic cannot turn into Engine traffic.
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
            _address = address;
            _validUntil = clock.GetUtcNow() + (succeeded ? AnswerLifetime : FailureLifetime);
            return address;
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
            using var response = await clients.CreateClient("EngineIdentity")
                .GetAsync("/network/outside-address", timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("The Engine answered {Status} when asked for the outside address; discovery goes on without it.", (int)response.StatusCode);
                return (null, false);
            }

            var body = await response.Content.ReadFromJsonAsync<NetworkOutsideAddressDto>(timeout.Token).ConfigureAwait(false);
            return (string.IsNullOrWhiteSpace(body?.OutsideAddress) ? null : body.OutsideAddress.Trim(), true);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "The Engine could not be asked for the outside address; discovery goes on without it.");
            return (null, false);
        }
    }
}
