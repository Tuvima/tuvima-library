using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// One browser frame (universal editor v5 D32, WP8a, acceptance A13): the supporting-files browser and the server folder picker are both the shared
/// <c>AppBrowserFrame</c>, and a list of folders is drawn only by its folder row (<c>AppBrowserFolderRow</c>) inside it. The signal is markup, not
/// intent: a Razor file outside the frame's own parts that carries the retired folder-list class names, or that walks a server folder listing
/// without handing each folder to <c>AppBrowserFolderRow</c>, or that draws a listbox, grid or option element whose own tag names a folder, is a second folder list.
/// A file the rule predates is listed with a reason; the list only shrinks (a stale entry fails too).
/// </summary>
public sealed class BrowserFrameGuardrailTests
{
    // The frame's own parts, by exact file name: only these may draw a folder row, a list or the retired class names.
    private static readonly string[] FrameParts =
    [
        "AppBrowserFrame.razor",
        "AppBrowserColumn.razor",
        "AppBrowserFolderRow.razor",
        "AppBrowserItemRow.razor",
        "AppBrowserRowDetail.razor",
        "AppBrowserSideItem.razor",
        "AppBrowserSideSection.razor",
    ];

    // The class names the two old folder lists drew their rows and lists with; a new list must not bring them back.
    private static readonly string[] RetiredFolderListMarkup =
    [
        "server-folder-picker__row",
        "server-folder-picker__folder",
        "server-folder-picker__list",
        "tl-fbfolders__list",
        "tl-fbfolders__row",
        "tl-fbfolders__entry",
        "app-browser-folder",
    ];

