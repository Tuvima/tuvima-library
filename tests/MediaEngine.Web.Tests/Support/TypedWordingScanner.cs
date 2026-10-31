using System.Net;
using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Finds wording typed straight into Razor markup (text between tags, and the label-like attributes of components and elements)
/// instead of being read from the string table with <c>L["Key"]</c>. Code blocks, comments, styles and scripts are not looked at.
/// The guardrail uses it to keep the count from ever going up; the same rules are what a person follows when adding a screen.
/// </summary>
internal static partial class TypedWordingScanner
{
    private const string AttributeNames =
        "Label|Title|Placeholder|Description|Text|Tooltip|Subtitle|Heading|HelperText|Helper|Hint|Caption|Message|Summary|EmptyTitle|EmptyMessage|EmptyText|ConfirmText|CancelText|ConfirmLabel|CancelLabel|SubmitLabel|ActionLabel|AriaLabel|Eyebrow|Kicker|aria-label|title|placeholder|alt|Prompt|Legend|Badge|Headline|Body";

    /// <summary>Screens that only developers see are not translated.</summary>
    private static readonly string[] Exempt =
        ["DesignSystemPreview", "ProviderTesterToolTab", "EnrichmentTesterToolTab", "DevHarnessTab", "DevelopmentData"];

    [GeneratedRegex(@"@\*.*?\*@", RegexOptions.Singleline)] private static partial Regex CommentPattern();
    [GeneratedRegex(@"<(style|script)\b.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex BlockPattern();
    [GeneratedRegex(@"^[ \t]*@(using|namespace|page|inject|implements|inherits|attribute|layout|rendermode|typeparam|preservewhitespace)\b[^\n]*", RegexOptions.Multiline)] private static partial Regex DirectivePattern();
    [GeneratedRegex(@"@(code|functions)\s*\{")] private static partial Regex CodeBlockStart();
    [GeneratedRegex(@">([^<>{}]*)(?=<)")] private static partial Regex TextPattern();
    [GeneratedRegex(@"(?<![\w@:-])(" + AttributeNames + @")=""([^""@<>]*)""")] private static partial Regex AttributePattern();
    [GeneratedRegex(@"[A-Za-z]{2,}")] private static partial Regex WordPattern();
    [GeneratedRegex(@"(;|=>|==|!=|&&|\|\||\{|\}|\bcase\b|\bbreak\b|\breturn\b|^\s*(else|var|if|for|foreach|switch|while|default)\b)")] private static partial Regex CodeLike();
    [GeneratedRegex(@"^[a-z0-9_.\-/#]+$")] private static partial Regex Identifier();
    [GeneratedRegex(@"^[A-Z][a-z0-9]+([A-Z][a-z0-9]+)+$")] private static partial Regex PascalIdentifier();
    [GeneratedRegex(@"^(if|else|for|foreach|while|switch|using|lock|try|code|functions|inject|implements|section|rendermode|page|namespace|inherits|attribute|layout|typeparam)\b")] private static partial Regex RazorKeyword();

    public static bool IsExempt(string path) => Exempt.Any(path.Contains);

    /// <summary>Every piece of typed-in wording in one Razor file, with the line it starts on.</summary>
    public static IReadOnlyList<(int Line, string Text)> Find(string razor)
    {
        var source = razor.Replace("\r\n", "\n");
        var masked = Mask(source);
        var found = new List<(int, string)>();

        var spans = new List<(int Start, int End)>();
        for (var i = masked.IndexOf("@(", StringComparison.Ordinal); i >= 0; i = masked.IndexOf("@(", i + 2, StringComparison.Ordinal))
        {
            var end = Balanced(masked, i + 1, '(', ')');
            if (end > 0) spans.Add((i, end));
        }

        foreach (Match match in TextPattern().Matches(masked))
        {
            var start = match.Groups[1].Index;
            if (start >= 2 && source[start - 2] == '=') continue;
            if (spans.Any(span => span.Start < start - 1 && start - 1 < span.End)) continue;
            var lineStart = masked.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
            var before = masked[lineStart..(start - 1)];
            if (before.Count(c => c == '(') > before.Count(c => c == ')')) continue;

            var core = source[start..(start + match.Groups[1].Length)].Trim();
            if (core.Length == 0) continue;
            if (!TryLiteralText(core, out var literal)) continue;
            if (IsProse(literal)) found.Add((LineOf(source, start), core));
        }

        foreach (Match match in AttributePattern().Matches(masked))
        {
            var value = match.Groups[2].Value;
            if (WordPattern().IsMatch(value) && !PascalIdentifier().IsMatch(value) && (value.Contains(' ') || char.IsUpper(value[0]))
                && !Identifier().IsMatch(value) && IsProse(WebUtility.HtmlDecode(value)))
            {
                found.Add((LineOf(source, match.Groups[2].Index), value));
            }
        }

        return found;
    }

    private static bool TryLiteralText(string text, out string literal)
    {
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '@') { builder.Append(text[i]); continue; }
            if (i + 1 >= text.Length) { literal = ""; return false; }
            if (text[i + 1] == '@') { builder.Append('@'); i++; continue; }
            var end = ExpressionEnd(text, i);
            if (end < 0) { literal = ""; return false; }
            if (RazorKeyword().IsMatch(text[(i + 1)..])) { literal = ""; return false; }
            i = end - 1;
        }
        literal = builder.ToString();
        return true;
    }

