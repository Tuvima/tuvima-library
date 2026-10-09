using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Intelligence.Contracts;
using MediaEngine.Intelligence.Models;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using ProviderConfiguration = MediaEngine.Domain.Configuration.ProviderConfiguration;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// Exercises the actual Stage 3 harvesting persistence path. Adapter-level evidence
/// tests alone are insufficient because event chronology must become canonical data
/// before the shared editor can project it onto the in-universe timeline.
/// </summary>
public sealed class MetadataHarvestingScalarEvidencePersistenceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly DatabaseConnection _database;

    public MetadataHarvestingScalarEvidencePersistenceTests()
    {
        DapperConfiguration.Configure();
        _tempRoot = Path.Combine(Path.GetTempPath(), $"tuvima_scalar_evidence_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _database = new DatabaseConnection(Path.Combine(_tempRoot, "library.db"));
        _database.InitializeSchema();
        _database.RunStartupChecks();
    }

    public void Dispose()
    {
        try { _database.Dispose(); } catch { }
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task ProcessSynchronousAsync_PersistsDirectEventChronologyEvidenceAsCanonicalValues()
    {
        var eventId = Guid.NewGuid();
        var entities = new FictionalEntityRepository(_database);
        await entities.CreateAsync(new FictionalEntity
        {
            Id = eventId,
            WikidataQid = "Q-event",
            Label = "Battle of the Red Dunes",
            EntitySubType = FictionalEntityType.Event,
            FictionalUniverseQid = "Q-universe",
            FictionalUniverseLabel = "The Red Dunes",
        });

        var claims = new MetadataClaimRepository(_database);
        var canonicals = new CanonicalValueRepository(_database);
        var provider = new EventEvidenceProvider();
        var service = new MetadataHarvestingService(
            [provider],
            claims,
            canonicals,
            new PersonRepository(_database),
            entities,
            new NoOpRelationshipPopulationService(),
            new MetadataHarvestQueue(),
            new DeterministicScoringEngine(),
            new NoOpEventPublisher(),
            new SingleProviderConfigurationLoader(provider.Name),
            new NoOpHttpClientFactory(),
            new ImageCacheRepository(_database),
            new SystemActivityRepository(_database),
            new QidLabelRepository(_database),
            new AssetPathService(_tempRoot),
            NullLogger<MetadataHarvestingService>.Instance,
            entityAssetRepo: new EntityAssetRepository(_database));

        await service.ProcessSynchronousAsync(new HarvestRequest
        {
            EntityId = eventId,
            EntityType = EntityType.Event,
            MediaType = MediaType.Unknown,
            Hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [BridgeIdKeys.WikidataQid] = "Q-event",
                ["entity_sub_type"] = FictionalEntityType.Event,
                ["universe_qid"] = "Q-universe",
                ["label"] = "Battle of the Red Dunes",
            },
        });

        var persistedClaims = await claims.GetByEntityAsync(eventId);
        Assert.Contains(persistedClaims, claim => claim.ClaimKey == "point_in_time" && claim.ClaimValue == "+0019-01-01T00:00:00Z");
        Assert.Contains(persistedClaims, claim => claim.ClaimKey == "start_time" && claim.ClaimValue == "+0018-01-01T00:00:00Z");
        Assert.Contains(persistedClaims, claim => claim.ClaimKey == "end_time" && claim.ClaimValue == "+0020-01-01T00:00:00Z");
        Assert.Contains(persistedClaims, claim => claim.ClaimKey == "time_index" && claim.ClaimValue == "battle-12");

        var persistedCanonicals = (await canonicals.GetByEntityAsync(eventId))
            .ToDictionary(value => value.Key, value => value.Value, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("+0019-01-01T00:00:00Z", persistedCanonicals["point_in_time"]);
        Assert.Equal("+0018-01-01T00:00:00Z", persistedCanonicals["start_time"]);
        Assert.Equal("+0020-01-01T00:00:00Z", persistedCanonicals["end_time"]);
        Assert.Equal("battle-12", persistedCanonicals["time_index"]);

        var enriched = await entities.FindByIdAsync(eventId);
        Assert.NotNull(enriched);
        Assert.Equal("Battle of the Red Dunes", enriched!.Label);
        Assert.Equal("A decisive fictional battle.", enriched.Description);
        Assert.Equal("https://commons.wikimedia.org/battle.jpg", enriched.ImageUrl);
        Assert.NotNull(enriched.EnrichedAt);
        var managedArtwork = await new EntityAssetRepository(_database).GetByEntityAsync(
            eventId.ToString("D"), AssetType.CharacterPortrait.ToString());
        var portrait = Assert.Single(managedArtwork);
        Assert.True(File.Exists(portrait.LocalImagePath));
        using var verify = _database.CreateConnection();
        Assert.Equal(1, await verify.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM artwork_asset_context
            WHERE entity_id=@eventId AND entity_type='FictionalEntity' AND role='Portrait';
            """, new { eventId }));
    }

    private sealed class EventEvidenceProvider : IExternalMetadataProvider, IFictionalEntityGraphEvidenceProvider
    {
        public string Name => "wikidata_reconciliation";
        public ProviderDomain Domain => ProviderDomain.Universal;
        public IReadOnlyList<string> CapabilityTags => [];
        public Guid ProviderId { get; } = WellKnownProviders.Wikidata;

        public bool CanHandle(MediaType mediaType) => true;
        public bool CanHandle(EntityType entityType) => entityType == EntityType.Event;

        public Task<IReadOnlyList<ProviderClaim>> FetchAsync(ProviderLookupRequest request, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProviderClaim>>(
            [
                new("name", "Battle of the Red Dunes", 0.95),
                new(MetadataFieldConstants.ShortDescription, "A decisive fictional battle.", 0.9),
                new("image_url", "https://commons.wikimedia.org/battle.jpg", 0.9),
            ]);

        public Task<FictionalEntityGraphEvidence?> FetchFictionalEntityGraphEvidenceAsync(
            string qid,
            string entitySubType,
            CancellationToken ct = default) =>
            Task.FromResult<FictionalEntityGraphEvidence?>(new FictionalEntityGraphEvidence(
                SourceProvider: Name,
                Provenance: "Wikidata",
                NarrativeUniverseQid: "Q-universe",
                NarrativeUniverseLabel: "The Red Dunes",
                Statements: [],
                ScalarStatements:
                [
                    new("point_in_time", "+0019-01-01T00:00:00Z", "Time", 0.9, []),
                    new("start_time", "+0018-01-01T00:00:00Z", "Time", 0.9, []),
                    new("end_time", "+0020-01-01T00:00:00Z", "Time", 0.9, []),
                    new("time_index", "battle-12", "Text", 0.9, []),
                ]));
    }

    private sealed class DeterministicScoringEngine : IScoringEngine
    {
        public Task<ScoringResult> ScoreEntityAsync(ScoringContext context, CancellationToken ct = default) =>
            Task.FromResult(new ScoringResult
            {
                EntityId = context.EntityId,
                OverallConfidence = 0.9,
                ScoredAt = DateTimeOffset.UtcNow,
                FieldScores = context.Claims
                    .GroupBy(claim => claim.ClaimKey, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.OrderByDescending(claim => claim.Confidence).First())
                    .Select(claim => new FieldScore
                    {
                        Key = claim.ClaimKey,
                        WinningValue = claim.ClaimValue,
                        Confidence = claim.Confidence,
                        WinningProviderId = claim.ProviderId,
                    })
                    .ToList(),
            });

        public async Task<IReadOnlyList<ScoringResult>> ScoreBatchAsync(
            IEnumerable<ScoringContext> contexts,
            CancellationToken ct = default) =>
            await Task.WhenAll(contexts.Select(context => ScoreEntityAsync(context, ct)));
    }

    private sealed class NoOpRelationshipPopulationService : IRelationshipPopulationService
    {
        public Task PopulateAsync(
            string entityQid,
            IReadOnlyDictionary<string, string> canonicalValues,
            string universeQid,
            string? universeLabel,
            string? contextWorkQid = null,
            IReadOnlyDictionary<string, (string? StartTime, string? EndTime)>? temporalQualifiers = null,
            IReadOnlyList<FictionalEntityRelationshipStatement>? statementEvidence = null,
            int currentDepth = 0,
            int maxDepth = 1,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoOpEventPublisher : IEventPublisher
    {
        public Task PublishAsync<TPayload>(string eventName, TPayload payload, CancellationToken ct = default)
            where TPayload : notnull => Task.CompletedTask;
    }

    private sealed class NoOpHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new ImageHandler());

        private sealed class ImageHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var bytes = Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes),
                });
            }
        }
    }

    private sealed class SingleProviderConfigurationLoader(string providerName) : IConfigurationLoader
    {
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
        public ProviderConfiguration? LoadProvider(string name) =>
            string.Equals(name, providerName, StringComparison.OrdinalIgnoreCase)
                ? new ProviderConfiguration { Name = providerName, Enabled = true, Weight = 1.0 }
                : null;
        public void SaveProvider(ProviderConfiguration config) { }
        public IReadOnlyList<ProviderConfiguration> LoadAllProviders() =>
            [new ProviderConfiguration { Name = providerName, Enabled = true, Weight = 1.0 }];
        public T? LoadConfig<T>(string subdirectory, string name) where T : class => null;
        public void SaveConfig<T>(string subdirectory, string name, T config) where T : class { }
        public T? LoadAi<T>() where T : class => null;
        public void SaveAi<T>(T settings) where T : class { }
        public PaletteConfiguration LoadPalette() => new();
        public void SavePalette(PaletteConfiguration palette) { }
    }
}
