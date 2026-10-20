using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// While the details overlay is open the address bar names the detail item, so a routed page that binds the address
/// bar directly with <c>[SupplyParameterFromQuery]</c> would lose its filters and search under the overlay. Routed
/// pages read their query from <c>RoutedPageQuery</c>, the address they were routed with (see
/// DetailsOverlayQueryProtectionTests for what the framework binding does).
/// </summary>
public sealed partial class RoutedPageQueryGuardrailTests
{
    private static readonly string WebRoot = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");

    [Fact]
    public void NoRoutedPageBindsTheAddressBarWithSupplyParameterFromQuery()
    {
        var offenders = RoutedPageSources()
            .Where(page => SupplyParameterRegex().IsMatch(page.Source))
            .Select(page => page.RelativePath)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Routed pages must read their query from RoutedPageQuery (cascaded by DetailsOverlayRouteView), not [SupplyParameterFromQuery]: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void RoutedPagesAreFoundAcrossBothRazorAndCodeBehindFiles()
    {
        var pages = RoutedPageSources().Select(page => page.RelativePath).ToList();

        Assert.Contains("Components/Pages/Collections.razor", pages);
        Assert.Contains("Components/Pages/ListenPage.razor", pages);
        Assert.Contains("Components/Pages/ShelfGridPage.razor", pages);
    }

    // A routed page's source is its @page file plus a code-behind beside it (Page.razor.cs).
    private static IEnumerable<(string RelativePath, string Source)> RoutedPageSources()
    {
        foreach (var path in Directory.EnumerateFiles(WebRoot, "*.razor", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var relative = Path.GetRelativePath(WebRoot, path).Replace(Path.DirectorySeparatorChar, '/');
            var source = File.ReadAllText(path);
            // Only components with an @page directive are routed pages. (Settings/LibraryFoldersPanel is a child of the Settings
            // page and still binds the address bar; it is converted separately.)
            if (!PageDirectiveRegex().IsMatch(source))
            {
                continue;
            }

            var codeBehind = path + ".cs";
            yield return (relative, File.Exists(codeBehind) ? source + Environment.NewLine + File.ReadAllText(codeBehind) : source);
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

    [GeneratedRegex(@"^\s*@page\s+""", RegexOptions.Multiline)]
    private static partial Regex PageDirectiveRegex();

    [GeneratedRegex(@"\bSupplyParameterFromQuery\b")]
    private static partial Regex SupplyParameterRegex();
}
