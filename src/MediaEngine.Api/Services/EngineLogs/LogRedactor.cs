using System.Text;
using System.Text.RegularExpressions;

namespace MediaEngine.Api.Services.EngineLogs;

/// <summary>
/// Hides private details in a log line before it leaves the server in an export: keys and tokens, sign-in details
/// (email addresses and the user named in a sign-in message), and file and folder paths. It is pure text in, text out,
/// with no settings and no state, so it can be tested pattern by pattern.
///
/// Paths are replaced first, by markers nothing else matches, so a long file name is never mistaken for a key and a
/// path is never half-hidden. With <c>showFileNames</c> a path becomes <c>[path]/name.ext</c>: the last segment only,
/// never the folders.
/// </summary>
public static partial class LogRedactor
{
    public const string HiddenSecret = "[hidden]";
    public const string HiddenEmail = "[email]";
    public const string HiddenUser = "[user]";
    public const string HiddenPath = "[path]";

    private const char MarkerStart = '\u0001';
    private const char MarkerEnd = '\u0002';

    // Folders (which may hold spaces), then a file name with an extension, or a last folder name that may hold spaces and
    // ends where the sentence turns: at a quote, a comma, the end of the line, or a word like "is" or "was".
    private const string SentenceTurns =
        @"\s+(?:is|was|are|were|not|could|can|cannot|failed|fails|because|from|to|for|at|and|with|has|have|had|does|did|will|when|while|after|before|which|but|or)\b|[,;)]|\.\s|\r|\n|$";

    private const string WindowsRest =
        @"(?:[^\\/:*?""<>|\r\n]+[\\/])*(?:[^\\/:*?""<>|\r\n]*?\.[A-Za-z0-9]{2,5}(?![A-Za-z0-9])|[^\\/:*?""<>|\r\n,;)]*?(?=" + SentenceTurns + @"|""))";

