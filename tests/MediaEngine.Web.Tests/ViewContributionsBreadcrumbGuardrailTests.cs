namespace MediaEngine.Web.Tests;

public sealed class ViewContributionsBreadcrumbGuardrailTests
{
    [Fact]
    public void ContributionsPage_PreservesSharedLibraryBreadcrumbHierarchy()
    {
        var source = Read(@"src/MediaEngine.Web/Components/Pages/ViewContributionsPage.razor");

        Assert.Contains("<AppBreadcrumbs Class=\"view-contributions-breadcrumbs\">", source, StringComparison.Ordinal);
        Assert.Contains("<a href=\"/view\">View</a>", source, StringComparison.Ordinal);
        Assert.Contains("<a href=\"/view?scope=shared\">Shared Library</a>", source, StringComparison.Ordinal);
        Assert.Contains("<span aria-current=\"page\">Contributions</span>", source, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath)));
}
