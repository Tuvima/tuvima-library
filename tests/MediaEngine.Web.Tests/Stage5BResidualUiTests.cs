using MediaEngine.Domain;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Caching.Memory;

namespace MediaEngine.Web.Tests;

public sealed class Stage5BResidualUiTests
{
    private static readonly string RepoRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Theory]
    [InlineData(@"src/MediaEngine.Web/Components/Collections/CollectionsPage.razor")]
    [InlineData(@"src/MediaEngine.Web/Components/Pages/EpubReader.razor")]
    [InlineData(@"src/MediaEngine.Web/Components/Pages/ForMe.razor")]
    [InlineData(@"src/MediaEngine.Web/Components/Pages/Settings.razor")]
    [InlineData(@"src/MediaEngine.Web/Components/Pages/WatchPlayerPage.razor")]
    public void PageSizedLoadingStates_UseAppPageState(string relativePath)
    {
        var source = Read(relativePath);

        Assert.Contains("<AppPageState Kind=\"AppPageStateKind.Loading\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<MudProgressCircular", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<AppSpinner", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"src/MediaEngine.Web/Components/Pages/EpubReader.razor")]
    [InlineData(@"src/MediaEngine.Web/Components/Pages/ForMe.razor")]
    [InlineData(@"src/MediaEngine.Web/Components/Pages/WatchPlayerPage.razor")]
    public void RetryableFailures_KeepRicherAppErrorState(string relativePath)
    {
        var source = Read(relativePath);

        Assert.Contains("<AppErrorState", source, StringComparison.Ordinal);
        Assert.Contains("Retry=", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderCatalogueService_OwnsOfflinePresentationFallbacks()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new ProviderCatalogueService(EngineApiClientStub.CreateDefault(), cache);

        Assert.Equal("Apple API", service.GetDisplayName("apple_api"));
        Assert.Equal("#01B4E4", service.GetAccentColor("TMDB"));
        Assert.NotEmpty(service.GetMaterialIcon("Open Library"));
        Assert.Equal("Apple", ProviderCatalogueService.FormatProviderLabel("apple_api"));
        Assert.Equal("File Scan", ProviderCatalogueService.FormatSourceName("local_filesystem"));
        Assert.Equal(
            "Manual Match",
            ProviderCatalogueService.FormatProviderName(WellKnownProviders.UserManual));
    }

    [Fact]
    public void RetiredProviderPresentationMaps_DoNotReturn()
    {
        Assert.False(File.Exists(Path.Combine(
            RepoRoot,
            @"src/MediaEngine.Web/Models/ProviderAccentMap.cs")));
        Assert.False(File.Exists(Path.Combine(
            RepoRoot,
            @"src/MediaEngine.Web/Components/Shared/ProviderDisplayNames.cs")));

        var productionSources = Directory
            .EnumerateFiles(
                Path.Combine(RepoRoot, @"src/MediaEngine.Web"),
                "*.*",
                SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                           || path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase));
        var combined = string.Join('\n', productionSources.Select(File.ReadAllText));

        Assert.DoesNotContain("ProviderAccentMap", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("ProviderDisplayNames", combined, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"src/MediaEngine.Web/Services/MediaTiles/CollectionSurfaceTileComposer.cs")]
    public void ResidualTileCallers_DelegateArtworkSelection(string relativePath)
    {
        var source = Read(relativePath);

        Assert.Contains("MediaTileArtworkResolver.Resolve(", source, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot, relativePath));
}
