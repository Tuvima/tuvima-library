using MediaEngine.Contracts.Playback;

namespace MediaEngine.Api.Services.Playback;

/// <summary>Builds the one <see cref="PlaybackPathDto"/> a start reports, so every player shows the same words (playback-path spec D7, D8, D16).</summary>
public static class PlaybackPathBuilder
{
    public const string WaitingForSlotLabel = "Waiting for another title to finish preparing";

    public static PlaybackPathDto ForDirect(PlaybackDecision decision) => new()
    {
        Rung = PlaybackRungs.Direct,
        Reason = decision.Reason,
        Encoder = "copy",
        RanOn = PlaybackEncodePlaces.None,
        Phase = PlaybackPathPhases.Ready,
    };

    /// <param name="decision">The ladder's answer.</param>
    /// <param name="status">The package status: preparing, streaming, ready, failed or unavailable.</param>
    /// <param name="run">What the packager is doing right now, or null when nothing is running.</param>
    /// <param name="error">The Engine's error text when the status is failed or unavailable.</param>
    public static PlaybackPathDto ForStream(PlaybackDecision decision, string status, AdaptiveHlsRunInfo? run, string? error)
    {
        var encoding = decision.Rung == PlaybackRungs.EncodePicture;
        var phase = status switch
        {
            "ready" or "streaming" => PlaybackPathPhases.Ready,
            "failed" or "unavailable" => PlaybackPathPhases.Failed,
            _ => run?.WaitingForSlot == true ? PlaybackPathPhases.WaitingForSlot : PlaybackPathPhases.Working,
        };

        var ranOn = !encoding ? PlaybackEncodePlaces.None : run?.RanOn ?? PlaybackEncodePlaces.Processor;
        return new PlaybackPathDto
        {
            Rung = decision.Rung,
            Reason = decision.FullReason,
            Encoder = !encoding ? "copy" : run?.Encoder ?? "libx264",
            RanOn = ranOn,
            FallbackReason = encoding ? run?.FallbackReason : null,
            Phase = phase,
            PercentPrepared = run?.PercentPrepared,
            FailureDetail = phase == PlaybackPathPhases.Failed ? error : null,
            WaitingLabel = phase switch
            {
                PlaybackPathPhases.WaitingForSlot => WaitingForSlotLabel,
                PlaybackPathPhases.Failed => "This title could not be prepared",
                _ => LabelFor(decision.Rung, ranOn, encoding ? run?.FallbackReason : null),
            },
        };
    }

    public static string LabelFor(string rung, string ranOn, string? fallbackReason) => rung switch
    {
        PlaybackRungs.Repackage => "Getting this ready · about a few seconds",
        PlaybackRungs.ConvertSound => "Getting this ready · converting sound",
        PlaybackRungs.EncodePicture when ranOn == PlaybackEncodePlaces.GraphicsCard => "Preparing picture · using your graphics card",
        PlaybackRungs.EncodePicture when !string.IsNullOrWhiteSpace(fallbackReason) => "Preparing picture · using processor: graphics card unavailable",
        PlaybackRungs.EncodePicture => "Preparing picture · using the processor",
        _ => string.Empty,
    };
}
