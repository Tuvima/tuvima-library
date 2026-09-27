using System.Net;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace MediaEngine.Storage;

/// <summary>Canonical display text; raw provider evidence stays in metadata_claims.</summary>
public static class DescriptionText
{
    public static bool IsDescription(string key) => key is "description" or "short_description" or "issue_description" or "synopsis" or "biography";

    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        // Providers sometimes encode an entire HTML fragment, including more than once.
        var decoded = value;
        for (var i = 0; i < 3; i++)
        {
            var next = WebUtility.HtmlDecode(decoded);
            if (next == decoded) break;
            decoded = next;
        }
        using var document = new HtmlParser().ParseDocument(decoded);
        var text = new StringBuilder();
        void Visit(INode node)
        {
            if (node is IText content) { text.Append(content.Data); return; }
            if (node is IElement element && element.LocalName is "script" or "style" or "template" or "iframe" or "object") return;
            var block = node is IElement e && e.LocalName is "p" or "div" or "br" or "li" or "blockquote" or "h1" or "h2" or "h3";
            if (block) text.Append('\n');
            foreach (var child in node.ChildNodes) Visit(child);
            if (block) text.Append('\n');
        }
        Visit(document.Body!);
        return string.Join("\n\n", text.ToString().Replace('\u00a0', ' ').Split('\n')
            .Select(line => line.Trim()).Where(line => line.Length > 0));
    }
}
