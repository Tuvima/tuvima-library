using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Look pass v3 D17 (Shy, 2026-11-07): no redirects or address forwarding anywhere in the Dashboard. The app is pre-beta, so an old
/// or retired address is simply not found; no page, part or middleware silently takes the person somewhere else. What stays is
/// address replacement that is part of an action (closing the Vault, the details overlay's replace, switching or leaving a
/// profile), a page rewriting its own filter in its own address, and the sign-in and change-password gates. Every one of those is
/// listed below with its reason; a new one fails until it is reviewed and listed.
/// </summary>
public sealed class NoRedirectsGuardrailTests
{
    // Where the Dashboard replaces the current address or issues an HTTP redirect, per file, and why that is an action or a gate
    // and not a forward from an old address.
    private static readonly Dictionary<string, string> ReplaceOrRedirectAllowlist = new(StringComparer.Ordinal)
    {
        ["Services/Vault/VaultExit.cs"] = "Vault exit: closing or locking the Vault returns to a safe address.",
        ["Services/Navigation/DetailsOverlayState.cs"] = "Details overlay replace: the overlay's own replaces (dismissal, and the shown item having been removed).",
        ["Components/Navigation/TopNavProfileSwitcher.razor"] = "Profile switch: full reload to Home after choosing another profile.",
        ["Components/Pages/ProfilePickerPage.razor"] = "Profile switch: full reload to the sanitized return address after choosing a profile.",
        ["Shared/MainLayout.razor"] = "Profile switch: full reload to the profile picker when the signed-in profile must be chosen again.",
        ["Components/Settings/ProfileDetailPage.razor"] = "Leaving a deleted or unavailable profile: back to the profile list.",
        ["Components/Settings/ProfilesTab.razor"] = "Role-dependent landing, not an old address: a member without household management opens Manage Profiles on their own profile page.",
        ["Components/Pages/Settings.razor"] = "Access gate: a Kids profile or a page switched off for this device goes Home. An address that is not a Settings page shows the not-found page in place (no redirect).",
        ["Components/Pages/BookReader.razor"] = "Item removed: the open book left the library while it was open, so the reader leaves to Home (an action's result).",
        ["Components/Pages/SetupPage.razor"] = "Setup ended: full reload to Home or the sign-in page when first-run setup completes.",
        ["Components/Browse/MediaBrowseShell.razor"] = "Own filter: the browse page rewriting its own filter, sort and display choices in its own address.",
        ["Components/Pages/SearchPage.razor"] = "Own filter: the search page rewriting its own query in its own address.",
        ["Components/Pages/ShelfGridPage.razor.cs"] = "Own filter: the View all grid rewriting its own sort and filter address (and dropping a choice the Engine refuses).",
        ["Components/Settings/NeedsReviewPageContent.razor"] = "Own filter: the Needs Review list rewriting its own filter address.",
        ["Components/Settings/Libraries/LibrariesOverview.razor"] = "Own filter: the Libraries overview rewriting its own area chip address.",
        ["Program.cs"] = "Culture cookie: the language change returns to the page the person was on.",
        ["Services/Integration/DashboardAuthenticationEndpoints.cs"] = "Sign-in, sign-out, setup and change-password gates (security, not navigation).",
        ["Services/Integration/PasswordChangeRedirectMiddleware.cs"] = "Change-password gate: a signed-in person who must change their password is held on that page.",
    };

    // Files or declarations named Legacy...Address, ...Redirect... or ...Alias... in the Dashboard, and why each is not an address forward.
    private static readonly Dictionary<string, string> NamedRedirectOrAliasAllowlist = new(StringComparer.Ordinal)
    {
        ["Services/Integration/PasswordChangeRedirectMiddleware.cs"] = "Change-password gate (security).",
        ["Components/Navigation/SessionEndedRedirect.razor"] = "Sign-in gate (security): sends an open screen to the sign-in page when its sign-in is gone.",
        ["Services/Integration/DashboardAuthenticationEndpoints.cs"] = "Sign-in flow constants (SignedOutRedirect).",
        ["Services/Editing/MediaEditorDetailsPresenter.cs"] = "Metadata field aliases (names a field is also known by), not addresses.",
        ["Services/Branding/StreamingServiceLogoResolver.cs"] = "Streaming service name aliases for logos, not addresses.",
        ["Services/Integration/IEngineApiClient.cs"] = "Wikidata aliases of an item (alternate names), not addresses.",
        ["Services/Integration/IEngineApiClient.People.cs"] = "Aliases of a person (alternate names), not addresses.",
        ["Services/Integration/EngineApiClient.SearchMisc.cs"] = "Wikidata aliases of an item (alternate names), not addresses.",
        ["Services/Integration/EngineApiClient.People.cs"] = "Aliases of a person (alternate names), not addresses.",
        ["Services/Integration/UIOrchestratorService.cs"] = "Wikidata aliases of an item (alternate names), not addresses.",
        ["Components/Settings/ProviderTesterToolTab.razor"] = "Provider tester mapping a media type name to its other spelling, not an address.",
        ["Components/Details/SequencePlacementPanel.razor"] = "Alternate names of a series container, not addresses.",
    };

