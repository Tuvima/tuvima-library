namespace MediaEngine.Api.Services.Playback;

/// <summary>Turns FFmpeg's own error text about a graphics encoder into one plain sentence a person can act on.</summary>
public static class HardwareReasons
{
    public static string Plain(string? ffmpegError)
    {
        if (string.IsNullOrWhiteSpace(ffmpegError))
        {
            return "FFmpeg could not use the graphics card.";
        }

        var text = ffmpegError.ToLowerInvariant();
        if (text.Contains("minimum required nvidia driver") || text.Contains("driver does not support the required nvenc api"))
        {
            return "The graphics driver is too old for this card or this FFmpeg.";
        }

        if (text.Contains("no nvenc capable devices") || text.Contains("cannot load nvcuda") || text.Contains("cuda_error"))
        {
            return "No NVIDIA card could be opened for encoding.";
        }

        if (text.Contains("frame dimension") || text.Contains("not supported for this encoder"))
        {
            return "The graphics card refused the test picture size.";
        }

        if (text.Contains("incompatible with the minimum") || text.Contains("openencodesessionex failed"))
        {
            return "The graphics card is busy or has reached its limit of encodes.";
        }

        if (text.Contains("did not finish") || text.Contains("did not produce"))
        {
            return "The graphics card did not start encoding in time.";
        }

        if (text.Contains("device creation failed") || text.Contains("no such file or directory") || text.Contains("/dev/dri"))
        {
            return "No graphics device was available to FFmpeg.";
        }

        return "FFmpeg could not use the graphics card.";
    }
}
