using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Matching;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Services;

namespace MediaEngine.Api.Endpoints;

public static partial class MetadataEndpoints
{
    /// <summary>
    /// The Match tool's two starting reads (universal editor v3, WP4): what the item's file name says (and the provider's best guess for it),
    /// and what "Match the other episodes in this season by number" would cover. Both are reads: nothing is written until Save.
    /// </summary>
    private static void MapMatchContextEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/{entityId:guid}/match-context", async (
            Guid entityId, IItemCanonicalRepository items, IMediaAssetRepository assets, ProviderApiClient tvdb,
            ILoggerFactory loggers, CancellationToken ct) =>
        {
            var context = await items.ResolveWorkAssetContextAsync(entityId, ct);
            if (context is null)
            {
                return ApiErrors.NotFound("Match context was not found.");
            }

            var asset = await assets.FindByIdAsync(context.AssetId, ct);
            var path = asset?.FilePathRoot ?? string.Empty;
            var isTv = string.Equals(context.MediaType, "TV", StringComparison.OrdinalIgnoreCase);
            var parsed = EditorFileNameParser.Parse(path, isTv);
            var dto = new EditorMatchContextDto
            {
                EntityId = entityId,
                MediaType = context.MediaType,
                FileName = string.IsNullOrEmpty(path) ? string.Empty : Path.GetFileName(path.Replace('\\', '/')),
                ParsedTitle = parsed.Title,
                ParsedShow = parsed.Show,
                ParsedSeason = parsed.Season,
                ParsedEpisode = parsed.Episode,
                ParsedYear = parsed.Year,
            };

            // The best guess is the top TheTVDB answer for the parsed show. Without a connected provider, or when it does not answer, there
            // is simply no guess: the search still starts from the parsed name.
            if (isTv && !string.IsNullOrWhiteSpace(parsed.Show) && tvdb.IsConfigured("tvdb"))
            {
                try
                {
                    var found = await tvdb.GetAsync("tvdb", "search", ProviderApiClient.Args(("query", parsed.Show!)), ct);
                    if (TvMatchBrowserMapper.MapShows(found, null, limit: 1).FirstOrDefault() is { } top)
                    {
                        dto.BestGuess = new EditorMatchBestGuessDto
                        {
                            ProviderName = "tvdb",
                            ProviderItemId = top.SeriesId,
                            Name = top.Name,
                            Year = top.Year,
                            ImageUrl = top.ImageUrl,
                        };
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    loggers.CreateLogger("EditorMatchContext").LogWarning(ex, "TheTVDB did not answer for the best guess of item {EntityId}", entityId);
                }
            }

            return Results.Ok(dto);
        }).WithName("GetEditorMatchContext")
          .Produces<EditorMatchContextDto>(StatusCodes.Status200OK)
          .ProducesProblem(StatusCodes.Status404NotFound)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead);

        group.MapGet("/{entityId:guid}/match-siblings", async (
            Guid entityId, string seriesId, int seasonNumber, HttpContext http, IEpisodeSiblingMatcher matcher,
            CatalogueResourceAuthorizationService resources, ProviderApiClient tvdb, CancellationToken ct) =>
        {
            if (seriesId.Length is < 1 or > 12 || !seriesId.All(char.IsAsciiDigit) || seasonNumber is < 0 or > 999)
            {
                return ApiErrors.BadRequest("Choose a valid TheTVDB show and season.");
            }
            if (!tvdb.IsConfigured("tvdb"))
            {
                return ApiErrors.BadRequest("Connect TheTVDB in Settings before matching the other episodes.");
            }
            try
            {
                var preview = await matcher.PreviewAsync(entityId, seriesId, seasonNumber, LibraryItemEndpoints.CanEdit(http, resources), ct);
                return preview is null ? ApiErrors.NotFound("This item is not a TV episode.") : Results.Ok(preview);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Problem($"TheTVDB did not answer: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
            }
        }).WithName("PreviewEpisodeSiblingMatch")
          .Produces<EpisodeSiblingMatchPreviewDto>(StatusCodes.Status200OK)
          .ProducesProblem(StatusCodes.Status400BadRequest)
          .ProducesProblem(StatusCodes.Status404NotFound)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead);
    }
}
