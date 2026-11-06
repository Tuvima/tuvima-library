namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Gives an episode that has no provider picture a still taken from its own video file (universal editor v4.1, D8). The frame is
/// chosen for sharpness and colour and cached by the implementation; nothing is ever written beside the media. Null means no usable
/// frame (no file, no FFmpeg, or only dull frames).
/// </summary>
public interface IEpisodeFrameSource
{
    Task<byte[]?> GetFrameAsync(Guid episodeWorkId, CancellationToken ct = default);
}
