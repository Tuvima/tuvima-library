using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Rules that keep the details overlay (spec D1) honest as the Dashboard grows. The page under the overlay must not
/// react to the address bar naming a title, and a replacing navigation to a detail address must carry its own history state.
/// </summary>
public sealed partial class DetailsOverlayGuardrailTests
{
    private static readonly string WebRoot = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");

    // Every subscription to NavigationManager.LocationChanged outside this list is refused: a component that reacts to the
    // address bar reacts to the overlay opening over it. Each entry says why that is safe.
    private static readonly Dictionary<string, string> LocationChangedAllowlist = new(StringComparer.Ordinal)
    {
        ["Services/Navigation/DetailsOverlayState.cs"] =
            "The overlay's own state: it works out the underlay and overlay for each address.",
        ["Components/Browse/MediaBrowseShell.razor"] =
            "Asks RoutedPageAddress.Accept, which ignores the overlay opening, swapping and closing, so the lane neither re-reads its filters nor reloads.",
        ["Components/MediaHub/MediaSectionShell.razor"] =
            "Asks RoutedPageAddress.Accept, so the overlay does not scroll the section to the top or change its active entry.",
        ["Components/Listen/ListenNowPlayingBar.razor"] =
            "Only re-renders; whether it is on a Listen page comes from RoutedPageAddress.AbsoluteFor (the page under the overlay).",
        ["Components/Settings/Frame/SettingsAttentionBanner.razor"] =
            "Only re-renders; whether it is on the Tools page comes from RoutedPageAddress.AbsoluteFor (the page under the overlay).",
        ["Components/Watch/VideoPlaybackHost.razor"] =
            "Moving away from the player (including opening a title over it) continues the video in picture-in-picture or the mini player, as opening a title always did.",
        ["Shared/MainLayout.razor"] =
            "The shell tracks the page under the overlay as its current page and keeps that page's scroll while the overlay opens, swaps and closes.",
    };

