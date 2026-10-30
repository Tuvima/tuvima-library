using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Matching;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Domain;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Services;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Endpoints;

public static partial class MetadataEndpoints
{
    /// <summary>
    /// The Match tool's Show › Season › Episode browser (universal editor v2, D4). Unlike the scoped lookup above, which only
    /// ever lists the item's own show, every step here works for any TheTVDB show, and the episode step returns real episode
    /// records, so an episode can be matched into a different show. All three are reads: nothing is written until Save.
    /// </summary>
    private static void MapTvMatchBrowserEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/{entityId:guid}/tv-match-browser/shows", async (
            Guid entityId, string? query, HttpContext http, ICanonicalValueRepository canonicals,
            ILibraryItemRepository library, IMetadataEditorRepository editor,
            CatalogueResourceAuthorizationService resources, ProviderApiClient tvdb, CancellationToken ct) =>
        {
            var root = await ResolveTvdbRootScopeAsync(entityId, http, canonicals, library, editor, resources,
                ApplicationPermissionIds.MetadataRead, ct);
            if (root is null)
            {
                return ApiErrors.NotFound("TV match scope was not found.");
            }
            if (!tvdb.IsConfigured("tvdb"))
            {
                return ApiErrors.BadRequest("Connect TheTVDB in Settings before searching TV matches.");
            }
            var text = query?.Trim() ?? string.Empty;
            if (text.Length > 200)
            {
                return ApiErrors.BadRequest("Type a show name to search for.");
            }
            try
            {
                var currentShowId = GetCanonicalValue(
                    BuildLatestCanonicalMap(await canonicals.GetByEntityAsync(root.FieldEntityId, ct)), BridgeIdKeys.TvdbId);
                if (text.Length == 0)
                {
                    // No words typed: open on the item's own show so the person starts where the item already is.
                    if (currentShowId is null)
                    {
                        return ApiErrors.BadRequest("Type a show name to search for.");
                    }
                    var own = await tvdb.GetAsync("tvdb", "series_extended", ProviderApiClient.Args(("id", currentShowId)), ct);
                    if (own is null)
                    {
                        return Results.Ok(new TvMatchBrowserShowsDto(string.Empty, []));
                    }
                    var ownName = await ReadTvdbShowNameAsync(tvdb, currentShowId, own, ct);
                    var row = new System.Text.Json.Nodes.JsonObject
                    {
                        ["tvdb_id"] = currentShowId,
                        ["name"] = ownName,
                        ["year"] = own["year"]?.ToString(),
                        ["image_url"] = own["image"]?.ToString(),
                        ["overview"] = own["overview"]?.ToString(),
                    };
                    return Results.Ok(new TvMatchBrowserShowsDto(string.Empty,
                        TvMatchBrowserMapper.MapShows(new System.Text.Json.Nodes.JsonArray(row), currentShowId)));
                }
                var results = await tvdb.GetAsync("tvdb", "search", ProviderApiClient.Args(("query", text)), ct);
                return Results.Ok(new TvMatchBrowserShowsDto(text, TvMatchBrowserMapper.MapShows(results, currentShowId)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Problem($"TheTVDB did not answer: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
            }
        }).WithName("GetTvMatchBrowserShows")
          .Produces<TvMatchBrowserShowsDto>(StatusCodes.Status200OK)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead);

        group.MapGet("/{entityId:guid}/tv-match-browser/shows/{seriesId}/seasons", async (
            Guid entityId, string seriesId, HttpContext http, ICanonicalValueRepository canonicals,
            ILibraryItemRepository library, IMetadataEditorRepository editor,
            CatalogueResourceAuthorizationService resources, ProviderApiClient tvdb, CancellationToken ct) =>
        {
            var context = await ResolveTvMatchBrowserContextAsync(entityId, seriesId, http, canonicals, library, editor,
                resources, tvdb, ct);
            if (context.Error is not null)
            {
                return context.Error;
            }
            try
            {
                var show = await tvdb.GetAsync("tvdb", "series_extended", ProviderApiClient.Args(("id", seriesId)), ct);
                if (show is null)
                {
                    return ApiErrors.NotFound("That show is no longer on TheTVDB.");
                }
                var name = await ReadTvdbShowNameAsync(tvdb, seriesId, show, ct);
                return Results.Ok(new TvMatchBrowserSeasonsDto(seriesId, name,
                    TvMatchBrowserMapper.MapSeasons(show, seriesId, context.CurrentSeasonId)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Problem($"TheTVDB did not answer: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
            }
        }).WithName("GetTvMatchBrowserSeasons")
          .Produces<TvMatchBrowserSeasonsDto>(StatusCodes.Status200OK)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead);

        group.MapGet("/{entityId:guid}/tv-match-browser/shows/{seriesId}/seasons/{seasonNumber:int}/episodes", async (
            Guid entityId, string seriesId, int seasonNumber, HttpContext http, ICanonicalValueRepository canonicals,
            ILibraryItemRepository library, IMetadataEditorRepository editor, IBridgeIdRepository bridges,
            CatalogueResourceAuthorizationService resources, ProviderApiClient tvdb, CancellationToken ct) =>
        {
            var context = await ResolveTvMatchBrowserContextAsync(entityId, seriesId, http, canonicals, library, editor,
                resources, tvdb, ct);
            if (context.Error is not null)
            {
                return context.Error;
            }
            try
            {
                var show = await tvdb.GetAsync("tvdb", "series_extended", ProviderApiClient.Args(("id", seriesId)), ct);
                if (show is null)
                {
                    return ApiErrors.NotFound("That show is no longer on TheTVDB.");
                }
                var season = TvMatchBrowserMapper.MapSeasons(show, seriesId, context.CurrentSeasonId)
                    .FirstOrDefault(item => item.SeasonNumber == seasonNumber);
                if (season is null)
                {
                    return ApiErrors.NotFound($"Season {seasonNumber} is not in this show's default TheTVDB order.");
                }
                var name = await ReadTvdbShowNameAsync(tvdb, seriesId, show, ct);
                var ordered = await tvdb.GetAllPagesAsync("tvdb", "series_episodes",
                    ProviderApiClient.Args(("id", seriesId), ("season_type", "default"), ("lang", "eng")), ct);
                var inSeason = TvMatchBrowserMapper.SelectSeasonEpisodes(ordered, season);
                var files = new Dictionary<string, Guid>(StringComparer.Ordinal);
                foreach (var node in inSeason)
                {
                    var id = TvdbText(node, "id");
                    if (id is not null && (await bridges.FindByValueAsync(BridgeIdKeys.TvdbEpisodeId, id, ct)).FirstOrDefault() is { } hit)
                    {
                        files[id] = hit.EntityId;
                    }
                }
                return Results.Ok(new TvMatchBrowserEpisodesDto(seriesId, name, season.SeasonId, season.SeasonNumber,
                    TvMatchBrowserMapper.MapEpisodes(inSeason, seriesId, name, season, context.CurrentEpisodeId, files)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Problem($"TheTVDB did not answer: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
            }
        }).WithName("GetTvMatchBrowserEpisodes")
          .Produces<TvMatchBrowserEpisodesDto>(StatusCodes.Status200OK)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead);
    }

    private sealed record TvMatchBrowserContext(
        IResult? Error, string? CurrentSeasonId, string? CurrentEpisodeId);

    private static async Task<TvMatchBrowserContext> ResolveTvMatchBrowserContextAsync(
        Guid entityId, string seriesId, HttpContext http, ICanonicalValueRepository canonicals,
        ILibraryItemRepository library, IMetadataEditorRepository editor,
        CatalogueResourceAuthorizationService resources, ProviderApiClient tvdb, CancellationToken ct)
    {
        if (seriesId.Length is < 1 or > 12 || !seriesId.All(char.IsAsciiDigit))
        {
            return new(ApiErrors.BadRequest("Choose a valid TheTVDB show."), null, null);
        }
        var context = await ResolveEditorScopeContextAsync(entityId, canonicals, library, editor, ct);
        if (context is null || NormalizeEditorMediaType(context.MediaType) != "TV")
        {
            return new(ApiErrors.NotFound("TV match scope was not found."), null, null);
        }
        var root = context.Scopes.FirstOrDefault(item => item.ScopeId == "series");
        if (root is null || !await HasEditorScopeAccessAsync(http, resources, root, ApplicationPermissionIds.MetadataRead, ct))
        {
            return new(ApiErrors.NotFound("TV match scope was not found."), null, null);
        }
        if (!tvdb.IsConfigured("tvdb"))
        {
            return new(ApiErrors.BadRequest("Connect TheTVDB in Settings before searching TV matches."), null, null);
        }

        async Task<string?> ValueAsync(string scopeId, string key) =>
            context.Scopes.FirstOrDefault(item => item.ScopeId == scopeId) is { } scope
                ? GetCanonicalValue(BuildLatestCanonicalMap(await canonicals.GetByEntityAsync(scope.FieldEntityId, ct)), key)
                : null;

        return new(null, await ValueAsync("season", BridgeIdKeys.TvdbSeasonId), await ValueAsync("episode", BridgeIdKeys.TvdbEpisodeId));
    }

    private static async Task<string> ReadTvdbShowNameAsync(
        ProviderApiClient tvdb, string seriesId, System.Text.Json.Nodes.JsonNode show, CancellationToken ct)
    {
        var english = await tvdb.GetAsync("tvdb", "series_translation", ProviderApiClient.Args(("id", seriesId)), ct);
        return TvdbText(english, "name") ?? TvdbText(show, "name") ?? $"TheTVDB series {seriesId}";
    }
}
