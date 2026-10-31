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
            HttpContext httpContext,
            ClaimsPrincipal user,
            ClientAuthorizationService authorization,
            NetworkConnectionClassifier classifier,
            PlaybackCapabilitiesService playback,
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
            var trustedClient = TrustedClient(user, client);
            var forceAdaptive = string.Equals(delivery, "hls", StringComparison.OrdinalIgnoreCase);
            var manifest = await playback.BuildManifestAsync(assetId, trustedClient, profileId, ct, connection, capabilities, deviceId, forceAdaptive);
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
