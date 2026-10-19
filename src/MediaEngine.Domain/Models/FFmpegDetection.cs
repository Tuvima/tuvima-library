namespace MediaEngine.Domain.Models;

/// <summary>Where the Engine found FFmpeg, in the order the search looks.</summary>
public enum FFmpegDetectionSource
{
    /// <summary>Not found.</summary>
    None,

    /// <summary>The location chosen in <c>config/transcoding.json</c>.</summary>
    ExplicitPath,

    /// <summary><c>tools/ffmpeg/</c> inside the Tuvima data folder.</summary>
    DataFolder,

    /// <summary><c>tools/ffmpeg/</c> beside the app.</summary>
    AppFolder,

    /// <summary>The system PATH.</summary>
    SystemPath,

    /// <summary>A standard install folder for the operating system.</summary>
    StandardFolder,
}

/// <summary>
/// The outcome of the latest FFmpeg search. <paramref name="ExplicitPathMissing"/> is true when a location was
/// chosen in settings but no longer holds the tool. <paramref name="Version"/> is null when it was not probed.
/// </summary>
public sealed record FFmpegDetection(FFmpegDetectionSource Source, bool ExplicitPathMissing, string? Version)
{
    public static FFmpegDetection NotFound { get; } = new(FFmpegDetectionSource.None, false, null);
}
