namespace MediaEngine.Web.Components.Settings;

/// <summary>
/// The one plain line the folder step shows about subfolders (Library pages D23): in Watch they give structure, in Listen and Read they are
/// scanned and stay grouped with their folder, and in View they are scanned when "Include subfolders" is on and photos are shown by date.
/// The Create library dialog and the Add folder wizard both draw it from here, so the wording is written once.
/// </summary>
public static class SubfolderHints
{
    /// <summary>The line for an area (watch, listen, read or view), or null for an area with none.</summary>
    public static string? For(string? area) => area?.Trim().ToLowerInvariant() switch
    {
        "watch" => "Subfolders give structure: a folder of several videos is a series, folders inside it are sections, and a folder with one video stays on its own.",
        "listen" => "Audio in subfolders is scanned too and stays grouped with the folder it sits in.",
        "read" => "Books and comics in subfolders are scanned too and stay grouped with the folder they sit in.",
        "view" => "Subfolders are scanned when Include subfolders is on, and photos are shown by date.",
        _ => null,
    };
}
