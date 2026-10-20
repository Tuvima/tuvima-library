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

    [Fact]
    public void ReplacingNavigationsToADetailAddressAreOnlyMadeThroughTheOverlayState()
    {
        var offenders = new List<string>();
        foreach (var file in SourceFiles().Where(file => file.RelativePath != "Services/Navigation/DetailsOverlayState.cs"))
        {
            // The replace is made by the overlay state (DetailsOverlayNavigation) so that it carries its history state.
            var inDetails = file.RelativePath.StartsWith("Components/Details/", StringComparison.Ordinal);
            foreach (Match match in ReplacingNavigation().Matches(file.Source))
            {
                // A reader's return address (returnUrl) is its title's detail address.
                if (inDetails
                    || match.Value.Contains("/details", StringComparison.OrdinalIgnoreCase)
                    || match.Value.Contains("returnUrl", StringComparison.Ordinal))
                {
                    offenders.Add($"{file.RelativePath}: {match.Value.Trim()}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A replace to a detail address must go through DetailsOverlayNavigation (it cannot be re-issued by the overlay): "
            + string.Join("; ", offenders));
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

    // A NavigateTo(...) statement that replaces the current history entry.
    [GeneratedRegex(@"NavigateTo\([^;]*?replace:\s*true[^;]*;", RegexOptions.Singleline)]
    private static partial Regex ReplacingNavigation();
}
