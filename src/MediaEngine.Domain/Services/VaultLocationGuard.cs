using System.Text;

namespace MediaEngine.Domain.Services;

/// <summary>
/// Says whether a piece of text (a log line, an activity note, an error message) names a place inside a Vault library's folders
/// (Videos spec D17, D33). The Engine logs and keeps activity notes for ordinary files all the time, and nothing outside an unlocked
/// NSFW session may spell the name or location of a Vault file, so the places that write text for administrators ask here first
/// and leave the text out when it does. It works from the folder list alone, so it needs no data store access and can sit in the
/// logging pipeline. The list is kept up to date by the Engine (at start-up, every half minute, and the moment a folder is added).
/// </summary>
public static class VaultLocationGuard
{
    /// <summary>
    /// The log property that marks everything written while a Vault file is processed. Log writers leave out any line that has it,
    /// which covers a call that names the file by its bare name, where there is no folder to recognise.
    /// </summary>
    public const string LogScopeProperty = "VaultFile";

    private static volatile string[] _roots = [];

    /// <summary>True when at least one Vault folder is known, so callers can skip work when there is no Vault.</summary>
    public static bool HasVaultFolders => _roots.Length > 0;

    /// <summary>Replaces the list with the folders of every Vault library.</summary>
    public static void Replace(IEnumerable<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        _roots = folders.Select(Normalize).Where(folder => folder.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Adds one folder at once, before the Engine starts scanning it.</summary>
    public static void Add(string folder)
    {
        var normalized = Normalize(folder);
        if (normalized.Length == 0)
        {
            return;
        }

        var current = _roots;
        if (!current.Contains(normalized, StringComparer.Ordinal))
        {
            _roots = [.. current, normalized];
        }
    }

    /// <summary>
    /// True when <paramref name="text"/> contains a Vault folder or a path inside one, whatever the slashes, the case or the
    /// escaping of backslashes in JSON. A name that merely starts like a Vault folder (a longer folder name) is not a match.
    /// </summary>
    public static bool NamesVaultLocation(string? text)
    {
        var roots = _roots;
        if (roots.Length == 0 || string.IsNullOrEmpty(text))
        {
            return false;
        }

        var haystack = Normalize(text);
        foreach (var root in roots)
        {
            var from = 0;
            while (from <= haystack.Length - root.Length)
            {
                var at = haystack.IndexOf(root, from, StringComparison.Ordinal);
                if (at < 0)
                {
                    break;
                }

                var end = at + root.Length;
                // The place ends here (or goes on into a sub-folder or file), rather than continuing into a longer folder name.
                if (end == haystack.Length || (!char.IsLetterOrDigit(haystack[end]) && haystack[end] != '_' && haystack[end] != '-'))
                {
                    return true;
                }

                from = at + 1;
            }
        }

        return false;
    }

    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text.Trim())
        {
            var next = character == '\\' ? '/' : char.ToLowerInvariant(character);
            // "a//b" (an escaped backslash in JSON, or a doubled separator) reads the same as "a/b".
            if (next == '/' && builder.Length > 0 && builder[^1] == '/')
            {
                continue;
            }

            builder.Append(next);
        }

        while (builder.Length > 0 && builder[^1] == '/')
        {
            builder.Length--;
        }

        return builder.ToString();
    }
}
