namespace MediaEngine.Web.Tests;

/// <summary>
/// One popup style (one-look spec D7, WP1b): a Razor file that draws a popup (<c>&lt;dialog&gt;</c>, <c>AppDialogFrame</c> or
/// <c>role="dialog"</c>) must draw its card with the shared popup component (<c>AppDialogShell</c>). The listed files are the exceptions, each with the reason it is not a plain popup card.
/// </summary>
public sealed class PopupCardGuardrailTests
{
    private static readonly IReadOnlyDictionary<string, string> Exceptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // The shared popup components themselves.
        ["Components/Shared/AppDialogFrame.razor"] = "The shared native dialog element every popup sits in.",
        ["Components/Shared/AppViewer.razor"] = "The shared full-screen Viewer (Look v3 D14); draws the dialog element for the details overlay.",
        ["Components/Shared/AppDialogHost.razor"] = "Hosts the dialogs the service opens.",
        ["Components/Shared/AppDialogShell.razor"] = "The shared window card itself; it draws the native frame around the card when a page opens it inline (Framed).",
        ["Components/Shared/AppRowMenu.razor"] = "The shared row menu; its phone sheet.",
        ["Components/Shared/AppOverflowMenu.razor"] = "The shared overflow menu; its phone sheet.",
        ["Components/Shared/AppDrawer.razor"] = "Side drawer, not a popup (drawers follow the table and menu rules).",
        // Full-screen viewers and readers (Group E).
        // Playback popovers and sheets (Group E).
        // Phone bottom sheets (Group E): phone sheet behaviour, button order already cancel then main.
        ["Components/Browse/BrowseFilterSheet.razor"] = "Phone filter sheet (Clear left, Show right).",
        ["Components/Pages/ViewDocumentsPage.razor"] = "Phone view-options sheet.",
        ["Components/Navigation/PhoneNavigationMenu.razor"] = "Phone navigation sheet.",
        ["Components/Details/OverflowActionMenu.razor"] = "Phone details actions sheet.",
        // Editors (Group C): their layout belongs to the edit panels work; they use the shared close prompt.
        ["Components/Editor/ViewEditorPopover.razor"] = "The photos, videos, documents and galleries editor pop-over (spec photos-documents-editor D1): a native modal dialog drawn once by the layout around the shared editor window (EditorWindowChrome), exactly as the library editor is; it draws no frame of its own, and Esc and Discard use the shared unsaved-changes prompt.",
        ["Components/Editor/EditorPopover.razor"] = "The universal editor pop-over (v3 spec D1, D6): a native modal dialog drawn once by the layout around the editor window, with a tool rail and a fixed Cancel and Save footer; its layout belongs to the editor, and Esc and Cancel use the shared unsaved-changes prompt.",
        ["Components/Settings/Libraries/LibraryFoldersPopover.razor"] = "A small anchored list under the folder-count button (Libraries and folder picker D3); a phone shows it as a bottom sheet.",
        ["Components/Profiles/ProfileEditorSheet.razor"] = "Profile editor: the shared Window frame around its own avatar, PIN and delete form.",
        // Special layouts that keep their behaviour.
        ["Components/Layout/ReconnectModal.razor"] = "Connection-lost modal; keeps its special behaviour.",
    };

    private static readonly string[] CardMarkers = ["<AppDialogShell"];

    [Fact]
    public void EveryPopupIsDrawnWithTheSharedCardOrIsListedAsAnException()
    {
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");
        var offenders = Popups(web)
            .Where(file => !Exceptions.ContainsKey(file.Relative) && !CardMarkers.Any(marker => file.Source.Contains(marker, StringComparison.Ordinal)))
            .Select(file => file.Relative)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Draw this popup with AppDialogShell (one-look spec D7), or list it with a reason: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheExceptionListHasNoStaleEntries()
    {
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");
        var popups = Popups(web).Select(file => file.Relative).ToHashSet(StringComparer.Ordinal);
        var stale = Exceptions.Keys.Where(relative => !popups.Contains(relative)).ToList();

        Assert.True(stale.Count == 0, "No longer draws a popup, remove from the list: " + string.Join(", ", stale));
    }

    private static IEnumerable<(string Relative, string Source)> Popups(string web) =>
        Directory.EnumerateFiles(Path.Combine(web, "Components"), "*.razor", SearchOption.AllDirectories)
            .Select(path => (Relative: Path.GetRelativePath(web, path).Replace('\\', '/'), Source: File.ReadAllText(path)))
            .Where(file => file.Source.Contains("<dialog", StringComparison.Ordinal)
                           || file.Source.Contains("<AppDialogFrame", StringComparison.Ordinal)
                           || file.Source.Contains("role=\"dialog\"", StringComparison.Ordinal)
                           || file.Source.Contains("role=\"alertdialog\"", StringComparison.Ordinal));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
