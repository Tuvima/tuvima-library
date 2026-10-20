using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaEngine.Contracts.Collections;
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
    // -- GET /api/v1/display/home ---------------------------------------------

    public async Task<DisplayPageDto?> GetDisplayHomeAsync(Guid? profileId = null, CancellationToken ct = default)
    {
        const string endpoint = "GET /api/v1/display/home";
        try
        {
            var query = new List<string>();
            AddQuery(query, "profileId", profileId?.ToString("D"));
            var url = "/api/v1/display/home" + (query.Count == 0 ? string.Empty : "?" + string.Join("&", query));
            var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                await RecordHttpFailureAsync(endpoint, response, ct);
                return null;
            }

            var page = await response.Content.ReadFromJsonAsync<DisplayPageDto>(cancellationToken: ct);
            ClearFailure(endpoint);
            return NormalizeDisplayPage(page);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /api/v1/display/home failed");
            RecordExceptionFailure(endpoint, ex);
            return null;
        }
    }

    public async Task<DisplayPageDto?> GetDisplayBrowseAsync(
        string? lane = null,
        string? mediaType = null,
        string? grouping = null,
        string? search = null,
        int? offset = null,
        int? limit = null,
        bool? includeCatalog = null,
        Guid? profileId = null,
        CancellationToken ct = default,
        string? genres = null,
        string? creator = null,
        string? status = null,
        string? year = null,
        string? sort = null)
    {
        const string endpoint = "GET /api/v1/display/browse";
        try
        {
            var query = new List<string>();
            AddQuery(query, "lane", lane);
            AddQuery(query, "mediaType", mediaType);
            AddQuery(query, "grouping", grouping);
            AddQuery(query, "search", search);
            AddQuery(query, "genres", genres);
            AddQuery(query, "creator", creator);
            AddQuery(query, "status", status);
            AddQuery(query, "year", year);
            AddQuery(query, "sort", sort);
            AddQuery(query, "offset", offset?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddQuery(query, "limit", limit?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddQuery(query, "includeCatalog", includeCatalog?.ToString().ToLowerInvariant());
            AddQuery(query, "profileId", profileId?.ToString("D"));
            var url = "/api/v1/display/browse" + (query.Count == 0 ? string.Empty : "?" + string.Join("&", query));
            var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                await RecordHttpFailureAsync(endpoint, response, ct);
                return null;
            }

            var page = await response.Content.ReadFromJsonAsync<DisplayPageDto>(cancellationToken: ct);
            ClearFailure(endpoint);
            return NormalizeDisplayPage(page);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /api/v1/display/browse failed");
            RecordExceptionFailure(endpoint, ex);
            return null;
        }
    }

    public async Task<DisplayPageDto?> GetDisplayContinueAsync(
        string? lane = null,
        string? mediaType = null,
        int? limit = null,
        CancellationToken ct = default)
    {
        const string endpoint = "GET /api/v1/display/continue";
        try
        {
            var query = new List<string>();
            AddQuery(query, "lane", lane);
            AddQuery(query, "mediaType", mediaType);
            AddQuery(query, "limit", limit?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var url = "/api/v1/display/continue" + (query.Count == 0 ? string.Empty : "?" + string.Join("&", query));
            var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                await RecordHttpFailureAsync(endpoint, response, ct);
                return null;
            }

            var page = await response.Content.ReadFromJsonAsync<DisplayPageDto>(cancellationToken: ct);
            ClearFailure(endpoint);
            return NormalizeDisplayPage(page);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /api/v1/display/continue failed");
            RecordExceptionFailure(endpoint, ex);
            return null;
        }
    }

    public async Task<DisplayPageDto?> GetDisplayItemsAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        const string endpoint = "POST /api/v1/display/items";
        var distinct = ids.Where(id => id != Guid.Empty).Distinct().ToList();
        try
        {
            // The Engine answers at most MaxIds ids per call, so a longer list goes out as several calls and comes back as one page.
            var chunks = distinct.Chunk(DisplayItemsRequest.MaxIds).Select(chunk => LoadItemsChunkAsync(chunk, endpoint, ct)).ToList();
            var pages = await Task.WhenAll(chunks);
            if (pages.Any(page => page is null))
            {
                return null;
            }

            ClearFailure(endpoint);
            return new DisplayPageDto("items", "Items", null, null, [], pages.SelectMany(page => page!.Catalog).DistinctBy(card => card.Id).ToList());
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "POST /api/v1/display/items failed");
            RecordExceptionFailure(endpoint, ex);
            return null;
        }
    }

    private async Task<DisplayPageDto?> LoadItemsChunkAsync(Guid[] chunk, string endpoint, CancellationToken ct)
    {
        var response = await _http.PostAsJsonAsync("/api/v1/display/items", new DisplayItemsRequest(chunk), ct);
        if (!response.IsSuccessStatusCode)
        {
            await RecordHttpFailureAsync(endpoint, response, ct);
            return null;
        }

        return NormalizeDisplayPage(await response.Content.ReadFromJsonAsync<DisplayPageDto>(cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ContributorShelfDto>> GetContributorShelvesAsync(CancellationToken ct = default)
    {
        const string endpoint = "GET /api/v1/display/contributor-shelves";
        var shelves = await GetAsync<List<ContributorShelfDto>>(
            endpoint,
            "/api/v1/display/contributor-shelves",
            () => [],
            ct: ct);
        return shelves.Select(shelf => new ContributorShelfDto
        {
            Key = shelf.Key,
            PersonId = shelf.PersonId,
            PersonName = shelf.PersonName,
            HeadshotUrl = string.IsNullOrWhiteSpace(shelf.HeadshotUrl) ? null : AbsoluteUrl(shelf.HeadshotUrl),
            Role = shelf.Role,
            Lane = shelf.Lane,
            ShelfType = shelf.ShelfType,
            Title = shelf.Title,
            OwnedCount = shelf.OwnedCount,
            EarliestYear = shelf.EarliestYear,
            LatestYear = shelf.LatestYear,
            Items = shelf.Items.Select(item => new ContributorShelfItemDto
            {
                WorkId = item.WorkId,
                Title = item.Title,
                MediaType = item.MediaType,
                CoverUrl = string.IsNullOrWhiteSpace(item.CoverUrl) ? null : AbsoluteUrl(item.CoverUrl),
                Year = item.Year,
            }).ToList(),
        }).ToList();
    }

    // -- GET /api/v1/display/grids/{lane}/{shelfKey} ---------------------------

    public async Task<DisplayGridLoadResult> GetDisplayGridAsync(
        string lane,
        string shelfKey,
        string? sort,
        string? type,
        string? genre,
        string? format,
        string? status,
        int offset,
        int limit,
        CancellationToken ct = default)
    {
        const string endpoint = "GET /api/v1/display/grids/{lane}/{shelfKey}";
        try
        {
            var query = new List<string>();
            AddQuery(query, "sort", sort);
            AddQuery(query, "type", type);
            AddQuery(query, "genre", genre);
            AddQuery(query, "format", format);
            AddQuery(query, "status", status);
            AddQuery(query, "offset", offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddQuery(query, "limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var url = $"/api/v1/display/grids/{Uri.EscapeDataString(lane)}/{Uri.EscapeDataString(shelfKey)}?{string.Join("&", query)}";
            var page = await _http.GetFromJsonAsync<DisplayGridPageDto>(url, ct);
            if (page is null)
            {
                return DisplayGridLoadResult.Failed(DisplayGridLoadFailure.Failed);
            }

            ClearFailure(endpoint);
            return DisplayGridLoadResult.Loaded(page with { Items = page.Items.Select(NormalizeDisplayCard).ToList() });
        }
        catch (HttpRequestException ex) when (ex.StatusCode is { } code)
        {
            // GetFromJsonAsync throws on a non-success status. Keep the status on the shared failure state like the other
            // reads do, and hand the page its own answer so it never reads a status another call left behind.
            _logger.LogWarning(ex, "{Endpoint} returned {Status}", endpoint, (int)code);
            _failureState.RecordRawFailure(endpoint, (int)code, ex.Message);
            return DisplayGridLoadResult.Failed(code switch
            {
                HttpStatusCode.NotFound => DisplayGridLoadFailure.NotFound,
                HttpStatusCode.BadRequest => DisplayGridLoadFailure.BadRequest,
                _ => DisplayGridLoadFailure.Failed,
            });
        }
        catch (OperationCanceledException)
        {
            return DisplayGridLoadResult.Failed(DisplayGridLoadFailure.Failed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Endpoint} failed", endpoint);
            RecordExceptionFailure(endpoint, ex);
            return DisplayGridLoadResult.Failed(DisplayGridLoadFailure.Failed);
        }
    }

    public Task<bool> SetDisplayGridSortAsync(string rememberKey, string sort, CancellationToken ct = default) =>
        PutAsync(
            "PUT /api/v1/display/grid-preferences/{rememberKey}",
            $"/api/v1/display/grid-preferences/{Uri.EscapeDataString(rememberKey)}",
            new DisplayGridSortPreferenceRequest(sort),
            ct: ct);

}
