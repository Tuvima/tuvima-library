using MediaEngine.Api.Services;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Tests;

public sealed class ConfiguredIdentityBypassLibrariesTests
{
    // The real loader refuses local-only and manual policies on catalogued libraries, so the configuration is faked.
    [Fact]
    public void OnlyLocalOnlyAndManualLibraries_AreListed_NotTheOthers()
    {
        var loader = new LibrariesOnlyLoader(
            Library("a", LibraryMetadataPolicies.Enriched),
            Library("b", LibraryMetadataPolicies.LocalOnly),
            Library("c", LibraryMetadataPolicies.Manual),
            Library("d", LibraryMetadataPolicies.LocalPreferred));

        var ids = new ConfiguredIdentityBypassLibraries(loader).GetLibraryIds();

        Assert.Equal(["b", "c"], ids.Order().ToArray());
    }

    [Fact]
    public void AChangedConfiguration_AppliesOnTheNextRead()
    {
        var loader = new LibrariesOnlyLoader(Library("b", LibraryMetadataPolicies.LocalOnly));
        var resolver = new ConfiguredIdentityBypassLibraries(loader);
        Assert.Equal(["b"], resolver.GetLibraryIds().ToArray());

        loader.Libraries = new LibrariesConfiguration { Libraries = [Library("b", LibraryMetadataPolicies.Enriched)] };

        Assert.Empty(resolver.GetLibraryIds());
    }

    private static LibraryFolderConfig Library(string id, string policy) =>
        new() { Id = id, Name = id, MetadataPolicy = policy };

    private sealed class LibrariesOnlyLoader : IConfigurationLoader
    {
        public LibrariesOnlyLoader(params LibraryFolderConfig[] libraries) =>
            Libraries = new LibrariesConfiguration { Libraries = [.. libraries] };

        public LibrariesConfiguration Libraries { get; set; }

        public LibrariesConfiguration LoadLibraries() => Libraries;

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
        public ProviderConfiguration? LoadProvider(string name) => null;
        public void SaveProvider(ProviderConfiguration config) { }
        public IReadOnlyList<ProviderConfiguration> LoadAllProviders() => [];
        public T? LoadConfig<T>(string subdirectory, string name) where T : class => null;
        public void SaveConfig<T>(string subdirectory, string name, T config) where T : class { }
        public T? LoadAi<T>() where T : class => default;
        public void SaveAi<T>(T settings) where T : class { }
        public PaletteConfiguration LoadPalette() => new();
        public void SavePalette(PaletteConfiguration palette) { }
    }
}
