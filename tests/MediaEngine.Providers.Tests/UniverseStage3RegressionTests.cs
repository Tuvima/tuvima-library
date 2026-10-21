using System.Reflection;
using System.Runtime.CompilerServices;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Adapters;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging.Abstractions;
using ProviderConfiguration = MediaEngine.Domain.Configuration.ProviderConfiguration;

namespace MediaEngine.Providers.Tests;

public sealed class UniverseStage3RegressionTests
{
    [Theory]
    [InlineData(EntityType.Character)]
    [InlineData(EntityType.Location)]
    [InlineData(EntityType.Organization)]
    [InlineData(EntityType.Event)]
    [InlineData(EntityType.Object)]
    public void ReconciliationAdapter_CanHandle_Stage3FictionalEntityTypes(EntityType entityType)
    {
        var adapter = CreateAdapter();

        Assert.True(adapter.CanHandle(entityType));
    }

    [Fact]
    public void MetadataHarvestingService_BuildLookupRequest_PreservesHydrationPass()
    {
        var service = (MetadataHarvestingService)RuntimeHelpers.GetUninitializedObject(typeof(MetadataHarvestingService));
        var configField = typeof(MetadataHarvestingService).GetField(
            "_configLoader",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(configField);
        configField!.SetValue(service, new StubConfigurationLoader());

        var method = typeof(MetadataHarvestingService).GetMethod(
            "BuildLookupRequest",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var request = new HarvestRequest
        {
            EntityId = Guid.NewGuid(),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.TV,
            Pass = HydrationPass.Universe,
            Hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["title"] = "Breaking Bad",
            },
        };

        var lookup = method!.Invoke(service, [request, new StubExternalMetadataProvider(), "https://wikidata.test", null]);

        var providerLookup = Assert.IsType<ProviderLookupRequest>(lookup);
        Assert.Equal(HydrationPass.Universe, providerLookup.HydrationPass);
    }

    private static ReconciliationAdapter CreateAdapter()
    {
        var config = new ReconciliationProviderConfig
        {
            Reconciliation = new ReconciliationSettings
            {
                ReviewThreshold = 55,
            },
        };

        return new ReconciliationAdapter(
            config,
            new StubHttpClientFactory(),
            NullLogger<ReconciliationAdapter>.Instance,
            new StubFuzzyMatchingService());
    }

    private sealed class StubExternalMetadataProvider : IExternalMetadataProvider
    {
        public string Name => "stub_provider";
        public ProviderDomain Domain => ProviderDomain.Universal;
        public IReadOnlyList<string> CapabilityTags => [];
        public Guid ProviderId => Guid.NewGuid();
        public bool CanHandle(MediaType mediaType) => true;
        public bool CanHandle(EntityType entityType) => true;
        public Task<IReadOnlyList<ProviderClaim>> FetchAsync(ProviderLookupRequest request, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ProviderClaim>>([]);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class StubFuzzyMatchingService : IFuzzyMatchingService
    {
        public double ComputeTokenSetRatio(string a, string b) => 0.0;
        public double ComputePartialRatio(string a, string b) => 0.0;
        public FieldMatchResult ScoreCandidate(LocalMetadata local, CandidateMetadata candidate) => new();
    }

    private sealed class StubConfigurationLoader : IConfigurationLoader
    {
        public PipelineConfiguration LoadPipelines() => new();
        public HydrationSettings LoadHydration() => new();
        public IReadOnlyList<ProviderConfiguration> LoadAllProviders() => [];
        public ScoringSettings LoadScoring() => new();
        public T? LoadConfig<T>(string subdirectory, string name) where T : class => default;
        public CoreConfiguration LoadCore() => new()
        {
            Country = "US",
            Language = new LanguagePreferences
            {
                Metadata = "en",
            },
        };
        public void SaveCore(CoreConfiguration config) => throw new NotImplementedException();
        public void SaveScoring(ScoringSettings settings) => throw new NotImplementedException();
        public MaintenanceSettings LoadMaintenance() => throw new NotImplementedException();
        public void SaveMaintenance(MaintenanceSettings settings) => throw new NotImplementedException();
        public void SaveHydration(HydrationSettings settings) => throw new NotImplementedException();
        public void SavePipelines(PipelineConfiguration config) => throw new NotImplementedException();
        public DisambiguationSettings LoadDisambiguation() => throw new NotImplementedException();
        public void SaveDisambiguation(DisambiguationSettings settings) => throw new NotImplementedException();
        public TranscodingSettings LoadTranscoding() => throw new NotImplementedException();
        public void SaveTranscoding(TranscodingSettings settings) => throw new NotImplementedException();
        public MediaTypeConfiguration LoadMediaTypes() => throw new NotImplementedException();
        public void SaveMediaTypes(MediaTypeConfiguration config) => throw new NotImplementedException();
        public LibrariesConfiguration LoadLibraries() => throw new NotImplementedException();
        public FieldPriorityConfiguration LoadFieldPriorities() => throw new NotImplementedException();
        public void SaveFieldPriorities(FieldPriorityConfiguration config) => throw new NotImplementedException();
        public ProviderConfiguration? LoadProvider(string name) => throw new NotImplementedException();
        public void DeleteProvider(string name) { }
        public void SaveProvider(ProviderConfiguration config) => throw new NotImplementedException();
        public T? LoadAi<T>() where T : class => throw new NotImplementedException();
        public void SaveAi<T>(T settings) where T : class => throw new NotImplementedException();
        public PaletteConfiguration LoadPalette() => throw new NotImplementedException();
        public void SavePalette(PaletteConfiguration palette) => throw new NotImplementedException();
        public void SaveConfig<T>(string subdirectory, string name, T config) where T : class => throw new NotImplementedException();
    }
}
