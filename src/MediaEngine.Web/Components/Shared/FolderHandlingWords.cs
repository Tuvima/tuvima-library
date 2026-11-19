namespace MediaEngine.Web.Components.Shared;

/// <summary>
/// The two plain words for how Library uses a folder (Libraries v3 D19), used by the folder window, the Edit window, the folder pop-over
/// and the Folders tab so every place says the same thing.
/// </summary>
public static class FolderHandlingWords
{
    public const string ReadOnlyKey = "read_only";
    public const string ManagedKey = "managed";

    public const string ReadOnly = "Read-only";
    public const string Managed = "Managed";

    public const string ReadOnlyExplainer = "Library only reads this folder. Your files are never changed.";
    public const string ManagedExplainer = "Library keeps this folder tidy: new files are filed and named here.";

    /// <summary>The one line under the compact Read-only / Managed choice in the folder picker (issue 1041).</summary>
    public const string ChoiceHelp = "Read-only never changes your files. Managed can move and rename them.";
    public const string ReadOnlyOnlyHelp = "Read-only never changes your files.";

    /// <summary>The tooltips beside "Folder access" (issue 1155); they replace the help line that used to change the size of the window.</summary>
    public const string ReadOnlyTooltip = "Files stay exactly as they are. Library only reads them. Uploads need a Managed folder.";
    public const string ManagedTooltip = "Library may rename and move files here to keep them tidy, and new uploads are saved here.";
    public const string ManagedUndoWarning = "Renames and moves are not undone automatically.";

    /// <summary>"38 items" for a folder row; a dash when the Engine could not count; "1000+ items" at the Engine's cap.</summary>
    public static string ItemsText(int? count) => count switch
    {
        null => "—",
        1 => "1 item",
        >= 1000 => "1000+ items",
        _ => $"{count} items",
    };

    /// <summary>Free space on a drive in the short form the folder list uses ("212 GB").</summary>
    public static string FormatFree(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        return value >= 100 || unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }

    /// <summary>Heads up shown when Managed is chosen on a folder that already holds files.</summary>
    public const string ManagedOnFilesWarning = "Library will move and rename files here. Choose Read-only to leave them alone.";

    public static string LabelFor(bool managed) => managed ? Managed : ReadOnly;

    public static string ExplainerFor(bool managed) => managed ? ManagedExplainer : ReadOnlyExplainer;

    /// <summary>The last part of a path, read at either kind of slash (the server may be Windows or Linux), cut to the folder-name limit.</summary>
    public static string NameFromPath(string? path, int maxLength = MaxFolderNameLength)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var last = path.TrimEnd('\\', '/').Split('\\', '/')[^1];
        if (last.EndsWith(':'))
        {
            // A bare drive root (C:\) is a place, not a name: the friendly name stays empty.
            return string.Empty;
        }

        return last.Length > maxLength ? last[..maxLength] : last;
    }

    public const int MaxFolderNameLength = 60;
}
