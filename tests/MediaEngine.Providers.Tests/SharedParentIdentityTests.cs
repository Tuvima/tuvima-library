using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
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
/// An album's tracks each report the album's MusicBrainz release. Abbey Road's "Come Together" matches the 1969
/// release and "Something" only matches a 2019 reissue's recording; the album must end on the same release
/// whichever track finishes first.
/// </summary>
public sealed class SharedParentIdentityTests
{
    private const string Original = "03437e02-835f-3a0a-a37c-48a36c2e852a";
    private const string Reissue = "5c700b4e-949e-40a4-a363-7841d8195a50";
    private const string ReleaseGroup = "9162580e-5df4-32de-80cc-f45a8d8a9b1d";

    [Fact]
    public void Decide_NoEarlierReport_Agrees()
    {
        var decision = SharedParentIdentity.Decide(AlbumReport(Original, "1969"), []);

        Assert.Equal(SharedParentIdentity.Outcome.Agree, decision.Outcome);
    }

    [Fact]
    public void Decide_EarlierReportOfTheSameRelease_Agrees()
    {
        var decision = SharedParentIdentity.Decide(AlbumReport(Original, "1969"), Stored(AlbumReport(Original, "1969")));

        Assert.Equal(SharedParentIdentity.Outcome.Agree, decision.Outcome);
    }

    [Fact]
    public void Decide_EarlierDatedReleaseArrivingSecond_ReplacesTheStoredReport()
    {
        var decision = SharedParentIdentity.Decide(AlbumReport(Original, "1969"), Stored(AlbumReport(Reissue, "2019")));

        Assert.Equal(SharedParentIdentity.Outcome.IncomingWins, decision.Outcome);
        Assert.Contains(BridgeIdKeys.MusicBrainzReleaseId, decision.SupersededKeys);
        Assert.Contains(MetadataFieldConstants.Year, decision.SupersededKeys);
    }

    [Fact]
    public void Decide_LaterDatedReleaseArrivingSecond_AdoptsTheStoredReport()
    {
        var decision = SharedParentIdentity.Decide(AlbumReport(Reissue, "2019"), Stored(AlbumReport(Original, "1969")));

        Assert.Equal(SharedParentIdentity.Outcome.ExistingWins, decision.Outcome);
        Assert.Equal(Original, Value(decision.ParentClaims, BridgeIdKeys.MusicBrainzReleaseId));
        Assert.Equal("1969", Value(decision.ParentClaims, MetadataFieldConstants.Year));
    }

