using System.Text.RegularExpressions;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Finds the 11-character YouTube video ID in what a person pasted: a watch, short, embed or youtu.be link, or the bare ID.
/// Only the ID is ever used, so nothing else in the pasted text (a playlist, a time, a tracking tag) travels anywhere.
/// </summary>
public static partial class YoutubeLink
{
    private static readonly string[] Hosts = ["youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com", "youtu.be", "www.youtu.be"];

    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}\z")]
    private static partial Regex IdPattern();

    public static bool TryGetVideoId(string? pasted, out string videoId)
    {
        videoId = string.Empty;
        var text = pasted?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (IdPattern().IsMatch(text))
        {
            videoId = text;
            return true;
        }

        if (!Uri.TryCreate(text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !Hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        string? candidate = null;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (uri.Host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            candidate = segments.FirstOrDefault();
        }
        else if (segments.Length >= 2 && segments[0] is "shorts" or "embed" or "live" or "v")
        {
            candidate = segments[1];
        }
        else if (segments.Length == 1 && segments[0] == "watch")
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 && parts[0] == "v")
                {
                    candidate = Uri.UnescapeDataString(parts[1]);
                    break;
                }
            }
        }

        if (candidate is not null && IdPattern().IsMatch(candidate))
        {
            videoId = candidate;
            return true;
        }

        return false;
    }
}