    private static int ExpressionEnd(string text, int at)
    {
        var i = at + 1;
        if (text[i] == '(') return Balanced(text, i, '(', ')');
        if (!(char.IsLetter(text[i]) || text[i] == '_')) return -1;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
        while (i < text.Length)
        {
            if (text[i] == '.' && i + 1 < text.Length && (char.IsLetter(text[i + 1]) || text[i + 1] == '_'))
            {
                i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
            }
            else if ((text[i] == '?' || text[i] == '!') && i + 1 < text.Length && text[i + 1] == '.') i++;
            else if (text[i] == '(' || text[i] == '[')
            {
                var end = Balanced(text, i, text[i], text[i] == '(' ? ')' : ']');
                if (end < 0) return -1;
                i = end;
            }
            else break;
        }
        return i;
    }

    private static bool IsProse(string text)
    {
        var trimmed = text.Trim();
        if (!WordPattern().IsMatch(trimmed)) return false;
        if (CodeLike().IsMatch(trimmed)) return false;
        if (trimmed.Contains("://") || trimmed.Contains('\\')) return false;
        return !Identifier().IsMatch(trimmed);
    }

    private static string Mask(string source)
    {
        var chars = source.ToCharArray();
        void Blank(int start, int end)
        {
            for (var i = start; i < Math.Min(end, chars.Length); i++) if (chars[i] != '\n') chars[i] = ' ';
        }
        foreach (Match m in CommentPattern().Matches(source)) Blank(m.Index, m.Index + m.Length);
        foreach (Match m in DirectivePattern().Matches(source)) Blank(m.Index, m.Index + m.Length);
        foreach (Match m in BlockPattern().Matches(source)) Blank(m.Index, m.Index + m.Length);
        foreach (Match m in CodeBlockStart().Matches(source))
        {
            var i = m.Index + m.Length;
            var depth = 1;
            while (i < source.Length && depth > 0)
            {
                var c = source[i];
                if (c == '{') depth++;
                else if (c == '}') depth--;
                else if (c == '"')
                {
                    i++;
                    while (i < source.Length && source[i] != '"') { if (source[i] == '\\') i++; i++; }
                }
                i++;
            }
            Blank(m.Index, i);
        }
        return new string(chars);
    }

    private static int Balanced(string text, int at, char open, char close)
    {
        var depth = 0;
        for (var i = at; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                i++;
                while (i < text.Length && text[i] != '"') { if (text[i] == '\\') i++; i++; }
            }
            else if (c == open) depth++;
            else if (c == close && --depth == 0) return i + 1;
        }
        return -1;
    }

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++) if (text[i] == '\n') line++;
        return line;
    }
}
