namespace MediaEngine.Web.Tests;

public sealed class ArtworkProviderPagingUiTests
{
    [Fact]
    public void ProviderPickerCarriesServerContextAcrossExplicitPages()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "Artwork", "ArtworkAssetPickerDialog.razor"));

        Assert.Contains("Label=\"@(_loadingMore ? \"Loading…\" : \"Load more\")\"", source, StringComparison.Ordinal);
        Assert.Contains("_discovery.HasMore", source, StringComparison.Ordinal);
        Assert.Contains("ProviderArtworkDiscoveryRequestDto", source, StringComparison.Ordinal);
        Assert.Contains("ProviderItemId: context?.ProviderItemId", source, StringComparison.Ordinal);
        Assert.Contains("ReleaseId: context?.ReleaseId", source, StringComparison.Ordinal);
        Assert.Contains("OrderContext: context?.OrderContext", source, StringComparison.Ordinal);
        Assert.Contains("result.Context != existingContext", source, StringComparison.Ordinal);
        Assert.Contains("Reload provider results before continuing", source, StringComparison.Ordinal);
        Assert.Contains("Matched ID @providerContext.ProviderItemId", source, StringComparison.Ordinal);
        Assert.Contains("DistinctBy(candidate => candidate.Id", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
