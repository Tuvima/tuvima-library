namespace MediaEngine.Api.Services.LocalAssets.FileWrites;

/// <summary>How a View file can carry the name of the profile that added it (batched write-back D7).</summary>
public enum ViewProfileNameWriter
{
    /// <summary>Written by ExifTool into the file's own XMP.</summary>
    ExifTool,

    /// <summary>Written by Open XML as a custom document property.</summary>
    OpenXml,

    /// <summary>This kind of file cannot hold the name; the name stays in Library only.</summary>
    KeptInLibrary,
}

/// <summary>Which View file formats get the profile name, and with which tool (batched write-back D7).</summary>
public static class ViewProfileNameFormats
{
    /// <summary>The tag ExifTool writes (defined in <c>config/exiftool/tuvima.config</c>).</summary>
    public const string ExifToolTag = "XMP-tuvima:AddedBy";

    /// <summary>The custom document property Open XML writes into Word, Excel and PowerPoint files.</summary>
    public const string OfficePropertyName = "Tuvima Added By";

    public const string KeptFormatReason = "Kept in Library only: this kind of file can't hold the profile name.";
    public const string KeptSignedPdfReason = "Kept in Library only: the PDF is signed or encrypted.";
    public const string KeptSignedOfficeReason = "Kept in Library only: the Office file is signed or encrypted.";
    public const string KeptCopiedReason = "Kept in Library only: another copy of this file is also in Library.";

    private static readonly HashSet<string> ExifToolExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".gif", ".webp", ".heic", ".heif", ".avif",
        ".dng", ".cr2", ".cr3", ".nef", ".arw", ".mp4", ".mov", ".m4v", ".pdf",
    };

    private static readonly HashSet<string> OpenXmlExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".xlsx", ".pptx",
    };

    /// <summary>The writer for a file with this extension (with or without the dot, any case).</summary>
    public static ViewProfileNameWriter For(string? extension)
    {
        var dotted = string.IsNullOrEmpty(extension) || extension[0] == '.' ? extension ?? string.Empty : "." + extension;
        if (ExifToolExtensions.Contains(dotted))
        {
            return ViewProfileNameWriter.ExifTool;
        }

        return OpenXmlExtensions.Contains(dotted) ? ViewProfileNameWriter.OpenXml : ViewProfileNameWriter.KeptInLibrary;
    }

    /// <summary>The profile name as it is written: one line, with no leading or trailing spaces. Empty when nothing is left.</summary>
    public static string Clean(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return string.Empty;
        }

        var chars = displayName.Select(character => char.IsControl(character) ? ' ' : character).ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