    [Fact]
    public void LocationChangedSubscriptionsAreOnlyInTheReviewedComponents()
    {
        var offenders = SourceFiles()
            .Where(file => LocationChangedSubscription().IsMatch(file.Source))
            .Select(file => file.RelativePath)
            .Where(path => !LocationChangedAllowlist.ContainsKey(path))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Subscribing to NavigationManager.LocationChanged makes a component react to the details overlay opening over it. "
            + "Follow RoutedPageAddress (or read RoutedPageAddress.AbsoluteFor) and add the file to the allowlist with a justification: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void EveryAllowlistedLocationChangedSubscriptionStillExists()
    {
        var subscribed = SourceFiles()
            .Where(file => LocationChangedSubscription().IsMatch(file.Source))
            .Select(file => file.RelativePath)
            .ToHashSet(StringComparer.Ordinal);

        var stale = LocationChangedAllowlist.Keys.Where(path => !subscribed.Contains(path)).ToList();

        Assert.True(stale.Count == 0, "Remove allowlist entries whose subscription is gone: " + string.Join(", ", stale));
        Assert.All(LocationChangedAllowlist.Values, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
    }

    [Fact]
    public void TheShellsInsideRoutedPagesFollowTheRoutedAddressNotTheAddressBar()
    {
        var browse = File.ReadAllText(Path.Combine(WebRoot, "Components", "Browse", "MediaBrowseShell.razor"));
        var section = File.ReadAllText(Path.Combine(WebRoot, "Components", "MediaHub", "MediaSectionShell.razor"));

        Assert.Contains("_address!.Accept(e.Location)", browse, StringComparison.Ordinal);
        Assert.DoesNotContain("BrowseQueryBuilder.Read(Preset, Tab, Nav.Uri)", browse, StringComparison.Ordinal);
        Assert.Contains("_address!.Accept(args.Location)", section, StringComparison.Ordinal);
        Assert.DoesNotContain("Nav.ToAbsoluteUri(Nav.Uri)", section, StringComparison.Ordinal);
    }

    // Every call that replaces the current history entry, per file, and why it can never target a detail address. The overlay
    // cannot re-issue a replace (its location handler is not told whether a navigation replaces), so a replace that can reach
    // /details/ must go through DetailsOverlayNavigation, which gives the new entry its own history state. A new call site, or a
    // changed count, fails until it is reviewed and listed here.
    private static readonly Dictionary<string, (int Count, string Reason)> ReplacingNavigationAllowlist = new(StringComparer.Ordinal)
    {
        ["Services/Vault/VaultExit.cs"] = (1, "Locking returns Home, and a Vault address for anyone not unlocked goes to /not-found; neither can reach a /details/ address."),
        ["Components/Browse/MediaBrowseShell.razor"] = (1, "The lane rewriting its own filter address (a browse route)."),
        ["Components/Navigation/TopNavProfileSwitcher.razor"] = (1, "Full reload to Home after switching profile."),
        ["Components/Pages/BookReader.razor"] = (1, "The reader's no-title exit to the literal Home address; a title exit goes through DetailsOverlayNavigation."),
        ["Components/Pages/EpubReader.razor"] = (1, "The reader's no-title exit to the literal Home address; a title exit goes through DetailsOverlayNavigation."),
        ["Components/Pages/ForMe.razor"] = (2, "Redirects to the My Library page or a View all grid."),
        ["Components/Pages/Operations.razor"] = (2, "Redirects to Settings routes."),
        ["Components/Pages/ProfilePickerPage.razor"] = (2, "Full reload to the sanitized same-site return address after choosing a profile (ProfilePickerRoute)."),
        ["Components/Pages/SearchPage.razor"] = (1, "The search page rewriting its own query."),
        ["Components/Pages/Settings.razor"] = (10, "Settings redirects to Home, /not-found or another Settings route."),
        ["Components/Pages/SetupPage.razor"] = (1, "Full reload to Home or the sign-in page when setup ends."),
        ["Components/Pages/ShelfGridPage.razor.cs"] = (4, "The View all grid rewriting its own sort and filter address."),
        ["Components/Settings/IngestionTasksTab.razor"] = (1, "Redirect to the Activity log route."),
        ["Components/Settings/ProfileDetailPage.razor"] = (2, "Redirect to the Settings profiles list."),
        ["Shared/MainLayout.razor"] = (1, "Full reload to the profile picker."),
        ["Services/Navigation/DetailsOverlayState.cs"] = (4, "The overlay's own replaces (DetailsOverlayNavigation and dismissal), which carry their history state."),
    };

    [Fact]
    public void EveryReplacingNavigationIsReviewedAndNoneThatCanReachADetailAddressBypassesTheOverlayState()
    {
        var found = SourceFiles()
            .Select(file => (file.RelativePath, Count: ReplacingNavigation().Matches(file.Source).Count))
            .Where(file => file.Count > 0)
            .ToDictionary(file => file.RelativePath, file => file.Count, StringComparer.Ordinal);

        var unreviewed = found
            .Where(file => !ReplacingNavigationAllowlist.TryGetValue(file.Key, out var allowed) || allowed.Count != file.Value)
            .Select(file => $"{file.Key} ({file.Value})")
            .ToList();
        var stale = ReplacingNavigationAllowlist.Keys.Where(path => !found.ContainsKey(path)).ToList();

        Assert.True(
            unreviewed.Count == 0,
            "A call that replaces the history entry must be reviewed: if it can target a /details/ address, use DetailsOverlayNavigation; "
            + "otherwise list it (with its count and a reason) in ReplacingNavigationAllowlist: " + string.Join(", ", unreviewed));
        Assert.True(stale.Count == 0, "Remove allowlist entries whose replace is gone: " + string.Join(", ", stale));
        Assert.All(ReplacingNavigationAllowlist.Values, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Reason)));
    }

    private static IEnumerable<(string RelativePath, string Source)> SourceFiles()
    {
        foreach (var path in Directory.EnumerateFiles(WebRoot, "*", SearchOption.AllDirectories)
                     .Where(path => path.EndsWith(".razor", StringComparison.Ordinal) || path.EndsWith(".cs", StringComparison.Ordinal))
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                         && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            yield return (Path.GetRelativePath(WebRoot, path).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllText(path));
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    [GeneratedRegex(@"\.LocationChanged\s*\+=")]
    private static partial Regex LocationChangedSubscription();

    // A NavigateTo(...) statement that replaces the current history entry, or an options object that does.
    [GeneratedRegex(@"NavigateTo\([^;]*?replace:\s*(?!false)[^;]*;|ReplaceHistoryEntry\s*=\s*true", RegexOptions.Singleline)]
    private static partial Regex ReplacingNavigation();
}
