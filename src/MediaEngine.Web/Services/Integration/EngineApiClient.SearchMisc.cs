using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Paging;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Models;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Branding;
using MediaEngine.Web.Services.Integration.Clients;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    // -- GET /collections/search -----------------------------------------------------

    public async Task<List<SearchResultDto>> SearchWorksAsync(
        string query,
        CancellationToken ct = default)
    {
        var raw = await GetAsync(
            "GET /collections/search",
            "/collections/search",
            static () => new List<SearchResultDto>(),
            new Dictionary<string, string?> { ["q"] = query },
            ct: ct);
        return raw.Select(r => new SearchResultDto
        {
            WorkId = r.WorkId,
            CollectionId = r.CollectionId,
            Title = r.Title,
            Author = r.Author,
            MediaType = r.MediaType,
            CollectionDisplayName = r.CollectionDisplayName,
            Series = r.Series,
            SeriesPosition = r.SeriesPosition,
            ShowName = r.ShowName,
            SeasonNumber = r.SeasonNumber,
            EpisodeNumber = r.EpisodeNumber,
            CoverUrl = r.CoverUrl is null ? null : AbsoluteUrl(r.CoverUrl),
            Year = r.Year,
            Description = r.Description,
            Rating = r.Rating,
        }).ToList();
    }

    // -- Metadata search (/metadata/search) --------------------------------

    public async Task<MetadataSearchResponse?> SearchMetadataAsync(
        string providerName, string query, string? mediaType = null,
        int limit = 25, CancellationToken ct = default)
    {
        try
        {
            var body = new MetadataSearchRequest
            {
                ProviderName = providerName,
                Query = query,
                MediaType = mediaType,
                Limit = limit,
            };
            var resp = await _http.PostAsJsonAsync("/metadata/search", body, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var detail = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("POST /metadata/search returned {Status}: {Detail}",
                    (int)resp.StatusCode, detail);
                LastError = $"HTTP {(int)resp.StatusCode}: {detail}";
                return null;
            }
            return await resp.Content.ReadFromJsonAsync<MetadataSearchResponse>(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /metadata/search failed");
            LastError = ex.Message;
            return null;
        }
    }

    // -- Fan-out metadata search -----------------------------------------

    public async Task<FanOutSearchResponse?> SearchMetadataFanOutAsync(
        string query, string? mediaType = null, string? providerId = null,
        int maxResultsPerProvider = 5, CancellationToken ct = default)
    {
        try
        {
            var payload = new
            {
                query,
                media_type = mediaType,
                provider_id = providerId,
                max_results_per_provider = maxResultsPerProvider,
            };
            var response = await _http.PostAsJsonAsync("/metadata/search-all", payload, ct);
            if (!response.IsSuccessStatusCode)
            {
                LastError = $"search-all failed: {response.StatusCode}";
                return null;
            }
            return await response.Content.ReadFromJsonAsync<FanOutSearchResponse>(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogWarning(ex, "SearchMetadataFanOutAsync failed");
            return null;
        }
    }

    // -- Search results cache --------------------------------------------

    public async Task<string?> GetSearchResultsCacheAsync(Guid entityId, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync($"/metadata/{entityId}/search-cache", ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var wrapper = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: ct);
            return wrapper.TryGetProperty("results_json", out var rj) ? rj.GetString() : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetSearchResultsCacheAsync failed for {EntityId}", entityId);
            return null;
        }
    }

    public async Task SaveSearchResultsCacheAsync(Guid entityId, string resultsJson, CancellationToken ct = default)
    {
        _ = await PutAsync(
            "PUT /metadata/{entityId}/search-cache",
            $"/metadata/{entityId}/search-cache",
            new { results_json = resultsJson },
            ct: ct);
    }


    // -- Canonical values ------------------------------------------------

    public async Task<List<CanonicalFieldDto>> GetCanonicalValuesAsync(
        Guid entityId, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync($"/metadata/canonical/{entityId}", ct);
            if (!response.IsSuccessStatusCode)
            {
                LastError = $"canonical values failed: {response.StatusCode}";
                return [];
            }
            return await response.Content.ReadFromJsonAsync<List<CanonicalFieldDto>>(cancellationToken: ct) ?? [];
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogWarning(ex, "GetCanonicalValuesAsync failed for {EntityId}", entityId);
            return [];
        }
    }

    // -- Cover from URL --------------------------------------------------

    public async Task<bool> ApplyCoverFromUrlAsync(
        Guid entityId, string imageUrl, CancellationToken ct = default)
    {
        try
        {
            var payload = new { image_url = imageUrl };
            var response = await _http.PostAsJsonAsync($"/metadata/{entityId}/cover-from-url", payload, ct);
            if (!response.IsSuccessStatusCode)
            {
                LastError = $"cover-from-url failed: {response.StatusCode}";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogWarning(ex, "ApplyCoverFromUrlAsync failed for {EntityId}", entityId);
            return false;
        }
    }

    // -- Wikidata Aliases (/metadata/{qid}/aliases) ----------------------------

    public async Task<WikidataAliasesResponse?> GetAliasesAsync(string qid, CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<WikidataAliasesResponse>($"metadata/{qid}/aliases", ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogWarning(ex, "GET /metadata/{Qid}/aliases failed", qid);
            return null;
        }
    }

    // -- Collection shelves ---------------------------------------------------

    public async Task<List<ContentGroupViewModel>> GetContentGroupsAsync(CancellationToken ct = default)
    {
        try
        {
            var contracts = await _http.GetFromJsonAsync<List<ContentGroupDto>>("/collections/content-groups", ct) ?? [];
            var groups = contracts.Select(ContentGroupViewModel.FromContract).ToList();
            foreach (var group in groups)
            {
                if (group.CoverUrl is not null)
                {
                    group.CoverUrl = AbsoluteUrl(group.CoverUrl);
                }

                if (group.BackgroundUrl is not null)
                {
                    group.BackgroundUrl = AbsoluteUrl(group.BackgroundUrl);
                }

                if (group.BannerUrl is not null)
                {
                    group.BannerUrl = AbsoluteUrl(group.BannerUrl);
                }

                if (group.HeroUrl is not null)
                {
                    group.HeroUrl = AbsoluteUrl(group.HeroUrl);
                }

                if (group.LogoUrl is not null)
                {
                    group.LogoUrl = AbsoluteUrl(group.LogoUrl);
                }

                if (group.ArtistPhotoUrl is not null)
                {
                    group.ArtistPhotoUrl = AbsoluteUrl(group.ArtistPhotoUrl);
                }

                if (group.PersonPhotoUrl is not null)
                {
                    group.PersonPhotoUrl = AbsoluteUrl(group.PersonPhotoUrl);
                }

                group.PreviewItems = group.PreviewItems
                    .Select(preview => preview with { ImageUrl = AbsoluteUrl(preview.ImageUrl) })
                    .ToList();
            }

            return groups;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /collections/content-groups failed");
            throw;
        }
    }

    public async Task<List<ContentGroupViewModel>> GetSystemViewGroupsAsync(string? mediaType = null, string? groupField = null, CancellationToken ct = default)
    {
        try
        {
            var queryParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(mediaType))
            {
                queryParts.Add($"mediaType={Uri.EscapeDataString(mediaType)}");
            }

            if (!string.IsNullOrWhiteSpace(groupField))
            {
                queryParts.Add($"groupField={Uri.EscapeDataString(groupField)}");
            }

            var url = "/collections/system-views" + (queryParts.Count > 0 ? "?" + string.Join("&", queryParts) : "");
            var contracts = await _http.GetFromJsonAsync<List<ContentGroupDto>>(url, ct) ?? [];
            var groups = contracts.Select(ContentGroupViewModel.FromContract).ToList();
            foreach (var g in groups)
            {
                if (g.CoverUrl is not null)
                {
                    g.CoverUrl = AbsoluteUrl(g.CoverUrl);
                }

                if (g.BackgroundUrl is not null)
                {
                    g.BackgroundUrl = AbsoluteUrl(g.BackgroundUrl);
                }

                if (g.BannerUrl is not null)
                {
                    g.BannerUrl = AbsoluteUrl(g.BannerUrl);
                }

                if (g.HeroUrl is not null)
                {
                    g.HeroUrl = AbsoluteUrl(g.HeroUrl);
                }

                if (g.LogoUrl is not null)
                {
                    g.LogoUrl = AbsoluteUrl(g.LogoUrl);
                }

                if (g.ArtistPhotoUrl is not null)
                {
                    g.ArtistPhotoUrl = AbsoluteUrl(g.ArtistPhotoUrl);
                }

                if (g.PersonPhotoUrl is not null)
                {
                    g.PersonPhotoUrl = AbsoluteUrl(g.PersonPhotoUrl);
                }

                g.PreviewItems = g.PreviewItems
                    .Select(preview => preview with { ImageUrl = AbsoluteUrl(preview.ImageUrl) })
                    .ToList();
            }
            return groups;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /collections/system-views failed");
            throw;
        }
    }

    public async Task<List<CollectionItemDto>> GetCollectionItemsAsync(Guid collectionId, int limit = 20, Guid? profileId = null, CancellationToken ct = default)
    {
        try
        {
            var url = AppendCollectionProfileQuery($"/collections/{collectionId}/items?limit={limit}", profileId);
            var items = await _http.GetFromJsonAsync<List<CollectionItemDto>>(url, ct) ?? [];
            foreach (var item in items)
            {
                if (item.CoverUrl is not null)
                {
                    item.CoverUrl = AbsoluteUrl(item.CoverUrl);
                }
            }

            return items;
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /collections/{CollectionId}/items failed", collectionId);
            LastError = ex.Message;
            return [];
        }
    }

    public async Task<List<CollectionMediaLookupDto>> LookupCollectionMediaAsync(
        string? query,
        Guid? collectionId = null,
        string? mediaTypes = null,
        int offset = 0,
        int limit = 24,
        Guid? profileId = null,
        CancellationToken ct = default)
    {
        try
        {
            var parameters = new List<string>
            {
                $"offset={Math.Max(0, offset)}",
                $"limit={Math.Clamp(limit, 1, 100)}",
            };

            if (!string.IsNullOrWhiteSpace(query))
            {
                parameters.Add($"q={Uri.EscapeDataString(query.Trim())}");
            }

            if (collectionId.HasValue)
            {
                parameters.Add($"collectionId={collectionId.Value:D}");
            }

            if (!string.IsNullOrWhiteSpace(mediaTypes))
            {
                parameters.Add($"mediaTypes={Uri.EscapeDataString(mediaTypes)}");
            }

            var url = $"/collections/media-lookup?{string.Join("&", parameters)}";
            url = AppendCollectionProfileQuery(url, profileId);
            var items = await _http.GetFromJsonAsync<List<CollectionMediaLookupDto>>(url, ct) ?? [];
            foreach (var item in items)
            {
                if (item.ArtworkUrl is not null)
                {
                    item.ArtworkUrl = AbsoluteUrl(item.ArtworkUrl);
                }
            }

            return items;
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /collections/media-lookup failed");
            LastError = ex.Message;
            return [];
        }
    }

    public async Task<bool> AddCollectionItemAsync(Guid collectionId, Guid workId, Guid? profileId = null, CancellationToken ct = default)
    {
        try
        {
            var url = AppendCollectionProfileQuery($"/collections/{collectionId}/items", profileId);
            var resp = await _http.PostAsJsonAsync(
                url,
                new CollectionItemAddRequest { WorkId = workId },
                ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /collections/{CollectionId}/items failed", collectionId);
            LastError = ex.Message;
            return false;
        }
    }

    public async Task<bool> RemoveCollectionItemAsync(Guid collectionId, Guid itemId, Guid? profileId = null, CancellationToken ct = default)
    {
        try
        {
            var url = AppendCollectionProfileQuery($"/collections/{collectionId}/items/{itemId}", profileId);
            var resp = await _http.DeleteAsync(url, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DELETE /collections/{CollectionId}/items/{ItemId} failed", collectionId, itemId);
            LastError = ex.Message;
            return false;
        }
    }

    public async Task<bool> ReorderCollectionItemsAsync(Guid collectionId, IReadOnlyList<Guid> itemIds, Guid? profileId = null, CancellationToken ct = default)
    {
        try
        {
            var url = AppendCollectionProfileQuery($"/collections/{collectionId}/items/reorder", profileId);
            var resp = await _http.PutAsJsonAsync(
                url,
                new CollectionItemReorderRequest { ItemIds = itemIds.ToList() },
                ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PUT /collections/{CollectionId}/items/reorder failed", collectionId);
            LastError = ex.Message;
            return false;
        }
    }

    public async Task<bool> UpdateCollectionEnabledAsync(Guid collectionId, bool enabled, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PutAsJsonAsync(
                $"/collections/{collectionId}/enabled",
                new CollectionEnabledRequest(enabled),
                ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PUT /collections/{CollectionId}/enabled failed", collectionId);
            LastError = ex.Message;
            return false;
        }
    }

    public async Task<bool> UpdateCollectionFeaturedAsync(Guid collectionId, bool featured, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PutAsJsonAsync(
                $"/collections/{collectionId}/featured",
                new CollectionFeaturedRequest(featured),
                ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PUT /collections/{CollectionId}/featured failed", collectionId);
            LastError = ex.Message;
            return false;
        }
    }

    public async Task<CollectionPreviewResult?> PreviewCollectionRulesAsync(
        CollectionRuleDefinitionViewModel definition, string? sortField, string sortDirection, string? query = null, int limit = 20, CancellationToken ct = default, string? secondarySortField = null, string? secondarySortDirection = null)
    {
        try
        {
            var body = new CollectionPreviewRequest
            {
                RuleDefinition = ToContract(definition),
                SortField = sortField,
                SortDirection = sortDirection,
                SecondarySortField = secondarySortField,
                SecondarySortDirection = secondarySortDirection,
                Query = query,
                Limit = limit,
            };
            var response = await _http.PostAsJsonAsync("/collections/preview", body, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<CollectionPreviewResponse>(cancellationToken: ct);
            return result is null ? null : new CollectionPreviewResult
            {
                Count = result.Count,
                MediaTypeCounts = result.MediaTypeCounts,
                Items = result.Items.Select(item => new CollectionResolvedItemViewModel
                {
                    EntityId = item.EntityId,
                    Title = item.Title,
                    Creator = item.Creator,
                    MediaType = item.MediaType,
                    CoverUrl = item.CoverUrl is null ? null : AbsoluteUrl(item.CoverUrl),
                    Year = item.Year,
                }).ToList(),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /collections/preview failed");
            LastError = ex.Message;
            return null;
        }
    }

    public async Task<IReadOnlyList<CollectionRuleValueDto>> GetCollectionEntityFieldValuesAsync(
        string field,
        string? query = null,
        int limit = 100,
        CancellationToken ct = default)
    {
        return await GetAsync<IReadOnlyList<CollectionRuleValueDto>>(
            "GET /collections/entity-field-values/{field}",
            $"/collections/entity-field-values/{Uri.EscapeDataString(field)}",
            static () => [],
            new Dictionary<string, string?> { ["q"] = query, ["limit"] = Math.Clamp(limit, 1, 500).ToString() },
            ct: ct);
    }

    public async Task<IReadOnlyList<string>> GetCollectionFieldValuesAsync(
        string field,
        string? query = null,
        int limit = 100,
        CancellationToken ct = default)
    {
        return await GetAsync<IReadOnlyList<string>>(
            "GET /collections/field-values/{field}",
            $"/collections/field-values/{Uri.EscapeDataString(field)}",
            static () => [],
            new Dictionary<string, string?> { ["q"] = query, ["limit"] = Math.Clamp(limit, 1, 500).ToString() },
            ct: ct);
    }

    public async Task<bool> CreateCollectionAsync(
        string name,
        string? description,
        string? iconName,
        string collectionType,
        CollectionRuleDefinitionViewModel definition,
        string? sortField,
        string sortDirection,
        string visibility,
        Guid? profileId = null,
        CancellationToken ct = default,
        string? secondarySortField = null,
        string? secondarySortDirection = null)
        => await CreateCollectionAndReturnIdAsync(name, description, iconName, collectionType, definition, sortField, sortDirection, visibility, profileId, ct, secondarySortField, secondarySortDirection) is not null;

    public async Task<Guid?> CreateCollectionAndReturnIdAsync(
        string name,
        string? description,
        string? iconName,
        string collectionType,
        CollectionRuleDefinitionViewModel definition,
        string? sortField,
        string sortDirection,
        string visibility,
        Guid? profileId = null,
        CancellationToken ct = default,
        string? secondarySortField = null,
        string? secondarySortDirection = null,
        string membershipMode = "Manual",
        string primaryArea = "Mixed",
        string ownerKind = "Profile",
        string audience = "Private",
        IReadOnlyList<Guid>? selectedProfileIds = null)
    {
        try
        {
            var body = new CollectionCreateRequest
            {
                Name = name,
                Description = description,
                IconName = iconName,
                Visibility = visibility,
                CollectionType = collectionType,
                MembershipMode = membershipMode,
                PrimaryArea = primaryArea,
                OwnerKind = ownerKind,
                Audience = audience,
                SelectedProfileIds = selectedProfileIds?.Where(id => id != Guid.Empty).Distinct().ToList() ?? [],
                RuleDefinition = ToContract(definition),
                SortField = sortField,
                SortDirection = sortDirection,
                SecondarySortField = secondarySortField,
                SecondarySortDirection = secondarySortDirection,
            };
            var response = await _http.PostAsJsonAsync(AppendCollectionProfileQuery("/collections", profileId), body, ct);
            if (!response.IsSuccessStatusCode)
            {
                LastError = await ReadCollectionFailureAsync(response, ct);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<CollectionCreatedResponse>(cancellationToken: ct);
            LastError = result is null ? "The Engine returned an empty response after creating the collection." : null;
            return result?.id;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /collections failed");
            LastError = ex.Message;
            return null;
        }
    }

    public async Task<Guid?> CreateCollectionWithItemsAsync(
        string name,
        string? description,
        string? iconName,
        string collectionType,
        CollectionRuleDefinitionViewModel definition,
        string? sortField,
        string sortDirection,
        string visibility,
        IReadOnlyList<Guid> workIds,
        Guid? profileId = null,
        CancellationToken ct = default,
        string? secondarySortField = null,
        string? secondarySortDirection = null)
    {
        try
        {
            var body = new CollectionCreateRequest
            {
                Name = name,
                Description = description,
                IconName = iconName,
                Visibility = visibility,
                CollectionType = collectionType,
                RuleDefinition = ToContract(definition),
                SortField = sortField,
                SortDirection = sortDirection,
                SecondarySortField = secondarySortField,
                SecondarySortDirection = secondarySortDirection,
                WorkIds = workIds.Where(id => id != Guid.Empty).Distinct().ToList(),
            };
            var url = AppendCollectionProfileQuery("/collections", profileId);
            var response = await _http.PostAsJsonAsync(url, body, ct);
            if (!response.IsSuccessStatusCode)
            {
                LastError = await ReadCollectionFailureAsync(response, ct);
                _logger.LogWarning(
                    "POST /collections failed with HTTP {StatusCode}: {Error}",
                    (int)response.StatusCode,
                    LastError);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<CollectionCreatedResponse>(cancellationToken: ct);
            LastError = result is null ? "The Engine returned an empty response after creating the collection." : null;
            return result?.id;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /collections failed");
            LastError = ex.Message;
            return null;
        }
    }

    private static async Task<string> ReadCollectionFailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = document.RootElement;
            if (root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
            {
                return SafeProblemText(detail.GetString(), 600, $"The Engine rejected the collection (HTTP {(int)response.StatusCode}).");
            }

            if (root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
            {
                return SafeProblemText(title.GetString(), 300, $"The Engine rejected the collection (HTTP {(int)response.StatusCode}).");
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            // Fall through to a stable user-facing message while the caller logs the status.
        }

        return $"The Engine rejected the collection (HTTP {(int)response.StatusCode}).";
    }

    public async Task<bool> UpdateCollectionAsync(
        Guid collectionId,
        string? name,
        string? description,
        string? iconName,
        CollectionRuleDefinitionViewModel? definition,
        string? visibility,
        string? sortField,
        string? sortDirection,
        bool? isEnabled,
        bool? isFeatured,
        Guid? profileId = null,
        CancellationToken ct = default,
        string? secondarySortField = null,
        string? secondarySortDirection = null,
        string? membershipMode = null,
        string? primaryArea = null,
        string? ownerKind = null,
        string? audience = null,
        IReadOnlyList<Guid>? selectedProfileIds = null)
    {
        try
        {
            var body = new CollectionUpdateRequest
            {
                Name = name,
                Description = description,
                IconName = iconName,
                Visibility = visibility,
                MembershipMode = membershipMode,
                PrimaryArea = primaryArea,
                OwnerKind = ownerKind,
                Audience = audience,
                SelectedProfileIds = selectedProfileIds?.Where(id => id != Guid.Empty).Distinct().ToList(),
                RuleDefinition = definition is null ? null : ToContract(definition),
                SortField = sortField,
                SortDirection = sortDirection,
                SecondarySortField = secondarySortField,
                SecondarySortDirection = secondarySortDirection,
                IsEnabled = isEnabled,
                IsFeatured = isFeatured,
            };
            var url = AppendCollectionProfileQuery($"/collections/{collectionId}", profileId);
            var response = await _http.PutAsJsonAsync(url, body, ct);
            if (!response.IsSuccessStatusCode)
            {
                LastError = await ReadCollectionFailureAsync(response, ct);
                _logger.LogWarning(
                    "PUT /collections/{CollectionId} failed with HTTP {StatusCode}: {Error}",
                    collectionId,
                    (int)response.StatusCode,
                    LastError);
                return false;
            }

            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PUT /collections/{CollectionId} failed", collectionId);
            LastError = ex.Message;
            return false;
        }
    }

    private static CollectionRulePredicateDto ToContract(CollectionRulePredicateViewModel source) => new()
    {
        Field = source.Field,
        Op = source.Op,
        Value = source.Value,
        DisplayValue = source.DisplayValue,
        Values = source.Values,
        DisplayValues = source.DisplayValues,
    };

    private static CollectionRuleDefinitionDto ToContract(CollectionRuleDefinitionViewModel source) => new()
    {
        Version = source.Version,
        Groups = source.Groups.Select(group => new CollectionRuleGroupDto
        {
            Id = group.Id,
            JoinWithPrevious = group.JoinWithPrevious,
            MatchMode = group.MatchMode,
            Conditions = group.Conditions.Select(ToContract).ToList(),
        }).ToList(),
    };

    public async Task<bool> DeleteCollectionAsync(Guid collectionId, Guid? profileId = null, CancellationToken ct = default)
    {
        return await DeleteAsync(
            "DELETE /collections/{id}",
            AppendCollectionProfileQuery($"/collections/{collectionId}", profileId),
            ct: ct);
    }

    public async Task<bool> UploadCollectionArtworkAsync(
        Guid collectionId,
        string slot,
        Stream fileStream,
        string fileName,
        Guid? profileId = null,
        CancellationToken ct = default)
    {
        try
        {
            using var content = new MultipartFormDataContent();
            var fileContent = new StreamContent(fileStream);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(GetImageContentType(fileName));
            content.Add(fileContent, "file", fileName);

            var url = AppendCollectionProfileQuery($"/collections/{collectionId}/artwork/{Uri.EscapeDataString(slot)}", profileId);
            var response = await _http.PostAsync(url, content, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /collections/{CollectionId}/artwork/{Slot} failed", collectionId, slot);
            LastError = ex.Message;
            return false;
        }
    }

    public async Task<bool> DeleteCollectionArtworkAsync(Guid collectionId, string slot, Guid? profileId = null, CancellationToken ct = default)
    {
        return await DeleteAsync(
            "DELETE /collections/{id}/artwork/{slot}",
            AppendCollectionProfileQuery($"/collections/{collectionId}/artwork/{Uri.EscapeDataString(slot)}", profileId),
            ct: ct);
    }

}
