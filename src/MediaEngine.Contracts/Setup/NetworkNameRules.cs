using System.Text;

namespace MediaEngine.Contracts.Setup;

/// <summary>
/// Turns a friendly Library name into the short network name (<c>name.local</c>) that phones and TVs
/// look for: lower-case, ASCII letters and digits, words joined by single hyphens.
/// </summary>
public static class NetworkNameRules
{
    /// <summary>The network name a server has before anyone names it.</summary>
    public const string DefaultLabel = "tuvima";

    /// <summary>Longest label a network name may have.</summary>
    public const int MaxLabelLength = 63;

    /// <summary>
    /// "Shaya's Library" becomes "shayas-library". Apostrophes are dropped rather than hyphenated,
    /// every other character that is not an ASCII letter or digit separates words.
    /// </summary>
    public static string ToLabel(string? value)
    {
        var builder = new StringBuilder();
        foreach (var character in (value ?? string.Empty).Trim().ToLowerInvariant())
        {
            if (character is '\'' or '’' or '‘' or '`')
            {
                continue;
            }
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(character);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }
        var label = builder.ToString().Trim('-');
        return label.Length > MaxLabelLength ? label[..MaxLabelLength].TrimEnd('-') : label;
    }

    /// <summary>
    /// True while the stored network name is still the one the friendly name would give, or the untouched default,
    /// so it can keep following the friendly name. A name typed in by hand that differs stops following.
    /// </summary>
    public static bool IsDerived(string? serverName, string? hostName)
    {
        var host = ToLabel(hostName);
        return host.Length == 0
            || string.Equals(host, DefaultLabel, StringComparison.Ordinal)
            || string.Equals(host, ToLabel(serverName), StringComparison.Ordinal);
    }
}
