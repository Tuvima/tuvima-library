using System.Net;
using System.Net.Http.Json;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Universe;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    // -- GET /universe/{qid}/overview --------------------------------------------------

    public async Task<UniversePageLoadResult> GetUniversePageAsync(string qid, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync($"universe/{Uri.EscapeDataString(qid)}/overview", ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return UniversePageLoadResult.NotFound;
            }

            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadFromJsonAsync<UniversePageDto>(cancellationToken: ct);
            return page is null
                ? UniversePageLoadResult.Unavailable
                : UniversePageLoadResult.Loaded(NormalizeUniversePage(page));
        }
        catch (OperationCanceledException) { return UniversePageLoadResult.Unavailable; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /universe/{Qid}/overview failed", qid);
            LastError = ex.Message;
            return UniversePageLoadResult.Unavailable;
        }
    }

    private UniversePageDto NormalizeUniversePage(UniversePageDto page) => new()
    {
        Detail = new UniverseClientDetailDto
        {
            Qid = page.Detail.Qid,
            Name = page.Detail.Name,
            Description = page.Detail.Description,
            GroupType = page.Detail.GroupType,
            ItemCount = page.Detail.ItemCount,
            SeriesCount = page.Detail.SeriesCount,
            MediaTypes = page.Detail.MediaTypes,
            Shelves = page.Detail.Shelves.Select(shelf => new UniverseMemberShelfDto
            {
                CollectionId = shelf.CollectionId,
                GroupType = shelf.GroupType,
                ItemCount = shelf.ItemCount,
                Shelf = NormalizeUniverseShelf(shelf.Shelf),
                AlsoInThisWorld = shelf.AlsoInThisWorld is null ? null : NormalizeUniverseShelf(shelf.AlsoInThisWorld),
                AlsoInThisWorldCount = shelf.AlsoInThisWorldCount,
            }).ToList(),
            Cast = page.Detail.Cast.Select(member => new UniverseClientCastMemberDto
            {
                PersonId = member.PersonId,
                Name = member.Name,
                HeadshotUrl = NormalizeOptionalUrl(member.HeadshotUrl),
                Characters = member.Characters,
                WorkCount = member.WorkCount,
            }).ToList(),
            Adaptations = page.Detail.Adaptations,
        },
        Standalone = page.Standalone is null ? null : NormalizeUniverseShelf(page.Standalone),
        Missing = page.Missing,
        Enrichment = page.Enrichment,
        GraphEntityCount = page.GraphEntityCount,
        GraphRelationshipCount = page.GraphRelationshipCount,
    };

    private DisplayShelfDto NormalizeUniverseShelf(DisplayShelfDto shelf) =>
        shelf with { Items = shelf.Items.Select(NormalizeDisplayCard).ToList() };
}
