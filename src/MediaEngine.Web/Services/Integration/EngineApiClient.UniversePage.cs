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

    // -- GET /universe/{qid}/things/{id} -----------------------------------------------

    public async Task<UniverseThingLoadResult> GetUniverseThingAsync(string qid, Guid thingId, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync($"universe/{Uri.EscapeDataString(qid)}/things/{thingId:D}", ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return UniverseThingLoadResult.NotFound;
            }

            response.EnsureSuccessStatusCode();
            var thing = await response.Content.ReadFromJsonAsync<UniverseThingDto>(cancellationToken: ct);
            return thing is null ? UniverseThingLoadResult.Unavailable : UniverseThingLoadResult.Loaded(NormalizeUniverseThing(thing));
        }
        catch (OperationCanceledException) { return UniverseThingLoadResult.Unavailable; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /universe/{Qid}/things/{ThingId} failed", qid, thingId);
            LastError = ex.Message;
            return UniverseThingLoadResult.Unavailable;
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
        },
        // The page reads Cast and Adaptations from Connections; Detail's own lists are empty on this read.
        Connections = new UniverseConnectionsDto
        {
            Characters = page.Connections.Characters,
            CharacterCount = page.Connections.CharacterCount,
            Cast = page.Connections.Cast.Select(row => new UniverseSourcedCastMemberDto
            {
                Member = new UniverseClientCastMemberDto
                {
                    PersonId = row.Member.PersonId,
                    Name = row.Member.Name,
                    HeadshotUrl = NormalizeOptionalUrl(row.Member.HeadshotUrl),
                    Characters = row.Member.Characters,
                    WorkCount = row.Member.WorkCount,
                },
                Sources = row.Sources,
            }).ToList(),
            Adaptations = page.Connections.Adaptations,
        },
        Standalone = page.Standalone is null ? null : NormalizeUniverseShelf(page.Standalone),
        Missing = page.Missing,
        Contents = page.Contents is null ? null : NormalizeUniverseContents(page.Contents),
        Enrichment = page.Enrichment,
        GraphEntityCount = page.GraphEntityCount,
        GraphRelationshipCount = page.GraphRelationshipCount,
    };

    // Pictures come from the Engine as addresses on the Engine; the Dashboard is served from elsewhere, so each is made absolute.
    private UniverseContentsDto NormalizeUniverseContents(UniverseContentsDto contents) => new()
    {
        Sections = contents.Sections.Select(section => new UniverseContentsSectionDto
        {
            Key = section.Key,
            Label = section.Label,
            Count = section.Count,
            Tiles = section.Tiles.Select(tile => new UniverseContentsTileDto
            {
                Id = tile.Id,
                Label = tile.Label,
                Qid = tile.Qid,
                PictureUrl = NormalizeOptionalUrl(tile.PictureUrl),
                LogoUrl = NormalizeOptionalUrl(tile.LogoUrl),
                AddedByUser = tile.AddedByUser,
                NoPlotDetails = tile.NoPlotDetails,
                PictureSource = tile.PictureSource,
            }).ToList(),
        }).ToList(),
        Timeline = contents.Timeline is null ? null : new UniverseTimelineDto
        {
            DefaultOrder = contents.Timeline.DefaultOrder,
            StoryOrderAvailable = contents.Timeline.StoryOrderAvailable,
            Stops = contents.Timeline.Stops.Select(stop => new UniverseTimelineStopDto
            {
                Id = stop.Id,
                Label = stop.Label,
                Kind = stop.Kind,
                PictureUrl = NormalizeOptionalUrl(stop.PictureUrl),
                StoryYear = stop.StoryYear,
                ReleaseYear = stop.ReleaseYear,
                WorkLabel = stop.WorkLabel,
            }).ToList(),
        },
        HiddenCount = contents.HiddenCount,
    };

    private UniverseThingDto NormalizeUniverseThing(UniverseThingDto thing) => new()
    {
        Id = thing.Id,
        UniverseQid = thing.UniverseQid,
        UniverseLabel = thing.UniverseLabel,
        Label = thing.Label,
        KindKey = thing.KindKey,
        KindLabel = thing.KindLabel,
        Qid = thing.Qid,
        AddedByUser = thing.AddedByUser,
        PictureUrl = NormalizeOptionalUrl(thing.PictureUrl),
        PictureSource = thing.PictureSource,
        LogoUrl = NormalizeOptionalUrl(thing.LogoUrl),
        Description = thing.Description,
        DescriptionSource = thing.DescriptionSource,
        Aliases = thing.Aliases,
        DetailsHidden = thing.DetailsHidden,
        AppearsIn = thing.AppearsIn.Select(work => new UniverseThingWorkDto
        {
            WorkQid = work.WorkQid,
            Label = work.Label,
            MediaType = work.MediaType,
            CoverUrl = NormalizeOptionalUrl(work.CoverUrl),
            Role = work.Role,
        }).ToList(),
        Related = thing.Related.Select(group => new UniverseThingRelatedGroupDto
        {
            Key = group.Key,
            Label = group.Label,
            Count = group.Count,
            Things = group.Things.Select(link => new UniverseThingLinkDto
            {
                Id = link.Id,
                Label = link.Label,
                KindKey = link.KindKey,
                PictureUrl = NormalizeOptionalUrl(link.PictureUrl),
                LogoUrl = NormalizeOptionalUrl(link.LogoUrl),
                AddedByUser = link.AddedByUser,
            }).ToList(),
        }).ToList(),
    };

    private DisplayShelfDto NormalizeUniverseShelf(DisplayShelfDto shelf) =>
        shelf with { Items = shelf.Items.Select(NormalizeDisplayCard).ToList() };
}
