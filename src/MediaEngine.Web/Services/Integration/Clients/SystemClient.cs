using System.Net.Http.Json;
using MediaEngine.Contracts.System;
using MediaEngine.Web.Models.ViewDTOs;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Services.Integration.Clients;

public sealed class SystemClient
{
    private readonly HttpClient _http;
    private readonly ILogger<SystemClient> _logger;
    private readonly EngineApiFailureState _failureState;

    public SystemClient(HttpClient http, ILogger<SystemClient> logger, EngineApiFailureState failureState)
    {
        _http = http;
        _logger = logger;
        _failureState = failureState;
    }

    public async Task<SystemStatusViewModel?> GetSystemStatusAsync(CancellationToken ct = default)
    {
        const string endpoint = "GET /system/status";
        try
        {
            var response = await _http.GetAsync("/system/status", ct);
            if (!response.IsSuccessStatusCode)
            {
                await _failureState.RecordHttpFailureAsync(endpoint, response, _logger, ct, logAsWarning: false);
                return null;
            }

            var raw = await response.Content.ReadFromJsonAsync<SystemStatusResponse>(cancellationToken: ct);
            _failureState.Clear(endpoint);
            return raw is null ? null : new SystemStatusViewModel
            {
                Status = raw.Status,
                Version = raw.Version,
                Language = raw.Language ?? "en",
            };
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _failureState.RecordExceptionFailure(endpoint, ex, _logger, logAsWarning: false);
            return null;
        }
    }

    public async Task<DevelopmentToolsResponse?> GetDevelopmentToolsAsync(CancellationToken ct = default)
    {
        const string endpoint = "GET /system/development-tools";
        try
        {
            var response = await _http.GetAsync("/system/development-tools", ct);
            if (!response.IsSuccessStatusCode)
            {
                await _failureState.RecordHttpFailureAsync(endpoint, response, _logger, ct, logAsWarning: false);
                return null;
            }

            var tools = await response.Content.ReadFromJsonAsync<DevelopmentToolsResponse>(cancellationToken: ct);
            _failureState.Clear(endpoint);
            return tools;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _failureState.RecordExceptionFailure(endpoint, ex, _logger, logAsWarning: false);
            return null;
        }
    }

    public Task<StartupReadinessResponse?> GetStartupReadinessAsync(CancellationToken ct = default)
        => ReadReadinessAsync("/system/readiness", ct);

    // The storage checks only look at folders, so a screen that shows folder problems never waits for the data store scan.
    public Task<StartupReadinessResponse?> GetStorageReadinessAsync(CancellationToken ct = default)
        => ReadReadinessAsync("/system/readiness?category=storage", ct);

    private async Task<StartupReadinessResponse?> ReadReadinessAsync(string path, CancellationToken ct)
    {
        var endpoint = $"GET {path}";
        try
        {
            var response = await _http.GetAsync(path, ct);
            if (!response.IsSuccessStatusCode)
            {
                await _failureState.RecordHttpFailureAsync(endpoint, response, _logger, ct, logAsWarning: false);
                return null;
            }

            var readiness = await response.Content.ReadFromJsonAsync<StartupReadinessResponse>(cancellationToken: ct);
            _failureState.Clear(endpoint);
            return readiness;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _failureState.RecordExceptionFailure(endpoint, ex, _logger, logAsWarning: false);
            return null;
        }
    }
}
