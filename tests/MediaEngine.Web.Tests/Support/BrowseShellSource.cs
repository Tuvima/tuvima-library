namespace MediaEngine.Web.Tests;

/// <summary>
/// The browse shell's source as one text: its markup (localized like any component) followed by its code files
/// (<c>MediaBrowseShell.*.cs</c>), so a guardrail that pins a line of the shell keeps finding it wherever the line now lives.
/// </summary>
internal static class BrowseShellSource
{
    public static string ReadAll()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        var folder = Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("Repository root not found."),
            "src", "MediaEngine.Web", "Components", "Browse");
        var parts = new List<string> { LocalizedSource.ReadAllText(Path.Combine(folder, "MediaBrowseShell.razor")) };
        parts.AddRange(Directory.EnumerateFiles(folder, "MediaBrowseShell*.cs")
            .Order(StringComparer.Ordinal)
            .Select(File.ReadAllText));
        return string.Join("\n", parts);
    }
}
