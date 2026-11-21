using System.Text.RegularExpressions;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// Turns what the Dashboard's Engine client keeps in <c>LastError</c> into a sentence a person can read: the Engine's own explanation, without the
/// status code in front ("HTTP 400:"), the generic "Invalid request." lead-in or the trace number at the end.
/// </summary>
public static partial class EngineFailureText
{
    [GeneratedRegex(@"^\s*(?:POST|PUT|PATCH|DELETE|GET)?[^:]*?returned\s+\d{3}:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ReturnedLead();

    [GeneratedRegex(@"^\s*HTTP\s+\d{3}:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex StatusLead();

    [GeneratedRegex(@"^\s*(?:Invalid request|Bad request|Forbidden|Not found)\.\s*", RegexOptions.IgnoreCase)]
    private static partial Regex GenericLead();

    [GeneratedRegex(@"\s*Trace:\s*\S+\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TraceTail();

    /// <summary>The explanation alone, or null when there is nothing a person could use.</summary>
    public static string? Plain(string? lastError)
    {
        if (string.IsNullOrWhiteSpace(lastError))
        {
            return null;
        }

        var text = lastError.Trim();
        text = ReturnedLead().Replace(text, string.Empty);
        text = StatusLead().Replace(text, string.Empty);
        text = GenericLead().Replace(text, string.Empty);
        text = TraceTail().Replace(text, string.Empty).Trim();
        return text.Length == 0 ? null : text;
    }
}