    private static readonly Regex ReplaceOrRedirect = new(
        @"replace:\s*true|Results\.Redirect\(|Response\.Redirect\(|\bRedirectToPage\(|\bLocalRedirect\(",
        RegexOptions.CultureInvariant);

    private static readonly Regex NamedDeclaration = new(
        @"(?<![\w.])[\w<>\[\].?]+\s+([A-Z]\w*(?:Redirect|Alias|Legacy\w*Address)\w*)\s*(?:\(|=>|=\s|\{|;)",
        RegexOptions.CultureInvariant);

    private static readonly Regex NamedFile = new(@"Redirect|Alias|Legacy\w*Address", RegexOptions.CultureInvariant);

    [Fact]
    public void NoPageOnlyForwardsToAnotherAddress()
    {
        var forwards = new List<string>();
        foreach (var (relative, source) in WebSources().Where(file => file.Relative.EndsWith(".razor", StringComparison.Ordinal)))
        {
            if (!Regex.IsMatch(source, @"^@page\b", RegexOptions.Multiline))
            {
                continue;
            }

            var code = source.IndexOf("@code", StringComparison.Ordinal);
            var markup = code >= 0 ? source[..code] : source;
            var drawn = Regex.Replace(markup, @"@\*.*?\*@|^@\w+[^\n]*$", string.Empty, RegexOptions.Multiline | RegexOptions.Singleline).Trim();
            if (drawn.Length == 0 && code >= 0 && source[code..].Contains("NavigateTo(", StringComparison.Ordinal))
            {
                forwards.Add(relative);
            }
        }

        Assert.True(forwards.Count == 0, "A page that only forwards to another address is not allowed (D17): " + string.Join(", ", forwards));
    }

    [Fact]
    public void NoAddressForwardsAnywhere_OutsideTheWrittenAllowlist()
    {
        var found = WebSources()
            .Select(file => (file.Relative, Count: ReplaceOrRedirect.Matches(file.Source).Count))
            .Where(file => file.Count > 0)
            .Select(file => file.Relative)
            .ToHashSet(StringComparer.Ordinal);

        var unlisted = found.Where(path => !ReplaceOrRedirectAllowlist.ContainsKey(path)).OrderBy(path => path, StringComparer.Ordinal).ToList();
        var stale = ReplaceOrRedirectAllowlist.Keys.Where(path => !found.Contains(path)).OrderBy(path => path, StringComparer.Ordinal).ToList();

        Assert.True(unlisted.Count == 0, "A replace or redirect must be an action, an own-address filter or a gate, listed with its reason in ReplaceOrRedirectAllowlist (D17): " + string.Join(", ", unlisted));
        Assert.True(stale.Count == 0, "Remove allowlist entries whose replace or redirect is gone: " + string.Join(", ", stale));
        Assert.All(ReplaceOrRedirectAllowlist.Values, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
    }

    [Fact]
    public void NoFileOrMethodIsNamedLikeAnAddressForward_OutsideTheWrittenAllowlist()
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (relative, source) in WebSources())
        {
            if (NamedFile.IsMatch(Path.GetFileName(relative)) || NamedDeclaration.IsMatch(source))
            {
                found.Add(relative);
            }
        }

        var unlisted = found.Where(path => !NamedRedirectOrAliasAllowlist.ContainsKey(path)).OrderBy(path => path, StringComparer.Ordinal).ToList();
        var stale = NamedRedirectOrAliasAllowlist.Keys.Where(path => !found.Contains(path)).OrderBy(path => path, StringComparer.Ordinal).ToList();

        Assert.True(unlisted.Count == 0, "A file or method named Legacy...Address, ...Redirect... or ...Alias... must be listed with its reason in NamedRedirectOrAliasAllowlist (D17): " + string.Join(", ", unlisted));
        Assert.True(stale.Count == 0, "Remove allowlist entries that no longer match: " + string.Join(", ", stale));
        Assert.All(NamedRedirectOrAliasAllowlist.Values, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
    }

    [Fact]
    public void TheRetiredForwardingAddressesAndHelpersAreGone()
    {
        var root = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");
        Assert.False(File.Exists(Path.Combine(root, "Components", "Pages", "Operations.razor")));

        var all = string.Join("\n", WebSources().Select(file => file.Source));
        foreach (var retired in new[] { "@page \"/operations/", "RenamedTypeKeys", "RedirectSavedLink", "ShouldRedirect", "IsCanonicalRoute", "\"/view/artwork/media\"", "\"/view/artwork/people\"", "\"/view/artwork/universes\"", "\"/view/artwork/assets\"", "\"/settings/account\"" })
        {
            Assert.DoesNotContain(retired, all, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<(string Relative, string Source)> WebSources()
    {
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");
        var sep = Path.DirectorySeparatorChar;
        foreach (var path in Directory.EnumerateFiles(web, "*", SearchOption.AllDirectories)
                     .Where(path => path.EndsWith(".razor", StringComparison.Ordinal) || path.EndsWith(".cs", StringComparison.Ordinal))
                     .Where(path => !path.Contains($"{sep}obj{sep}", StringComparison.Ordinal) && !path.Contains($"{sep}bin{sep}", StringComparison.Ordinal)))
        {
            yield return (Path.GetRelativePath(web, path).Replace(sep, '/'), File.ReadAllText(path));
        }
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
