using System.Globalization;
using MediaEngine.Contracts.System;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    // Null from each of these means the Engine could not be asked (unreachable, refused or failed); the failure is
    // already recorded and logged by the envelope helpers.
    public Task<LogRetentionStatusDto?> GetLogRetentionAsync(CancellationToken ct = default) =>
        GetAsync<LogRetentionStatusDto>("GET /logs/retention", "/logs/retention", ct: ct);

    public Task<LogRetentionStatusDto?> UpdateLogRetentionAsync(LogRetentionSettingsDto settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return PutAsync<LogRetentionSettingsDto, LogRetentionStatusDto>(
            "PUT /logs/retention", "/logs/retention", settings, ct: ct);
    }

    public Task<TechnicalLogPageDto?> GetTechnicalLogAsync(TechnicalLogRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = new Dictionary<string, string?>
        {
            ["from"] = request.From?.ToString("O", CultureInfo.InvariantCulture),
            ["to"] = request.To?.ToString("O", CultureInfo.InvariantCulture),
            ["level"] = request.Level,
            ["area"] = request.Area,
            ["q"] = request.Search,
            ["cursor"] = request.Cursor,
            ["limit"] = request.Limit.ToString(CultureInfo.InvariantCulture),
        };
        return GetAsync<TechnicalLogPageDto>("GET /logs/technical", "/logs/technical", query, ct: ct);
    }

    // The filters go in the body, never in the address, so an id or search text is not left in browser history.
    public Task<LogExportPreviewDto?> PreviewLogExportAsync(LogExportRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return PostAsync<LogExportRequest, LogExportPreviewDto>(
            "POST /logs/export/preview", "/logs/export/preview", request, ct: ct);
    }

    public async Task<LogExportDownload?> ExportLogsAsync(LogExportRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            using var response = await _http.PostAsJsonAsync("/logs/export", request, ct);
            if (!response.IsSuccessStatusCode)
            {
                await RecordHttpFailureAsync("POST /logs/export", response, ct, logAsWarning: true);
                return null;
            }

            var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                ?? "tuvima-logs.txt";
            var content = await response.Content.ReadAsByteArrayAsync(ct);
            ClearFailure("POST /logs/export");
            return new LogExportDownload(fileName, content);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{Endpoint} failed", "POST /logs/export");
            RecordExceptionFailure("POST /logs/export", ex, logAsWarning: true);
            return null;
        }
    }

    public Task<ScheduledTasksResponse?> GetScheduledTasksAsync(CancellationToken ct = default) =>
        GetAsync<ScheduledTasksResponse>("GET /system/scheduled-tasks", "/system/scheduled-tasks", ct: ct);
}
