using System.Text;

namespace MediaEngine.Web.Components.Settings;

/// <summary>What a piece of a JSON line is, so the viewer can colour it (Plugins and Integrations screens v5 D54).</summary>
public enum PluginJsonTokenKind
{
    /// <summary>Brackets, braces, colons, commas.</summary>
    Punctuation,

    /// <summary>A name before a colon.</summary>
    Key,

    /// <summary>A text value.</summary>
    String,

    /// <summary>A number.</summary>
    Number,

    /// <summary><c>true</c>, <c>false</c> or <c>null</c>.</summary>
    Literal,

    /// <summary>A <c>//</c> or <c>/* */</c> comment.</summary>
    Comment,

    /// <summary>Spaces and tabs.</summary>
    Space,

    /// <summary>Anything else; shown plain.</summary>
    Text,
}

/// <summary>One coloured piece of one line.</summary>
public readonly record struct PluginJsonToken(PluginJsonTokenKind Kind, string Text);

/// <summary>
/// A small JSON tokenizer for the read-only plugin file viewer: pure C#, no library, one list of pieces per line. It never throws
/// and never rejects text: a file with a mistake in it is exactly the file somebody opens the viewer for, so unknown characters are
/// shown as plain text. Comments (which the Engine allows in plugin files) are coloured too, including across lines.
/// </summary>
public static class PluginJsonTokenizer
{
    /// <summary>Splits <paramref name="text"/> into lines (at each line feed; CRLF and LF are the same) and each line into coloured pieces. An empty text is one empty line.</summary>
    public static IReadOnlyList<IReadOnlyList<PluginJsonToken>> Lines(string? text)
    {
        var rows = new List<IReadOnlyList<PluginJsonToken>>();
        var inBlockComment = false;
        foreach (var line in SplitLines(text ?? string.Empty))
        {
            rows.Add(Tokenize(line, ref inBlockComment));
        }

        return rows;
    }

    /// <summary>The number of lines <see cref="Lines"/> gives for <paramref name="text"/>.</summary>
    public static int LineCount(string? text) => SplitLines(text ?? string.Empty).Count;

    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                // A line ends at a line feed, as the Engine counts them, so the numbers here are the numbers in its messages.
                continue;
            }

            if (c == '\n')
            {
                lines.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        // A file that ends with a line break does not have an extra empty line to number.
        if (current.Length > 0 || lines.Count == 0)
        {
            lines.Add(current.ToString());
        }

        return lines;
    }

    private static List<PluginJsonToken> Tokenize(string line, ref bool inBlockComment)
    {
        var tokens = new List<PluginJsonToken>();
        var i = 0;
        if (inBlockComment)
        {
            var end = line.IndexOf("*/", StringComparison.Ordinal);
            if (end < 0)
            {
                tokens.Add(new PluginJsonToken(PluginJsonTokenKind.Comment, line));
                return tokens;
            }

            tokens.Add(new PluginJsonToken(PluginJsonTokenKind.Comment, line[..(end + 2)]));
            i = end + 2;
            inBlockComment = false;
        }

        while (i < line.Length)
        {
            var c = line[i];
            if (c is ' ' or '\t')
            {
                var start = i;
                while (i < line.Length && line[i] is ' ' or '\t')
                {
                    i++;
                }

                tokens.Add(new PluginJsonToken(PluginJsonTokenKind.Space, line[start..i]));
            }
            else if (c == '"')
            {
                var start = i;
                i++;
                while (i < line.Length && line[i] != '"')
                {
                    i += line[i] == '\\' && i + 1 < line.Length ? 2 : 1;
                }

                i = Math.Min(i + 1, line.Length);
                var after = i;
                while (after < line.Length && line[after] is ' ' or '\t')
                {
                    after++;
                }

                var isKey = after < line.Length && line[after] == ':';
                tokens.Add(new PluginJsonToken(isKey ? PluginJsonTokenKind.Key : PluginJsonTokenKind.String, line[start..i]));
            }
            else if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                tokens.Add(new PluginJsonToken(PluginJsonTokenKind.Comment, line[i..]));
                i = line.Length;
            }
            else if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                var end = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    tokens.Add(new PluginJsonToken(PluginJsonTokenKind.Comment, line[i..]));
                    inBlockComment = true;
                    i = line.Length;
                }
                else
                {
                    tokens.Add(new PluginJsonToken(PluginJsonTokenKind.Comment, line[i..(end + 2)]));
                    i = end + 2;
                }
            }
            else if (c is '{' or '}' or '[' or ']' or ':' or ',')
            {
                tokens.Add(new PluginJsonToken(PluginJsonTokenKind.Punctuation, c.ToString()));
                i++;
            }
            else if (c == '-' || char.IsAsciiDigit(c))
            {
                var start = i;
                while (i < line.Length && (char.IsAsciiDigit(line[i]) || line[i] is '-' or '+' or '.' or 'e' or 'E'))
                {
                    i++;
                }

                tokens.Add(new PluginJsonToken(PluginJsonTokenKind.Number, line[start..i]));
            }
            else if (char.IsAsciiLetter(c))
            {
                var start = i;
                while (i < line.Length && char.IsAsciiLetter(line[i]))
                {
                    i++;
                }

                var word = line[start..i];
                tokens.Add(new PluginJsonToken(word is "true" or "false" or "null" ? PluginJsonTokenKind.Literal : PluginJsonTokenKind.Text, word));
            }
            else
            {
                tokens.Add(new PluginJsonToken(PluginJsonTokenKind.Text, c.ToString()));
                i++;
            }
        }

        return tokens;
    }
}
