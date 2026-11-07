namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// A read-only answer to "is a transcode being served right now?" for low-priority background work such as scrub previews. It
/// changes nothing about how transcodes run.
/// </summary>
public interface ITranscodeActivitySignal
{
    bool IsBusy { get; }
}

/// <inheritdoc cref="ITranscodeActivitySignal"/>
public sealed class TranscodeActivitySignal(AdaptiveHlsService hls, EncodeQueueService encodes) : ITranscodeActivitySignal
{
    /// <summary>A player fetches segments every few seconds, so the stream counts as served until it has been quiet this long.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(30);

    public bool IsBusy => encodes.IsEncoding || hls.IsServing(Quiet);
}
