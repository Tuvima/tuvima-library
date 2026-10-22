using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Intelligence.Tests;

/// <summary>
/// Minimal <see cref="IConfigurationLoader"/> stub for scoring engine tests.
/// Returns defaults for all methods — no file I/O.
/// </summary>
internal sealed class StubConfigurationLoader : IConfigurationLoader
{
    public PipelineConfiguration PipelineConfiguration { get; init; } = new();
    public IReadOnlyList<ProviderConfiguration> ProviderConfigurations { get; init; } = [];

    public CoreConfiguration LoadCore() => new();
    public void SaveCore(CoreConfiguration config) { }
    public ScoringSettings LoadScoring() => new();
    public void SaveScoring(ScoringSettings settings) { }
    public MaintenanceSettings LoadMaintenance() => new();
    public void SaveMaintenance(MaintenanceSettings settings) { }
    public HydrationSettings LoadHydration() => new();
    public void SaveHydration(HydrationSettings settings) { }
    public PipelineConfiguration LoadPipelines() => PipelineConfiguration;
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
    public IReadOnlyList<ProviderConfiguration> LoadAllProviders() => ProviderConfigurations;
    public T? LoadConfig<T>(string subdirectory, string name) where T : class => null;
    public void SaveConfig<T>(string subdirectory, string name, T config) where T : class { }
    public T? LoadAi<T>() where T : class => default;
    public void SaveAi<T>(T settings) where T : class { }
    public PaletteConfiguration LoadPalette() => new();
    public void SavePalette(PaletteConfiguration palette) { }
}
