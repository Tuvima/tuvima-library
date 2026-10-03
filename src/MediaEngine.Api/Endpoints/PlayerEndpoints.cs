using System.Security.Claims;
using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Paging;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Playback;

namespace MediaEngine.Api.Endpoints;

public static class PlayerEndpoints
{
    public static IEndpointRouteBuilder MapPlayerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/player")
            .WithTags("Player")
            .AddEndpointFilter(new PlayerResourceFailureFilter());

        group.MapGet("/capabilities", (PlayerService player) =>
            Results.Ok(player.GetCapabilities()))
        .WithName("GetPlayerCapabilities")
        .WithSummary("Return capabilities for Engine-backed player clients.")
        .Produces<PlayerCapabilitiesDto>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.PlaybackRead);

        group.MapGet("/state", async (
            Guid? profileId,
            string? deviceId,
            string? client,
            ClaimsPrincipal user,
            PlayerService player,
            CancellationToken ct) =>
        {
            var identity = Bind(user, profileId, deviceId, client);
            var state = await player.GetStateAsync(identity.ProfileId, identity.DeviceId, identity.Client, ct);
            return Results.Ok(state);
        })
        .WithName("GetPlayerState")
        .WithSummary("Return the current player session state and queue.")
        .Produces<PlayerStateDto>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.QueueRead);

        group.MapPost("/queue/replace", async (
            PlayerQueueMutationDto request,
            ClaimsPrincipal user,
            PlayerService player,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await player.ReplaceQueueAsync(Bind(user, request), ct));
            }
            catch (PlayerStateConflictException ex)
            {
                return ApiErrors.Conflict(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return ApiErrors.BadRequest(ex.Message);
            }
        })
        .WithName("ReplacePlayerQueue")
        .WithSummary("Replace the queue and start playback from the requested item.")
        .Produces<PlayerStateDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequireClientScope(ClientApiScopes.QueueWrite);

        group.MapPost("/queue/items", async (
            PlayerQueueMutationDto request,
            ClaimsPrincipal user,
            PlayerService player,
            CancellationToken ct) =>
        {
            try
            {
                var insertNext = string.Equals(request.Mode, PlayerQueueMutationModes.AddNext, StringComparison.OrdinalIgnoreCase);
                return Results.Ok(await player.AddQueueItemsAsync(Bind(user, request), insertNext, ct));
            }
            catch (PlayerStateConflictException ex)
            {
                return ApiErrors.Conflict(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return ApiErrors.BadRequest(ex.Message);
            }
        })
        .WithName("AddPlayerQueueItems")
        .WithSummary("Add works to the current queue at the end or next slot.")
        .Produces<PlayerStateDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequireClientScope(ClientApiScopes.QueueWrite);

        group.MapMethods("/queue/order", ["PUT", "POST"], async (
            PlayerQueueMutationDto request,
            ClaimsPrincipal user,
            PlayerService player,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await player.ReorderQueueAsync(Bind(user, request), ct));
            }
            catch (PlayerStateConflictException ex)
            {
                return ApiErrors.Conflict(ex.Message);
            }
        })
        .WithName("ReorderPlayerQueue")
        .WithSummary("Persist a new queue order.")
        .Produces<PlayerStateDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequireClientScope(ClientApiScopes.QueueWrite);

        group.MapDelete("/queue/items/{queueItemId:guid}", async (
            Guid queueItemId,
            Guid? profileId,
            string? deviceId,
            string? client,
            long? expectedStateVersion,
            bool? force,
            ClaimsPrincipal user,
            PlayerService player,
            CancellationToken ct) =>
        {
            try
            {
                var request = new PlayerQueueMutationDto
                {
                    ProfileId = profileId,
                    DeviceId = deviceId,
                    Client = client,
                    ExpectedStateVersion = expectedStateVersion,
                    Force = force.GetValueOrDefault(),
                };
                return Results.Ok(await player.RemoveQueueItemAsync(queueItemId, Bind(user, request), ct));
            }
            catch (PlayerStateConflictException ex)
            {
                return ApiErrors.Conflict(ex.Message);
            }
        })
        .WithName("RemovePlayerQueueItem")
        .WithSummary("Remove one queue item.")
        .Produces<PlayerStateDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequireClientScope(ClientApiScopes.QueueWrite);

        group.MapDelete("/queue", async (
            Guid? profileId,
            string? deviceId,
            string? client,
            long? expectedStateVersion,
            bool? force,
            ClaimsPrincipal user,
            PlayerService player,
            CancellationToken ct) =>
        {
            try
            {
                var request = new PlayerQueueMutationDto
                {
                    ProfileId = profileId,
                    DeviceId = deviceId,
                    Client = client,
                    ExpectedStateVersion = expectedStateVersion,
                    Force = force.GetValueOrDefault(),
                };
                return Results.Ok(await player.ClearQueueAsync(Bind(user, request), ct));
            }
            catch (PlayerStateConflictException ex)
            {
                return ApiErrors.Conflict(ex.Message);
            }
        })
        .WithName("ClearPlayerQueue")
        .WithSummary("Clear the queue and stop playback.")
        .Produces<PlayerStateDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequireClientScope(ClientApiScopes.QueueWrite);

        group.MapPost("/command", async (
            PlayerCommandRequestDto request,
            ClaimsPrincipal user,
            PlayerService player,
            CancellationToken ct) =>
        {
            try
            {
                var state = await player.ApplyCommandAsync(Bind(user, request), ct);
                return Results.Ok(state);
            }
            catch (ArgumentException ex)
            {
                return ApiErrors.BadRequest(ex.Message);
            }
        })
        .WithName("SendPlayerCommand")
        .WithSummary("Send a playback command such as play, pause, next, seek, volume, speed, shuffle, or repeat.")
        .Produces<PlayerStateDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireClientScope(ClientApiScopes.PlaybackWrite);

        group.MapPost("/heartbeat", async (
            PlayerHeartbeatDto request,
            ClaimsPrincipal user,
            PlayerService player,
            CancellationToken ct) =>
        {
            if (request.PlaybackRate is double rate && !PlaybackRatePolicy.IsValid(rate))
            {
                return ApiErrors.BadRequest($"Playback speed must be finite and between {PlaybackRatePolicy.Minimum} and {PlaybackRatePolicy.Maximum}.");
            }

            if (!HasValidTelemetryNumbers(request))
            {
                return ApiErrors.BadRequest("Playback heartbeat numeric values must be finite and non-negative.");
            }

            try
            {
                var state = await player.HeartbeatAsync(Bind(user, request), ct);
                return Results.Ok(state);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return ApiErrors.BadRequest(ex.Message);
            }
        })
        .WithName("PostPlayerHeartbeat")
        .WithSummary("Update active player timing and persist exact resume progress.")
        .Produces<PlayerStateDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireClientScope(ClientApiScopes.ProgressWrite);

        group.MapPost("/session/takeover", async (
            PlayerSessionTakeoverRequestDto request,
            ClaimsPrincipal user,
            PlayerService player,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await player.TakeoverAsync(Bind(user, request), ct));
            }
            catch (PlayerSessionConflictException ex)
            {
                return ApiErrors.Conflict(ex.Message);
            }
        })
        .WithName("TakeOverPlayerSession")
        .WithSummary("Take control of a stale or explicitly forced player session from another client.")
        .Produces<PlayerStateDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequireClientScope(ClientApiScopes.PlaybackWrite);

        group.MapGet("/audiobooks/{workId:guid}/history", async (
            Guid workId,
            Guid? profileId,
            int? limit,
            ClaimsPrincipal user,
            PlayerService player,
            CancellationToken ct) =>
        {
            // A caller-supplied limit is clamped to PagedRequest.MaxLimit; when absent, null is
            // preserved so the service falls back to the profile's configured history limit.
            var clampedLimit = limit.HasValue ? PagedRequest.From(null, limit).Limit : (int?)null;
            var identity = Bind(user, profileId, null, null);
            var history = await player.GetAudiobookHistoryAsync(identity.ProfileId, workId, clampedLimit, ct);
            return Results.Ok(history);
        })
        .WithName("GetAudiobookListenHistory")
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.ProgressRead, "work", "workId")
        .WithSummary("Return recent qualified audiobook listen checkpoints for resume recovery.")
        .Produces<IReadOnlyList<AudiobookListenHistoryItemDto>>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.ProgressRead);

        group.MapGet("/audiobooks/{workId:guid}/bookmarks", async (
            Guid workId,
            Guid? profileId,
            ClaimsPrincipal user,
            PlayerService player,
            CancellationToken ct) =>
        {
            var identity = Bind(user, profileId, null, null);
            var bookmarks = await player.GetAudiobookBookmarksAsync(identity.ProfileId, workId, ct);
            return Results.Ok(bookmarks);
        })
        .WithName("GetAudiobookBookmarks")
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.ProgressRead, "work", "workId")
        .WithSummary("Return saved audiobook playback bookmarks for a work.")
        .Produces<IReadOnlyList<AudiobookBookmarkDto>>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.ProgressRead);

        group.MapPost("/audiobooks/{workId:guid}/bookmarks", async (
            Guid workId,
            Guid? profileId,
            CreateAudiobookBookmarkRequestDto request,
            ClaimsPrincipal user,
            PlayerService player,
            CancellationToken ct) =>
        {
            if (!PlayerService.TryNormalizeAudiobookBookmarkRequest(request, out var normalizedRequest, out var validationError))
            {
                return ApiErrors.BadRequest(validationError!);
            }

            var identity = Bind(user, profileId, null, null);
            var bookmark = await player.CreateAudiobookBookmarkAsync(identity.ProfileId, workId, normalizedRequest!, ct);
            return Results.Created($"/player/audiobooks/{workId:D}/bookmarks/{bookmark.Id:D}", bookmark);
        })
        .WithName("CreateAudiobookBookmark")
        .WithSummary("Save the current audiobook playback position as a bookmark.")
        .Produces<AudiobookBookmarkDto>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireClientScope(ClientApiScopes.ProgressWrite);

        group.MapDelete("/audiobooks/bookmarks/{bookmarkId:guid}", async (
            Guid bookmarkId,
            Guid? profileId,
            ClaimsPrincipal user,
            PlayerService player,
            CancellationToken ct) =>
        {
            var identity = Bind(user, profileId, null, null);
            var deleted = await player.DeleteAudiobookBookmarkAsync(identity.ProfileId, bookmarkId, ct);
            return deleted ? Results.NoContent() : ApiErrors.NotFound($"Audiobook bookmark '{bookmarkId}' not found.");
        })
        .WithName("DeleteAudiobookBookmark")
        .WithSummary("Delete one saved audiobook bookmark.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.ProgressWrite);

        group.MapGet("/audiobooks/{workId:guid}/chapter-overrides", async (
            Guid workId,
            Guid? assetId,
            AudiobookChapterNamingService naming,
            PlayerCatalogueScope scope,
            CancellationToken ct) =>
        {
            if (assetId.HasValue)
            {
                await scope.RequireAssetAsync(assetId.Value, ct, workId);
            }

            var allowed = await scope.AssetIdsAsync(ct);
            var overrides = await naming.GetOverridesAsync(workId, assetId, ct);
            return Results.Ok(overrides.Where(item => allowed.Contains(item.AssetId)).ToList());
        })
        .WithName("GetAudiobookChapterTitleOverrides")
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.LibraryRead, "work", "workId")
        .WithSummary("Return display-only audiobook chapter title overrides.")
        .Produces<IReadOnlyList<AudiobookChapterTitleOverrideDto>>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.LibraryRead);

        group.MapPost("/audiobooks/{workId:guid}/chapter-overrides", async (
            Guid workId,
            UpsertAudiobookChapterTitleOverrideRequestDto request,
            AudiobookChapterNamingService naming,
            CancellationToken ct) =>
        {
            try
            {
                var saved = await naming.UpsertOverrideAsync(workId, request, ct);
                return Results.Ok(saved);
            }
            catch (ArgumentException ex)
            {
                return ApiErrors.BadRequest(ex.Message);
            }
            catch (KeyNotFoundException ex)
            {
                return ApiErrors.NotFound(ex.Message);
            }
        })
        .WithName("UpsertAudiobookChapterTitleOverride")
        .WithSummary("Create or update one display-only audiobook chapter title override.")
        .Produces<AudiobookChapterTitleOverrideDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite);

        group.MapDelete("/audiobooks/{workId:guid}/chapter-overrides/{assetId:guid}/{chapterIndex:int}", async (
            Guid workId,
            Guid assetId,
            int chapterIndex,
            AudiobookChapterNamingService naming,
            CancellationToken ct) =>
        {
            var deleted = await naming.DeleteOverrideAsync(workId, assetId, chapterIndex, ct);
            return deleted
                ? Results.NoContent()
                : ApiErrors.NotFound($"Chapter title override not found for work '{workId}', asset '{assetId}', chapter {chapterIndex}.");
        })
        .WithName("DeleteAudiobookChapterTitleOverride")
        .WithSummary("Delete one display-only audiobook chapter title override.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite);

        return app;
    }

    private static (Guid? ProfileId, string? DeviceId, string? Client) Bind(
        ClaimsPrincipal user, Guid? profileId, string? deviceId, string? client)
    {
        var trustedProfile = Guid.TryParse(user.FindFirstValue(TuvimaClaimTypes.ActiveProfileId), out var parsedProfile)
            ? parsedProfile
            : profileId;
        var trustedDevice = user.FindFirstValue(TuvimaClaimTypes.DeviceId) ?? deviceId;
        var trustedClient = user.FindFirstValue(TuvimaClaimTypes.ClientId) ?? client;
        return (trustedProfile, trustedDevice, trustedClient);
    }

    private static PlayerQueueMutationDto Bind(ClaimsPrincipal user, PlayerQueueMutationDto request)
    {
        var identity = Bind(user, request.ProfileId, request.DeviceId, request.Client);
        return request with { ProfileId = identity.ProfileId, DeviceId = identity.DeviceId, Client = identity.Client };
    }

    private static PlayerCommandRequestDto Bind(ClaimsPrincipal user, PlayerCommandRequestDto request)
    {
        var identity = Bind(user, request.ProfileId, request.DeviceId, request.Client);
        return request with { ProfileId = identity.ProfileId, DeviceId = identity.DeviceId, Client = identity.Client };
    }

    private static PlayerHeartbeatDto Bind(ClaimsPrincipal user, PlayerHeartbeatDto request)
    {
        var identity = Bind(user, request.ProfileId, request.DeviceId, request.Client);
        return request with { ProfileId = identity.ProfileId, DeviceId = identity.DeviceId, Client = identity.Client };
    }

    private static bool HasValidTelemetryNumbers(PlayerHeartbeatDto request) =>
        double.IsFinite(request.PositionSeconds) && request.PositionSeconds >= 0 &&
        (!request.DurationSeconds.HasValue ||
            double.IsFinite(request.DurationSeconds.Value) && request.DurationSeconds.Value >= 0) &&
        (!request.ProgressPct.HasValue ||
            double.IsFinite(request.ProgressPct.Value) && request.ProgressPct.Value >= 0) &&
        (!request.Volume.HasValue || double.IsFinite(request.Volume.Value)) &&
        (!request.PlaybackRate.HasValue || PlaybackRatePolicy.IsValid(request.PlaybackRate.Value)) &&
        request.Sequence is not < 0;

    private static PlayerSessionTakeoverRequestDto Bind(ClaimsPrincipal user, PlayerSessionTakeoverRequestDto request)
    {
        var identity = Bind(user, request.ProfileId, request.DeviceId, request.Client);
        return request with { ProfileId = identity.ProfileId, DeviceId = identity.DeviceId, Client = identity.Client };
    }
}
