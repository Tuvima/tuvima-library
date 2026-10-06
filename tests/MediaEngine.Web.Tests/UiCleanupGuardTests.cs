namespace MediaEngine.Web.Tests;

public sealed class UiCleanupGuardTests
{
    [Fact]
    public void SettingsLibraries_UsesSharedClassesForCardsAndPreview()
    {
        var source = ReadRepoFile(@"src\MediaEngine.Web\Components\Settings\LibrariesTab.razor");

        Assert.Contains("<AppCard", source, StringComparison.Ordinal);
        Assert.Contains("Density=\"AppSurfaceDensity.Compact\"", source, StringComparison.Ordinal);
        Assert.Contains("<AppPanel", source, StringComparison.Ordinal);
        Assert.Contains("<AppPageState", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LibraryCardStyle", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PreviewStyle", source, StringComparison.Ordinal);
        Assert.DoesNotContain("tl-card--compact", source, StringComparison.Ordinal);
        Assert.DoesNotContain("tl-panel", source, StringComparison.Ordinal);
        Assert.DoesNotContain("font-size: 3rem", source, StringComparison.Ordinal);
        Assert.DoesNotContain("font-weight: 600", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicationsAccess_UsesSharedControlsAndScopedCredentialCode()
    {
        var source = ReadRepoFile(@"src\MediaEngine.Web\Components\Settings\ManagedAccessApplications.razor");

        Assert.Contains("<AppButton", source, StringComparison.Ordinal);
        Assert.Contains("access-credential-secret", source, StringComparison.Ordinal);
        Assert.Contains("<AppFocusTrap", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<MudButton", source, StringComparison.Ordinal);
        Assert.DoesNotContain("style=\"font-family: monospace", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BrowseShell_DelegatesQueryAndArtworkRules()
    {
        var source = ReadRepoFile(@"src\MediaEngine.Web\Components\Browse\MediaBrowseShell.razor");

        Assert.Contains("BrowseQueryBuilder.Read", source, StringComparison.Ordinal);
        Assert.Contains("BrowseArtworkRules.ResolveWideArtwork", source, StringComparison.Ordinal);
        Assert.DoesNotContain("private static string? ResolveWideArtwork", source, StringComparison.Ordinal);
        Assert.DoesNotContain("private static IReadOnlyList<string> CompactFacts", source, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(string relativePath) =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath)));
}
