namespace MediaEngine.Contracts.Artwork;

/// <summary>
/// One frame taken from a video, offered in the editor's "Frames from the video" strip (review mock-ups 2026-11-18, D2).
/// <see cref="SourceKey"/> is what the Engine records as the saved picture's source address when this frame is chosen, so the strip can
/// outline the frame that is in use by comparing it with the picture's source.
/// </summary>
public sealed record VideoFrameDto(
    string Id,
    double Seconds,
    string ThumbnailUrl,
    bool IsBest,
    string SourceKey);

/// <summary>The frames of a video for one editor scope. <see cref="Message"/> says why the strip is empty (no FFmpeg, no file, all frames dark).</summary>
public sealed record VideoFramesDto(IReadOnlyList<VideoFrameDto> Items, string? Message = null);

/// <summary>The words and addresses of video-frame pictures that the Engine and the Dashboard both rely on.</summary>
public static class VideoFrameSources
{
    /// <summary>The source of a picture the Engine made from a frame of the video itself.</summary>
    public const string GeneratedProvider = "generated";

    /// <summary>The prefix of a frame picture's recorded source address.</summary>
    public const string SourceKeyPrefix = "video-frame:";

    /// <summary>The source address recorded for a frame taken <paramref name="seconds"/> into the video.</summary>
    public static string SourceKeyFor(double seconds) =>
        SourceKeyPrefix + ((long)Math.Round(seconds * 1000)).ToString(global::System.Globalization.CultureInfo.InvariantCulture);
}