    // A Windows path: a drive, then the rest.
    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Za-z]:[\\/]" + WindowsRest)]
    private static partial Regex WindowsPath();

    // A network path: \\server\share\folder\file.ext (or the forward-slash form), never in the middle of a web address.
    [GeneratedRegex(@"(?<![\\/\w:.)\]\-])[\\/]{2}[^\\/:*?""<>|\s]+[\\/]" + WindowsRest)]
    private static partial Regex UncPath();

    // A POSIX path: starts at the beginning of a word, never in the middle of a URL, and has at least two segments. A name
    // may hold an apostrophe; a path written inside quotes is handled first, by QuotedPath.
    [GeneratedRegex(
        @"(?<![\w:/.)\]\-])/(?:[^/\s""<>|*?:][^/""<>|*?:\r\n]*/)+(?:[^/""<>|*?:\r\n]*?\.[A-Za-z0-9]{2,5}(?![A-Za-z0-9])|[^/""<>|*?:\r\n,;)]*?(?=" + SentenceTurns + @"|""))")]
    private static partial Regex PosixPath();

    // A path inside quotes: from the opening quote to the next quote of the same kind that is not followed by a letter or
    // digit. An apostrophe inside a name (Schindler's List) is followed by a letter, so it does not close the quote.
    [GeneratedRegex(@"(['""])((?:[A-Za-z]:[\\/]|[\\/]{2}[^\\/\s'""]+[\\/]|/(?=[^/\r\n'""]+/))[^\r\n]*?)\1(?![A-Za-z0-9])")]
    private static partial Regex QuotedPath();

    // An Authorization header (or JSON field): the scheme and everything after it are hidden, whatever the scheme is.
    [GeneratedRegex(@"(?i)([""']?\bauthorization[""']?\s*[:=]\s*)(?:""[^""]*""|'[^']*'|[^\r\n]*)")]
    private static partial Regex AuthorizationHeader();

    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9._~+/=\-]+")]
    private static partial Regex BearerToken();

    // api_key=..., "password":"...", token: ..., secret=... (the value may be quoted).
    [GeneratedRegex(
        @"(?i)([""']?\b(?:api[_-]?key|apikey|access[_-]?token|refresh[_-]?token|auth[_-]?token|token|key|secret|client[_-]?secret|password|passwd|pwd)[""']?\s*[:=]\s*)(""[^""]*""|'[^']*'|[^\s&;,""')\]}]+)")]
    private static partial Regex KeyValueSecret();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{5,}\.[A-Za-z0-9_\-]{5,}\.[A-Za-z0-9_\-]*")]
    private static partial Regex JsonWebToken();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}")]
    private static partial Regex EmailAddress();

    // A line that is about signing in.
    [GeneratedRegex(@"(?i)sign[\s_-]?in|signed[\s_-]?in|log[\s_-]?(?:in|on)|logged[\s_-]?in|authenticat|credential|password")]
    private static partial Regex SignInContext();

    // "user alice", "username=alice", "account 'alice'", "login: alice".
    [GeneratedRegex(@"(?i)\b(user(?:name)?|account|login)(\s*[:=]\s*|\s+)(['""]?)(?!(?:was|is|not|has|had|failed|could|cannot|name)\b)[^\s'"",;()]+\3")]
    private static partial Regex NamedUser();

    // "Sign-in failed for alice", "signed in as alice", "login attempt by alice".
    [GeneratedRegex(@"(?i)\b((?:sign(?:ed)?[\s_-]?in|log(?:ged)?[\s_-]?(?:in|on))(?:\s+(?:failed|succeeded|attempt|denied|refused|rejected))*\s+(?:for|as|by)\s+)(['""]?)[^\s'"",;()]+\2")]
    private static partial Regex SignInSubject();

    // Long unbroken runs: hex digests, base64 keys, url-safe tokens. Checked by ShouldHideRun before anything is hidden.
    [GeneratedRegex(@"[A-Za-z0-9+/_\-]{32,}={0,2}")]
    private static partial Regex LongRun();

    /// <summary>Returns <paramref name="text"/> with private details hidden.</summary>
    public static string Redact(string text, bool showFileNames = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        // Paths first, parked behind markers so no later pattern can touch them.
        var parked = new List<string>();
        var result = ParkQuotedPaths(text, showFileNames, parked);
        result = ParkPaths(UncPath(), result, showFileNames, parked);
        result = ParkPaths(WindowsPath(), result, showFileNames, parked);
        result = ParkPaths(PosixPath(), result, showFileNames, parked);

        result = AuthorizationHeader().Replace(result, match => match.Groups[1].Value + HiddenSecret);
        result = BearerToken().Replace(result, "Bearer " + HiddenSecret);
        result = KeyValueSecret().Replace(result, match => match.Groups[1].Value + HiddenSecret);
        result = JsonWebToken().Replace(result, HiddenSecret);
        result = EmailAddress().Replace(result, HiddenEmail);

        if (SignInContext().IsMatch(result))
        {
            result = SignInSubject().Replace(result, match => match.Groups[1].Value + HiddenUser);
            result = NamedUser().Replace(result, match => match.Groups[1].Value + match.Groups[2].Value + HiddenUser);
        }

        result = LongRun().Replace(result, match => ShouldHideRun(match.Value) ? HiddenSecret : match.Value);

        return RestorePaths(result, parked);
    }

    // 'D:\Media\Schindler's List' becomes '[path]': the quotes stay, everything between them goes.
    private static string ParkQuotedPaths(string text, bool showFileNames, List<string> parked) =>
        QuotedPath().Replace(text, match =>
        {
            var replacement = showFileNames ? WithLastSegment(match.Groups[2].Value) : HiddenPath;
            parked.Add(replacement);
            var quote = match.Groups[1].Value;
            return $"{quote}{MarkerStart}{parked.Count - 1}{MarkerEnd}{quote}";
        });

    private static string ParkPaths(Regex pattern, string text, bool showFileNames, List<string> parked) =>
        pattern.Replace(text, match =>
        {
            var replacement = showFileNames ? WithLastSegment(match.Value) : HiddenPath;
            parked.Add(replacement);
            return $"{MarkerStart}{parked.Count - 1}{MarkerEnd}";
        });

    // [path]/name.ext - the last segment only. A path with nothing after its last separator has no name to show.
    private static string WithLastSegment(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var split = trimmed.LastIndexOfAny(['\\', '/']);
        var name = split < 0 ? string.Empty : trimmed[(split + 1)..].Trim();
        return name.Length == 0 || name.EndsWith(':') ? HiddenPath : $"{HiddenPath}/{name}";
    }

    private static string RestorePaths(string text, List<string> parked)
    {
        if (parked.Count == 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != MarkerStart)
            {
                builder.Append(text[i]);
                continue;
            }

            var end = text.IndexOf(MarkerEnd, i + 1);
            if (end > i && int.TryParse(text.AsSpan(i + 1, end - i - 1), out var index) && index >= 0 && index < parked.Count)
            {
                builder.Append(parked[index]);
                i = end;
            }
            else
            {
                builder.Append(HiddenPath);
            }
        }

        return builder.ToString();
    }

    // A run is hidden when it looks like a secret: a hex digest, or a mix of letters and digits. Plain long words and GUIDs
    // (run and item ids a bug report needs) stay.
    private static bool ShouldHideRun(string run)
    {
        if (run.Contains('-') && Guid.TryParse(run, out _))
        {
            return false;
        }

        var hasLetter = false;
        var hasDigit = false;
        foreach (var character in run)
        {
            hasLetter |= char.IsAsciiLetter(character);
            hasDigit |= char.IsAsciiDigit(character);
        }

        return hasLetter && hasDigit || run.All(char.IsAsciiHexDigit);
    }
}
