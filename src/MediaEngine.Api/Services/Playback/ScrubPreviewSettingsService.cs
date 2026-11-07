using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// The "Generate scrub previews" switch of Settings > Playback > Advanced. It is stored with the other server-side playback settings
/// (<c>scrub_previews.enabled</c> in <c>config/transcoding.json</c>). Turning it off stops the job (the worker reads the switch) and
/// removes the whole preview cache; turning it back on lets the back-fill pass make them again.
/// </summary>
public sealed class ScrubPreviewSettingsService(
    IConfigurationLoader configuration,
    ScrubPreviewCache store,
    IFFmpegService ffmpeg,
    ILogger<ScrubPreviewSettingsService> logger)
{
    public ScrubPreviewStatusDto GetStatus() => new()
    {
        Enabled = configuration.LoadTranscoding().ScrubPreviews.Enabled,
        BytesUsed = store.UsedBytes(),
        FfmpegAvailable = ffmpeg.IsAvailable,
    };

    public ScrubPreviewStatusDto SetEnabled(bool enabled)
    {
        var settings = configuration.LoadTranscoding();
        if (settings.ScrubPreviews.Enabled != enabled)
        {
            settings.ScrubPreviews.Enabled = enabled;
            configuration.SaveTranscoding(settings);
        }

        if (!enabled)
        {
            var freed = store.DeleteAll();
            logger.LogInformation("Scrub previews were switched off; removed {Bytes} bytes of previews.", freed);
        }

        return GetStatus();
    }
}
