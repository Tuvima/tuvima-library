using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The provider-key to product-name lookup reads the config folder once and again only after a config file reloads (universal editor v7 D4),
/// and the History lines a saved match writes name the product, never the key.
/// </summary>
public sealed class EditorProviderNamesCacheTests
{
    private static ProviderConfiguration Apple(string productName) => new()
    {
        Name = "apple_api",
        DisplayName = "Apple API",
        ProductName = productName,
        ProductNameByKind = new Dictionary<string, string> { ["album"] = "Apple Music" },
    };

    [Fact]
    public void ManyLookups_ReadTheProviderConfigsOnlyOnce()
    {
        var loader = new CountingLoader([Apple("Apple Books")]);
        var names = new EditorProviderNames(loader);

        for (var lookup = 0; lookup < 25; lookup++)
        {
            Assert.Equal("Apple Books", names.ProductName("apple_api"));
        }

        Assert.Equal(1, loader.LoadCount);
    }

    [Fact]
    public void AReloadedConfigFile_MakesTheNextLookupReadTheProvidersAgain()
    {
        var loader = new CountingLoader([Apple("Apple Books")]);
        var names = new EditorProviderNames(loader);
        Assert.Equal("Apple Books", names.ProductName("apple_api"));

        loader.Providers = [Apple("Apple Library")];
        loader.RaiseChanged("providers/apple_api.json", applied: true);

        Assert.Equal("Apple Library", names.ProductName("apple_api"));
        Assert.Equal(2, loader.LoadCount);
    }

    [Fact]
    public void ARejectedConfigReload_KeepsTheProvidersAlreadyRead()
    {
        var loader = new CountingLoader([Apple("Apple Books")]);
        var names = new EditorProviderNames(loader);
        Assert.Equal("Apple Books", names.ProductName("apple_api"));

        loader.RaiseChanged("providers/apple_api.json", applied: false);

        Assert.Equal("Apple Books", names.ProductName("apple_api"));
        Assert.Equal(1, loader.LoadCount);
    }

    [Fact]
    public void HistoryNames_UseTheProductNameForTheKindOfItem_AndNeverTheKey()
    {
        var names = new EditorProviderNames(new CountingLoader([Apple("Apple Books")]));

        Assert.Equal("Apple Books", names.NameForHistory("apple_api", "book"));
        Assert.Equal("Apple Music", names.NameForHistory("apple_api", "album"));
        Assert.Equal(EditorProviderNames.UnknownProviderText, names.NameForHistory("mystery_api", "album"));
    }

    [Fact]
    public void TheHistoryLinesOfAReplacedMatch_NameTheProductAndNotTheProviderKey()
    {
        var names = new EditorProviderNames(new CountingLoader([Apple("Apple Books")]));
        var provider = names.NameForHistory("apple_api");

        var replaced = ItemCanonicalEndpoints.RetailMatchReplacedDetail(provider, "id-9");
        var changed = ItemCanonicalEndpoints.RetailIdentityChangedDetail(provider, "id-9", "Cover replaced.", "Refreshed the album.");
        var changedWithoutArtwork = ItemCanonicalEndpoints.RetailIdentityChangedDetail(provider, "id-9", null, null);

        Assert.Equal("Retail match replaced with Apple Books id-9.", replaced);
        Assert.Equal("Retail identity changed to Apple Books id-9. Cover replaced. Refreshed the album.", changed);
        Assert.Equal("Retail identity changed to Apple Books id-9.", changedWithoutArtwork);
        Assert.DoesNotContain("apple_api", replaced + changed + changedWithoutArtwork, StringComparison.Ordinal);
    }

    private sealed class CountingLoader(IReadOnlyList<ProviderConfiguration> providers) : IConfigurationLoader
    {
        public int LoadCount { get; private set; }

        public IReadOnlyList<ProviderConfiguration> Providers { get; set; } = providers;

        public event EventHandler<ConfigurationFileChangedEventArgs>? ConfigurationChanged;

        public void RaiseChanged(string relativePath, bool applied) =>
            ConfigurationChanged?.Invoke(this, new ConfigurationFileChangedEventArgs(relativePath, relativePath, applied, null));

        public IReadOnlyList<ProviderConfiguration> LoadAllProviders()
        {
            LoadCount++;
            return Providers;
        }

        public string ConfigDirectoryPath => string.Empty;
        public CoreConfiguration LoadCore() => new();
        public void SaveCore(CoreConfiguration config) { }
        public ScoringSettings LoadScoring() => new();
        public void SaveScoring(ScoringSettings settings) { }
        public MaintenanceSettings LoadMaintenance() => new();
        public void SaveMaintenance(MaintenanceSettings settings) { }
        public HydrationSettings LoadHydration() => new();
        public void SaveHydration(HydrationSettings settings) { }
        public PipelineConfiguration LoadPipelines() => new();
        public void SavePipelines(PipelineConfiguration config) { }
        public DisambiguationSettings LoadDisambiguation() => new();
        public void SaveDisambiguation(DisambiguationSettings settings) { }
        public MediaTypeConfiguration LoadMediaTypes() => new();
        public void SaveMediaTypes(MediaTypeConfiguration config) { }
        public TranscodingSettings LoadTranscoding() => new();
        public void SaveTranscoding(TranscodingSettings settings) { }
        public FieldPriorityConfiguration LoadFieldPriorities() => new();
        public void SaveFieldPriorities(FieldPriorityConfiguration config) { }
        public LibrariesConfiguration LoadLibraries() => new();
        public ProviderConfiguration? LoadProvider(string name) => null;
        public void DeleteProvider(string name) { }
        public void SaveProvider(ProviderConfiguration config) { }
        public T? LoadConfig<T>(string subdirectory, string name) where T : class => null;
        public void SaveConfig<T>(string subdirectory, string name, T config) where T : class { }
        public T? LoadAi<T>() where T : class => default;
        public void SaveAi<T>(T settings) where T : class { }
        public PaletteConfiguration LoadPalette() => new();
        public void SavePalette(PaletteConfiguration palette) { }
    }
}
