namespace MediaEngine.Web.Tests;

/// <summary>
/// One menu look and one text alignment for every "..." and dropdown menu (one-look spec D3, WP3): a menu is drawn by the shared
/// overflow menu, row menu and menu item, so a new screen cannot build its own centred-text menu. A <c>role="menu"</c> element
/// is allowed only in the shared menu components and in the listed surfaces, each with the reason it is not a plain action menu.
/// </summary>
public sealed class MenuMarkupGuardrailTests
{
    private static readonly IReadOnlyDictionary<string, string> Allowed = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Components/Shared/AppOverflowMenu.razor"] = "The shared menu.",
        ["Components/Shared/AppRowMenu.razor"] = "The shared row menu (its phone sheet).",
        ["Components/Details/ManageActionsMenu.razor"] = "The shared details action list (icons, left-aligned, destructive last).",
        ["Components/Details/OverflowActionMenu.razor"] = "Phone bottom sheet of the details actions; keeps the sheet behaviour.",
        ["Components/Cinematic/PhoneHeroCard.razor"] = "Phone bottom sheet of the hero actions; keeps the sheet behaviour.",
        ["Components/Navigation/TopNavAccountMenu.razor"] = "Account panel with a header and a secure-account card, not a plain action list.",
        ["Components/Shared/MediaRateControl.razor"] = "Two thumb icons that open on hover, not a text menu.",
        ["Components/Listen/SongActions.razor"] = "The player's song menu, a playback popover (Group E); it uses the shared menu items.",
    };

    [Fact]
    public void OnlyTheSharedMenuComponentsAndTheListedSurfacesDrawARoleMenuElement()
    {
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");
        var offenders = Directory.EnumerateFiles(Path.Combine(web, "Components"), "*.razor", SearchOption.AllDirectories)
            .Select(path => (Path: path, Relative: Path.GetRelativePath(web, path).Replace('\\', '/')))
            .Where(file => File.ReadAllText(file.Path).Contains("role=\"menu\"", StringComparison.Ordinal))
            .Select(file => file.Relative)
            .Where(relative => !Allowed.ContainsKey(relative))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Draw menus with AppOverflowMenu, AppRowMenu and AppMenuItem (one-look spec D3), or add the surface to the list with a reason: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void TheListedSurfacesStillDrawAMenu()
    {
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");
        var stale = Allowed.Keys
            .Where(relative => !File.Exists(Path.Combine(web, relative)) || !File.ReadAllText(Path.Combine(web, relative)).Contains("role=\"menu\"", StringComparison.Ordinal))
            .ToList();

        Assert.True(stale.Count == 0, "Remove from the list, it no longer draws a role=\"menu\": " + string.Join(", ", stale));
    }

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
