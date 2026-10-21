using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Intelligence.Contracts;
using MediaEngine.Intelligence.Models;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using ProviderConfiguration = MediaEngine.Domain.Configuration.ProviderConfiguration;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// After scoring, a Work in two Series holds one <c>series_placement</c> row per (Series, position) pair in
/// <c>canonical_value_arrays</c>: value = the position text, value QID = the Series.
/// </summary>
public sealed class SeriesPlacementPersistenceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly DatabaseConnection _database;
    private readonly Guid _provider = WellKnownProviders.Wikidata;

    public SeriesPlacementPersistenceTests()
    {
        DapperConfiguration.Configure();
        _tempRoot = Path.Combine(Path.GetTempPath(), $"tuvima_series_placement_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _database = new DatabaseConnection(Path.Combine(_tempRoot, "library.db"));
        _database.InitializeSchema();
        _database.RunStartupChecks();
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT OR IGNORE INTO metadata_providers (id, name, version, is_enabled) VALUES (@provider, 'placement-test', '1', 1);",
            new { provider = _provider });
    }

    public void Dispose()
    {
        try { _database.Dispose(); } catch (IOException) { }
        try { Directory.Delete(_tempRoot, recursive: true); } catch (IOException) { }
    }

    private async Task<List<(int Ordinal, string Value, string? Qid)>> ScoreAndReadAsync(Guid entityId, params ProviderClaim[] claims)
    {
        await ScoringHelper.PersistClaimsAndScoreAsync(
            entityId,
            claims,
            _provider,
            new MetadataClaimRepository(_database),
            new CanonicalValueRepository(_database),
            new DeterministicScoringEngine(),
            new MinimalConfigLoader(),
            allProviders: [],
            CancellationToken.None,
            arrayRepo: new CanonicalValueArrayRepository(_database));

        using var connection = _database.CreateConnection();
        return connection.Query<(int Ordinal, string Value, string? Qid)>(
            "SELECT ordinal AS Ordinal, value AS Value, value_qid AS Qid FROM canonical_value_arrays WHERE entity_id = @entityId AND key = 'series_placement' ORDER BY ordinal;",
            new { entityId }).ToList();
    }

    private static ProviderClaim[] Pair(string qid, string position) =>
    [
        new(MetadataFieldConstants.SeriesPlacement, position, 0.9),
        new(MetadataFieldConstants.SeriesPlacement + MetadataFieldConstants.CompanionQidSuffix, $"{qid}::{position}", 0.9),
    ];

    [Fact]
    public async Task AWorkInTwoSeries_KeepsBothPairs_WithTheSeriesQidAndThePositionText()
    {
        var rows = await ScoreAndReadAsync(Guid.NewGuid(), [.. Pair("Q111", "3"), .. Pair("Q222", "7.5")]);

        Assert.Equal([(0, "3", "Q111"), (1, "7.5", "Q222")], rows);
    }

    [Fact]
    public async Task TwoSeriesWithTheSamePositionText_BothSurvive_AndNoValueOnlyDuplicateIsAdded()
    {
        var rows = await ScoreAndReadAsync(Guid.NewGuid(), [.. Pair("Q111", "1"), .. Pair("Q222", "1")]);

        Assert.Equal([(0, "1", "Q111"), (1, "1", "Q222")], rows);
    }

    [Fact]
    public async Task AValueOnlyClaimRepeatingAPairsPosition_IsNotAddedBesideThePair()
    {
        var rows = await ScoreAndReadAsync(
            Guid.NewGuid(),
            [.. Pair("Q111", "3"), new ProviderClaim(MetadataFieldConstants.SeriesPlacement, "3", 0.9)]);

        Assert.Equal([(0, "3", "Q111")], rows);
    }

    [Fact]
    public async Task ThePairsNeverReachTheScalarCanonicalTable()
    {
        var entityId = Guid.NewGuid();
        await ScoreAndReadAsync(entityId, [.. Pair("Q111", "3"), new ProviderClaim(MetadataFieldConstants.SeriesPosition, "3", 0.9)]);

        using var connection = _database.CreateConnection();
        var scalarKeys = connection.Query<string>("SELECT key FROM canonical_values WHERE entity_id = @entityId;", new { entityId }).ToList();
        Assert.Contains(MetadataFieldConstants.SeriesPosition, scalarKeys);
        Assert.DoesNotContain(scalarKeys, key => key.StartsWith(MetadataFieldConstants.SeriesPlacement, StringComparison.Ordinal));
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

    private sealed class MinimalConfigLoader : IConfigurationLoader
    {
        public ScoringSettings LoadScoring() => new();
        public IReadOnlyList<ProviderConfiguration> LoadAllProviders() => [];

        public PipelineConfiguration LoadPipelines() => new();
        public HydrationSettings LoadHydration() => new();
        public T? LoadConfig<T>(string subdirectory, string name) where T : class => default;
        public CoreConfiguration LoadCore() => throw new NotImplementedException();
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
        public void SaveProvider(ProviderConfiguration config) => throw new NotImplementedException();
        public T? LoadAi<T>() where T : class => throw new NotImplementedException();
        public void SaveAi<T>(T settings) where T : class => throw new NotImplementedException();
        public PaletteConfiguration LoadPalette() => throw new NotImplementedException();
        public void SavePalette(PaletteConfiguration palette) => throw new NotImplementedException();
        public void SaveConfig<T>(string subdirectory, string name, T config) where T : class => throw new NotImplementedException();
    }
}
