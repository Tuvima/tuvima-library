using MediaEngine.Web.Components.Shared;

namespace MediaEngine.Web.Services.Navigation;

/// <summary>One destination of the primary navigation: the desktop top bar, the phone menu and the phone section chips.</summary>
public readonly record struct ShellNavLink(string Key, string Path, string LabelKey, string Icon);

/// <summary>
/// The one catalogue of primary destinations. The desktop and tablet top bar renders <see cref="TopBarLinks"/>; the
/// phone menu and section chips are generated from the same list, so the three can never drift apart. Lanes (Read,
/// Watch, Listen, View, Collections) take their label and icon from <see cref="ShellDock.LibrarySurfaces"/>, the same
/// source as the TV Libraries switcher.
/// View is one entry: Photos and Documents live inside it, so they are never listed here and a profile that may not
/// see Documents has nothing to hide in the menu, the chips or the dock.
/// </summary>
public static class ShellNavigation
{
    /// <summary>Home, the phone menu's first entry (on desktop the logo is the Home link).</summary>
    public static readonly ShellNavLink Home = new("Home", "/", "Nav_Home", AppMaterialIcons.Outlined.Home);

    // The desktop top bar order. Changing it changes the tablet/desktop bar, the phone menu and the phone chips together.
    private static readonly string[] AdultTopBarPaths = ["/read", "/watch", "/listen", "/view", "/collections"];

    /// <summary>
    /// The lanes in the top bar for this profile. The Kids shell keeps only the lanes a child needs
    /// (the Libraries switcher's Kids rows: Watch, Listen, Read).
    /// </summary>
    public static IReadOnlyList<ShellNavLink> TopBarLinks(bool isKids) => isKids
        ? [.. ShellDock.SwitcherRows(isKids: true).Select(ToLink)]
        : [.. AdultTopBarPaths.Select(path => ToLink(ShellDock.LibrarySurfaces.First(surface => surface.Path == path)))];

    /// <summary>The Vault area (Videos spec D45). Listed only through <see cref="WithVault"/>, which the shell calls while NSFW mode is on.</summary>
    public static readonly ShellNavLink Vault = new("Vault", VaultPath, "Nav_Vault", AppMaterialIcons.Outlined.Lock);

    public const string VaultPath = "/vault";

    /// <summary>
    /// The fourth top-level area joins the end of a list of destinations only while <paramref name="showVault"/> is true, which the
    /// shell sets only for an adult whose Engine-reported NSFW mode is on for this device session. Otherwise the list comes back untouched.
    /// </summary>
    public static IReadOnlyList<ShellNavLink> WithVault(IReadOnlyList<ShellNavLink> links, bool showVault) =>
        showVault ? [.. links, Vault] : links;

    /// <summary>My Library, the phone menu's link to the personal page (the same page as the dock's My Library tab).</summary>
    public static readonly ShellNavLink MyLibrary = new("MyLibrary", ShellDock.MyLibraryPath, "Nav_MyLibrary", AppMaterialIcons.Outlined.LibraryBooks);

    /// <summary>The phone menu's destinations: Home, My Library, Recently added (adults), then the top bar lanes in the top bar's order.</summary>
    public static IReadOnlyList<ShellNavLink> MenuLinks(bool isKids)
    {
        var links = new List<ShellNavLink> { Home, MyLibrary };
        if (!isKids)
        {
            links.Add(ToLink(ShellDock.RecentlyAdded));
        }

        links.AddRange(TopBarLinks(isKids));
        return links;
    }

    /// <summary>The phone section chips: the same lanes as the top bar, in the same order.</summary>
    public static IReadOnlyList<ShellNavLink> SectionChips(bool isKids) => TopBarLinks(isKids);

    /// <summary>True when <paramref name="currentPath"/> is the destination or one of its sub-pages. Home matches only itself.</summary>
    public static bool IsCurrent(string linkPath, string currentPath) =>
        linkPath == "/"
            ? currentPath == "/"
            : currentPath.Equals(linkPath, StringComparison.OrdinalIgnoreCase)
              || currentPath.StartsWith(linkPath + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The phone menu shows Settings only to a profile that has it (not Kids), matching the account menu.</summary>
    public static bool ShowsSettings(bool isKids) => !isKids;

    /// <summary>The phone menu shows Switch profile only when the household has more than one profile, matching the account menu.</summary>
    public static bool ShowsSwitchProfile(int profileCount) => profileCount > 1;

    private static ShellNavLink ToLink(LibrarySurface surface) => new(surface.Key, surface.Path, surface.LabelKey, surface.Icon);
}
