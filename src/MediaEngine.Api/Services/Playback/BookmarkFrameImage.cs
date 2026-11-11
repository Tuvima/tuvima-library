using MediaEngine.Contracts.Playback;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// The picture of a video bookmark as a bookmark file carries it (universal editor v5 D37, Unified player v2 D21a): one small JPEG per
/// bookmark. A file is untrusted, so a picture is used only when it is small enough and really is a JPEG; anything else is ignored and the
/// Engine makes the picture itself.
/// </summary>
public static class BookmarkFrameImage
{
    /// <summary>True when the bytes are a JPEG within the size limit (start and end markers present).</summary>
    public static bool IsValid(byte[]? bytes) =>
        bytes is { Length: > 4 } && bytes.Length <= BookmarkPackLimits.MaximumFrameBytes
        && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF
        && bytes[^2] == 0xFF && bytes[^1] == 0xD9;

    /// <summary>The picture as the base64 text the wire carries, or null when it is not a valid picture.</summary>
    public static string? ToBase64(byte[]? bytes) => IsValid(bytes) ? Convert.ToBase64String(bytes!) : null;

    /// <summary>The picture bytes of base64 text off the wire, or null when the text is missing, too long, not base64, or not a valid picture.</summary>
    public static byte[]? FromBase64(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > BookmarkPackLimits.MaximumFrameBytes * 4L / 3 + 8)
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(text);
            return IsValid(bytes) ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
