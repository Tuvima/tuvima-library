using System.Net.Http.Json;
using MediaEngine.Contracts.Universe;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public Task<SharedEntityEditorContextDto?> GetSharedEntityEditorContextAsync(SharedEntityEditorTargetDto target, CancellationToken ct = default) =>
        GetSharedEntityAsync<SharedEntityEditorContextDto>(BuildPath(target, "context"), ct);

    public async Task<IReadOnlyList<SharedEntityCategorySummaryDto>> GetSharedEntityCategoriesAsync(string universeQid, CancellationToken ct = default) =>
        await GetSharedEntityAsync<List<SharedEntityCategorySummaryDto>>($"/entity-editor/universes/{Escape(universeQid)}/categories", ct).ConfigureAwait(false) ?? [];

    public Task<SharedEntitySelectorPageDto?> GetSharedEntitySelectorPageAsync(string universeQid, string? category, string? search, int offset, int limit, CancellationToken ct = default)
    {
        var query = new List<string> { $"offset={Math.Max(0, offset)}", $"limit={Math.Clamp(limit, 1, 100)}" };
        if (!string.IsNullOrWhiteSpace(category))
        {
            query.Add($"category={Escape(category)}");
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            query.Add($"search={Escape(search.Trim())}");
        }
        return GetSharedEntityAsync<SharedEntitySelectorPageDto>($"/entity-editor/universes/{Escape(universeQid)}/entities?{string.Join("&", query)}", ct);
    }

    public Task<SharedEntityDetailsDto?> GetSharedEntityDetailsAsync(SharedEntityEditorTargetDto target, CancellationToken ct = default) =>
        GetSharedEntityAsync<SharedEntityDetailsDto>(BuildPath(target, "details"), ct);

    public Task<SharedEntityDetailsDto?> UpdateSharedEntityDetailsAsync(SharedEntityEditorTargetDto target, SharedEntityDetailsUpdateRequest request, CancellationToken ct = default) =>
        SendSharedEntityAsync<SharedEntityDetailsDto>(HttpMethod.Put, BuildPath(target, "details"), request, ct);

    public async Task<IReadOnlyList<SharedEntityArtworkDto>> GetSharedEntityArtworkAsync(SharedEntityEditorTargetDto target, CancellationToken ct = default) =>
        await GetSharedEntityAsync<List<SharedEntityArtworkDto>>(BuildPath(target, "artwork"), ct).ConfigureAwait(false) ?? [];

    public async Task<IReadOnlyList<SharedEntityArtworkDto>?> UpdateSharedEntityArtworkAsync(SharedEntityEditorTargetDto target, SharedEntityArtworkUpdateRequest request, CancellationToken ct = default) =>
        await SendSharedEntityAsync<List<SharedEntityArtworkDto>>(HttpMethod.Put, BuildPath(target, "artwork"), request, ct).ConfigureAwait(false);

    public Task<IReadOnlyList<SharedEntityArtworkDto>?> UploadSharedEntityArtworkAsync(SharedEntityEditorTargetDto target, string assetType, Stream stream, string fileName, string contentType, CancellationToken ct = default) =>
        UploadSharedEntityArtworkCoreAsync(BuildPath(target, $"artwork/{Escape(assetType)}/upload"), stream, fileName, contentType, ct);

    public async Task<IReadOnlyList<SharedEntityAppearanceDto>> GetSharedEntityAppearancesAsync(SharedEntityEditorTargetDto target, CancellationToken ct = default) =>
        IsEntity(target) ? await GetSharedEntityAsync<List<SharedEntityAppearanceDto>>(BuildPath(target, "appearances"), ct).ConfigureAwait(false) ?? [] : [];

    public async Task<IReadOnlyList<SharedEntityRelationshipDto>> GetSharedEntityRelationshipsAsync(SharedEntityEditorTargetDto target, CancellationToken ct = default) =>
        await GetSharedEntityAsync<List<SharedEntityRelationshipDto>>(BuildPath(target, "relationships"), ct).ConfigureAwait(false) ?? [];

    public async Task<IReadOnlyList<SharedEntityTimelineEntryDto>> GetSharedEntityTimelineAsync(SharedEntityEditorTargetDto target, CancellationToken ct = default) =>
        await GetSharedEntityAsync<List<SharedEntityTimelineEntryDto>>(BuildPath(target, "timeline"), ct).ConfigureAwait(false) ?? [];

    public async Task<IReadOnlyList<SharedEntitySourceDto>> GetSharedEntitySourcesAsync(SharedEntityEditorTargetDto target, CancellationToken ct = default) =>
        await GetSharedEntityAsync<List<SharedEntitySourceDto>>(BuildPath(target, "sources"), ct).ConfigureAwait(false) ?? [];

    public async Task<IReadOnlyList<SharedEntityHistoryEntryDto>> GetSharedEntityHistoryAsync(SharedEntityEditorTargetDto target, CancellationToken ct = default) =>
        await GetSharedEntityAsync<List<SharedEntityHistoryEntryDto>>(BuildPath(target, "history"), ct).ConfigureAwait(false) ?? [];

    public Task<SharedEntityEnrichmentStatusDto?> GetSharedEntityEnrichmentAsync(SharedEntityEditorTargetDto target, CancellationToken ct = default) =>
        GetSharedEntityAsync<SharedEntityEnrichmentStatusDto>(BuildPath(target, "enrichment"), ct);

    public Task<SharedEntityRefreshDto?> RefreshSharedEntityAsync(SharedEntityEditorTargetDto target, CancellationToken ct = default) =>
        SendSharedEntityAsync<SharedEntityRefreshDto>(HttpMethod.Post, BuildPath(target, "refresh"), new { }, ct);

    private async Task<T?> GetSharedEntityAsync<T>(string path, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await RecordHttpFailureAsync(path, response, ct).ConfigureAwait(false);
                return default;
            }

            var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            if (value is null)
            {
                LastError = $"GET {path} returned an empty response.";
            }
            else
            {
                ClearFailure(path);
            }
            return value;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return default; }
        catch (Exception ex) { RecordExceptionFailure(path, ex); return default; }
    }

    private async Task<T?> SendSharedEntityAsync<T>(HttpMethod method, string path, object body, CancellationToken ct)
    {
        try
        {
            using var response = method == HttpMethod.Put
                ? await _http.PutAsJsonAsync(path, body, ct).ConfigureAwait(false)
                : await _http.PostAsJsonAsync(path, body, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await RecordHttpFailureAsync(path, response, ct).ConfigureAwait(false);
                return default;
            }

            var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
            if (value is null)
            {
                LastError = $"{method} {path} returned an empty response.";
            }
            else
            {
                ClearFailure(path);
            }
            return value;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return default; }
        catch (Exception ex) { RecordExceptionFailure(path, ex); return default; }
    }

    private async Task<IReadOnlyList<SharedEntityArtworkDto>?> UploadSharedEntityArtworkCoreAsync(string path, Stream stream, string fileName, string contentType, CancellationToken ct)
    {
        var normalizedContentType = contentType?.Trim().ToLowerInvariant();
        if (normalizedContentType is not ("image/jpeg" or "image/png"))
        {
            normalizedContentType = Path.GetExtension(fileName).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                _ => null,
            };
        }

        if (normalizedContentType is null)
        {
            LastError = "Managed entity artwork uploads must be JPEG or PNG images.";
            return null;
        }

        try
        {
            using var content = new MultipartFormDataContent();
            var fileContent = new StreamContent(stream);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(normalizedContentType);
            content.Add(fileContent, "file", Path.GetFileName(fileName));
            using var response = await _http.PostAsync(path, content, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await RecordHttpFailureAsync(path, response, ct).ConfigureAwait(false);
                return null;
            }

            var value = await response.Content.ReadFromJsonAsync<List<SharedEntityArtworkDto>>(cancellationToken: ct).ConfigureAwait(false);
            if (value is null)
            {
                LastError = $"POST {path} returned an empty response.";
            }
            else
            {
                ClearFailure(path);
            }
            return value;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex) { RecordExceptionFailure(path, ex); return null; }
    }

    private static bool IsEntity(SharedEntityEditorTargetDto target) =>
        string.Equals(target.Kind, SharedEntityEditorTargetKinds.FictionalEntity, StringComparison.OrdinalIgnoreCase)
        && target.FictionalEntityId.HasValue;

    private static string BuildPath(SharedEntityEditorTargetDto target, string suffix)
    {
        var root = $"/entity-editor/universes/{Escape(target.UniverseQid)}";
        return $"{root}{(IsEntity(target) ? $"/entities/{target.FictionalEntityId!.Value:D}" : string.Empty)}/{suffix}";
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);
}
