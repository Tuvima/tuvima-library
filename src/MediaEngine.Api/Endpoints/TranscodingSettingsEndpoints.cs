using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Contracts;
using Microsoft.AspNetCore.Mvc;
using ContractTranscodingSettings = MediaEngine.Contracts.Settings.TranscodingSettings;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// <c>GET</c> and <c>PUT /settings/transcoding</c>: the playback encode, FFmpeg and offline variant policy. The scrub-preview switch travels
/// in the same object (the Dashboard loads and saves the whole thing) but belongs to <c>PUT /settings/playback/scrub-previews</c>, which also
/// removes the previews when it is turned off, so an encode-settings save never changes it.
/// </summary>
public static class TranscodingSettingsEndpoints
{
    public static RouteGroupBuilder MapTranscodingSettings(this RouteGroupBuilder grp)
    {
        grp.MapGet("/transcoding", (IConfigurationLoader configLoader) =>
        {
            return Results.Ok(SettingsContractMapper.ToContract(configLoader.LoadTranscoding()));
        })
        .WithName("GetTranscodingSettings")
        .WithSummary("Returns playback encode, FFmpeg, and offline variant policy.")
        .Produces<ContractTranscodingSettings>(StatusCodes.Status200OK)
        .RequireEffectiveAdministrator();

        grp.MapPut("/transcoding", (
            ContractTranscodingSettings request,
            IConfigurationLoader configLoader) =>
        {
            var settings = SettingsContractMapper.ToStorage(request);
            // The scrub-preview switch belongs to PUT /settings/playback/scrub-previews (which also removes the cache); an encode-settings
            // save that carries a stale copy of it must not change it.
            settings.ScrubPreviews = configLoader.LoadTranscoding().ScrubPreviews;
            settings.MaxConcurrentTranscodes = Math.Clamp(settings.MaxConcurrentTranscodes, 1, 8);
            settings.GraceSecondsAfterLastViewer = Math.Clamp(settings.GraceSecondsAfterLastViewer, 10, 3600);
            settings.PausedCountsAsWatchingMinutes = Math.Clamp(settings.PausedCountsAsWatchingMinutes, 1, 240);
            settings.ShadowStorageLimitGb = Math.Clamp(settings.ShadowStorageLimitGb, 1, 10_000);
            settings.VariantRetentionDays = Math.Clamp(settings.VariantRetentionDays, 1, 3650);
            settings.HardwareAcceleration = string.IsNullOrWhiteSpace(settings.HardwareAcceleration)
                ? "auto"
                : settings.HardwareAcceleration.Trim().ToLowerInvariant();
            settings.MaintenanceWindow = string.IsNullOrWhiteSpace(settings.MaintenanceWindow)
                ? "01:00-05:00"
                : settings.MaintenanceWindow.Trim();
            settings.VariantCachePath = string.IsNullOrWhiteSpace(settings.VariantCachePath)
                ? ".data/variants"
                : settings.VariantCachePath.Trim();
            settings.DefaultMobileProfile = string.IsNullOrWhiteSpace(settings.DefaultMobileProfile)
                ? settings.QualityProfiles.FirstOrDefault()?.Name ?? "mobile-small"
                : settings.DefaultMobileProfile.Trim();
            settings.AdaptiveHls.ProfileName = string.IsNullOrWhiteSpace(settings.AdaptiveHls.ProfileName)
                ? "tv-adaptive-hls"
                : settings.AdaptiveHls.ProfileName.Trim().ToLowerInvariant();
            settings.AdaptiveHls.SegmentSeconds = Math.Clamp(settings.AdaptiveHls.SegmentSeconds, 2, 12);
            settings.AdaptiveHls.AccessLifetimeMinutes = Math.Clamp(settings.AdaptiveHls.AccessLifetimeMinutes, 15, 720);
            settings.AdaptiveHls.PreparationWaitSeconds = Math.Clamp(settings.AdaptiveHls.PreparationWaitSeconds, 1, 60);
            settings.AdaptiveHls.CleanupIntervalMinutes = Math.Clamp(settings.AdaptiveHls.CleanupIntervalMinutes, 1, 1440);
            settings.AdaptiveHls.Renditions = settings.AdaptiveHls.Renditions
                .Where(rendition => rendition.Height is >= 144 and <= 4320
                    && rendition.VideoBitrateKbps is >= 100 and <= 100_000)
                .OrderByDescending(rendition => rendition.Height)
                .GroupBy(rendition => rendition.Height)
                .Select(group => group.First())
                .Take(8)
                .ToList();
            foreach (var rendition in settings.AdaptiveHls.Renditions)
            {
                rendition.Name = string.IsNullOrWhiteSpace(rendition.Name) ? $"{rendition.Height}p" : rendition.Name.Trim();
                rendition.MaxRateKbps = Math.Max(rendition.VideoBitrateKbps, rendition.MaxRateKbps);
                rendition.BufferSizeKbps = Math.Max(rendition.MaxRateKbps, rendition.BufferSizeKbps);
            }

            configLoader.SaveTranscoding(settings);
            return Results.Ok(SettingsContractMapper.ToContract(settings));
        })
        .WithName("UpdateTranscodingSettings")
        .WithSummary("Saves playback encode, FFmpeg, and offline variant policy.")
        .Produces<ContractTranscodingSettings>(StatusCodes.Status200OK)
        .RequireEffectiveAdministrator();

        // What the Engine found when it tested the graphics hardware, without testing again (Settings > Playback > Quality).
        grp.MapGet("/playback/hardware", ([FromServices] IFFmpegService ffmpeg, IConfigurationLoader configLoader) =>
            Results.Ok(GraphicsHardwareReport.Current(ffmpeg, configLoader.LoadTranscoding().HardwareAcceleration)))
        .WithName("GetPlaybackHardware")
        .WithSummary("Returns what the last graphics hardware test found.")
        .Produces<GraphicsHardwareDto>(StatusCodes.Status200OK)
        .RequireEffectiveAdministrator();

        // The Test hardware button: runs the graphics test again right now and returns the new answer.
        grp.MapPost("/playback/test-hardware", async ([FromServices] IFFmpegService ffmpeg, IConfigurationLoader configLoader, [FromServices] TimeProvider clock, CancellationToken ct) =>
        {
            var mode = configLoader.LoadTranscoding().HardwareAcceleration;
            // The test starts FFmpeg processes, so it runs off the request thread.
            var answer = await Task.Run(() => GraphicsHardwareReport.Test(ffmpeg, mode, clock), ct).ConfigureAwait(false);
            return Results.Ok(answer);
        })
        .WithName("TestPlaybackHardware")
        .WithSummary("Tests the graphics hardware again and returns what was found.")
        .Produces<GraphicsHardwareDto>(StatusCodes.Status200OK)
        .RequireEffectiveAdministrator();

        return grp;
    }
}
