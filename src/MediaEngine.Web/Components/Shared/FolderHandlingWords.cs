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
            last = last.TrimEnd(':');
        }

        return last.Length > maxLength ? last[..maxLength] : last;
    }

    public const int MaxFolderNameLength = 60;
}
