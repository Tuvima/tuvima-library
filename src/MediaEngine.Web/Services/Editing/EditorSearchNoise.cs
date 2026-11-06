using System.Text.RegularExpressions;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// Takes release-name noise (resolution, source, codec, [group] tags) out of a search so a match is looked up by what the
/// item is called, and remembers what it left out so the person can put it back (universal editor v4.1, Match tab).
/// </summary>
public static partial class EditorSearchNoise
{
    public sealed record Result(string Query, IReadOnlyList<string> LeftOut)
    {
        public bool HasLeftOut => LeftOut.Count > 0;
    }

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*(?:p|rip|hdr|dts|aac|x26[45])[^)]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:(?:480|576|720|1080|2160)[pi]|4k|uhd|hdr10\+?|hdr|dv|x26[45]|h\.?26[45]|hevc|avc|xvid|divx|blu-?ray|bdrip|brrip|dvdrip|web-?dl|web-?rip|webrip|hdtv|remux|proper|repack|extended|unrated|dts(?:-hd)?|truehd|atmos|aac(?:2\.0)?|ac3|ddp?5\.1|flac|10-?bit)(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex Token();

    public static Result Clean(string? query)
    {
        var text = (query ?? string.Empty).Replace('.', ' ').Replace('_', ' ');
        var left = new List<string>();
        text = Bracketed().Replace(text, match => { left.Add(match.Value.Trim()); return " "; });
        text = Token().Replace(text, match => { left.Add(match.Value); return " "; });
        text = Regex.Replace(text, @"\s{2,}", " ").Trim(' ', '-');
        // Never strip a search down to nothing: an empty result keeps what the person typed.
        if (text.Length == 0)
        {
            return new Result((query ?? string.Empty).Trim(), []);
        }

        return new Result(text, left);
    }
}