    // Files that draw a folder list of their own and predate the frame. Moving one onto the frame removes its entry.
    private static readonly IReadOnlyDictionary<string, string> LegacyFolderLists = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Components/Settings/Libraries/AddFilesDialog.razor"] = "Add files: picks files (not a folder) on the server and copies them; its rows are a tick list of folders to open and files to tick. It is a file chooser, not a folder picker, and was not part of the D32 scope.",
    };

    [Fact]
    public void AFolderListIsDrawnOnlyByTheSharedFrameAndItsFolderRow()
    {
        var offenders = Razor()
            .Where(file => !IsFramePart(file.Relative) && !LegacyFolderLists.ContainsKey(file.Relative))
            .Where(file => DrawsAFolderList(file.Source))
            .Select(file => file.Relative)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Draw folders as AppBrowserFolderRow rows inside AppBrowserFrame (universal editor v5 D32), or list the file with a reason: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheLegacyListHasNoStaleEntries()
    {
        var current = Razor().ToDictionary(file => file.Relative, file => file.Source, StringComparer.Ordinal);
        var stale = LegacyFolderLists.Keys
            .Where(key => !current.TryGetValue(key, out var source) || !DrawsAFolderList(source))
            .ToList();

        Assert.True(stale.Count == 0, "Remove these from the list, they now use the shared frame or no longer draw a folder list: " + string.Join(", ", stale));
    }

    [Fact]
    public void TheSupportingFilesBrowserAndTheFolderPickerBothDrawTheSharedFrame()
    {
        var files = Razor().ToDictionary(file => file.Relative, file => file.Source, StringComparer.Ordinal);

        Assert.Contains("<AppBrowserFrame", files["Components/Shared/ServerFolderPicker.razor"], StringComparison.Ordinal);
        Assert.Contains("<AppBrowserFolderRow", files["Components/Shared/ServerFolderPicker.razor"], StringComparison.Ordinal);
        Assert.Contains("<AppBrowserFrame", files["Components/Editor/EditorFileBrowserDialog.razor"], StringComparison.Ordinal);
        Assert.Contains("<AppBrowserItemRow", files["Components/Editor/EditorFileBrowserTable.razor"], StringComparison.Ordinal);
        Assert.Contains("<AppBrowserFrame", files["Components/Editor/EditorFileBrowserFolders.razor"], StringComparison.Ordinal);
        Assert.Contains("<AppBrowserFolderRow", files["Components/Editor/EditorFileBrowserFolders.razor"], StringComparison.Ordinal);
    }

    [Fact]
    public void TheFramesListIsTheOnlyGridTheBrowsersDraw()
    {
        var offenders = Razor()
            .Where(file => file.Relative.Contains("EditorFileBrowser", StringComparison.Ordinal) || file.Relative.EndsWith("/ServerFolderPicker.razor", StringComparison.Ordinal))
            .Where(file => file.Source.Contains("<table", StringComparison.Ordinal))
            .Select(file => file.Relative)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void ADetectorFlagsASecondFolderListAndLeavesTheFrameUsersAlone()
    {
        const string retiredRow = "<div class=\"server-folder-picker__row\"><button class=\"server-folder-picker__folder\">@folder.Name</button></div>";
        const string ownList = "<ul role=\"listbox\" aria-label=\"Folders\">@foreach (var folder in _browse.Directories) { <li role=\"option\">@folder.Name</li> }</ul>";
        const string walksWithoutTheRow = "@foreach (var folder in browse.Directories) { <li>@folder.Name</li> }";
        const string usesTheFrame = "<AppBrowserFrame><Rows>@foreach (var folder in _browse.Directories) { <AppBrowserFolderRow Name=\"@folder.Name\" /> }</Rows></AppBrowserFrame>";
        const string unrelatedListbox = "<ul role=\"listbox\" aria-label=\"Languages\"><li role=\"option\">English</li></ul>";

        Assert.True(DrawsAFolderList(retiredRow));
        Assert.True(DrawsAFolderList(ownList));
        Assert.True(DrawsAFolderList(walksWithoutTheRow));
        Assert.False(DrawsAFolderList(usesTheFrame));
        Assert.False(IsFramePart("Components/Settings/AppBrowserLookalike.razor"));
        Assert.False(IsFramePart("Components/Shared/AppBrowserSomethingElse.razor"));
        Assert.True(IsFramePart("Components/Shared/AppBrowserFolderRow.razor"));
        Assert.False(DrawsAFolderList(unrelatedListbox));
    }

    // The documented signal: (1) the retired folder-list class names, (2) a walk over a server folder listing (.Directories, FolderEntries) that never hands
    // the folders to AppBrowserFolderRow, (3) a listbox, grid or option element whose own tag names a folder.
    private static bool DrawsAFolderList(string source)
    {
        if (RetiredFolderListMarkup.Any(marker => source.Contains(marker, StringComparison.Ordinal)))
        {
            return true;
        }

        var walksFolders = Regex.IsMatch(source, @"\bin\s+[\w.!]*(Directories|FolderEntries)\b") || Regex.IsMatch(source, @"\b(Directories|FolderEntries)\[");
        if (walksFolders && !source.Contains("<AppBrowserFolderRow", StringComparison.Ordinal))
        {
            return true;
        }

        // A list element (listbox, grid or option) whose own tag names a folder: its class, id or label says it is the folder list.
        var namesItselfAFolderList = Regex.IsMatch(
            source,
            "<[a-zA-Z]+\\s[^<>]*?role=\"(listbox|grid|option)\"[^<>]*?folder[^<>]*>|<[a-zA-Z]+\\s[^<>]*?folder[^<>]*?role=\"(listbox|grid|option)\"[^<>]*>",
            RegexOptions.IgnoreCase);
        return namesItselfAFolderList && !source.Contains("<AppBrowserFolderRow", StringComparison.Ordinal);
    }

    private static bool IsFramePart(string relative) =>
        relative.StartsWith("Components/Shared/", StringComparison.Ordinal) && FrameParts.Contains(Path.GetFileName(relative), StringComparer.Ordinal);

    private static IEnumerable<(string Relative, string Source)> Razor()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "MediaEngine.slnx")))
        {
            root = Path.GetDirectoryName(root);
        }

        Assert.NotNull(root);
        var web = Path.Combine(root!, "src", "MediaEngine.Web");
        foreach (var path in Directory.EnumerateFiles(web, "*.razor", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(web, path).Replace('\\', '/');
            if (relative.StartsWith("obj/", StringComparison.Ordinal) || relative.StartsWith("bin/", StringComparison.Ordinal))
            {
                continue;
            }

            yield return (relative, File.ReadAllText(path));
        }
    }
}
