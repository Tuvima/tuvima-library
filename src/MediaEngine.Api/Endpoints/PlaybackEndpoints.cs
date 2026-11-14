using System.Security.Claims;
using System.Text.Json;
using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Networking;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Authorization;

namespace MediaEngine.Api.Endpoints;

public static class PlaybackEndpoints
{
    private static readonly Dictionary<string, string> MimeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/x-m4v",
        [".m3u8"] = "application/vnd.apple.mpegurl",
        [".ts"] = "video/mp2t",
        [".mp3"] = "audio/mpeg",
        [".m4a"] = "audio/mp4",
        [".m4b"] = "audio/mp4",
        [".aac"] = "audio/aac",
        [".flac"] = "audio/flac",
        [".ogg"] = "audio/ogg",
        [".wav"] = "audio/wav",
    };

    /// <summary>The client id bound to a paired device's token wins over the caller-supplied <c>client</c> query value.</summary>
    internal static string? TrustedClient(ClaimsPrincipal user, string? client) =>
        user.FindFirstValue(TuvimaClaimTypes.ClientId) ?? client;

    /// <summary>What the calling device reported: the paired device's record, or this browser's report for the signed-in person.</summary>
    private static async Task<ClientCapabilitiesDto?> CallerCapabilitiesAsync(
        ClaimsPrincipal user,
        string? browser,
        ClientAuthorizationService authorization,
        BrowserPlaybackAbilityRegistryService browserAbilities,
        CancellationToken ct)
    {
        if (ClaimGuid(user, TuvimaClaimTypes.DeviceId) is { } deviceId)
        {
            return await authorization.GetDeviceAsync(deviceId, ct) is { } device
                ? ClientAuthorizationService.ReadCapabilities(device)
                : null;
        }

        return ClaimGuid(user, TuvimaClaimTypes.ActiveProfileId) is { } profileId
            ? browserAbilities.TryGet(profileId, browser)
            : null;
    }

    public static IEndpointRouteBuilder MapPlaybackEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/playback")
            .WithTags("Playback");

        group.MapGet("/{assetId:guid}/manifest", async (
            Guid assetId,
            string? client,
            string? connectionPath,
            string? provider,
            double? bandwidthMbps,
            int? latencyMs,
            Guid? roomId,
            string? delivery,
            string? browser,
            HttpContext httpContext,
            ClaimsPrincipal user,
            ClientAuthorizationService authorization,
            NetworkConnectionClassifier classifier,
            PlaybackCapabilitiesService playback,
            BrowserPlaybackAbilityRegistryService browserAbilities,
            VideoOptionsService videoOptions,
            CancellationToken ct) =>
        {
            var connection = classifier.Classify(
                httpContext,
                connectionPath,
                provider,
                bandwidthMbps,
                latencyMs,
                roomId);
            var profileId = ClaimGuid(user, TuvimaClaimTypes.ActiveProfileId);
            var deviceId = ClaimGuid(user, TuvimaClaimTypes.DeviceId);
            ClientCapabilitiesDto? capabilities = null;
            if (deviceId is { } pairedDeviceId)
            {
                var device = await authorization.GetDeviceAsync(pairedDeviceId, ct);
                if (device is not null)
                {
                    capabilities = JsonSerializer.Deserialize<ClientCapabilitiesDto>(device.CapabilitiesJson);
                }
            }
            else if (profileId is { } browserProfileId)
            {
                // The Dashboard's browser is not a paired device: use what it measured about itself this session, if it has reported.
                capabilities = browserAbilities.TryGet(browserProfileId, browser);
            }
            var trustedClient = TrustedClient(user, client);
            var forceAdaptive = string.Equals(delivery, "hls", StringComparison.OrdinalIgnoreCase);
            // This person's and this device's "Video quality and devices" choices decide the rung and the quality (playback-path spec D4, D15).
            var choices = await videoOptions.ResolveChoicesAsync(profileId, capabilities, ct);
            var manifest = await playback.BuildManifestAsync(assetId, trustedClient, profileId, ct, connection, capabilities, deviceId, forceAdaptive, choices);
            if (manifest?.DirectStreamUrl is { } streamUrl)
            {
                manifest = manifest with { DirectStreamUrl = "/api/v1" + streamUrl };
            }

            return manifest is null
                ? ApiErrors.NotFound($"Asset '{assetId}' not found.")
                : Results.Ok(manifest);
        })
        .WithName("GetPlaybackManifest")
        .WithSummary("Return the centralized playback manifest for an asset and client profile.")
        .Produces<PlaybackManifestDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.PlaybackRead)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.PlaybackRead);

        // The Dashboard's browser reports what it measured about itself, once per session (playback-path spec D12). Paired apps
        // report through PUT /devices/current/capabilities instead, because they have a device record.
        group.MapPut("/browser-capabilities", (
            BrowserCapabilitiesReportDto request,
            ClaimsPrincipal user,
            BrowserPlaybackAbilityRegistryService browserAbilities) =>
        {
            if (ClaimGuid(user, TuvimaClaimTypes.ActiveProfileId) is not { } profileId)
            {
                return ApiErrors.Forbidden("A signed-in profile is required to report what this browser can play.");
            }

            return browserAbilities.Set(profileId, request.Browser, request.Capabilities)
                ? Results.NoContent()
                : ApiErrors.BadRequest("The browser session key is missing or not valid.");
        })
        .WithName("ReportBrowserPlaybackCapabilities")
        .WithSummary("Keep what the Dashboard's browser measured about the video it can play, for this session.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .RequireClientScope(ClientApiScopes.PlaybackWrite);

        // The "Video quality and devices" list (playback-path spec D15): one list from the Engine, drawn by every client, with this
        // person's and this device's saved values. The Dashboard's browser names itself with the browser session key.
        group.MapGet("/video-options", async (
            string? browser,
            ClaimsPrincipal user,
            ClientAuthorizationService authorization,
            BrowserPlaybackAbilityRegistryService browserAbilities,
            VideoOptionsService videoOptions,
            CancellationToken ct) =>
        {
            var profileId = ClaimGuid(user, TuvimaClaimTypes.ActiveProfileId);
            var capabilities = await CallerCapabilitiesAsync(user, browser, authorization, browserAbilities, ct);
            return Results.Ok(await videoOptions.BuildAsync(profileId, capabilities, ct));
        })
        .WithName("GetVideoPlaybackOptions")
        .WithSummary("Return the Video quality and devices list with this person's and this device's saved values.")
        .Produces<VideoPlaybackOptionsDto>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.PlaybackRead);

        // Saves one row. A person's row follows them to every device; a device row applies to the calling paired device, or to the browser
        // named by the session key. Everything defaults to automatic, so clearing a row means choosing Automatic.
        group.MapPut("/video-options", async (
            SetVideoOptionRequest request,
            ClaimsPrincipal user,
            ClientAuthorizationService authorization,
            BrowserPlaybackAbilityRegistryService browserAbilities,
            VideoOptionsService videoOptions,
            CancellationToken ct) =>
        {
            var profileId = ClaimGuid(user, TuvimaClaimTypes.ActiveProfileId);
            if (VideoOptionCatalog.IsProfileOption(request.Id))
            {
                if (profileId is not { } person)
                {
                    return ApiErrors.Forbidden("A signed-in profile is required to change this.");
                }

                return await videoOptions.SaveProfileOptionAsync(person, request.Id, request.Value, ct) switch
                {
                    VideoOptionOutcome.Saved => Results.Ok(await videoOptions.BuildAsync(
                        person, await CallerCapabilitiesAsync(user, request.Browser, authorization, browserAbilities, ct), ct)),
                    _ => ApiErrors.BadRequest("That is not a choice this option offers."),
                };
            }

            if (!VideoOptionCatalog.IsDeviceOption(request.Id))
            {
                return ApiErrors.BadRequest("That is not a Video quality and devices option.");
            }

            var saved = false;
            ClientCapabilitiesDto? capabilities = null;
            if (ClaimGuid(user, TuvimaClaimTypes.DeviceId) is { } deviceId)
            {
                var device = await authorization.GetDeviceAsync(deviceId, ct);
                var changed = device is null
                    ? null
                    : VideoOptionCatalog.TryApplyDeviceOption(ClientAuthorizationService.ReadCapabilities(device).VideoOptions, request.Id, request.Value);
                if (changed is null)
                {
                    return ApiErrors.BadRequest("That is not a choice this option offers.");
                }

                saved = await authorization.SetVideoOptionsAsync(deviceId, changed, ct);
                capabilities = await CallerCapabilitiesAsync(user, request.Browser, authorization, browserAbilities, ct);
            }
            else if (profileId is { } browserProfile && request.Browser is { } key)
            {
                var current = browserAbilities.TryGet(browserProfile, key);
                if (current is null)
                {
                    return ApiErrors.BadRequest("This browser has not said what it can play yet; try again in a moment.");
                }

                var changed = VideoOptionCatalog.TryApplyDeviceOption(current.VideoOptions, request.Id, request.Value);
                if (changed is null)
                {
                    return ApiErrors.BadRequest("That is not a choice this option offers.");
                }

                saved = browserAbilities.SetVideoOptions(browserProfile, key, changed);
                capabilities = browserAbilities.TryGet(browserProfile, key);
            }

            return saved
                ? Results.Ok(await videoOptions.BuildAsync(profileId, capabilities, ct))
                : ApiErrors.NotFound("This device was not found.");
        })
        .WithName("SetVideoPlaybackOption")
        .WithSummary("Save one Video quality and devices option for this person or this device.")
        .Produces<VideoPlaybackOptionsDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.PlaybackWrite);

        // A player says it started a title in a rung and could not play it; the next start of that kind of file begins one rung lower
        // (playback-path spec D13). The Engine looks up the kind of file itself; the player only names the rung it was playing.
        group.MapPost("/{assetId:guid}/failure", async (
            Guid assetId,
            PlaybackFailureReportDto request,
            ClaimsPrincipal user,
            ClientAuthorizationService authorization,
            PlaybackCapabilitiesService playback,
            BrowserPlaybackAbilityRegistryService browserAbilities,
            ILogger<PlaybackCapabilitiesService> logger,
            CancellationToken ct) =>
        {
            if (!LearnedPlaybackLimits.IsKnownRung(request.Rung))
            {
                return ApiErrors.BadRequest("The rung is not one the Engine knows.");
            }

            if (await playback.GetStoredFileFactsAsync(assetId, ct) is not { } facts)
            {
                return ApiErrors.NotFound($"Asset '{assetId}' not found.");
            }

            if (LearnedPlaybackLimits.KindOf(facts.Extension, facts.Probe) is not { } kind)
            {
                // Nothing to remember about a file the Engine has not inspected: the next start decides as before.
                return Results.NoContent();
            }

            bool recorded;
            if (ClaimGuid(user, TuvimaClaimTypes.DeviceId) is { } deviceId)
            {
                recorded = await authorization.RecordLearnedLimitAsync(deviceId, request.Rung, kind.VideoCodec, kind.Container, kind.TenBit, ct);
            }
            else if (ClaimGuid(user, TuvimaClaimTypes.ActiveProfileId) is { } profileId && request.Browser is { } browser)
            {
                recorded = browserAbilities.RecordLearnedLimit(profileId, browser, request.Rung, kind.VideoCodec, kind.Container, kind.TenBit);
            }
            else
            {
                recorded = false;
            }

            var said = request.Detail?.Trim() ?? string.Empty;
            logger.LogInformation(
                "Playback failed in the {Rung} rung for {Kind}{TenBit} in {Container} (asset {AssetId}); remembered: {Recorded}. Player said: {Detail}",
                request.Rung, kind.VideoCodec, kind.TenBit ? " 10-bit" : string.Empty, kind.Container, assetId, recorded,
                said.Length == 0 ? "nothing" : said[..Math.Min(said.Length, 200)]);
            return Results.NoContent();
        })
        .WithName("ReportPlaybackFailure")
        .WithSummary("Tell the Engine a title could not be played in the rung it chose, so the next start uses a heavier one.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.PlaybackWrite)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.PlaybackWrite);

        group.MapPost("/{assetId:guid}/encode", async (
            Guid assetId,
            QueueEncodeRequestDto request,
            ClaimsPrincipal user,
            PlaybackCapabilitiesService playback,
            CancellationToken ct) =>
        {
            if (!TryGetNativeOwner(user, out var profileId, out var deviceId))
            {
                return ApiErrors.Forbidden("A paired profile and device are required for offline downloads.");
            }

            var job = await playback.QueueEncodeAsync(
                assetId,
                request,
                profileId,
                deviceId,
                ct);
            return job is null
                ? ApiErrors.NotFound($"Asset '{assetId}' not found.")
                : Results.Accepted($"/playback/encode/jobs/{job.Id}", job);
        })
        .WithName("QueueEncodeJob")
        .WithSummary("Queue or schedule a managed encode/offline variant job.")
        .Produces<EncodeJobDto>(StatusCodes.Status202Accepted)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.DownloadsWrite)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.DownloadsWrite);

        group.MapGet("/encode/jobs", async (
            ClaimsPrincipal user,
            PlaybackCapabilitiesService playback,
            CancellationToken ct) =>
        {
            if (!TryGetNativeOwner(user, out var profileId, out var deviceId))
            {
                return ApiErrors.Forbidden("A paired profile and device are required for offline downloads.");
            }

            var jobs = await playback.ListEncodeJobsAsync(
                profileId,
                deviceId,
                ct);
            return Results.Ok(jobs);
        })
        .WithName("ListEncodeJobs")
        .WithSummary("List recent encode jobs.")
        .Produces<IReadOnlyList<EncodeJobDto>>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .RequireClientScope(ClientApiScopes.DownloadsRead);

        group.MapPost("/encode/jobs/{jobId:guid}/cancel", async (
            Guid jobId,
            ClaimsPrincipal user,
            PlaybackCapabilitiesService playback,
            CancellationToken ct) =>
        {
            if (!TryGetNativeOwner(user, out var profileId, out var deviceId))
            {
                return ApiErrors.Forbidden("A paired profile and device are required for offline downloads.");
            }

            var cancelled = await playback.CancelEncodeJobAsync(
                jobId,
                profileId,
                deviceId,
                ct);
            return cancelled
                ? Results.NoContent()
                : ApiErrors.NotFound("Encode job not found or no longer cancellable.");
        })
        .WithName("CancelEncodeJob")
        .WithSummary("Cancel a queued, scheduled, or running encode job.")
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireClientScope(ClientApiScopes.DownloadsWrite);

        group.MapGet("/diagnostics", async (
            PlaybackCapabilitiesService playback,
            CancellationToken ct) =>
        {
            var diagnostics = await playback.GetDiagnosticsAsync(ct);
            return Results.Ok(diagnostics);
        })
        .WithName("GetPlaybackDiagnostics")
        .WithSummary("Report playback, inspection, and encode readiness.")
        .Produces<PlaybackDiagnosticsDto>(StatusCodes.Status200OK)
        .RequireClientScope(ClientApiScopes.PlaybackRead);

        group.MapGet("/{assetId:guid}/offline/{variantId:guid}", async (
            Guid assetId,
            Guid variantId,
            ClaimsPrincipal user,
            PlaybackCapabilitiesService playback,
            CancellationToken ct) =>
        {
            if (!TryGetNativeOwner(user, out var profileId, out var deviceId))
            {
                return ApiErrors.Forbidden("A paired profile and device are required for offline downloads.");
            }

            var variant = await playback.GetOfflineVariantFileAsync(assetId, variantId, profileId, deviceId, ct);
            if (variant is null)
            {
                return ApiErrors.NotFound("Offline variant not found.");
            }

            if (!string.Equals(variant.Status, OfflineVariantStatuses.Ready, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(variant.OutputPath)
                || !File.Exists(variant.OutputPath))
            {
                return ApiErrors.NotFound("Offline variant is not ready.");
            }

            var stream = File.OpenRead(variant.OutputPath);
            var mime = MimeMap.GetValueOrDefault(Path.GetExtension(variant.OutputPath), "application/octet-stream");
            return Results.File(stream, mime, Path.GetFileName(variant.OutputPath), enableRangeProcessing: true);
        })
        .WithName("DownloadOfflineVariant")
        .WithSummary("Download a prepared offline variant with HTTP range support.")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status206PartialContent)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .RequireClientScope(ClientApiScopes.DownloadsRead)
        .RequireCatalogueAssetAccess(ApplicationPermissionIds.DownloadsRead)
        .RequireRateLimiting("streaming");

        return app;
    }

    private static Guid? ClaimGuid(ClaimsPrincipal user, string type) =>
        Guid.TryParse(user.FindFirstValue(type), out var value) ? value : null;

    private static bool TryGetNativeOwner(ClaimsPrincipal user, out Guid profileId, out Guid deviceId)
    {
        profileId = ClaimGuid(user, TuvimaClaimTypes.ActiveProfileId) ?? Guid.Empty;
        deviceId = ClaimGuid(user, TuvimaClaimTypes.DeviceId) ?? Guid.Empty;
        return profileId != Guid.Empty && deviceId != Guid.Empty;
    }
}
