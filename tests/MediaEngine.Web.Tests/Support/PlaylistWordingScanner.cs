using System.Text;
using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Finds the word "playlist" (or "playlists") typed straight into the Listen playlist screens: inside a quoted string, in an attribute
/// value, or as text between Razor tags. The words people read there come from <c>SharedStrings.resx</c> through <c>L["Key"]</c>, so the
/// whole app can follow the household's language (issue 1205). Comments, route and CSS names (one lowercase token such as
/// <c>"/listen/playlists"</c> or <c>"playlist-picker__row"</c>), resource keys and log or exception text for developers are not wording.
/// </summary>
internal static partial class PlaylistWordingScanner
{
    // "playlist" as a word of its own: not part of a longer name (PlaylistPage_Kind, playlist-tile, listen/playlists, @playlist).
    [GeneratedRegex(@"(?<![\w\-/.#@])[Pp]laylists?(?![\w\-/#])")] private static partial Regex PlaylistWord();

    // One lowercase token (a route, a CSS class, a data attribute value): never wording on its own.
    [GeneratedRegex(@"^[a-z0-9_.\-/#:?=&]+$")] private static partial Regex Token();

    [GeneratedRegex(@"@\*.*?\*@|<!--.*?-->", RegexOptions.Singleline)] private static partial Regex RazorComment();
    [GeneratedRegex(@"<(style|script)\b.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex Block();

    /// <summary>Every playlist word typed into one C# file or one Razor file, with the line it is on and the text around it.</summary>
    public static IReadOnlyList<(int Line, string Text)> Find(string source, bool razor)
    {
        var text = source.Replace("\r\n", "\n");
        if (razor)
        {
            text = Mask(text, RazorComment());
            text = Mask(text, Block());
        }

        var found = new List<(int, string)>();
        var scanner = new Reader(text, razor);
        foreach (var (index, literal) in scanner.Literals())
        {
            if (IsWording(literal) && !IsDeveloperLine(text, index))
            {
                found.Add((LineOf(text, index), literal));
            }
        }

        if (razor)
        {
            foreach (var (line, prose) in TypedWordingScanner.Find(source))
            {
                if (PlaylistWord().IsMatch(prose))
                {
                    found.Add((line, prose));
                }
            }
        }

        return found;
    }

    /// <summary>True when a quoted string is a sentence or label (it has the word and is more than a route or CSS token).</summary>
    public static bool IsWording(string literal) =>
        PlaylistWord().IsMatch(literal) && !Token().IsMatch(literal.Trim());

    private static bool IsDeveloperLine(string text, int index)
    {
        var start = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var end = text.IndexOf('\n', index);
        var line = text[start..(end < 0 ? text.Length : end)];
        return line.Contains("Logger.Log", StringComparison.Ordinal)
            || line.Contains("logger.Log", StringComparison.Ordinal)
            || line.Contains("throw new", StringComparison.Ordinal);
    }

    private static string Mask(string text, Regex pattern) =>
        pattern.Replace(text, match => new string(match.Value.Select(c => c == '\n' ? '\n' : ' ').ToArray()));

    private static int LineOf(string text, int index) => 1 + text.Take(Math.Min(index, text.Length)).Count(c => c == '\n');

    /// <summary>Walks code and markup, handing back the static text of every quoted string (interpolation holes are walked as code, not as text).</summary>
    private sealed class Reader(string text, bool razor)
    {
        private int _i;
        private readonly List<(int, string)> _found = [];

        public IReadOnlyList<(int Index, string Literal)> Literals()
        {
            Code(closer: '\0');
            return _found;
        }

        private void Code(char closer)
        {
            var depth = 0;
            while (_i < text.Length)
            {
                var c = text[_i];
                if (closer is '"' or '\'' && c == closer && depth == 0)
                {
                    _i++;
                    return;
                }

                if (c == '/' && Next(1) == '/')
                {
                    while (_i < text.Length && text[_i] != '\n') { _i++; }
                    continue;
                }

                if (c == '/' && Next(1) == '*')
                {
                    var end = text.IndexOf("*/", _i + 2, StringComparison.Ordinal);
                    _i = end < 0 ? text.Length : end + 2;
                    continue;
                }

                if ((c == '$' && Next(1) == '@' && Next(2) == '"') || (c == '@' && Next(1) == '$' && Next(2) == '"'))
                {
                    _i += 2;
                    Str(interpolated: true, verbatim: true);
                    continue;
                }

                if (c == '$' && Next(1) == '"')
                {
                    _i++;
                    Str(interpolated: true);
                    continue;
                }

                if (c == '@' && Next(1) == '"')
                {
                    _i++;
                    Str(interpolated: false, verbatim: true);
                    continue;
                }

                if (c == '"')
                {
                    if (razor && _i > 0 && text[_i - 1] == '=')
                    {
                        Attribute('"');
                    }
                    else
                    {
                        Str(interpolated: false);
                    }
                    continue;
                }

                if (c == '\'')
                {
                    if (razor && _i > 0 && text[_i - 1] == '=')
                    {
                        Attribute('\'');
                        continue;
                    }

                    // A character literal ('a', '\n', '"'); anywhere else an apostrophe is just a letter in text.
                    if (Next(1) == '\\' && Next(3) == '\'') { _i += 4; continue; }
                    if (Next(2) == '\'' && Next(1) != '\'') { _i += 3; continue; }
                    _i++;
                    continue;
                }

                if (closer != '\0')
                {
                    if (c == '(' || c == '{' || c == '[') { depth++; }
                    else if (c == ')' || c == '}' || c == ']')
                    {
                        if (depth == 0 && c == closer) { _i++; return; }
                        depth = Math.Max(0, depth - 1);
                    }
                }

                _i++;
            }
        }

        // A string literal starting at the opening quote of text[_i]. Interpolation holes are read as code.
        private void Str(bool interpolated, bool verbatim = false)
        {
            var startQuote = _i;
            var raw = text.AsSpan(_i).StartsWith("\"\"\"");
            if (raw)
            {
                var end = text.IndexOf("\"\"\"", _i + 3, StringComparison.Ordinal);
                var body = end < 0 ? text[(_i + 3)..] : text[(_i + 3)..end];
                _found.Add((startQuote, interpolated ? Holes(body) : body));
                _i = end < 0 ? text.Length : end + 3;
                return;
            }

            _i++; // opening quote
            var literal = new StringBuilder();
            while (_i < text.Length)
            {
                var c = text[_i];
                if (c == '\\' && !verbatim)
                {
                    literal.Append(c);
                    if (_i + 1 < text.Length) { literal.Append(text[_i + 1]); }
                    _i += 2;
                    continue;
                }

                if (c == '"')
                {
                    if (verbatim && Next(1) == '"') { literal.Append('"'); _i += 2; continue; }
                    _i++;
                    break;
                }

                if (interpolated && c == '{')
                {
                    if (Next(1) == '{') { literal.Append("{{"); _i += 2; continue; }
                    _i++;
                    Code('}');
                    literal.Append("{}");
                    continue;
                }

                literal.Append(c);
                _i++;
            }

            _found.Add((startQuote, literal.ToString()));
        }

        // An attribute value in markup (after =" or ='): static text plus @expressions, which are read as code.
        private void Attribute(char quote)
        {
            var startQuote = _i;
            var nameStart = startQuote - 1;
            while (nameStart > 0 && !char.IsWhiteSpace(text[nameStart - 1])) { nameStart--; }
            _i++;
            if (text[nameStart] == '@')
            {
                // @key="...", @onclick="...", @bind="...": the whole value is code.
                Code(quote);
                return;
            }

            var literal = new StringBuilder();
            while (_i < text.Length && text[_i] != quote)
            {
                if (text[_i] == '@' && Next(1) == '@') { literal.Append('@'); _i += 2; continue; }
                if (text[_i] == '@' && Next(1) == '(')
                {
                    _i += 2;
                    Code(')');
                    literal.Append("{}");
                    continue;
                }

                if (text[_i] == '@')
                {
                    _i++;
                    while (_i < text.Length && (char.IsLetterOrDigit(text[_i]) || text[_i] is '_' or '.')) { _i++; }
                    if (_i < text.Length && text[_i] is '(' or '[')
                    {
                        var close = text[_i] == '(' ? ')' : ']';
                        _i++;
                        Code(close);
                    }

                    literal.Append("{}");
                    continue;
                }

                literal.Append(text[_i]);
                _i++;
            }

            _i++; // closing quote
            _found.Add((startQuote, literal.ToString()));
        }

        private static string Holes(string body) => Regex.Replace(body, @"\{[^{}]*\}", "{}");

        private char Next(int ahead) => _i + ahead < text.Length ? text[_i + ahead] : '\0';
    }
}
