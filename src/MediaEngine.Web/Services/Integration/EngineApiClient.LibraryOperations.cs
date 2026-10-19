using System.Net.Http.Json;
using System.Text.Json;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// Library preferences, batch editing, overview, and universe-alignment operations.
/// </summary>
public sealed partial class EngineApiClient
{
    public async Task<LibraryPreferencesSettings?> GetLibraryPreferencesAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<LibraryPreferencesSettings>("settings/ui/library-preferences");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /settings/ui/library-preferences failed");
            return null;
        }
    }

    public async Task<SeriesMissingItemPreferenceDto?> GetSeriesMissingItemPreferenceAsync(
        Guid profileId,
        string mediaType,
        string containerKey,
        CancellationToken ct = default)
    {
        var endpoint = BuildSeriesMissingItemPreferenceEndpoint(profileId, mediaType, containerKey);
        try
        {
            return await _http.GetFromJsonAsync<SeriesMissingItemPreferenceDto>(endpoint, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "GET profile series missing-item preference failed for {ProfileId} {MediaType} {ContainerKey}",
                profileId,
                mediaType,
                containerKey);
            return null;
        }
    }

    public async Task<SeriesMissingItemPreferenceDto?> SaveSeriesMissingItemPreferenceAsync(
        Guid profileId,
        string mediaType,
        string containerKey,
        bool showMissing,
        CancellationToken ct = default)
    {
        var endpoint = $"profiles/{profileId:D}/sequence-preferences/missing-items";
        try
        {
            using var response = await _http.PutAsJsonAsync(
                endpoint,
                new SaveSeriesMissingItemPreferenceRequest
                {
                    MediaType = mediaType,
                    ContainerKey = containerKey,
                    ShowMissing = showMissing,
                },
                ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<SeriesMissingItemPreferenceDto>(cancellationToken: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "PUT profile series missing-item preference failed for {ProfileId} {MediaType} {ContainerKey}",
                profileId,
                mediaType,
                containerKey);
            return null;
        }
    }

    public async Task<SeriesMissingItemPreferenceDto?> ResetSeriesMissingItemPreferenceAsync(
        Guid profileId,
        string mediaType,
        string containerKey,
        CancellationToken ct = default)
    {
        var endpoint = BuildSeriesMissingItemPreferenceEndpoint(profileId, mediaType, containerKey);
        try
        {
            using var response = await _http.DeleteAsync(endpoint, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<SeriesMissingItemPreferenceDto>(cancellationToken: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "DELETE profile series missing-item preference failed for {ProfileId} {MediaType} {ContainerKey}",
                profileId,
                mediaType,
                containerKey);
            return null;
        }
    }

    private static string BuildSeriesMissingItemPreferenceEndpoint(
        Guid profileId,
        string mediaType,
        string containerKey) =>
        $"profiles/{profileId:D}/sequence-preferences/missing-items" +
        $"?mediaType={Uri.EscapeDataString(mediaType)}" +
        $"&containerKey={Uri.EscapeDataString(containerKey)}";

    public async Task<LibraryOverviewDto?> GetLibraryOverviewAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<LibraryOverviewDto>("library/overview", ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /library/overview failed");
            LastError = ex.Message;
            return null;
        }
    }

    public async Task<LibraryBatchEditResult?> BatchEditAsync(
        List<Guid> entityIds,
        Dictionary<string, string> fieldChanges,
        CancellationToken ct = default)
    {
        try
        {
            var body = new
            {
                entity_ids = entityIds,
                field_changes = fieldChanges.Select(kv => new { key = kv.Key, value = kv.Value }).ToList(),
            };
            var response = await _http.PostAsJsonAsync("library/batch-edit", body, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<LibraryBatchEditResult>(ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /library/batch-edit failed");
            return null;
        }
    }
}
