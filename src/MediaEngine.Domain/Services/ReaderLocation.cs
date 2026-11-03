using System.Text.RegularExpressions;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Domain.Services;

/// <summary>
/// A saved place in an EPUB book, read from the reader's location text (an EPUB CFI such as <c>epubcfi(/6/14!/4/2/6/1:20)</c>) so two
/// bookmarks in the same book can be compared (Global bookmarks spec D7, D12). The part before the <c>!</c> names the chapter file
/// (the spine position); the part after it walks down the chapter's markup to a paragraph and then to a character in it.
/// Two locations are only comparable inside the same file: another edition of a book has different markup, so its bookmarks are
/// placed by chapter name instead (spec D14).
/// </summary>
public sealed record ReaderLocation(int Chapter, string Paragraph)
{
    private static readonly Regex Assertions = new(@"\[[^\]]*\]", RegexOptions.Compiled);

    /// <summary>Reads a location. Returns null for text that is not a point location in this reader (a range, or the older character offsets).</summary>
    public static ReaderLocation? TryParse(string? cfi)
    {
        if (string.IsNullOrWhiteSpace(cfi))
        {
            return null;
        }

        var text = cfi.Trim();
        if (!text.StartsWith("epubcfi(", StringComparison.Ordinal) || !text.EndsWith(')'))
        {
            return null;
        }

        var inner = Assertions.Replace(text["epubcfi(".Length..^1], string.Empty);
        if (inner.Contains(','))
        {
            return null;
        }

        var bang = inner.IndexOf('!');
        var package = bang < 0 ? inner : inner[..bang];
        var document = bang < 0 ? string.Empty : inner[(bang + 1)..];

        // The package path is /6/<n>: the spine item is the (n / 2)th item, counting from one.
        var steps = package.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (steps.Length < 2 || !int.TryParse(steps[^1].Split(':')[0], out var spineStep) || spineStep < 2)
        {
            return null;
        }

        // Drop the character offset, then a trailing odd step (the text inside the paragraph): what is left names the paragraph.
        var colon = document.IndexOf(':');
        if (colon >= 0)
        {
            document = document[..colon];
        }

        var path = document.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (path.Count > 0 && int.TryParse(path[^1], out var last) && last % 2 == 1)
        {
            path.RemoveAt(path.Count - 1);
        }

        return new ReaderLocation(spineStep / 2 - 1, "/" + string.Join('/', path));
    }

    /// <summary>
    /// How close two locations in the same book are: the same paragraph is the same spot, the same chapter is close, and a chapter
    /// away is far up to the configured limit (<c>close</c> and <c>far</c> are whole chapters in the text block). Anything further is new.
    /// </summary>
    public static BookmarkCloseness Classify(BookmarkGapLimits text, ReaderLocation a, ReaderLocation b)
    {
        var chapters = Math.Abs(a.Chapter - b.Chapter);
        if (chapters == 0 && string.Equals(a.Paragraph, b.Paragraph, StringComparison.Ordinal))
        {
            return BookmarkCloseness.SameSpot;
        }

        if (chapters <= text.Close)
        {
            return BookmarkCloseness.Close;
        }

        return chapters <= text.Far ? BookmarkCloseness.Far : BookmarkCloseness.New;
    }
}
