using MediaEngine.Contracts.LocalAssets;

namespace MediaEngine.Web.Models.ViewDTOs;

/// <summary>
/// How a View document reads on a card, in the list and in the viewer: kind chips, the placeholder tile and the small
/// notes about how its words were read. Dashboard-only; the wire shapes stay in Contracts.
/// </summary>
public static class ViewDocumentPresentation
{
    /// <summary>Marks a highlighted word in a search snippet. The Engine uses control characters so text is never markup.</summary>
    public const char HighlightStart = '\u0001';
    public const char HighlightEnd = '\u0002';

    public static bool IsPdf(LocalAssetDto item) =>
        string.Equals(item.MimeType, "application/pdf", StringComparison.OrdinalIgnoreCase)
        || Extension(item).Equals("pdf", StringComparison.OrdinalIgnoreCase);

    public static bool IsImage(LocalAssetDto item) =>
        item.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when a first-page or image thumbnail can be requested; other files get a typed placeholder tile.</summary>
    public static bool HasPicture(LocalAssetDto item) => IsPdf(item) || IsImage(item);

    public static string Extension(LocalAssetDto item)
    {
        var extension = Path.GetExtension(item.FileName);
        return string.IsNullOrEmpty(extension) ? string.Empty : extension.TrimStart('.');
    }

    /// <summary>The short extension shown on the placeholder tile, such as DOCX or TXT.</summary>
    public static string ExtensionLabel(LocalAssetDto item)
    {
        var extension = Extension(item);
        return extension.Length == 0 ? "FILE" : extension.ToUpperInvariant();
    }

    public static string DisplayTitle(LocalAssetDto item) =>
        !string.IsNullOrWhiteSpace(item.Title) ? item.Title! : Path.GetFileNameWithoutExtension(item.FileName);

    /// <summary>The date a document is dated by: the date in the file when known, otherwise the day it was added.</summary>
    public static DateTimeOffset DocumentDate(LocalAssetDto item) => item.CapturedAt ?? item.CreatedAt;

    public static bool UsesAddedDate(LocalAssetDto item) => item.CapturedAt is null;

    /// <summary>Chips such as PDF, Word or Scan, in the order they are shown.</summary>
    public static IReadOnlyList<string> KindChips(LocalAssetDto item)
    {
        var chips = new List<string>();
        var extension = Extension(item).ToLowerInvariant();
        var kind = extension switch
        {
            "pdf" => "PDF",
            "doc" or "docx" or "odt" or "rtf" => "Word",
            "xls" or "xlsx" or "ods" => "Spreadsheet",
            "ppt" or "pptx" => "Slides",
            "txt" or "md" => "Text",
            _ => IsImage(item) ? "Image" : ExtensionLabel(item),
        };
        chips.Add(kind);
        if (item.DocumentInfo is { TextSource: "ocr" or "mixed" })
        {
            chips.Add("Scan");
        }

        if (item.DocumentInfo?.Encrypted == true)
        {
            chips.Add("Protected");
        }

        return chips;
    }

    /// <summary>
    /// Small, plain notes about how a document's words were read. A photo with no words is not a problem, so
    /// "no text found" is only ever a neutral note on a document, never an error.
    /// </summary>
    public static IReadOnlyList<string> TextNotes(LocalAssetDto item, bool isDocument = true)
    {
        var info = item.DocumentInfo;
        var notes = new List<string>();
        if (info is null)
        {
            if (isDocument)
            {
                notes.Add("Its words have not been read yet.");
            }

            return notes;
        }

        if (info.Encrypted)
        {
            notes.Add("Password-protected, so its words are not searchable.");
        }
        else
        {
            switch (info.TextSource)
            {
                case "ocr":
                    notes.Add("Text read by OCR.");
                    break;
                case "mixed":
                    notes.Add("Text read from the file and by OCR.");
                    break;
                case "none" when isDocument:
                    notes.Add("No searchable words were found in this file.");
                    break;
                case null when isDocument:
                    notes.Add("Its words have not been read yet.");
                    break;
            }
        }

        if (info.TextTruncated)
        {
            notes.Add("It is very long, so only the first part of its text is searchable.");
        }

        return notes;
    }

    /// <summary>Splits a snippet into plain and highlighted pieces without ever treating the text as markup.</summary>
    public static IReadOnlyList<(string Text, bool Highlight)> SnippetParts(string? snippet)
    {
        var parts = new List<(string, bool)>();
        if (string.IsNullOrEmpty(snippet))
        {
            return parts;
        }

        var highlight = false;
        var start = 0;
        for (var index = 0; index <= snippet.Length; index++)
        {
            var boundary = index == snippet.Length || snippet[index] is HighlightStart or HighlightEnd;
            if (!boundary)
            {
                continue;
            }

            if (index > start)
            {
                parts.Add((snippet[start..index], highlight));
            }

            if (index < snippet.Length)
            {
                highlight = snippet[index] == HighlightStart;
            }

            start = index + 1;
        }

        return parts;
    }
}
