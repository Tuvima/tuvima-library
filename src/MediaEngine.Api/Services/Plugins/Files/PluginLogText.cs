namespace MediaEngine.Api.Services.Plugins.Files;

/// <summary>
/// Makes text that came from a request or a file safe to put in a log line: line breaks and other control characters are
/// replaced and the length is capped, so a plugin id or file name cannot forge a second log entry.
/// </summary>
internal static class PluginLogText
{
    private const int MaxLength = 200;

    public static string Safe(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var text = value.Length > MaxLength ? value[..MaxLength] : value;
        return string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = char.IsControl(source[i]) ? '_' : source[i];
            }
        });
    }
}
