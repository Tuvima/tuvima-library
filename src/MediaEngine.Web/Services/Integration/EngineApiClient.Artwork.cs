using System.Net.Http.Json;
using MediaEngine.Contracts.Artwork;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public Task<ArtworkWritebackSettingsDto?> GetArtworkWritebackSettingsAsync(CancellationToken ct = default) =>
        GetAsync<ArtworkWritebackSettingsDto>("Artwork write-back settings", "/settings/writeback/artwork", ct: ct);

    public Task<ArtworkWritebackSettingsDto?> UpdateArtworkWritebackSettingsAsync(UpdateArtworkWritebackSettingsDto settings, CancellationToken ct = default) =>
        PutAsync<UpdateArtworkWritebackSettingsDto, ArtworkWritebackSettingsDto>(
            "Artwork write-back settings", "/settings/writeback/artwork", settings, ct: ct);

    public async Task<ProviderArtworkDiscoveryDto?> DiscoverProviderArtworkAsync(Guid entityId, string scope, string role, CancellationToken ct = default)
        => await DiscoverProviderArtworkAsync(entityId, scope, role, new ProviderArtworkDiscoveryRequestDto(), ct);

    public async Task<ProviderArtworkDiscoveryDto?> DiscoverProviderArtworkAsync(Guid entityId, string scope, string role,
        ProviderArtworkDiscoveryRequestDto request, CancellationToken ct = default)
    {
        var query = new List<string>
        {
            $"page={Math.Max(1, request.Page)}",
            $"pageSize={Math.Clamp(request.PageSize, 1, 100)}",
        };
        AddQuery(query, "sourceAssetType", request.SourceAssetType);
        AddQuery(query, "provider", request.Provider);
        AddQuery(query, "providerItemId", request.ProviderItemId);
        AddQuery(query, "releaseId", request.ReleaseId);
        AddQuery(query, "orderContext", request.OrderContext);
        var response = await _http.GetAsync($"/metadata/{entityId}/artwork/{Uri.EscapeDataString(scope)}/provider-candidates/{Uri.EscapeDataString(role)}?{string.Join('&', query)}", ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<ProviderArtworkDiscoveryDto>(cancellationToken: ct) : null;
    }
    public async Task<ProviderArtworkImportResultDto?> ImportProviderArtworkAsync(Guid entityId, string scope, string role, IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync($"/metadata/{entityId}/artwork/{Uri.EscapeDataString(scope)}/provider-candidates/{Uri.EscapeDataString(role)}", new ProviderArtworkImportRequest(ids), ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<ProviderArtworkImportResultDto>(cancellationToken: ct) : null;
    }
    public async Task<ArtworkBrowsePageDto?> GetArtworkLibraryAsync(
        string? entityKind = null,
        string? artworkType = null,
        string? search = null,
        string? browseAs = null,
        string? mediaType = null,
        string? artworkState = null,
        string? sort = null,
        int offset = 0,
        int limit = 48,
        CancellationToken ct = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["entityKind"] = entityKind,
            ["artworkType"] = artworkType,
            ["search"] = search,
            ["browseAs"] = browseAs,
            ["mediaType"] = mediaType,
            ["artworkState"] = artworkState,
            ["sort"] = sort,
            ["offset"] = offset.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["limit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        var page = await GetAsync<ArtworkBrowsePageDto>(
            "GET /api/v1/display/artwork", "/api/v1/display/artwork", query, ct: ct);
        return page is null
            ? null
            : page with
            {
                Items = page.Items.Select(item => item with
                {
                    ImageUrl = string.IsNullOrWhiteSpace(item.ImageUrl) ? null : AbsoluteUrl(item.ImageUrl),
                    BackgroundImageUrl = string.IsNullOrWhiteSpace(item.BackgroundImageUrl) ? null : AbsoluteUrl(item.BackgroundImageUrl),
                    LogoImageUrl = string.IsNullOrWhiteSpace(item.LogoImageUrl) ? null : AbsoluteUrl(item.LogoImageUrl),
                    PreviewItems = item.PreviewItems.Select(preview => preview with
                    {
                        ImageUrl = AbsoluteUrl(preview.ImageUrl),
                    }).ToList(),
                }).ToList(),
            };
    }

    public async Task<ArtworkAssetPageDto?> GetArtworkAssetsAsync(
        string? search = null, string? role = null, string? aspect = null,
        Guid? targetEntityId = null, int offset = 0, int limit = 48, CancellationToken ct = default)
        => await GetArtworkAssetsAsync(new ArtworkAssetQuery(
            Search: search,
            Roles: string.IsNullOrWhiteSpace(role) ? null : [role],
            Aspects: string.IsNullOrWhiteSpace(aspect) ? null : [aspect],
            TargetEntityId: targetEntityId,
            Offset: offset,
            Limit: limit), ct);

    public async Task<ArtworkAssetPageDto?> GetArtworkAssetsAsync(
        ArtworkAssetQuery request,
        CancellationToken ct = default)
    {
        var query = new List<string>();
        AddQuery(query, "search", request.Search);
        AddQuery(query, "role", request.Roles);
        AddQuery(query, "aspect", request.Aspects);
        AddQuery(query, "mediaType", request.MediaTypes);
        AddQuery(query, "source", request.SourceProviders);
        AddQuery(query, "year", request.Years);
        AddQuery(query, "entityType", request.EntityTypes);
        AddQuery(query, "relatedEntityType", request.RelatedEntityType);
        AddQuery(query, "relatedEntityId", request.RelatedEntityId?.ToString("D"));
        AddQuery(query, "targetEntityType", request.TargetEntityType);
        AddQuery(query, "targetEntityId", request.TargetEntityId?.ToString("D"));
        AddQuery(query, "targetRole", request.TargetRole);
        AddQuery(query, "targetSourceAssetType", request.TargetSourceAssetType);
        AddQuery(query, "pickerScope", request.PickerScope.ToString());
        AddQuery(query, "usage", request.Usage.ToString());
        AddQuery(query, "sort", request.Sort.ToString());
        AddQuery(query, "minimumWidth", request.MinimumWidth?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddQuery(query, "minimumHeight", request.MinimumHeight?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddQuery(query, "offset", request.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddQuery(query, "limit", request.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var path = $"/api/v1/display/artwork/assets?{string.Join('&', query)}";
        var page = await GetAsync<ArtworkAssetPageDto>(
            "GET /api/v1/display/artwork/assets", path, ct: ct);
        return page is null ? null : page with { Items = page.Items.Select(NormalizeAsset).ToList() };
    }

    public async Task<IReadOnlyList<ArtworkLibraryItemDto>> GetUniverseArtworkHierarchyAsync(
        Guid collectionId, CancellationToken ct = default)
    {
        var items = await GetAsync<List<ArtworkLibraryItemDto>>(
            "GET /api/v1/display/artwork/universes/{collectionId}/entities",
            $"/api/v1/display/artwork/universes/{collectionId:D}/entities", ct: ct) ?? [];
        return items.Select(item => item with
        {
            ImageUrl = string.IsNullOrWhiteSpace(item.ImageUrl) ? null : AbsoluteUrl(item.ImageUrl),
            BackgroundImageUrl = string.IsNullOrWhiteSpace(item.BackgroundImageUrl) ? null : AbsoluteUrl(item.BackgroundImageUrl),
            LogoImageUrl = string.IsNullOrWhiteSpace(item.LogoImageUrl) ? null : AbsoluteUrl(item.LogoImageUrl),
        }).ToList();
    }

    public async Task<ArtworkEntityWorkspaceDto?> GetEntityArtworkAsync(
        string entityType, Guid entityId, string? mediaType = null, string? groupKind = null,
        IReadOnlyList<string>? assetTypes = null, CancellationToken ct = default)
    {
        var query = new List<string>();
        AddQuery(query, "mediaType", mediaType);
        AddQuery(query, "groupKind", groupKind);
        AddQuery(query, "assetType", assetTypes);
        var suffix = query.Count == 0 ? string.Empty : $"?{string.Join('&', query)}";
        var result = await GetAsync<ArtworkEntityWorkspaceDto>(
            "GET /api/v1/display/artwork/entities/{entityType}/{entityId}",
            $"/api/v1/display/artwork/entities/{Uri.EscapeDataString(entityType)}/{entityId:D}{suffix}", ct: ct);
        return result is null ? null : NormalizeWorkspace(result);
    }

    public async Task<EffectiveArtworkSelection?> GetEffectiveWorkArtworkAsync(
        Guid workId, string role, string? sourceAssetType = null, CancellationToken ct = default)
    {
        var path = $"/api/v1/display/artwork/works/{workId:D}/effective?role={Uri.EscapeDataString(role)}";
        if (!string.IsNullOrWhiteSpace(sourceAssetType))
        {
            path += $"&sourceAssetType={Uri.EscapeDataString(sourceAssetType)}";
        }
        var selection = await GetAsync<EffectiveArtworkSelection>("GET /api/v1/display/artwork/works/{workId}/effective", path, ct: ct);
        return selection?.Variant is { } variant
            ? selection with { Variant = variant with { ContentUrl = AbsoluteUrl(variant.ContentUrl), ThumbnailUrl = AbsoluteUrl(variant.ThumbnailUrl) } }
            : selection;
    }

    public async Task<EffectiveArtworkSelection?> GetEffectiveOwnedAssetCoverAsync(
        Guid assetId, CancellationToken ct = default)
    {
        var selection = await GetAsync<EffectiveArtworkSelection>(
            "GET /api/v1/display/artwork/media-assets/{assetId}/effective-cover",
            $"/api/v1/display/artwork/media-assets/{assetId:D}/effective-cover", ct: ct);
        return selection?.Variant is { } variant
            ? selection with { Variant = variant with
                { ContentUrl = AbsoluteUrl(variant.ContentUrl), ThumbnailUrl = AbsoluteUrl(variant.ThumbnailUrl) } }
            : selection;
    }

    public Task<ArtworkWritebackStatusDto?> GetArtworkWritebackStatusAsync(Guid mediaAssetId, CancellationToken ct = default) =>
        GetAsync<ArtworkWritebackStatusDto>(
            "GET /metadata/assets/{assetId}/artwork-writeback",
            $"/metadata/assets/{mediaAssetId:D}/artwork-writeback", ct: ct);

    public async Task<IReadOnlyList<ArtworkWritebackStatusDto>> GetArtworkWritebackStatusesAsync(
        IReadOnlyList<Guid> mediaAssetIds, CancellationToken ct = default)
    {
        if (mediaAssetIds.Count is < 1 or > 100)
        {
            return [];
        }
        return await PostAsync<ArtworkWritebackStatusesRequestDto, List<ArtworkWritebackStatusDto>>(
            "Artwork statuses for media page", "/metadata/artwork-writeback/statuses",
            new ArtworkWritebackStatusesRequestDto(mediaAssetIds), ct: ct) ?? [];
    }

    public async Task<ArtworkWritebackStatusDto?> RetryArtworkWritebackAsync(Guid mediaAssetId, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PostAsync($"/metadata/assets/{mediaAssetId:D}/artwork-writeback/retry", null, ct);
            if (!response.IsSuccessStatusCode)
            {
                LastError = $"Artwork retry failed: {response.StatusCode}";
                return null;
            }
            return await response.Content.ReadFromJsonAsync<ArtworkWritebackStatusDto>(cancellationToken: ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogWarning(ex, "Artwork retry failed for {MediaAssetId}", mediaAssetId);
            return null;
        }
    }

    public async Task<ArtworkEntityWorkspaceDto?> LinkArtworkAssetAsync(
        string entityType, Guid entityId, ArtworkLinkRequest request, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(
            $"/api/v1/display/artwork/entities/{Uri.EscapeDataString(entityType)}/{entityId:D}/links", request, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        var result = await response.Content.ReadFromJsonAsync<ArtworkEntityWorkspaceDto>(cancellationToken: ct);
        return result is null ? null : NormalizeWorkspace(result);
    }

    public async Task<ArtworkAssetDto?> AddArtworkFromUrlAsync(
        string entityType, Guid entityId, ArtworkFromUrlRequest request, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(
            $"/api/v1/display/artwork/entities/{Uri.EscapeDataString(entityType)}/{entityId:D}/from-url", request, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        var result = await response.Content.ReadFromJsonAsync<ArtworkAssetDto>(cancellationToken: ct);
        return result is null ? null : NormalizeAsset(result);
    }

    public async Task<ArtworkAssetDto?> UploadCanonicalArtworkAsync(
        string entityType, Guid entityId, string role, Stream stream, string fileName,
        string? entityLabel = null, string? mediaType = null, string? year = null,
        string? sourceAssetType = null,
        CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(GetImageContentType(fileName));
        content.Add(fileContent, "file", fileName);
        content.Add(new StringContent(role), "role");
        if (!string.IsNullOrWhiteSpace(entityLabel))
        {
            content.Add(new StringContent(entityLabel), "entityLabel");
        }
        if (!string.IsNullOrWhiteSpace(mediaType))
        {
            content.Add(new StringContent(mediaType), "mediaType");
        }
        if (!string.IsNullOrWhiteSpace(year))
        {
            content.Add(new StringContent(year), "year");
        }
        if (!string.IsNullOrWhiteSpace(sourceAssetType))
        {
            content.Add(new StringContent(sourceAssetType), "sourceAssetType");
        }
        var response = await _http.PostAsync(
            $"/api/v1/display/artwork/entities/{Uri.EscapeDataString(entityType)}/{entityId:D}/upload", content, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        var result = await response.Content.ReadFromJsonAsync<ArtworkAssetDto>(cancellationToken: ct);
        return result is null ? null : NormalizeAsset(result);
    }

    public async Task<bool> RemoveArtworkLinkAsync(Guid linkId, CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync($"/api/v1/display/artwork/links/{linkId:D}", ct);
        return response.IsSuccessStatusCode;
    }

    private ArtworkAssetDto NormalizeAsset(ArtworkAssetDto asset) => asset with
    {
        ContentUrl = AbsoluteUrl(asset.ContentUrl),
        ThumbnailUrl = AbsoluteUrl(asset.ThumbnailUrl),
    };

    private ArtworkEntityWorkspaceDto NormalizeWorkspace(ArtworkEntityWorkspaceDto workspace) => workspace with
    {
        Variants = workspace.Variants.Select(variant => variant with
        {
            ContentUrl = AbsoluteUrl(variant.ContentUrl),
            ThumbnailUrl = AbsoluteUrl(variant.ThumbnailUrl),
        }).ToList(),
    };

    private static void AddQuery(List<string> query, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            query.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
        }
    }

    private static void AddQuery(List<string> query, string key, IEnumerable<string>? values)
    {
        if (values is null)
        {
            return;
        }
        foreach (var value in values)
        {
            AddQuery(query, key, value);
        }
    }
}