    [Fact]
    public void Decide_UndatedRelease_LosesToADatedOne()
    {
        var decision = SharedParentIdentity.Decide(AlbumReport(Original, year: null), Stored(AlbumReport(Reissue, "2019")));

        Assert.Equal(SharedParentIdentity.Outcome.ExistingWins, decision.Outcome);
        Assert.Equal(Reissue, Value(decision.ParentClaims, BridgeIdKeys.MusicBrainzReleaseId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Decide_SameYear_LowestReleaseIdWinsInEitherOrder(bool originalFirst)
    {
        var first = AlbumReport(originalFirst ? Original : Reissue, "1969");
        var second = AlbumReport(originalFirst ? Reissue : Original, "1969");

        var decision = SharedParentIdentity.Decide(second, Stored(first));

        var winner = decision.Outcome == SharedParentIdentity.Outcome.IncomingWins ? second : decision.ParentClaims;
        Assert.Equal(Original, Value(winner, BridgeIdKeys.MusicBrainzReleaseId));
    }

    [Fact]
    public void Decide_CompilationTracksNamingDifferentArtists_AgreeOnTheirAlbum()
    {
        var first = AlbumReport(Original, "1969");
        first.Add(new ProviderClaim(BridgeIdKeys.AppleArtistId, "136975", 1.0));
        var second = AlbumReport(Original, "1969");
        second.Add(new ProviderClaim(BridgeIdKeys.AppleArtistId, "5468295", 1.0));

        var decision = SharedParentIdentity.Decide(second, Stored(first));

        Assert.Equal(SharedParentIdentity.Outcome.Agree, decision.Outcome);
    }

    [Fact]
    public void Decide_UserChoseTheReissue_AnAutomaticReportOfTheOriginalDoesNotReplaceIt()
    {
        var decision = SharedParentIdentity.Decide(
            AlbumReport(Original, "1969"),
            Stored(AlbumReport(Reissue, "2019"), userDecided: true));

        Assert.Equal(SharedParentIdentity.Outcome.ExistingWins, decision.Outcome);
        Assert.Equal(Reissue, Value(decision.ParentClaims, BridgeIdKeys.MusicBrainzReleaseId));
    }

    [Fact]
    public void Decide_UserChoosesTheReissue_ReplacesTheAutomaticOriginal()
    {
        var decision = SharedParentIdentity.Decide(
            AlbumReport(Reissue, "2019"),
            Stored(AlbumReport(Original, "1969")),
            incomingIsUserDecision: true);

        Assert.Equal(SharedParentIdentity.Outcome.IncomingWins, decision.Outcome);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LineageWrite_TwoTracksInEitherOrder_AlbumEndsOnTheOriginalRelease(bool originalTrackFirst)
    {
        var albumId = Guid.NewGuid();
        var claimRepo = new SupersedingClaimRepository();
        var comeTogether = Track(albumId);
        var something = Track(albumId);
        var writes = originalTrackFirst
            ? new[] { (comeTogether, Original, "1969"), (something, Reissue, "2019") }
            : new[] { (something, Reissue, "2019"), (comeTogether, Original, "1969") };

        var written = new List<ScoringHelper.LineageWriteResult>();
        foreach (var (lineage, release, year) in writes)
        {
            written.Add(await ScoringHelper.PersistAndScoreWithLineageDetailedAsync(
                lineage.AssetId,
                [new ProviderClaim(MetadataFieldConstants.Title, "Track", 0.9), .. AlbumReport(release, year)],
                WellKnownProviders.MusicBrainz,
                lineage,
                claimRepo,
                new NullCanonicalRepository(),
                new EmptyScoringEngine(),
                new MinimalConfigLoader(),
                allProviders: [],
                CancellationToken.None));
        }

        var album = claimRepo.Current(albumId);
        Assert.Equal([Original], album.Where(c => c.ClaimKey == BridgeIdKeys.MusicBrainzReleaseId).Select(c => c.ClaimValue).Distinct());
        Assert.Equal(["1969"], album.Where(c => c.ClaimKey == MetadataFieldConstants.Year).Select(c => c.ClaimValue).Distinct());

        // The second track hands the agreed release on to bridge ids and external identifiers.
        Assert.Equal(Original, Value(written[1].Claims, BridgeIdKeys.MusicBrainzReleaseId));
        Assert.Equal(!originalTrackFirst, written[1].ParentIdentityReplaced);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LineageWrite_StoredInTheDataStore_AlbumKeepsOneCurrentReleaseInEitherOrder(bool originalTrackFirst)
    {
        DapperConfiguration.Configure();
        var databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-shared-parent-{Guid.NewGuid():N}.db");
        var database = new DatabaseConnection(databasePath);
        try
        {
            database.InitializeSchema();
            using (var connection = database.CreateConnection())
            {
                connection.Execute(
                    "INSERT OR IGNORE INTO metadata_providers (id, name, version, is_enabled) VALUES (@id, 'musicbrainz', '1', 1)",
                    new { id = WellKnownProviders.MusicBrainz });
            }

            var albumId = Guid.NewGuid();
            var claimRepo = new MetadataClaimRepository(database);
            var reports = originalTrackFirst
                ? new[] { (Original, "1969"), (Reissue, "2019") }
                : new[] { (Reissue, "2019"), (Original, "1969") };
            foreach (var (release, year) in reports)
            {
                var lineage = Track(albumId);
                await ScoringHelper.PersistAndScoreWithLineageDetailedAsync(
                    lineage.AssetId,
                    [new ProviderClaim(MetadataFieldConstants.Title, "Track", 0.9), .. AlbumReport(release, year)],
                    WellKnownProviders.MusicBrainz,
                    lineage,
                    claimRepo,
                    new CanonicalValueRepository(database),
                    new EmptyScoringEngine(),
                    new MinimalConfigLoader(),
                    allProviders: [],
                    CancellationToken.None);
            }

            var album = await claimRepo.GetByEntityAsync(albumId);
            Assert.Equal([Original], album.Where(c => c.ClaimKey == BridgeIdKeys.MusicBrainzReleaseId).Select(c => c.ClaimValue).Distinct());
            Assert.Equal(["1969"], album.Where(c => c.ClaimKey == MetadataFieldConstants.Year).Select(c => c.ClaimValue).Distinct());
        }
        finally
        {
            database.Dispose();
            DatabaseConnection.ReleasePooledConnections(databasePath);
            File.Delete(databasePath);
        }
    }

    private static List<ProviderClaim> AlbumReport(string releaseId, string? year)
    {
        var claims = new List<ProviderClaim>
        {
            new(MetadataFieldConstants.Album, "Abbey Road", 0.75),
            new(BridgeIdKeys.MusicBrainzReleaseId, releaseId, 1.0),
            new(BridgeIdKeys.MusicBrainzReleaseGroupId, ReleaseGroup, 1.0),
        };
        if (year is not null)
        {
            claims.Insert(1, new ProviderClaim(MetadataFieldConstants.Year, year, 0.85));
        }

        return claims;
    }

    private static List<MetadataClaim> Stored(IEnumerable<ProviderClaim> report, bool userDecided = false)
    {
        var observation = Guid.NewGuid();
        var claimedAt = DateTimeOffset.UtcNow;
        return report.Select(claim => new MetadataClaim
        {
            Id = Guid.NewGuid(),
            ProviderId = WellKnownProviders.MusicBrainz,
            DecisionSourceProviderId = userDecided ? WellKnownProviders.UserManual : null,
            ObservationSetId = observation,
            ClaimKey = claim.Key,
            ClaimValue = claim.Value,
            Confidence = claim.Confidence,
            ClaimedAt = claimedAt,
        }).ToList();
    }

    private static string? Value(IEnumerable<ProviderClaim> claims, string key) =>
        claims.FirstOrDefault(claim => claim.Key == key)?.Value;

    private static WorkLineage Track(Guid albumId) => new(
        AssetId: Guid.NewGuid(),
        EditionId: Guid.NewGuid(),
        WorkId: Guid.NewGuid(),
        ParentWorkId: albumId,
        RootParentWorkId: albumId,
        WorkKind: WorkKind.Child,
        MediaType: MediaType.Music);

    private sealed class SupersedingClaimRepository : IMetadataClaimRepository
    {
        private readonly List<MetadataClaim> _claims = [];

        public IReadOnlyList<MetadataClaim> Current(Guid entityId) =>
            _claims.Where(claim => claim.EntityId == entityId && claim.IsCurrent).ToList();

        public Task InsertBatchAsync(IReadOnlyList<MetadataClaim> claims, CancellationToken ct = default)
        {
            _claims.AddRange(claims);
            return Task.CompletedTask;
        }

        public Task ReplaceCurrentProviderClaimsAsync(
            Guid entityId,
            Guid providerId,
            IReadOnlyCollection<string> fieldKeys,
            IReadOnlyList<MetadataClaim> claims,
            CancellationToken ct = default)
        {
            foreach (var claim in _claims.Where(claim => claim.EntityId == entityId
                         && claim.ProviderId == providerId
                         && claim.IsCurrent
                         && fieldKeys.Contains(claim.ClaimKey, StringComparer.OrdinalIgnoreCase)))
            {
                claim.IsCurrent = false;
            }

            _claims.AddRange(claims);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MetadataClaim>> GetByEntityAsync(Guid entityId, CancellationToken ct = default) =>
            Task.FromResult(Current(entityId));

        public Task DeleteByEntityAsync(Guid entityId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NullCanonicalRepository : ICanonicalValueRepository
    {
        public Task<IReadOnlyList<CanonicalValue>> GetByEntityAsync(Guid entityId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CanonicalValue>>([]);

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalValue>>> GetByEntitiesAsync(
            IReadOnlyList<Guid> entityIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalValue>>>(
                new Dictionary<Guid, IReadOnlyList<CanonicalValue>>());

        public Task UpsertBatchAsync(IReadOnlyList<CanonicalValue> values, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteByEntityAsync(Guid entityId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<CanonicalValue>> GetConflictedAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CanonicalValue>>([]);

        public Task DeleteByKeyAsync(Guid entityId, string key, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<Guid>> FindByValueAsync(string key, string value, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task<IReadOnlyList<CanonicalValue>> FindByKeyAndPrefixAsync(string key, string prefix, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CanonicalValue>>([]);

        public Task<IReadOnlyList<Guid>> GetEntitiesNeedingEnrichmentAsync(string hasField, string missingField, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>([]);
    }

    private sealed class EmptyScoringEngine : IScoringEngine
    {
        public Task<ScoringResult> ScoreEntityAsync(ScoringContext context, CancellationToken ct = default)
            => Task.FromResult(new ScoringResult
            {
                EntityId = context.EntityId,
                OverallConfidence = 0.9,
                ScoredAt = DateTimeOffset.UtcNow,
                FieldScores = [],
            });

        public Task<IReadOnlyList<ScoringResult>> ScoreBatchAsync(IEnumerable<ScoringContext> contexts, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ScoringResult>>(
                contexts.Select(context => ScoreEntityAsync(context, ct).Result).ToList());
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
