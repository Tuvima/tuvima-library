using System.Text.Json.Nodes;
using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Domain;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Services;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Caching.Memory;
using MediaEngine.Providers.Helpers;
using SkiaSharp;

namespace MediaEngine.Api.Endpoints;

public static partial class MetadataEndpoints
{
    private sealed record TvdbPreviewEntry(Guid EntityId, Guid OwnerWorkId, string SourceUrl);
    private const string TvdbSeasonTypeKey = "tvdb_season_type";
    private static readonly string[] SupportedTvdbSeasonTypes = ["default", "official", "dvd", "absolute"];
    private static readonly IReadOnlyDictionary<string, string> EmptyTvdbScopeValues =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static void MapTvdbScopedMatchEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/{entityId:guid}/tvdb-match/previews/{token}", async (
            Guid entityId, string token, HttpContext http, IMemoryCache cache,
            CatalogueResourceAuthorizationService resources, IHttpClientFactory httpFactory,
            CancellationToken ct) =>
        {
            if (token.Length != 32 || !token.All(Uri.IsHexDigit)
                || !cache.TryGetValue<TvdbPreviewEntry>($"tvdb-preview:{token}", out var entry)
                || entry is null || entry.EntityId != entityId)
                return ApiErrors.NotFound("TheTVDB image preview is no longer available.");
            if (await resources.EvaluateEntityAsync(http, "Work", entry.OwnerWorkId,
                    ApplicationPermissionIds.MetadataRead, ct) != CatalogueResourceAccess.Allowed)
                return ApiErrors.NotFound("TheTVDB image preview is not available for this item.");
            if (!Uri.TryCreate(entry.SourceUrl, UriKind.Absolute, out var source)
                || source.Scheme != Uri.UriSchemeHttps
                || !(source.Host.Equals("thetvdb.com", StringComparison.OrdinalIgnoreCase)
                     || source.Host.EndsWith(".thetvdb.com", StringComparison.OrdinalIgnoreCase)))
                return ApiErrors.NotFound("TheTVDB image source is unavailable.");
            using var client = httpFactory.CreateClient("cover_download");
            using var response = await client.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return ApiErrors.NotFound("TheTVDB image source is unavailable.");
            var bytes = await BoundedHttpContent.ReadImageAsync(response.Content, ct);
            using var bitmap = SKBitmap.Decode(bytes);
            if (bitmap is null || bitmap.Width < 1 || bitmap.Height < 1) return ApiErrors.NotFound("TheTVDB image source is invalid.");
            var width = Math.Min(bitmap.Width, 320);
            var height = Math.Max(1, (int)Math.Round(bitmap.Height * (width / (double)bitmap.Width)));
            using var resized = bitmap.Resize(new SKImageInfo(width, height), SKSamplingOptions.Default);
            if (resized is null) return ApiErrors.NotFound("TheTVDB image source cannot be previewed.");
            using var image = SKImage.FromBitmap(resized);
            using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 82);
            http.Response.Headers.CacheControl = "private, max-age=900";
            return Results.File(encoded.ToArray(), "image/jpeg");
        }).WithName("GetTvdbMatchPreview")
          .Produces(StatusCodes.Status200OK, contentType: "image/jpeg")
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead);

        group.MapGet("/{entityId:guid}/tvdb-match/{scopeId}/candidates", async (
            Guid entityId, string scopeId, int? seasonNumber, string? seasonType, HttpContext http,
            ICanonicalValueRepository canonicals, ILibraryItemRepository library,
            IMetadataEditorRepository editor, CatalogueResourceAuthorizationService resources,
            TvdbRetailClient tvdb, IMemoryCache cache, CancellationToken ct) =>
        {
            var resolved = await ResolveTvdbMatchScopeAsync(entityId, scopeId, http, canonicals,
                library, editor, resources, ApplicationPermissionIds.MetadataRead, ct);
            if (resolved is null) return ApiErrors.NotFound("TV match scope was not found.");
            if (!tvdb.IsConfigured()) return ApiErrors.BadRequest("Connect TheTVDB in Settings before searching TV matches.");

            var (scope, root, seasonScope) = resolved.Value;
            var rootValues = BuildLatestCanonicalMap(await canonicals.GetByEntityAsync(root.FieldEntityId, ct));
            var showId = GetCanonicalValue(rootValues, BridgeIdKeys.TvdbId);
            if (string.IsNullOrWhiteSpace(showId))
                return ApiErrors.BadRequest("Match this show to TheTVDB before matching its seasons or episodes.");

            var scopeValues = BuildLatestCanonicalMap(await canonicals.GetByEntityAsync(scope.FieldEntityId, ct));
            var localSeason = ParseTvdbNumber(GetCanonicalValue(scopeValues, MetadataFieldConstants.SeasonNumber))
                ?? ParseOwnedSeasonNumber(scope);
            var localEpisode = ParseTvdbNumber(GetCanonicalValue(scopeValues, MetadataFieldConstants.EpisodeNumber));
            try
            {
                var show = await tvdb.GetSeriesAsync(showId, ct);
                if (show is null) return ApiErrors.BadRequest("The matched TheTVDB show is no longer available.");
                var seasonScopeValues = scope.ScopeId == "season"
                    ? scopeValues
                    : await LoadTvdbSeasonScopeValuesAsync(seasonScope, canonicals, ct);
                var selectedSeasonType = ResolveTvdbSeasonType(seasonType, seasonScopeValues, rootValues, show);
                if (selectedSeasonType is null)
                    return ApiErrors.BadRequest("Select a supported TheTVDB episode order.");
                var showSeasonType = ResolveTvdbSeasonType(null, EmptyTvdbScopeValues, rootValues, show)
                    ?? "default";
                var showEnglish = await tvdb.GetSeriesTranslationAsync(showId, ct: ct);
                var showName = TvdbText(showEnglish, "name") ?? $"TheTVDB series {showId}";
                var seasons = show["seasons"]?.AsArray()
                    .Where(node => node is not null && TvdbText(node, "id") is not null)
                    .Where(node => IsTvdbSeasonInOrder(node, show, selectedSeasonType))
                    .OrderBy(node => ParseTvdbNumber(TvdbText(node, "number")))
                    .ToList() ?? [];
                var availableSeasons = seasons.Select(node => ParseTvdbNumber(TvdbText(node, "number")))
                    .Where(number => number.HasValue).Select(number => number!.Value).Distinct().Order().ToList();
                var confirmedSeason = scope.ScopeId == "episode"
                    ? seasons.FirstOrDefault(node => TvdbText(node, "id")
                        == seasonScopeValues.GetValueOrDefault(BridgeIdKeys.TvdbSeasonId))
                    : null;
                IReadOnlyList<TvdbMatchCandidateDto> candidates;
                if (scope.ScopeId == "season")
                {
                    var results = new List<TvdbMatchCandidateDto>();
                    foreach (var node in seasons)
                    {
                        var id = TvdbText(node, "id")!;
                        var number = ParseTvdbNumber(TvdbText(node, "number"));
                        if (number is null) continue;
                        var english = await tvdb.GetSeasonTranslationAsync(id, ct: ct);
                        results.Add(new TvdbMatchCandidateDto(
                            id, showId, TvdbText(english, "name") ?? SeasonLabel(number.Value),
                            number.Value, null, TvdbText(node, "firstAired"),
                            TvdbText(english, "overview"), TvdbText(node, "image"), selectedSeasonType,
                            $"https://thetvdb.com/seasons/{id}"));
                    }
                    candidates = results;
                }
                else
                {
                    if (seasonNumber is { } requested && !availableSeasons.Contains(requested))
                        return ApiErrors.BadRequest("That season is not in the show's selected TheTVDB episode order.");
                    var selectedSeason = seasonNumber
                        ?? (ParseTvdbNumber(TvdbText(confirmedSeason, "number")) is { } confirmed
                            && availableSeasons.Contains(confirmed) ? confirmed
                            : localSeason is { } owned && availableSeasons.Contains(owned)
                            ? owned : availableSeasons.FirstOrDefault());
                    var episodes = await tvdb.GetAllEpisodesAsync(showId, selectedSeasonType, language: "eng", ct: ct);
                    var selectedEpisodes = episodes
                        .Where(node => ParseTvdbNumber(TvdbText(node, "seasonNumber")) == selectedSeason)
                        .Where(node => TvdbText(node, "id") is not null)
                        .Where(node => ParseTvdbNumber(TvdbText(node, "number")) is >= 1)
                        .OrderBy(node => ParseTvdbNumber(TvdbText(node, "seasonNumber")))
                        .ThenBy(node => ParseTvdbNumber(TvdbText(node, "number")))
                        .ToList();
                    var results = new List<TvdbMatchCandidateDto>();
                    foreach (var node in selectedEpisodes)
                    {
                        var id = TvdbText(node, "id")!;
                        var english = await tvdb.GetEpisodeTranslationAsync(id, ct: ct);
                        results.Add(new TvdbMatchCandidateDto(
                            id, showId,
                            TvdbText(english, "name") ?? $"Episode {TvdbText(node, "number")}",
                            ParseTvdbNumber(TvdbText(node, "seasonNumber")) ?? 0,
                            ParseTvdbNumber(TvdbText(node, "number")),
                            TvdbText(node, "aired"), TvdbText(english, "overview"),
                            TvdbText(node, "image"), selectedSeasonType,
                            $"https://thetvdb.com/episodes/{id}"));
                    }
                    candidates = results;
                }
                candidates = candidates.Select(candidate => candidate with
                {
                    ImageUrl = CreateTvdbPreviewUrl(cache, entityId, scope.FieldEntityId,
                        candidate.ImageUrl),
                }).ToList();
                return Results.Ok(new TvdbScopedMatchCandidatesDto(scope.ScopeId, showName, showId,
                    GetCanonicalValue(scopeValues, MetadataFieldConstants.IdentityRevision) ?? string.Empty,
                    localSeason, localEpisode, availableSeasons, candidates, selectedSeasonType,
                    GetAvailableTvdbSeasonTypes(show),
                    confirmedSeason is not null,
                    ParseTvdbNumber(TvdbText(confirmedSeason, "number")), showSeasonType));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Problem($"TheTVDB search failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
            }
        }).WithName("GetTvdbScopedMatchCandidates")
          .Produces<TvdbScopedMatchCandidatesDto>(StatusCodes.Status200OK)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead);

        group.MapPost("/{entityId:guid}/tvdb-match/{scopeId}", async (
            Guid entityId, string scopeId, ApplyTvdbScopedMatchDto request, HttpContext http,
            ICanonicalValueRepository canonicals, IMetadataClaimRepository claims,
            IBridgeIdRepository bridges, ILibraryItemRepository library,
            IMetadataEditorRepository editor, CatalogueResourceAuthorizationService resources,
            TvdbRetailClient tvdb, ImageEnrichmentService images, CancellationToken ct) =>
        {
            var resolved = await ResolveTvdbMatchScopeAsync(entityId, scopeId, http, canonicals,
                library, editor, resources, ApplicationPermissionIds.MetadataWrite, ct);
            if (resolved is null) return ApiErrors.NotFound("TV match scope was not found.");
            if (!tvdb.IsConfigured()) return ApiErrors.BadRequest("Connect TheTVDB in Settings before matching TV items.");
            var (scope, root, seasonScope) = resolved.Value;
            var rootValues = BuildLatestCanonicalMap(await canonicals.GetByEntityAsync(root.FieldEntityId, ct));
            var showId = GetCanonicalValue(rootValues, BridgeIdKeys.TvdbId);
            if (string.IsNullOrWhiteSpace(showId) || !string.Equals(showId, request.SeriesId, StringComparison.Ordinal))
                return ApiErrors.Conflict("The show match changed. Reload the editor and select a result again.");
            if (string.IsNullOrWhiteSpace(request.CandidateId) || !request.CandidateId.All(char.IsDigit))
                return ApiErrors.BadRequest("Select a valid TheTVDB result.");

            var current = BuildLatestCanonicalMap(await canonicals.GetByEntityAsync(scope.FieldEntityId, ct));
            var revision = GetCanonicalValue(current, MetadataFieldConstants.IdentityRevision) ?? string.Empty;
            if (!string.Equals(revision, request.ExpectedRevision, StringComparison.Ordinal))
                return ApiErrors.Conflict("This match changed while the picker was open. Reload and review it again.");

            try
            {
                var show = await tvdb.GetSeriesAsync(showId, ct);
                var seasonScopeValues = scope.ScopeId == "season"
                    ? current
                    : await LoadTvdbSeasonScopeValuesAsync(seasonScope, canonicals, ct);
                var selectedSeasonType = ResolveTvdbSeasonType(request.SeasonType, seasonScopeValues, rootValues, show);
                if (selectedSeasonType is null)
                    return ApiErrors.BadRequest("Select a supported TheTVDB episode order.");
                var showSeasonType = ResolveTvdbSeasonType(null, EmptyTvdbScopeValues, rootValues, show);
                if (!string.Equals(selectedSeasonType, showSeasonType, StringComparison.Ordinal))
                    return ApiErrors.Conflict("Apply this episode order to the show before matching a season or episode within it.");
                var remote = scope.ScopeId == "season"
                    ? await tvdb.GetSeasonAsync(request.CandidateId, ct)
                    : await tvdb.GetEpisodeAsync(request.CandidateId, ct);
                if (show is null || remote is null)
                    return ApiErrors.BadRequest("The selected TheTVDB result is no longer available.");
                JsonNode? confirmedSeason = null;
                if (scope.ScopeId == "episode")
                {
                    var confirmedSeasonId = seasonScopeValues.GetValueOrDefault(BridgeIdKeys.TvdbSeasonId);
                    if (string.IsNullOrWhiteSpace(confirmedSeasonId))
                        return ApiErrors.Conflict("Match this season to TheTVDB before selecting an episode.");
                    confirmedSeason = show["seasons"]?.AsArray().FirstOrDefault(node =>
                        TvdbText(node, "id") == confirmedSeasonId
                        && IsTvdbSeasonInOrder(node, show, selectedSeasonType));
                    if (confirmedSeason is null)
                        return ApiErrors.Conflict("The confirmed season is not available in this TheTVDB episode order. Reload and match the season again.");
                    if (!IsTvdbEpisodeInSeason(remote, confirmedSeason))
                        return ApiErrors.Conflict("The selected episode belongs to a different season than the confirmed season.");
                }
                var belongsToShow = scope.ScopeId == "season"
                    ? show["seasons"]?.AsArray().Any(node =>
                        TvdbText(node, "id") == request.CandidateId
                        && IsTvdbSeasonInOrder(node, show, selectedSeasonType)) == true
                    : TvdbText(remote, "seriesId") == showId
                        && (await tvdb.GetAllEpisodesAsync(showId, selectedSeasonType, language: "eng", ct: ct))
                            .Any(node => TvdbText(node, "id") == request.CandidateId);
                if (!belongsToShow)
                    return ApiErrors.BadRequest("The selected result is outside this show's selected TheTVDB episode order.");

                var english = scope.ScopeId == "season"
                    ? await tvdb.GetSeasonTranslationAsync(request.CandidateId, ct: ct)
                    : await tvdb.GetEpisodeTranslationAsync(request.CandidateId, ct: ct);

                var idKey = scope.ScopeId == "season" ? BridgeIdKeys.TvdbSeasonId : BridgeIdKeys.TvdbEpisodeId;
                var now = DateTimeOffset.UtcNow;
                var newRevision = Guid.NewGuid().ToString("N");
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [idKey] = request.CandidateId,
                    [MetadataFieldConstants.IdentityProvider] = "tvdb",
                    [MetadataFieldConstants.IdentityProviderItemId] = request.CandidateId,
                    [MetadataFieldConstants.IdentityRevision] = newRevision,
                    [TvdbSeasonTypeKey] = selectedSeasonType,
                };
                if ((TvdbText(english, "name") ?? (scope.ScopeId == "season"
                        ? SeasonLabel(ParseTvdbNumber(TvdbText(remote, "number")) ?? 0) : null)) is { Length: > 0 } title)
                    values[scope.ScopeId == "season" ? MetadataFieldConstants.Title : MetadataFieldConstants.EpisodeTitle] = title;
                if (TvdbText(english, "overview") is { Length: > 0 } description)
                    values[MetadataFieldConstants.Description] = description;
                if (TvdbText(remote, "number") is { Length: > 0 } number)
                    values[scope.ScopeId == "season" ? "tvdb_source_season_number" : "tvdb_source_episode_number"] = number;
                if (scope.ScopeId == "episode" && TvdbText(remote, "seasonNumber") is { Length: > 0 } remoteSeason)
                    values["tvdb_source_season_number"] = remoteSeason;

                await claims.InsertBatchAsync(values.Where(pair => pair.Key != MetadataFieldConstants.IdentityRevision)
                    .Select(pair => new MetadataClaim
                    {
                        Id = Guid.NewGuid(), EntityId = scope.FieldEntityId,
                        ProviderId = WellKnownProviders.Tvdb,
                        DecisionSourceProviderId = WellKnownProviders.UserManual,
                        ClaimKey = pair.Key, ClaimValue = pair.Value,
                        ClaimedAt = now, Confidence = 1,
                    }).ToList(), ct);
                await canonicals.UpsertBatchAsync(values.Select(pair => new CanonicalValue
                {
                    EntityId = scope.FieldEntityId, Key = pair.Key, Value = pair.Value,
                    LastScoredAt = now,
                    WinningProviderId = pair.Key == MetadataFieldConstants.IdentityRevision
                        ? WellKnownProviders.UserManual : WellKnownProviders.Tvdb,
                }).ToList(), ct);
                await bridges.UpsertAsync(new BridgeIdEntry
                {
                    EntityId = scope.FieldEntityId, IdType = idKey,
                    IdValue = request.CandidateId, ProviderId = "tvdb", CreatedAt = now,
                }, ct);
                try
                {
                    await RefreshTvdbArtworkAsync(scope, request.CandidateId, images, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    http.RequestServices.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("TvdbScopedMatch")
                        .LogWarning(ex, "TheTVDB match was saved but artwork refresh failed for {ScopeId} {ScopeEntityId}",
                            scope.ScopeId, scope.FieldEntityId);
                }
                return Results.Ok(new TvdbScopedMatchResultDto(scope.ScopeId, request.CandidateId,
                    newRevision, $"Matched only this {scope.ScopeId} to TheTVDB ({selectedSeasonType} order)."));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Problem($"TheTVDB match failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
            }
        }).WithName("ApplyTvdbScopedMatch")
           .Produces<TvdbScopedMatchResultDto>(StatusCodes.Status200OK)
           .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)
           .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite);

        group.MapGet("/{entityId:guid}/tvdb-match/order-preview", async (Guid entityId, string? seasonType,
            HttpContext http, ICanonicalValueRepository canonicals, ILibraryItemRepository library,
            IMetadataEditorRepository editor, IWorkRepository works, CatalogueResourceAuthorizationService resources,
            TvdbRetailClient tvdb, CancellationToken ct) =>
        {
            var root = await ResolveTvdbRootScopeAsync(entityId, http, canonicals, library, editor, resources, ApplicationPermissionIds.MetadataRead, ct);
            if (root is null) return ApiErrors.NotFound("TV show match scope was not found.");
            var values = BuildLatestCanonicalMap(await canonicals.GetByEntityAsync(root.FieldEntityId, ct));
            var showId = values.GetValueOrDefault(BridgeIdKeys.TvdbId);
            if (string.IsNullOrWhiteSpace(showId) || !tvdb.IsConfigured()) return ApiErrors.BadRequest("Match this show to TheTVDB before selecting an episode order.");
            try
            {
                var show = await tvdb.GetSeriesAsync(showId, ct);
                var requested = ResolveTvdbSeasonType(seasonType, EmptyTvdbScopeValues, values, show);
                if (show is null || requested is null) return ApiErrors.BadRequest("Select a supported TheTVDB episode order.");
                return Results.Ok(await BuildTvdbShowOrderPreviewAsync(root.FieldEntityId, showId, show, values, requested, canonicals, works, tvdb, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Problem($"TheTVDB order preview failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
            }
        }).WithName("PreviewTvdbShowOrder")
          .Produces<TvdbShowOrderPreviewDto>(StatusCodes.Status200OK)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead);

        group.MapPost("/{entityId:guid}/tvdb-match/order", async (Guid entityId, ApplyTvdbShowOrderDto request,
            HttpContext http, ICanonicalValueRepository canonicals, IMetadataClaimRepository claims,
            ILibraryItemRepository library, IMetadataEditorRepository editor, IWorkRepository works,
            CatalogueResourceAuthorizationService resources, TvdbRetailClient tvdb, CancellationToken ct) =>
        {
            var root = await ResolveTvdbRootScopeAsync(entityId, http, canonicals, library, editor, resources, ApplicationPermissionIds.MetadataWrite, ct);
            if (root is null) return ApiErrors.NotFound("TV show match scope was not found.");
            var values = BuildLatestCanonicalMap(await canonicals.GetByEntityAsync(root.FieldEntityId, ct));
            var revision = values.GetValueOrDefault(MetadataFieldConstants.IdentityRevision) ?? string.Empty;
            if (!string.Equals(revision, request.ExpectedRevision, StringComparison.Ordinal)) return ApiErrors.Conflict("This show match changed while the order picker was open. Reload and review it again.");
            var showId = values.GetValueOrDefault(BridgeIdKeys.TvdbId);
            if (string.IsNullOrWhiteSpace(showId) || !tvdb.IsConfigured()) return ApiErrors.BadRequest("Match this show to TheTVDB before selecting an episode order.");
            try
            {
                var show = await tvdb.GetSeriesAsync(showId, ct);
                var requested = ResolveTvdbSeasonType(request.SeasonType, EmptyTvdbScopeValues, values, show);
                if (show is null || requested is null) return ApiErrors.BadRequest("Select a supported TheTVDB episode order.");
                var preview = await BuildTvdbShowOrderPreviewAsync(root.FieldEntityId, showId, show, values, requested, canonicals, works, tvdb, ct);
                if (!preview.CanApply) return ApiErrors.Conflict(preview.BlockingMessage ?? "Existing TVDB matches must be rematched before changing the episode order.");
                if (preview.CurrentSeasonType == requested) return Results.Ok(new TvdbShowOrderResultDto(requested, revision, "This show already uses the selected TheTVDB episode order."));
                var now = DateTimeOffset.UtcNow; var newRevision = Guid.NewGuid().ToString("N");
                await claims.InsertBatchAsync([new MetadataClaim { Id = Guid.NewGuid(), EntityId = root.FieldEntityId, ProviderId = WellKnownProviders.Tvdb, DecisionSourceProviderId = WellKnownProviders.UserManual, ClaimKey = TvdbSeasonTypeKey, ClaimValue = requested, ClaimedAt = now, Confidence = 1 }], ct);
                await canonicals.UpsertBatchAsync([new CanonicalValue { EntityId = root.FieldEntityId, Key = TvdbSeasonTypeKey, Value = requested, LastScoredAt = now, WinningProviderId = WellKnownProviders.Tvdb }, new CanonicalValue { EntityId = root.FieldEntityId, Key = MetadataFieldConstants.IdentityRevision, Value = newRevision, LastScoredAt = now, WinningProviderId = WellKnownProviders.UserManual }], ct);
                return Results.Ok(new TvdbShowOrderResultDto(requested, newRevision, "The show order was updated. Existing season and episode IDs were preserved."));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Problem($"TheTVDB order update failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
            }
        }).WithName("ApplyTvdbShowOrder")
          .Produces<TvdbShowOrderResultDto>(StatusCodes.Status200OK)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite);
    }

    private static async Task<(EditorScopeResolution Scope, EditorScopeResolution Root, EditorScopeResolution? Season)?> ResolveTvdbMatchScopeAsync(
        Guid entityId, string scopeId, HttpContext http, ICanonicalValueRepository canonicals,
        ILibraryItemRepository library, IMetadataEditorRepository editor,
        CatalogueResourceAuthorizationService resources, ApplicationPermissionId permission,
        CancellationToken ct)
    {
        if (scopeId is not ("season" or "episode")) return null;
        var context = await ResolveEditorScopeContextAsync(entityId, canonicals, library, editor, ct);
        if (context is null || NormalizeEditorMediaType(context.MediaType) != "TV") return null;
        var scope = context.Scopes.FirstOrDefault(item => item.ScopeId == scopeId);
        var root = context.Scopes.FirstOrDefault(item => item.ScopeId == "series");
        if (scope is null || root is null || !await HasEditorScopeAccessAsync(
                http, resources, scope, permission, ct)) return null;
        return (scope, root, context.Scopes.FirstOrDefault(item => item.ScopeId == "season"));
    }

    private static async Task<EditorScopeResolution?> ResolveTvdbRootScopeAsync(
        Guid entityId, HttpContext http, ICanonicalValueRepository canonicals,
        ILibraryItemRepository library, IMetadataEditorRepository editor,
        CatalogueResourceAuthorizationService resources, ApplicationPermissionId permission,
        CancellationToken ct)
    {
        var context = await ResolveEditorScopeContextAsync(entityId, canonicals, library, editor, ct);
        if (context is null || NormalizeEditorMediaType(context.MediaType) != "TV") return null;
        var root = context.Scopes.FirstOrDefault(item => item.ScopeId == "series");
        return root is not null && await HasEditorScopeAccessAsync(http, resources, root, permission, ct)
            ? root : null;
    }

    private static string? TvdbText(JsonNode? node, string key) =>
        string.IsNullOrWhiteSpace(node?[key]?.ToString()) ? null : node![key]!.ToString().Trim();

    private static async Task<IReadOnlyDictionary<string, string>> LoadTvdbSeasonScopeValuesAsync(
        EditorScopeResolution? seasonScope,
        ICanonicalValueRepository canonicals,
        CancellationToken ct) => seasonScope is null
        ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        : BuildLatestCanonicalMap(await canonicals.GetByEntityAsync(seasonScope.FieldEntityId, ct));

    internal static string? ResolveTvdbSeasonType(
        string? requested,
        IReadOnlyDictionary<string, string> seasonScopeValues,
        IReadOnlyDictionary<string, string> rootScopeValues,
        JsonNode? show)
    {
        var type = string.IsNullOrWhiteSpace(requested)
            ? rootScopeValues.GetValueOrDefault(TvdbSeasonTypeKey)
              ?? seasonScopeValues.GetValueOrDefault(TvdbSeasonTypeKey)
              ?? "default"
            : requested.Trim().ToLowerInvariant();
        if (!SupportedTvdbSeasonTypes.Contains(type, StringComparer.Ordinal))
            return null;
        return type == "default" || GetAvailableTvdbSeasonTypes(show).Contains(type, StringComparer.Ordinal)
            ? type : null;
    }

    private static async Task<TvdbShowOrderPreviewDto> BuildTvdbShowOrderPreviewAsync(
        Guid rootWorkId, string seriesId, JsonNode show,
        IReadOnlyDictionary<string, string> rootValues, string requestedSeasonType,
        ICanonicalValueRepository canonicals, IWorkRepository works, TvdbRetailClient tvdb,
        CancellationToken ct)
    {
        var currentSeasonType = rootValues.GetValueOrDefault(TvdbSeasonTypeKey) ?? "default";
        var targetSeasons = (show["seasons"]?.AsArray() ?? [])
            .Where(node => node is not null && IsTvdbSeasonInOrder(node, show, requestedSeasonType))
            .ToList();
        var targetSeasonsById = targetSeasons
            .Select(node => (Id: TvdbText(node, "id"), Node: node))
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .ToDictionary(item => item.Id!, item => item.Node, StringComparer.Ordinal);
        var impacts = new List<TvdbShowOrderImpactDto>();
        IReadOnlyList<JsonNode>? targetEpisodes = null;

        foreach (var season in await works.GetDirectChildrenAsync(rootWorkId, ct))
        {
            var seasonValues = BuildLatestCanonicalMap(await canonicals.GetByEntityAsync(season.WorkId, ct));
            var seasonId = seasonValues.GetValueOrDefault(BridgeIdKeys.TvdbSeasonId);
            JsonNode? targetSeason = null;
            if (!string.IsNullOrWhiteSpace(seasonId))
            {
                var isMapped = targetSeasonsById.TryGetValue(seasonId, out targetSeason);
                impacts.Add(new TvdbShowOrderImpactDto(
                    season.WorkId, "season", seasonId,
                    ParseTvdbNumber(TvdbText(targetSeason, "number")), null, isMapped,
                    isMapped ? null : "This matched season is unavailable in the requested TheTVDB order."));
            }

            foreach (var episode in await works.GetDirectChildrenAsync(season.WorkId, ct))
            {
                var episodeValues = BuildLatestCanonicalMap(await canonicals.GetByEntityAsync(episode.WorkId, ct));
                var episodeId = episodeValues.GetValueOrDefault(BridgeIdKeys.TvdbEpisodeId);
                if (string.IsNullOrWhiteSpace(episodeId)) continue;

                targetEpisodes ??= await tvdb.GetAllEpisodesAsync(seriesId, requestedSeasonType, language: "eng", ct: ct);
                var targetEpisode = targetEpisodes.FirstOrDefault(node => TvdbText(node, "id") == episodeId);
                var isMapped = targetEpisode is not null
                    && (targetSeason is null || IsTvdbEpisodeInSeason(targetEpisode, targetSeason));
                impacts.Add(new TvdbShowOrderImpactDto(
                    episode.WorkId, "episode", episodeId,
                    ParseTvdbNumber(TvdbText(targetEpisode, "seasonNumber")),
                    ParseTvdbNumber(TvdbText(targetEpisode, "number")), isMapped,
                    isMapped ? null : "This matched episode is unavailable in the requested TheTVDB order."));
            }
        }

        var blocked = impacts.Where(impact => !impact.IsMapped).ToList();
        var message = blocked.Count == 0 ? null
            : $"{blocked.Count} matched season or episode {(blocked.Count == 1 ? "is" : "are")} unavailable in the requested TheTVDB order. Rematch {(blocked.Count == 1 ? "it" : "them")} before changing the show order.";
        return new TvdbShowOrderPreviewDto(
            seriesId, currentSeasonType, requestedSeasonType,
            rootValues.GetValueOrDefault(MetadataFieldConstants.IdentityRevision) ?? string.Empty,
            GetAvailableTvdbSeasonTypes(show), impacts, blocked.Count == 0, message);
    }

    internal static IReadOnlyList<string> GetAvailableTvdbSeasonTypes(JsonNode? show)
    {
        var available = new HashSet<string>(StringComparer.Ordinal) { "default" };
        foreach (var season in show?["seasons"]?.AsArray() ?? [])
        {
            var type = GetTvdbSeasonType(season);
            if (type is not null)
            {
                available.Add(type);
            }
        }
        return SupportedTvdbSeasonTypes.Where(available.Contains).ToList();
    }

    internal static bool IsTvdbSeasonInOrder(JsonNode? season, JsonNode? show, string seasonType)
    {
        if (string.Equals(seasonType, "default", StringComparison.Ordinal))
            return IsDefaultTvdbSeason(season, show);
        return string.Equals(GetTvdbSeasonType(season), seasonType, StringComparison.Ordinal);
    }

    internal static bool IsTvdbEpisodeInSeason(JsonNode? episode, JsonNode? season)
    {
        var episodeSeasonId = TvdbText(episode, "seasonId");
        var confirmedSeasonId = TvdbText(season, "id");
        if (!string.IsNullOrWhiteSpace(episodeSeasonId) && !string.IsNullOrWhiteSpace(confirmedSeasonId))
            return string.Equals(episodeSeasonId, confirmedSeasonId, StringComparison.Ordinal);

        return ParseTvdbNumber(TvdbText(episode, "seasonNumber")) is { } episodeSeason
            && ParseTvdbNumber(TvdbText(season, "number")) is { } confirmedSeason
            && episodeSeason == confirmedSeason;
    }

    private static string? GetTvdbSeasonType(JsonNode? season)
    {
        var type = season?["type"];
        var value = type is JsonObject
            ? TvdbText(type, "name") ?? TvdbText(type, "type")
            : type?.ToString();
        if (!string.IsNullOrWhiteSpace(value))
        {
            var normalized = value.Trim().ToLowerInvariant();
            if (SupportedTvdbSeasonTypes.Contains(normalized, StringComparer.Ordinal))
                return normalized;
        }

        var id = type is JsonObject ? TvdbText(type, "id") : null;
        return id switch
        {
            "1" => "official",
            "2" => "dvd",
            "3" => "absolute",
            _ => null,
        };
    }
    private static string? CreateTvdbPreviewUrl(IMemoryCache cache, Guid entityId,
        Guid ownerId, string? sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var source)
            || source.Scheme != Uri.UriSchemeHttps
            || !(source.Host.Equals("thetvdb.com", StringComparison.OrdinalIgnoreCase)
                 || source.Host.EndsWith(".thetvdb.com", StringComparison.OrdinalIgnoreCase))) return null;
        var token = Guid.NewGuid().ToString("N");
        cache.Set($"tvdb-preview:{token}", new TvdbPreviewEntry(entityId, ownerId, source.ToString()),
            TimeSpan.FromMinutes(15));
        return $"/metadata/{entityId}/tvdb-match/previews/{token}";
    }
    private static int? ParseTvdbNumber(string? value) => int.TryParse(value, out var number) ? number : null;
    private static string SeasonLabel(int number) => number == 0 ? "Specials" : $"Season {number}";
    private static int? ParseOwnedSeasonNumber(EditorScopeResolution scope)
    {
        var text = scope.ScopeId == "season" ? scope.DisplayTitle : scope.DisplaySubtitle;
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(text,
            @"\bSeason\s+(\d+)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? ParseTvdbNumber(match.Groups[1].Value) : null;
    }
    internal static bool IsDefaultTvdbSeason(JsonNode? season, JsonNode? show)
    {
        var type = season?["type"];
        var typeId = type is JsonObject ? TvdbText(type, "id") : type?.ToString();
        var defaultType = show?["defaultSeasonType"];
        var defaultId = defaultType is JsonObject ? TvdbText(defaultType, "id") : defaultType?.ToString();
        return string.IsNullOrWhiteSpace(defaultId) || typeId == defaultId;
    }
}
