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
                return Results.NotFound();
            if (await resources.EvaluateEntityAsync(http, "Work", entry.OwnerWorkId,
                    ApplicationPermissionIds.MetadataRead, ct) != CatalogueResourceAccess.Allowed)
                return Results.NotFound();
            if (!Uri.TryCreate(entry.SourceUrl, UriKind.Absolute, out var source)
                || source.Scheme != Uri.UriSchemeHttps
                || !(source.Host.Equals("thetvdb.com", StringComparison.OrdinalIgnoreCase)
                     || source.Host.EndsWith(".thetvdb.com", StringComparison.OrdinalIgnoreCase)))
                return Results.NotFound();
            using var client = httpFactory.CreateClient("cover_download");
            using var response = await client.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return Results.NotFound();
            var bytes = await BoundedHttpContent.ReadImageAsync(response.Content, ct);
            using var bitmap = SKBitmap.Decode(bytes);
            if (bitmap is null || bitmap.Width < 1 || bitmap.Height < 1) return Results.NotFound();
            var width = Math.Min(bitmap.Width, 320);
            var height = Math.Max(1, (int)Math.Round(bitmap.Height * (width / (double)bitmap.Width)));
            using var resized = bitmap.Resize(new SKImageInfo(width, height), SKSamplingOptions.Default);
            if (resized is null) return Results.NotFound();
            using var image = SKImage.FromBitmap(resized);
            using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 82);
            http.Response.Headers.CacheControl = "private, max-age=900";
            return Results.File(encoded.ToArray(), "image/jpeg");
        }).WithName("GetTvdbMatchPreview")
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead);

        group.MapGet("/{entityId:guid}/tvdb-match/{scopeId}/candidates", async (
            Guid entityId, string scopeId, int? seasonNumber, HttpContext http,
            ICanonicalValueRepository canonicals, ILibraryItemRepository library,
            IMetadataEditorRepository editor, CatalogueResourceAuthorizationService resources,
            TvdbRetailClient tvdb, IMemoryCache cache, CancellationToken ct) =>
        {
            var resolved = await ResolveTvdbMatchScopeAsync(entityId, scopeId, http, canonicals,
                library, editor, resources, ApplicationPermissionIds.MetadataRead, ct);
            if (resolved is null) return ApiErrors.NotFound("TV match scope was not found.");
            if (!tvdb.IsConfigured()) return ApiErrors.BadRequest("Connect TheTVDB in Settings before searching TV matches.");

            var (scope, root) = resolved.Value;
            var rootValues = BuildLatestCanonicalMap(await canonicals.GetByEntityAsync(root.FieldEntityId, ct));
            var showId = GetCanonicalValue(rootValues, BridgeIdKeys.TvdbId);
            if (string.IsNullOrWhiteSpace(showId))
                return ApiErrors.BadRequest("Match this show to TheTVDB before matching its seasons or episodes.");

            var scopeValues = BuildLatestCanonicalMap(await canonicals.GetByEntityAsync(scope.FieldEntityId, ct));
            var localSeason = ParseTvdbNumber(GetCanonicalValue(scopeValues, MetadataFieldConstants.SeasonNumber));
            var localEpisode = ParseTvdbNumber(GetCanonicalValue(scopeValues, MetadataFieldConstants.EpisodeNumber));
            try
            {
                var show = await tvdb.GetSeriesAsync(showId, ct);
                if (show is null) return ApiErrors.BadRequest("The matched TheTVDB show is no longer available.");
                var showName = TvdbText(show, "name") ?? root.DisplayTitle;
                var seasons = show["seasons"]?.AsArray()
                    .Where(node => node is not null && TvdbText(node, "id") is not null)
                    .Where(node => IsDefaultTvdbSeason(node, show))
                    .ToList() ?? [];
                var availableSeasons = seasons.Select(node => ParseTvdbNumber(TvdbText(node, "number")))
                    .Where(number => number.HasValue).Select(number => number!.Value).Distinct().Order().ToList();
                IReadOnlyList<TvdbMatchCandidateDto> candidates;
                if (scope.ScopeId == "season")
                {
                    candidates = seasons.Select(node => new TvdbMatchCandidateDto(
                        TvdbText(node, "id")!, showId,
                        TvdbText(node, "name") ?? $"Season {TvdbText(node, "number")}",
                        ParseTvdbNumber(TvdbText(node, "number")) ?? 0, null,
                        TvdbText(node, "firstAired"), TvdbText(node, "overview"),
                        TvdbText(node, "image"), "default",
                        $"https://thetvdb.com/seasons/{TvdbText(node, "id")}"))
                        .ToList();
                }
                else
                {
                    var selectedSeason = seasonNumber ?? localSeason;
                    var episodes = await tvdb.GetAllEpisodesAsync(showId, ct: ct);
                    candidates = episodes
                        .Where(node => selectedSeason is null || ParseTvdbNumber(TvdbText(node, "seasonNumber")) == selectedSeason)
                        .Where(node => TvdbText(node, "id") is not null)
                        .OrderBy(node => ParseTvdbNumber(TvdbText(node, "seasonNumber")))
                        .ThenBy(node => ParseTvdbNumber(TvdbText(node, "number")))
                        .Select(node => new TvdbMatchCandidateDto(
                            TvdbText(node, "id")!, showId,
                            TvdbText(node, "name") ?? $"Episode {TvdbText(node, "number")}",
                            ParseTvdbNumber(TvdbText(node, "seasonNumber")) ?? 0,
                            ParseTvdbNumber(TvdbText(node, "number")),
                            TvdbText(node, "aired"), TvdbText(node, "overview"),
                            TvdbText(node, "image"), "default",
                            $"https://thetvdb.com/episodes/{TvdbText(node, "id")}"))
                        .ToList();
                }
                candidates = candidates.Select(candidate => candidate with
                {
                    ImageUrl = CreateTvdbPreviewUrl(cache, entityId, scope.FieldEntityId,
                        candidate.ImageUrl),
                }).ToList();
                return Results.Ok(new TvdbScopedMatchCandidatesDto(scope.ScopeId, showName, showId,
                    GetCanonicalValue(scopeValues, MetadataFieldConstants.IdentityRevision) ?? string.Empty,
                    localSeason, localEpisode, availableSeasons, candidates));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Problem($"TheTVDB search failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
            }
        }).WithName("GetTvdbScopedMatchCandidates")
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
            var (scope, root) = resolved.Value;
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
                var remote = scope.ScopeId == "season"
                    ? await tvdb.GetSeasonAsync(request.CandidateId, ct)
                    : await tvdb.GetEpisodeAsync(request.CandidateId, ct);
                if (show is null || remote is null)
                    return ApiErrors.BadRequest("The selected TheTVDB result is no longer available.");
                var belongsToShow = scope.ScopeId == "season"
                    ? show["seasons"]?.AsArray().Any(node =>
                        TvdbText(node, "id") == request.CandidateId && IsDefaultTvdbSeason(node, show)) == true
                    : TvdbText(remote, "seriesId") == showId;
                if (!belongsToShow)
                    return ApiErrors.BadRequest("The selected result is outside this show's default season order.");

                var idKey = scope.ScopeId == "season" ? BridgeIdKeys.TvdbSeasonId : BridgeIdKeys.TvdbEpisodeId;
                var now = DateTimeOffset.UtcNow;
                var newRevision = Guid.NewGuid().ToString("N");
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [idKey] = request.CandidateId,
                    [MetadataFieldConstants.IdentityProvider] = "tvdb",
                    [MetadataFieldConstants.IdentityProviderItemId] = request.CandidateId,
                    [MetadataFieldConstants.IdentityRevision] = newRevision,
                };
                if (TvdbText(remote, "name") is { Length: > 0 } title)
                    values[scope.ScopeId == "season" ? MetadataFieldConstants.Title : MetadataFieldConstants.EpisodeTitle] = title;
                if (TvdbText(remote, "overview") is { Length: > 0 } description)
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
                    newRevision, $"Matched only this {scope.ScopeId} to TheTVDB."));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Problem($"TheTVDB match failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
            }
        }).WithName("ApplyTvdbScopedMatch")
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataWrite);
    }

    private static async Task<(EditorScopeResolution Scope, EditorScopeResolution Root)?> ResolveTvdbMatchScopeAsync(
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
        return (scope, root);
    }

    private static string? TvdbText(JsonNode? node, string key) => node?[key]?.ToString();
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
    private static bool IsDefaultTvdbSeason(JsonNode? season, JsonNode? show)
    {
        var typeId = TvdbText(season?["type"], "id");
        var defaultId = TvdbText(show, "defaultSeasonType");
        return string.IsNullOrWhiteSpace(typeId) || string.IsNullOrWhiteSpace(defaultId) || typeId == defaultId;
    }
}
