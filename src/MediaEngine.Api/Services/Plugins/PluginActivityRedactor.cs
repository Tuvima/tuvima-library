using System.Text.RegularExpressions;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>
/// Takes keys and personal paths out of a failure's technical line before it is written anywhere (Plugin architecture spec C17).
/// A key the plugin was given is removed by value; common key shapes and home-folder paths are removed by pattern, so the line
/// still says what happened without exposing who or what secret was involved.
/// </summary>
internal static partial class PluginActivityRedactor
{
    public const string Hidden = "[hidden]";
    public const string PathHidden = "[path]";
    public const int MaxDetailLength = 600;

    /// <param name="text">The technical line.</param>
    /// <param name="secretValues">Secret values the plugin holds, removed wherever they appear.</param>
    public static string? Redact(string? text, IEnumerable<string>? secretValues = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var result = text.Trim();
        foreach (var secret in (secretValues ?? []).Where(value => value.Length >= 4).OrderByDescending(value => value.Length))
        {
            result = result.Replace(secret, Hidden, StringComparison.Ordinal);
        }

        result = BearerToken().Replace(result, "Bearer " + Hidden);
        result = KeyValueSecret().Replace(result, "$1=" + Hidden);
        result = WindowsPath().Replace(result, PathHidden);
        result = HomePath().Replace(result, PathHidden);
        result = new string(result.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        return result.Length > MaxDetailLength ? result[..MaxDetailLength] + "..." : result;
    }

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._~+/=-]+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"\b(api[_-]?key|apikey|token|secret|password|passwd|authorization|access[_-]?key|client[_-]?secret)\s*[=:]\s*[^\s&;,""']+", RegexOptions.IgnoreCase)]
    private static partial Regex KeyValueSecret();

    [GeneratedRegex(@"[A-Za-z]:\\(?:[^\\\s""'<>|]+\\)*[^\\\s""'<>|]*")]
    private static partial Regex WindowsPath();

    [GeneratedRegex(@"(?<![A-Za-z0-9])/(?:home|Users|root|mnt|media|srv|var|opt|tmp)/[^\s""'<>|]*")]
    private static partial Regex HomePath();
}
