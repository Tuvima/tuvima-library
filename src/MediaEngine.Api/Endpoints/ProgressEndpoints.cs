using System.Security.Claims;
using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Details;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Application.ReadModels;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Paging;
using MediaEngine.Contracts.Progress;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;

namespace MediaEngine.Api.Endpoints;

public static class ProgressEndpoints
{
    public static IEndpointRouteBuilder MapProgressEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/progress")
                       .WithTags("Progress")
                       .AddEndpointFilter(new PlayerResourceFailureFilter())
                       .RequireAuthorization(AuthPolicies.Authenticated);

        group.MapGet("/{assetId:guid}", async (
            Guid assetId,
            ClaimsPrincipal user,
            IUserStateStore stateStore,
            CancellationToken ct) =>
        {
            var uid = ResolveUserId(user);
            var state = await stateStore.GetAsync(uid, assetId, ct);
            return state is null
                ? ApiErrors.NotFound("No progress recorded for this asset.")
                : Results.Ok(MapStateResponse(state));
        })
        .Produces<UserStateResponse>(StatusCodes.Status200OK)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.ProgressRead)
        .RequireClientScope(ClientApiScopes.ProgressRead);

        group.MapPut("/{assetId:guid}", (
            Guid assetId,
            ProgressUpdateRequest body,
            ClaimsPrincipal user,
            IUserStateStore stateStore,
            IMediaAssetRepository assetRepo,
            CancellationToken ct) =>
            PutProgressAsync(assetId, body, ResolveUserId(user), DateTimeOffset.UtcNow, stateStore, assetRepo, ct))
        .Produces<UserStateResponse>(StatusCodes.Status200OK)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.ProgressWrite)
        .RequireClientScope(ClientApiScopes.ProgressWrite);

        group.MapGet("/recent", async (
            ClaimsPrincipal user,
            int? limit,
            UserStateRepository stateStore,
            PlayerCatalogueScope scope,
            CancellationToken ct) =>
        {
            var uid = await scope.RequireProfileAsync(ResolveUserId(user), ct);
            var page = PagedRequest.From(null, limit, defaultLimit: 10);
            var items = await stateStore.GetRecentAuthorizedAsync(uid, await scope.AssetIdsAsync(ct), page.Limit, ct);
            return Results.Ok(items.Select(MapStateResponse));
        })
        .Produces<IEnumerable<UserStateResponse>>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.ProgressRead);

        group.MapGet("/journey", async (
            ClaimsPrincipal user,
            string? collectionId,
            int? limit,
            JourneyReadService journeyReadService,
            PlayerCatalogueScope scope,
            CancellationToken ct) =>
        {
            var uid = await scope.RequireProfileAsync(ResolveUserId(user), ct);
            var parsedCollectionId = Guid.TryParse(collectionId, out var value) ? value : (Guid?)null;
            var page = PagedRequest.From(null, limit, defaultLimit: 5);
            IReadOnlyList<JourneyItemResponse> results =
                await journeyReadService.GetAuthorizedJourneyAsync(uid, parsedCollectionId, await scope.AssetIdsAsync(ct), page.Limit, ct);
            return Results.Ok(results.Select(MapJourneyItem).ToList());
        })
        .Produces<IReadOnlyList<JourneyItemDto>>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.ProgressRead);

        group.MapGet("/status/{entityType}/{targetId:guid}", async (
            MediaEngine.Contracts.Details.DetailEntityType entityType, Guid targetId, ClaimsPrincipal user,
            IPersonalStatusRepository repository, PlayerCatalogueScope scope, CancellationToken ct) =>
        {
            await scope.RequireProfileAsync(ResolveUserId(user), ct);
            var status = await repository.ReadAsync(ResolveUserId(user), new PersonalStatusTarget(targetId,
                PersonalStatusPolicy.MediaTypeFor(entityType))
            { AuthorizedAssetIds = await scope.AssetIdsAsync(ct) }, ct);
            return Results.Ok(new PersonalStatusInfo(status.Revision, status.OwnedCount, status.StartedCount, status.CompletedCount, status.Hidden));
        }).Produces<PersonalStatusInfo>(StatusCodes.Status200OK)
          .RequireClientScope(ClientApiScopes.ProgressRead);

        group.MapPost("/status", async (PersonalStatusRequest body, ClaimsPrincipal user,
            IPersonalStatusRepository repository, PlayerCatalogueScope scope, CancellationToken ct) =>
        {
            try
            {
                await scope.RequireProfileAsync(ResolveUserId(user), ct);
                var result = await repository.ExecuteAsync(ResolveUserId(user),
                    new PersonalStatusTarget(body.TargetId, PersonalStatusPolicy.MediaTypeFor(body.EntityType)) { AuthorizedAssetIds = await scope.AssetIdsAsync(ct) },
                    (PersonalStatusCommand)body.Action, body.CommandId, body.ExpectedRevision, ct);
                return Results.Ok(new PersonalStatusCommandResult(result.CommandId, result.AffectedCount, result.Revision));
            }
            catch (StateRevisionConflictException ex) { return ApiErrors.Conflict(ex.Message); }
            catch (InvalidOperationException ex) { return ApiErrors.BadRequest(ex.Message); }
        }).Produces<PersonalStatusCommandResult>(StatusCodes.Status200OK)
          .RequireClientScope(ClientApiScopes.ProgressWrite);
        group.MapPost("/status/{commandId:guid}/undo", async (Guid commandId, ClaimsPrincipal user,
            IPersonalStatusRepository repository, PlayerCatalogueScope scope, CancellationToken ct) =>
        {
            try
            {
                await scope.RequireProfileAsync(ResolveUserId(user), ct);
                var result = await repository.UndoAsync(ResolveUserId(user), commandId, ct, await scope.AssetIdsAsync(ct));
                return Results.Ok(new PersonalStatusCommandResult(result.CommandId, result.AffectedCount, result.Revision));
            }
            catch (StateRevisionConflictException ex) { return ApiErrors.Conflict(ex.Message); }
            catch (InvalidOperationException ex) { return ApiErrors.BadRequest(ex.Message); }
        }).Produces<PersonalStatusCommandResult>(StatusCodes.Status200OK)
          .RequireClientScope(ClientApiScopes.ProgressWrite);
        group.MapGet("/status/{entityType}/{targetId:guid}/history", async (
            MediaEngine.Contracts.Details.DetailEntityType entityType, Guid targetId, ClaimsPrincipal user,
            IPersonalStatusRepository repository, PlayerCatalogueScope scope, CancellationToken ct) =>
        {
            await scope.RequireProfileAsync(ResolveUserId(user), ct);
            var items = await repository.HistoryAsync(ResolveUserId(user), new PersonalStatusTarget(targetId,
                PersonalStatusPolicy.MediaTypeFor(entityType))
            { AuthorizedAssetIds = await scope.AssetIdsAsync(ct) }, ct);
            return Results.Ok(items.Select(r => new PersonalStatusHistoryItem(r.Id, r.Command, r.ChangedAt, r.Undone)));
        }).Produces<IReadOnlyList<PersonalStatusHistoryItem>>(StatusCodes.Status200OK)
          .RequireClientScope(ClientApiScopes.ProgressRead);

        return app;
    }

    /// <summary>The progress write behind <c>PUT /api/v1/progress/{assetId}</c>, including the offline later-wins rule.</summary>
    internal static async Task<IResult> PutProgressAsync(
        Guid assetId,
        ProgressUpdateRequest body,
        Guid userId,
        DateTimeOffset now,
        IUserStateStore stateStore,
        IMediaAssetRepository assetRepo,
        CancellationToken ct)
    {
        var asset = await assetRepo.FindByIdAsync(assetId, ct);
        if (asset is null)
        {
            return ApiErrors.NotFound($"Asset '{assetId}' not found.");
        }

        // Only a paired app replaying offline progress gets the later-wins rule; the time is clamped to the server clock.
        var recordedAt = OfflineProgressSyncPolicy.ResolveClientTimestamp(body.Offline, body.ClientTimestamp, now);
        var state = new UserState
        {
            Revision = body.ExpectedRevision,
            UserId = userId,
            AssetId = assetId,
            ContentHash = asset.ContentHash,
            ProgressPct = Math.Clamp(body.ProgressPct, 0.0, 100.0),
            LastAccessed = recordedAt ?? now,
            ExtendedProperties = body.ExtendedProperties ?? [],
        };

        try { await stateStore.SaveAsync(state, ct); }
        catch (StateRevisionConflictException ex)
        {
            if (recordedAt is not { } offlineRecordedAt)
            {
                return ApiErrors.Conflict(ex.Message);
            }

            return await ResolveOfflineConflictAsync(stateStore, state, offlineRecordedAt, ex, ct);
        }
        return Results.Ok(MapStateResponse(state));
    }

    /// <summary>
    /// The client's revision is stale and it is replaying offline progress: keep whichever record is later for this asset.
    /// A later client record replaces the server's (written against the server's current revision); an earlier one
    /// leaves the server state untouched. Either way the response carries the state that now stands.
    /// </summary>
    private static async Task<IResult> ResolveOfflineConflictAsync(
        IUserStateStore stateStore,
        UserState attempted,
        DateTimeOffset recordedAt,
        StateRevisionConflictException conflict,
        CancellationToken ct)
    {
        // A concurrent writer can move the revision again; a few reads are enough before reporting the conflict.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var current = await stateStore.GetAsync(attempted.UserId, attempted.AssetId, ct);
            if (current is not null && !OfflineProgressSyncPolicy.ClientWins(current.LastAccessed, recordedAt))
            {
                return Results.Ok(MapStateResponse(current));
            }

            attempted.Revision = current?.Revision ?? 0;
            try
            {
                await stateStore.SaveAsync(attempted, ct);
                return Results.Ok(MapStateResponse(attempted));
            }
            catch (StateRevisionConflictException)
            {
                // Lost a race; read the winner and decide again.
            }
        }

        return ApiErrors.Conflict(conflict.Message);
    }

    private static Guid ResolveUserId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(TuvimaClaimTypes.ActiveProfileId), out var parsed)
            ? parsed
            : throw new UnauthorizedAccessException("The authenticated profile identity is missing.");

    private static UserStateResponse MapStateResponse(UserState s) => new(
        UserId: s.UserId,
        AssetId: s.AssetId,
        ContentHash: s.ContentHash,
        ProgressPct: s.ProgressPct,
        LastAccessed: s.LastAccessed.UtcDateTime,
        ExtendedProperties: s.ExtendedProperties)
    { Revision = s.Revision };

    private static JourneyItemDto MapJourneyItem(JourneyItemResponse source) => new(
        source.AssetId,
        source.WorkId,
        source.CollectionId,
        source.Title,
        source.Author,
        source.CoverUrl,
        source.BackgroundUrl,
        source.BannerUrl,
        source.LogoUrl,
        source.CoverWidthPx,
        source.CoverHeightPx,
        source.BackgroundWidthPx,
        source.BackgroundHeightPx,
        source.BannerWidthPx,
        source.BannerHeightPx,
        source.Narrator,
        source.Series,
        source.SeriesPosition,
        source.Description,
        source.MediaType,
        source.ProgressPct,
        source.LastAccessed,
        source.CollectionDisplayName,
        source.ExtendedProperties,
        source.HeroUrl);
}
