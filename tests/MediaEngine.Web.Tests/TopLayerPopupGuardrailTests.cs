namespace MediaEngine.Web.Tests;

/// <summary>
/// Pop-ups draw in the top layer (issue 704 part c, Look pass v2): a component that declares a menu, list box or pop-up
/// button must draw it through the shared popover, overflow menu, row menu or dialog frame, so no table row, card or scroll
/// container can clip it. The listed files predate this rule and still draw their own panel; the list only shrinks.
/// </summary>
public sealed class TopLayerPopupGuardrailTests
{
    private static readonly string[] SharedMarkers =
        ["<AppPopover", "<AppOverflowMenu", "<AppRowMenu", "<AppDialogFrame", "popover="];

    private static readonly IReadOnlyDictionary<string, string> LegacyOwnPanels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Components/Details/ManageActionsMenu.razor"] = "Details actions menu.",
        ["Components/Listen/SongActions.razor"] = "Song actions in the player.",
        ["Components/Shared/PlaybackItemMenu.razor"] = "Item actions menu of the bar; PlaybackPanel draws it in the top layer, this file only holds the rows.",
        ["Components/Listen/PlaybackLyricsMenu.razor"] = "Rows of the Lyrics footer's menu; PlaybackPanel draws the menu in the top layer, this file only holds the rows.",
        ["Components/Listen/PlaybackLyricsFooter.razor"] = "Holds the Lyrics footer's \"...\" button; PlaybackPanel draws the menu it opens in the top layer.",
        ["Components/Shared/PlaybackRowMenu.razor"] = "The \"...\" menu of a Queue or History row; PlaybackPanel draws it in the top layer, this file only holds the rows.",
        ["Components/Shared/MediaRateControl.razor"] = "Rate control flyout.",
        ["Components/Shared/ServerFolderPicker.razor"] = "Server folder picker.",
    };

    [Fact]
    public void EveryPopupPanelIsDrawnInTheTopLayerOrIsListed()
    {
        var offenders = PopupFiles()
            .Where(file => !LegacyOwnPanels.ContainsKey(file.Relative) && !SharedMarkers.Any(marker => file.Source.Contains(marker, StringComparison.Ordinal)))
            .Select(file => file.Relative)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Draw this pop-up through AppPopover, AppOverflowMenu, AppRowMenu or AppDialogFrame so it cannot be clipped (issue 704), or list it with a reason: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheLegacyListHasNoStaleEntries()
    {
        var current = PopupFiles().ToDictionary(file => file.Relative, file => file.Source, StringComparer.Ordinal);
        var stale = LegacyOwnPanels.Keys
            .Where(key => !current.TryGetValue(key, out var source) || SharedMarkers.Any(marker => source.Contains(marker, StringComparison.Ordinal)))
            .ToList();

        Assert.True(stale.Count == 0, "Remove these from the list, they now use the top layer or no longer draw a pop-up: " + string.Join(", ", stale));
    }

    private static IEnumerable<(string Relative, string Source)> PopupFiles()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "MediaEngine.slnx")))
        {
            root = Path.GetDirectoryName(root);
        }

        Assert.NotNull(root);
        var web = Path.Combine(root!, "src", "MediaEngine.Web");
        foreach (var path in Directory.EnumerateFiles(Path.Combine(web, "Components"), "*.razor", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(path);
            if (source.Contains("role=\"menu\"", StringComparison.Ordinal)
                || source.Contains("role=\"listbox\"", StringComparison.Ordinal)
                || source.Contains("aria-haspopup", StringComparison.Ordinal))
            {
                yield return (Path.GetRelativePath(web, path).Replace('\\', '/'), source);
            }
        }
    }
}
