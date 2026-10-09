using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public Task<ViewScopeResolutionDto?> GetViewScopesAsync(ViewScopeKind? scope = null, Guid? scopeProfileId = null, CancellationToken ct = default) =>
        GetAsync<ViewScopeResolutionDto>("GET /view/scopes", "/view/scopes", new Dictionary<string, string?>
        {
            ["scope"] = scope.HasValue ? ScopeValue(scope.Value) : null,
            ["scopeProfileId"] = scope == ViewScopeKind.Profile ? scopeProfileId?.ToString("D") : null,
        }, ct: ct);

    public Task<ViewPreferencesDto?> GetViewPreferencesAsync(CancellationToken ct = default) =>
        GetAsync<ViewPreferencesDto>("GET /view/preferences", "/view/preferences", ct: ct);

    public Task<LibraryCapacityDto?> GetLibraryCapacityAsync(string area, CancellationToken ct = default) =>
        GetAsync<LibraryCapacityDto>("GET /view/library-capacity", "/view/library-capacity", new Dictionary<string, string?>
        {
            ["area"] = area,
        }, ct: ct);

    public async Task<ViewPreferencesDto?> UpdateViewPreferencesAsync(ViewScopeKind scope, Guid? scopeProfileId, ViewTimelineDensity timelineDensity, bool viewerInfoOpen = true, CancellationToken ct = default)
    {
        const string endpoint = "PUT /view/preferences";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, "/view/preferences")
            {
                Content = JsonContent.Create(new ViewPreferencesRequest(
                    ScopeValue(scope),
                    scope == ViewScopeKind.Profile ? scopeProfileId : null,
                    timelineDensity,
                    viewerInfoOpen)),
            };
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) { await RecordHttpFailureAsync(endpoint, response, ct); return null; }
            ClearFailure(endpoint);
            return await response.Content.ReadFromJsonAsync<ViewPreferencesDto>(cancellationToken: ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { RecordExceptionFailure(endpoint, ex, true); return null; }
    }

    public Task<ViewAssetTimelinePageDto?> GetViewAssetsAsync(ViewAssetQueryOptions options, CancellationToken ct = default)
    {
        var query = new List<string>();
        AddQuery(query, "scope", ScopeValue(options.Scope));
        AddQuery(query, "scopeProfileId", options.Scope == ViewScopeKind.Profile
            ? options.ScopeProfileId?.ToString("D") : null);
        AddQuery(query, "cursor", options.Cursor);
        AddQuery(query, "anchorBefore", options.AnchorBefore?.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        AddQuery(query, "withoutLocation", options.WithoutLocation ? "true" : null);
        AddQuery(query, "personKey", options.PersonKey);
        AddQuery(query, "from", options.From?.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        AddQuery(query, "to", options.To?.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        AddQuery(query, "q", options.Search?.Trim());
        foreach (var kind in options.Kinds ?? [])
        {
            AddQuery(query, "kind", kind);
        }

        AddQuery(query, "favorite", options.FavoritesOnly ? "true" : null);
        AddQuery(query, "hidden", options.HiddenOnly ? "true" : null);
        AddQuery(query, "lifecycle", options.Lifecycle);
        AddQuery(query, "galleryId", options.GalleryId?.ToString("D"));
        AddQuery(query, "limit", Math.Clamp(options.Limit, 1, 500).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return GetAsync<ViewAssetTimelinePageDto>("GET /view/assets", $"/view/assets?{string.Join('&', query)}", ct: ct);
    }

    public Task<ViewTimelineIndexDto?> GetViewTimelineIndexAsync(ViewAssetQueryOptions options, CancellationToken ct = default)
    {
        var query = new List<string>();
        AddQuery(query, "scope", ScopeValue(options.Scope));
        AddQuery(query, "scopeProfileId", options.Scope == ViewScopeKind.Profile
            ? options.ScopeProfileId?.ToString("D") : null);
        AddQuery(query, "q", options.Search?.Trim());
        foreach (var kind in options.Kinds ?? [])
        {
            AddQuery(query, "kind", kind);
        }
        AddQuery(query, "favorite", options.FavoritesOnly ? "true" : null);
        AddQuery(query, "hidden", options.HiddenOnly ? "true" : null);
        AddQuery(query, "lifecycle", options.Lifecycle);
        AddQuery(query, "galleryId", options.GalleryId?.ToString("D"));
        return GetAsync<ViewTimelineIndexDto>("GET /view/assets/timeline-index", $"/view/assets/timeline-index?{string.Join('&', query)}", ct: ct);
    }

    public Task<LocalAssetDto?> GetViewItemAsync(Guid itemId, ViewScopeKind scope, Guid? scopeProfileId = null, CancellationToken ct = default) =>
        GetAsync<LocalAssetDto>("GET /view/items/{id}", $"/view/items/{itemId:D}", new Dictionary<string, string?>
        {
            ["scope"] = ScopeValue(scope),
            ["scopeProfileId"] = scope == ViewScopeKind.Profile ? scopeProfileId?.ToString("D") : null,
        }, ct: ct);

    public Task<ViewFolderPageDto?> GetViewFoldersAsync(ViewFolderQueryOptions options, CancellationToken ct = default)
    {
        var query = new List<string>();
        AddQuery(query, "scope", ScopeValue(options.Scope));
        AddQuery(query, "scopeProfileId", options.Scope == ViewScopeKind.Profile ? options.ScopeProfileId?.ToString("D") : null);
        AddQuery(query, "sourceId", options.SourceId?.ToString("D"));
        AddQuery(query, "path", options.RelativePath);
        AddQuery(query, "recursive", options.IncludeDescendants ? "true" : null);
        AddQuery(query, "q", options.Search?.Trim());
        AddQuery(query, "offset", options.Offset > 0 ? options.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
        AddQuery(query, "limit", Math.Clamp(options.Limit, 1, 200).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return GetAsync<ViewFolderPageDto>("GET /view/folders", $"/view/folders?{string.Join('&', query)}", ct: ct);
    }

    public Task<bool> SetViewFolderPinAsync(ViewFolderPinRequest request, CancellationToken ct = default) =>
        PutAsync("PUT /view/folders/pin", "/view/folders/pin", request, ct: ct);

    public Task<bool> SetViewFolderTimelinePolicyAsync(ViewFolderTimelinePolicyRequest request, CancellationToken ct = default) =>
        PutAsync("PUT /view/folders/timeline-policy", "/view/folders/timeline-policy", request, ct: ct);

    public Task<ViewPeoplePageDto?> GetViewPeopleAsync(ViewDiscoveryQueryOptions options, CancellationToken ct = default) =>
        GetViewDiscoveryAsync<ViewPeoplePageDto>("people", options, ct);

    public Task<ViewPlacesPageDto?> GetViewPlacesAsync(ViewDiscoveryQueryOptions options, CancellationToken ct = default) =>
        GetViewDiscoveryAsync<ViewPlacesPageDto>("places", options, ct);

    public Task<ViewAtlasPageDto?> GetViewAtlasAsync(ViewAtlasQueryOptions options, CancellationToken ct = default) =>
        GetAsync<ViewAtlasPageDto>("GET /view/places/atlas", "/view/places/atlas", AtlasQuery(options), ct: ct);

    public Task<ViewPlaceMediaPageDto?> GetViewPlaceMediaAsync(
        string placeKey,
        ViewAtlasQueryOptions options,
        int offset = 0,
        int limit = 250,
        CancellationToken ct = default) =>
        GetAsync<ViewPlaceMediaPageDto>(
            "GET /view/places/media",
            "/view/places/media",
            new Dictionary<string, string?>(AtlasQuery(options))
            {
                ["placeKey"] = placeKey,
                ["offset"] = Math.Max(0, offset).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["limit"] = Math.Clamp(limit, 1, 500).ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            ct: ct);

    public async Task<IReadOnlyList<string>> GetViewTagSuggestionsAsync(string? search = null, int limit = 20, CancellationToken ct = default) =>
        await GetAsync<List<string>>("GET /view/tag-suggestions", "/view/tag-suggestions", new Dictionary<string, string?>
        {
            ["q"] = search,
            ["limit"] = Math.Clamp(limit, 1, 100).ToString(System.Globalization.CultureInfo.InvariantCulture),
        }, ct: ct) ?? [];

    public async Task<ViewUploadResult> UploadViewMediaAsync(Stream fileStream, string fileName, string? contentType = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileStream);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        try
        {
            using var content = new MultipartFormDataContent();
            using var fileContent = new StreamContent(fileStream);
            if (MediaTypeHeaderValue.TryParse(contentType, out var mediaType))
            {
                fileContent.Headers.ContentType = mediaType;
            }

            content.Add(fileContent, "file", Path.GetFileName(fileName));
            using var response = await _http.PostAsync("/view/uploads", content, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new(false, ErrorMessage: await ReadViewErrorAsync(response, ct));
            }

            var upload = await response.Content.ReadFromJsonAsync<ViewUploadResponseDto>(cancellationToken: ct);
            return upload is null ? new(false, ErrorMessage: "The Engine returned no upload result.") : new(true, upload);
        }
        catch (OperationCanceledException) { return new(false, ErrorMessage: "The upload was canceled."); }
        catch (Exception ex) { _logger.LogWarning(ex, "POST /view/uploads failed"); return new(false, ErrorMessage: "The Dashboard could not upload this file."); }
    }

    public Task<bool> SetViewItemFavoriteAsync(Guid itemId, bool value, CancellationToken ct = default) =>
        PutAsync("PUT /view/items/{id}/favorite", $"/view/items/{itemId:D}/favorite", new SetLocalAssetFlagRequest(value), ct: ct);
    public Task<bool> SetViewItemHiddenAsync(Guid itemId, bool value, CancellationToken ct = default) =>
        PutAsync("PUT /view/items/{id}/hidden", $"/view/items/{itemId:D}/hidden", new SetLocalAssetFlagRequest(value), ct: ct);
    public Task<bool> ArchiveViewItemAsync(Guid itemId, CancellationToken ct = default) => LifecycleAsync(itemId, "archive", ct);
    public Task<bool> TrashViewItemAsync(Guid itemId, CancellationToken ct = default) => LifecycleAsync(itemId, "trash", ct);
    public Task<bool> RestoreViewItemAsync(Guid itemId, CancellationToken ct = default) => LifecycleAsync(itemId, "restore", ct);
    public Task<LocalAssetDto?> UpdateViewItemDescriptionAsync(Guid itemId, string? description, CancellationToken ct = default) =>
        PutViewItemAsync(itemId, "description", new UpdateLocalAssetDescriptionRequest(description), ct);
    public Task<LocalAssetDto?> UpdateViewItemCapturedAtAsync(Guid itemId, UpdateLocalAssetCapturedAtRequest request, CancellationToken ct = default) =>
        PutViewItemAsync(itemId, "captured-at", request, ct);
    public Task<LocalAssetDto?> UpdateViewItemTagsAsync(Guid itemId, IReadOnlyCollection<string> tags, CancellationToken ct = default) =>
        PutViewItemAsync(itemId, "tags", new UpdateLocalAssetTagsRequest(tags), ct);
    public Task<LocalAssetDto?> UpdateViewItemPeopleAsync(Guid itemId, IReadOnlyCollection<string> people, CancellationToken ct = default) =>
        PutViewItemAsync(itemId, "people", new UpdateLocalAssetPeopleRequest(people), ct);
    public Task<LocalAssetDto?> UpdateViewItemLocationAsync(Guid itemId, UpdateLocalAssetLocationRequest request, CancellationToken ct = default) =>
        PutViewItemAsync(itemId, "location", request, ct);
    public Task<ViewSharedContributionPreviewDto?> PreviewViewSharedContributionAsync(ViewSharedContributionPreviewRequest request, CancellationToken ct = default) =>
        PostAsync<ViewSharedContributionPreviewRequest, ViewSharedContributionPreviewDto>("POST /view/shared/contributions/preview", "/view/shared/contributions/preview", request, ct: ct);
    public Task<ViewSharedContributionDto?> SubmitViewSharedContributionAsync(ViewSharedContributionSubmitRequest request, CancellationToken ct = default) =>
        PostAsync<ViewSharedContributionSubmitRequest, ViewSharedContributionDto>("POST /view/shared/contributions", "/view/shared/contributions", request, ct: ct);
    public Task<ViewSharedContributionPageDto?> GetViewSharedContributionsAsync(string mode = "mine", string? status = null, int offset = 0, int limit = 50, CancellationToken ct = default) =>
        GetAsync<ViewSharedContributionPageDto>("GET /view/shared/contributions", "/view/shared/contributions", new Dictionary<string, string?>
        {
            ["mode"] = mode,
            ["status"] = status,
            ["offset"] = offset.ToString(),
            ["limit"] = limit.ToString(),
        }, ct: ct);
    public Task<ViewSharedContributionDto?> GetViewSharedContributionAsync(Guid contributionId, CancellationToken ct = default) =>
        GetAsync<ViewSharedContributionDto>("GET /view/shared/contributions/{id}", $"/view/shared/contributions/{contributionId:D}", ct: ct);
    public Task<ViewSharedContributionDto?> CancelViewSharedContributionAsync(Guid contributionId, int expectedRevision, CancellationToken ct = default) =>
        PostAsync<ViewSharedContributionRevisionRequest, ViewSharedContributionDto>("POST /view/shared/contributions/{id}/cancel", $"/view/shared/contributions/{contributionId:D}/cancel", new(expectedRevision), ct: ct);
    public Task<ViewSharedContributionDto?> DecideViewSharedContributionAsync(Guid contributionId, ViewSharedContributionDecisionRequest request, CancellationToken ct = default) =>
        PostAsync<ViewSharedContributionDecisionRequest, ViewSharedContributionDto>("POST /view/shared/contributions/{id}/decision", $"/view/shared/contributions/{contributionId:D}/decision", request, ct: ct);
    public Task<ViewSharedContributionDto?> RetryViewSharedContributionAsync(Guid contributionId, int expectedRevision, CancellationToken ct = default) =>
        PostAsync<ViewSharedContributionRevisionRequest, ViewSharedContributionDto>("POST /view/shared/contributions/{id}/retry", $"/view/shared/contributions/{contributionId:D}/retry", new(expectedRevision), ct: ct);
    public Task<ViewSharedContributionDto?> AddViewItemsDirectlyToSharedLibraryAsync(ViewSharedDirectAddRequest request, CancellationToken ct = default) =>
        PostAsync<ViewSharedDirectAddRequest, ViewSharedContributionDto>("POST /view/shared/items/direct", "/view/shared/items/direct", request, ct: ct);
    public Task<ViewGalleryListResponse?> GetViewGalleriesAsync(CancellationToken ct = default) =>
        GetAsync<ViewGalleryListResponse>("GET /view/galleries", "/view/galleries", ct: ct);
    public Task<ViewGalleryDto?> GetViewGalleryAsync(Guid galleryId, CancellationToken ct = default) =>
        GetAsync<ViewGalleryDto>("GET /view/galleries/{id}", $"/view/galleries/{galleryId:D}", ct: ct);
    public Task<ViewGalleryDto?> CreateViewGalleryAsync(ViewGalleryRequest request, CancellationToken ct = default) =>
        PostAsync<ViewGalleryRequest, ViewGalleryDto>("POST /view/galleries", "/view/galleries", request, ct: ct);

    public async Task<ViewGalleryDto?> UpdateViewGalleryAsync(Guid galleryId, ViewGalleryRequest request, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, $"/view/galleries/{galleryId:D}") { Content = JsonContent.Create(request) };
        using var response = await _http.SendAsync(message, ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<ViewGalleryDto>(cancellationToken: ct) : null;
    }

    public Task<bool> DeleteViewGalleryAsync(Guid galleryId, CancellationToken ct = default) =>
        DeleteAsync("DELETE /view/galleries/{id}", $"/view/galleries/{galleryId:D}", ct: ct);
    public Task<IReadOnlyList<ViewGalleryShareTargetDto>?> GetViewGalleryShareTargetsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<ViewGalleryShareTargetDto>>(
            "GET /view/share-targets", "/view/share-targets", ct: ct);
    public Task<IReadOnlyList<ViewGalleryShareDto>?> GetViewGallerySharesAsync(Guid galleryId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<ViewGalleryShareDto>>(
            "GET /view/galleries/{id}/shares", $"/view/galleries/{galleryId:D}/shares", ct: ct);
    public Task<bool> ReplaceViewGallerySharesAsync(
        Guid galleryId,
        IReadOnlyCollection<ViewGalleryShareRequest> shares,
        CancellationToken ct = default) =>
        PutAsync(
            "PUT /view/galleries/{id}/shares",
            $"/view/galleries/{galleryId:D}/shares",
            new ViewGallerySharesRequest(shares),
            ct: ct);
    public Task<AddViewGalleryItemsResponseDto?> AddViewGalleryItemsAsync(Guid galleryId, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default) =>
        PostAsync<ViewGalleryItemsRequest, AddViewGalleryItemsResponseDto>("POST /view/galleries/{id}/items", $"/view/galleries/{galleryId:D}/items", new ViewGalleryItemsRequest(itemIds), ct: ct);

    public async Task<ViewItemsRemovedResponse?> RemoveViewGalleryItemsAsync(Guid galleryId, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/view/galleries/{galleryId:D}/items") { Content = JsonContent.Create(new ViewGalleryItemsRequest(itemIds)) };
        using var response = await _http.SendAsync(request, ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<ViewItemsRemovedResponse>(cancellationToken: ct) : null;
    }

    public Task<ViewPersonalSpaceAdminReviewDto?> GetViewProfileSourcesAsync(
        Guid profileId,
        CancellationToken ct = default) =>
        GetAsync<ViewPersonalSpaceAdminReviewDto>(
            "GET /view/admin/profiles/{profileId}/sources",
            $"/view/admin/profiles/{profileId:D}/sources",
            ct: ct);

    public Task<ViewSourceAdminDto?> CreateViewProfileSourceAsync(
        Guid profileId, CreateViewSourceRequest request, CancellationToken ct = default) =>
        PostAsync<CreateViewSourceRequest, ViewSourceAdminDto>(
            "POST /view/admin/profiles/{profileId}/sources",
            $"/view/admin/profiles/{profileId:D}/sources", request, ct: ct);

    public async Task<ViewSourceAdminDto?> UpdateViewProfileSourceAsync(
        Guid profileId, Guid sourceId, UpdateViewSourceRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PutAsJsonAsync(
                $"/view/admin/profiles/{profileId:D}/sources/{sourceId:D}", request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<ViewSourceAdminDto>(cancellationToken: ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PUT View profile source failed");
            return null;
        }
    }

    public Task<bool> DeleteViewProfileSourceAsync(
        Guid profileId, Guid sourceId, CancellationToken ct = default) =>
        DeleteAsync("DELETE /view/admin/profiles/{profileId}/sources/{sourceId}",
            $"/view/admin/profiles/{profileId:D}/sources/{sourceId:D}", ct: ct);

    public Task<LocalAssetScanResultDto?> ReconcileViewPersonalSpaceAsync(
        Guid profileId, CancellationToken ct = default) =>
        PostAsync<object, LocalAssetScanResultDto>(
            "POST /view/admin/profiles/{profileId}/reconcile",
            $"/view/admin/profiles/{profileId:D}/reconcile", new { }, ct: ct);

    private Task<bool> LifecycleAsync(Guid itemId, string action, CancellationToken ct) =>
        PostAsync("POST /view/items/{id}/lifecycle", $"/view/items/{itemId:D}/{action}", new { }, ct: ct);

    private async Task<LocalAssetDto?> PutViewItemAsync<T>(Guid itemId, string field, T value, CancellationToken ct)
    {
        var endpoint = $"PUT /view/items/{{id}}/{field}";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"/view/items/{itemId:D}/{field}")
            {
                Content = JsonContent.Create(value)
            };
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                await RecordHttpFailureAsync(endpoint, response, ct);
                return null;
            }
            ClearFailure(endpoint);
            return await response.Content.ReadFromJsonAsync<LocalAssetDto>(cancellationToken: ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception exception) { RecordExceptionFailure(endpoint, exception, true); return null; }
    }

    private Task<T?> GetViewDiscoveryAsync<T>(string resource, ViewDiscoveryQueryOptions options, CancellationToken ct)
    {
        var query = new List<string>();
        AddQuery(query, "scope", ScopeValue(options.Scope));
        AddQuery(query, "scopeProfileId", options.Scope == ViewScopeKind.Profile
            ? options.ScopeProfileId?.ToString("D") : null);
        AddQuery(query, "q", options.Search?.Trim());
        AddQuery(query, "cursor", options.Cursor);
        AddQuery(query, "limit", Math.Clamp(options.Limit, 1, 100).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return GetAsync<T>($"GET /view/{resource}", $"/view/{resource}?{string.Join('&', query)}", ct: ct);
    }
    private static Dictionary<string, string?> AtlasQuery(ViewAtlasQueryOptions options) => new()
    {
        ["scope"] = ScopeValue(options.Scope),
        ["scopeProfileId"] = options.Scope == ViewScopeKind.Profile ? options.ScopeProfileId?.ToString("D") : null,
        ["q"] = options.Search?.Trim(),
        ["year"] = options.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["kind"] = options.MediaKind,
        ["from"] = options.From?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        ["to"] = options.To?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        ["resolution"] = options.TimelineResolution,
        ["favorites"] = options.FavoritesOnly ? "true" : null,
    };
    private static string ScopeValue(ViewScopeKind scope) => scope.ToString().ToLowerInvariant();

    private static async Task<string> ReadViewErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (document.RootElement.TryGetProperty("detail", out var detail) && !string.IsNullOrWhiteSpace(detail.GetString()))
            {
                return detail.GetString()!;
            }

            if (document.RootElement.TryGetProperty("title", out var title) && !string.IsNullOrWhiteSpace(title.GetString()))
            {
                return title.GetString()!;
            }
        }
        catch (JsonException) { }
        return $"The Engine rejected the request ({(int)response.StatusCode}).";
    }
}
