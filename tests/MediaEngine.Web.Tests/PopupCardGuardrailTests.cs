namespace MediaEngine.Web.Tests;

/// <summary>
/// One popup style (one-look spec D7, WP1b): a Razor file that draws a popup (<c>&lt;dialog&gt;</c>, <c>AppDialogFrame</c> or
/// <c>role="dialog"</c>) must draw its card with the shared popup components (<c>AppDialogShell</c>, <c>AppDialog</c> or
/// <c>AppHostedDialog</c>). The listed files are the exceptions, each with the reason it is not a plain popup card.
/// </summary>
public sealed class PopupCardGuardrailTests
{
    private static readonly IReadOnlyDictionary<string, string> Exceptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // The shared popup components themselves.
        ["Components/Shared/AppDialogFrame.razor"] = "The shared native dialog element every popup sits in.",
        ["Components/Shared/AppDialogHost.razor"] = "Hosts the dialogs the service opens.",
        ["Components/Shared/AppRowMenu.razor"] = "The shared row menu; its phone sheet.",
        ["Components/Shared/AppDrawer.razor"] = "Side drawer, not a popup (drawers follow the table and menu rules).",
        // Full-screen viewers and readers (Group E).
        ["Components/Pages/BookReader.razor"] = "Full-screen reader.",
        ["Components/Pages/ViewDocumentViewer.razor"] = "Full-screen document viewer.",
        ["Components/Shared/MediaViewerShell.razor"] = "Full-screen media viewer.",
        ["Components/Vault/VaultImageViewer.razor"] = "Full-screen image viewer.",
        // Playback popovers and sheets (Group E).
        ["Components/Shared/PlaybackPopover.razor"] = "Playback popover.",
        ["Components/Shared/PlaybackSeekRail.razor"] = "Seek rail.",
        // Phone bottom sheets (Group E): phone sheet behaviour, button order already cancel then main.
        ["Components/Browse/BrowseFilterSheet.razor"] = "Phone filter sheet (Clear left, Show right).",
        ["Components/Pages/ViewDocumentsPage.razor"] = "Phone view-options sheet.",
        ["Components/Navigation/PhoneNavigationMenu.razor"] = "Phone navigation sheet.",
        ["Components/Cinematic/PhoneHeroCard.razor"] = "Phone hero actions sheet.",
        ["Components/Details/OverflowActionMenu.razor"] = "Phone details actions sheet.",
        // Editors (Group C): their layout belongs to the edit panels work; they use the shared close prompt.
        ["Components/MediaEditor/SharedEntityEditorWorkspace.razor"] = "Editor workspace.",
        ["Components/Profiles/ProfileEditorSheet.razor"] = "Profile editor sheet.",
        ["Components/Details/DetailsOverlayHost.razor"] = "The details page overlay host.",
        // Special layouts that keep their behaviour.
        ["Components/Layout/ReconnectModal.razor"] = "Connection-lost modal; keeps its special behaviour.",
        ["Components/Search/UniversalSearchOverlay.razor"] = "Large search overlay; keeps its search layout.",
        // Not yet moved onto the card (one-look follow-up): multi-step sheets with their own layout.
        ["Components/Settings/SecuritySettingsPage.razor"] = "TO MOVE: security popup with several steps.",
        ["Components/Settings/ProfileAccessSheet.razor"] = "TO MOVE: Libraries and lanes sheet.",
    };

    private static readonly string[] CardMarkers = ["<AppDialogShell", "<AppDialog ", "<AppHostedDialog"];

    [Fact]
    public void EveryPopupIsDrawnWithTheSharedCardOrIsListedAsAnException()
    {
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");
        var offenders = Popups(web)
            .Where(file => !Exceptions.ContainsKey(file.Relative) && !CardMarkers.Any(marker => file.Source.Contains(marker, StringComparison.Ordinal)))
            .Select(file => file.Relative)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Draw this popup with AppDialogShell, AppDialog or AppHostedDialog (one-look spec D7), or list it with a reason: " + string.Join(", ", offenders));
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
