using MediaEngine.Contracts.Library;
using MediaEngine.Web.Services.Formatting;

namespace MediaEngine.Web.Components.Settings;

/// <summary>The words of Add files (Libraries spec D8): what a file of this library is called, the selection line and the result summary.</summary>
public static class AddFilesWording
{
    /// <summary>The refusal shown when the library has no import destination yet.</summary>
    public const string ChooseImportFolderFirst = "Choose an import folder first";

    /// <summary>The tooltip beside the disabled Add files button.</summary>
    public const string NeedsImportFolderHint = "Choose an import folder first. Add files copies into the import destination: pick a managed folder as the import destination on the Folders tab.";

    /// <summary>The tooltip beside a file the library's content type cannot take.</summary>
    public const string UnsupportedHint = "This kind of file cannot be added to this library.";

    /// <summary>What one file of a library is called, from its media types: "video", "audio file", "book", "comic" and otherwise "file".</summary>
    public static string NounFor(IEnumerable<string> mediaTypes)
    {
        var types = mediaTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (types.Overlaps(["Movies", "TV", "Videos"]))
        {
            return "video";
        }

        if (types.Overlaps(["Music", "Audiobooks"]))
        {
            return "audio file";
        }

        if (types.Contains("Books"))
        {
            return "book";
        }

        if (types.Contains("Comics"))
        {
            return "comic";
        }

        return "file";
    }

    /// <summary>"2 videos selected, 7.8 GB" (nothing selected reads "No videos selected").</summary>
    public static string SelectionText(int count, long bytes, string noun)
    {
        if (count == 0)
        {
            return $"No {Plural(noun, 2)} selected";
        }

        var size = DisplayFormat.FormatFileSize(bytes);
        var head = $"{DisplayFormat.FormatCount(count)} {Plural(noun, count)} selected";
        return size is null ? head : $"{head}, {size}";
    }

    /// <summary>"Copied 3. Already in your library: 2. Could not be read: 1." Parts that are zero are left out; all zero reads "Nothing was copied."</summary>
    public static string SummaryText(LibraryAddFilesResultDto result)
    {
        var parts = new List<string>();
        if (result.Copied > 0)
        {
            parts.Add($"Copied {DisplayFormat.FormatCount(result.Copied)}");
        }

        if (result.SkippedDuplicates > 0)
        {
            parts.Add($"Already in your library: {DisplayFormat.FormatCount(result.SkippedDuplicates)}");
        }

        if (result.Unreadable > 0)
        {
            parts.Add($"Could not be read: {DisplayFormat.FormatCount(result.Unreadable)}");
        }

        if (result.Unsupported > 0)
        {
            parts.Add($"Not supported: {DisplayFormat.FormatCount(result.Unsupported)}");
        }

        return parts.Count == 0 ? "Nothing was copied." : string.Join(". ", parts) + ".";
    }

    private static string Plural(string noun, int count) => count == 1 ? noun : noun == "audio file" ? "audio files" : noun + "s";
}
