using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Intelligence.Contracts;
using MediaEngine.Intelligence.Models;
using MediaEngine.Intelligence.Services;
using MediaEngine.Providers.Adapters;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Providers.Workers;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Tuvima.Wikidata;
using ProviderConfiguration = MediaEngine.Domain.Configuration.ProviderConfiguration;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// A file in a plain-kind library (Other audio, Other reading) is never looked up: no identity work, no retail search, no Wikidata
/// bridge, no artwork. Every worker is checked with the real guard, a real temp SQLite data store and hand-written spies; the libraries
/// deliberately keep an "enriched" policy so it is the kind, not the field, that stops the lookup (Library setup D38).
/// </summary>
public sealed class PlainKindLookupRefusalTests : IDisposable
{
    private const string OtherAudioLibraryId = "aaaaaaaa-0000-4000-8000-0000000000a1";
    private const string OtherReadingLibraryId = "aaaaaaaa-0000-4000-8000-0000000000b2";
    private const string BooksLibraryId = "aaaaaaaa-0000-4000-8000-0000000000c3";

    private readonly string _tempRoot;
    private readonly DatabaseConnection _db;
    private readonly WorkRepository _works;
    private readonly IdentityJobRepository _jobs;
    private readonly MetadataClaimRepository _claims;
    private readonly CanonicalValueRepository _canonicals;
    private readonly CanonicalValueArrayRepository _arrays;
    private readonly BridgeIdRepository _bridgeIds;
    private readonly ReviewQueueRepository _reviews;
    private readonly ConfigurationDirectoryLoader _config;
    private readonly StageOutcomeFactory _outcomes;
    private readonly TimelineRecorder _timeline;
    private readonly RecordingHandler _http = new();
    private readonly LibrariesOnlyLoader _libraries;
    private readonly ConfiguredExternalLookupGuard _guard;

    public PlainKindLookupRefusalTests()
    {
        DapperConfiguration.Configure();
        _tempRoot = Path.Combine(Path.GetTempPath(), $"tuvima_plain_kind_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _db = new DatabaseConnection(Path.Combine(_tempRoot, "library.db"));
        _db.InitializeSchema();
        _db.RunStartupChecks();

        _works = new WorkRepository(_db);
        _jobs = new IdentityJobRepository(_db);
        _claims = new MetadataClaimRepository(_db);
        _canonicals = new CanonicalValueRepository(_db);
        _arrays = new CanonicalValueArrayRepository(_db);
        _bridgeIds = new BridgeIdRepository(_db);
        _reviews = new ReviewQueueRepository(_db);
        _config = new ConfigurationDirectoryLoader(Path.Combine(FindRepoRoot(), "config"));
        _outcomes = new StageOutcomeFactory(
            _reviews,
            new SystemActivityRepository(_db),
            new NoOpEventPublisher(),
            _canonicals,
            NullLogger<StageOutcomeFactory>.Instance);
        _timeline = new TimelineRecorder(new EntityTimelineRepository(_db));

        // The fields say "enriched" on purpose: the kind alone must stop the lookup.
        _libraries = new LibrariesOnlyLoader(
            Library(OtherAudioLibraryId, "Recordings", "other_audio", "Music", LibraryAreas.Listen),
            Library(OtherReadingLibraryId, "Loose books", "other_reading", "Books", LibraryAreas.Read),
            Library(BooksLibraryId, "Books", "books", "Books", LibraryAreas.Read));
        _guard = new ConfiguredExternalLookupGuard(_libraries, new MediaAssetRepository(_db), NullLogger<ConfiguredExternalLookupGuard>.Instance);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch (Exception) { /* temp store: cleanup is best effort */ }
        try { Directory.Delete(_tempRoot, recursive: true); } catch (Exception) { /* best effort */ }
    }

    // ── The guard itself ──────────────────────────────────────────────────────

    [Fact]
    public void ThePlainKindLibraries_AreBlockedWhateverTheirPolicyField_AndALookupKindLibraryIsNot()
    {
        Assert.True(_guard.BlocksLibrary(OtherAudioLibraryId));
        Assert.True(_guard.BlocksLibrary(OtherReadingLibraryId));
        Assert.False(_guard.BlocksLibrary(BooksLibraryId));
        Assert.False(_guard.BlocksLibrary(null));
        Assert.False(_guard.BlocksLibrary("not-a-library"));
        Assert.Equal([OtherAudioLibraryId, OtherReadingLibraryId], _guard.GetBlockedLibraryIds().Order().ToArray());
    }

    [Fact]
    public void ALibraryWhoseSettingsCannotBeRead_IsBlockedRatherThanContacted()
    {
        var broken = new ConfiguredExternalLookupGuard(
            new LibrariesOnlyLoader { Failure = new IOException("libraries.json is locked") },
            new MediaAssetRepository(_db),
            NullLogger<ConfiguredExternalLookupGuard>.Instance);

        Assert.True(broken.BlocksLibrary(BooksLibraryId));
    }

    [Fact]
    public async Task AnAssetAndItsWork_AreBlockedThroughTheirLibrary()
    {
        var plain = await SeedAssetAsync(MediaType.Music, OtherAudioLibraryId);
        var lookup = await SeedAssetAsync(MediaType.Books, BooksLibraryId);

        Assert.True(await _guard.BlocksEntityAsync(plain.AssetId));
        Assert.True(await _guard.BlocksEntityAsync(plain.WorkId));
        Assert.False(await _guard.BlocksEntityAsync(lookup.AssetId));
        Assert.False(await _guard.BlocksEntityAsync(lookup.WorkId));
        Assert.False(await _guard.BlocksEntityAsync(Guid.NewGuid()));
    }

    // ── Identity workers ──────────────────────────────────────────────────────

    [Fact]
    public async Task TheRetailWorker_ClosesFilesInOtherAudioAndOtherReadingWithoutAskingAnyProvider_ButStillAsksForABook()
    {
        var audio = await SeedAssetAsync(MediaType.Music, OtherAudioLibraryId);
        var reading = await SeedAssetAsync(MediaType.Books, OtherReadingLibraryId);
        var book = await SeedAssetAsync(MediaType.Books, BooksLibraryId);
        var audioJob = await QueueJobAsync(audio, "Music", IdentityJobState.Queued);
        var readingJob = await QueueJobAsync(reading, "Books", IdentityJobState.Queued);
        var bookJob = await QueueJobAsync(book, "Books", IdentityJobState.Queued);
        var provider = new RecordingRetailProvider();

        var worker = new RetailMatchWorker(
            _jobs,
            new RetailCandidateRepository(_db),
            _outcomes,
            _timeline,
            [provider],
            new RetailMatchScoringService(new FuzzyMatchingService(), _config),
            _claims,
            _canonicals,
            new FixedScoringEngine(),
            _config,
            _bridgeIds,
            _works,
            new WorkClaimRouter(),
            new ThrowingHttpClientFactory(),
            null!,
            NullLogger<RetailMatchWorker>.Instance,
            arrayRepo: _arrays,
            lookupGuard: _guard);

        var processed = await worker.PollAsync(CancellationToken.None);

        Assert.Equal(3, processed);
        Assert.Equal(nameof(IdentityJobState.ReadyWithoutUniverse), (await _jobs.GetByIdAsync(audioJob))!.State);
        Assert.Equal(nameof(IdentityJobState.ReadyWithoutUniverse), (await _jobs.GetByIdAsync(readingJob))!.State);
        Assert.Equal(nameof(IdentityJobState.RetailNoMatch), (await _jobs.GetByIdAsync(bookJob))!.State);
        Assert.DoesNotContain(provider.Entities, entity => entity == audio.AssetId || entity == reading.AssetId);
        Assert.Contains(book.AssetId, provider.Entities);
        Assert.Empty(await _reviews.GetPendingByEntityAsync(audio.AssetId));
        Assert.Empty(await _reviews.GetPendingByEntityAsync(reading.AssetId));
    }

    [Fact]
    public async Task TheWikidataBridgeWorker_SendsNothingOutForFilesInOtherAudioAndOtherReading()
    {
        var audio = await SeedAssetAsync(MediaType.Music, OtherAudioLibraryId);
        var reading = await SeedAssetAsync(MediaType.Books, OtherReadingLibraryId);
        var audioJob = await QueueJobAsync(audio, "Music", IdentityJobState.RetailMatched);
        var readingJob = await QueueJobAsync(reading, "Books", IdentityJobState.RetailMatchedNeedsReview);

        await CreateBridgeWorker().PollAsync(CancellationToken.None);

        Assert.Empty(_http.Urls);
        Assert.Equal(nameof(IdentityJobState.ReadyWithoutUniverse), (await _jobs.GetByIdAsync(audioJob))!.State);
        Assert.Equal(nameof(IdentityJobState.ReadyWithoutUniverse), (await _jobs.GetByIdAsync(readingJob))!.State);
    }

    [Fact]
    public async Task TheQuickHydrationWorker_DoesNotEnrichFilesInOtherAudioAndOtherReading()
    {
        var audio = await SeedAssetAsync(MediaType.Music, OtherAudioLibraryId);
        var audioJob = await QueueJobAsync(audio, "Music", IdentityJobState.QidResolved);
        var enrichment = new RecordingEnrichmentService();

        var worker = new QuickHydrationWorker(
            _jobs,
            enrichment,
            null!,
            null!,
            _canonicals,
            null!,
            null!,
            _config,
            NullLogger<QuickHydrationWorker>.Instance,
            lookupGuard: _guard);

        var processed = await worker.PollAsync(CancellationToken.None);

        Assert.Equal(1, processed);
        Assert.Equal(0, enrichment.Calls);
        Assert.Equal(nameof(IdentityJobState.ReadyWithoutUniverse), (await _jobs.GetByIdAsync(audioJob))!.State);
    }

    // ── Entry points that create or run identity work ─────────────────────────

    [Fact]
    public async Task TheIdentityPipeline_QueuesAndRunsNothingForAFileInAPlainKindLibrary()
    {
        var audio = await SeedAssetAsync(MediaType.Music, OtherAudioLibraryId);
        var book = await SeedAssetAsync(MediaType.Books, BooksLibraryId);
        var service = new SynchronousIdentityPipelineService(
            _jobs, _canonicals, null!, null!, null!, NullLogger<SynchronousIdentityPipelineService>.Instance, lookupGuard: _guard);

        var queuedForAudio = await service.EnqueueAsync(Request(audio.AssetId, MediaType.Music));
        var ranForAudio = await service.RunSynchronousAsync(Request(audio.AssetId, MediaType.Music));
        var ranForWork = await service.RunSynchronousAsync(new HarvestRequest { EntityId = audio.WorkId, EntityType = EntityType.Work, MediaType = MediaType.Music });
        var queuedForBook = await service.EnqueueAsync(Request(book.AssetId, MediaType.Books));

        Assert.Equal(Guid.Empty, queuedForAudio);
        Assert.Equal(0, ranForAudio.TotalClaimsAdded);
        Assert.Equal(0, ranForWork.TotalClaimsAdded);
        Assert.Null(await _jobs.GetByEntityAsync(audio.AssetId));
        Assert.Null(await _jobs.GetByEntityAsync(audio.WorkId));
        Assert.NotEqual(Guid.Empty, queuedForBook);
        Assert.NotNull(await _jobs.GetByEntityAsync(book.AssetId));
    }

    [Fact]
    public async Task CoverArt_IsNeverDownloadedForAFileInAPlainKindLibrary_EvenFromAnExplicitUrl()
    {
        var audio = await SeedAssetAsync(MediaType.Music, OtherAudioLibraryId);
        var worker = new CoverArtWorker(
            new MediaAssetRepository(_db),
            _canonicals,
            _works,
            new ImageCacheRepository(_db),
            new RecordingHttpClientFactory(_http),
            new AssetPathService(Path.Combine(_tempRoot, "assets")),
            NullLogger<CoverArtWorker>.Instance,
            lookupGuard: _guard);

        await worker.DownloadAndPersistAsync(audio.AssetId, null, CancellationToken.None, "https://covers.example.test/cover.jpg");
        var replaced = await worker.ReplaceProviderArtworkAsync(audio.AssetId, "https://covers.example.test/cover.jpg", "apple_api", WellKnownProviders.AppleApi, CancellationToken.None);

        Assert.Empty(_http.Urls);
        Assert.False(replaced.CoverDownloaded);
    }

    [Fact]
    public async Task TheLeasedJobHelper_LeavesEveryJobAloneWhenNoGuardIsGiven()
    {
        var audio = await SeedAssetAsync(MediaType.Music, OtherAudioLibraryId);
        await QueueJobAsync(audio, "Music", IdentityJobState.Queued);
        var leased = await _jobs.LeaseNextAsync("test", [IdentityJobState.Queued], 10, TimeSpan.FromMinutes(1));

        var allowed = await ((IExternalLookupGuard?)null).SettleBlockedJobsAsync(_jobs, leased, NullLogger.Instance, CancellationToken.None);

        Assert.Single(allowed);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HarvestRequest Request(Guid assetId, MediaType mediaType) =>
        new() { EntityId = assetId, EntityType = EntityType.MediaAsset, MediaType = mediaType };

    private static LibraryFolderConfig Library(string id, string name, string kindKey, string mediaType, string area) => new()
    {
        Id = id,
        Name = name,
        Kind = LibraryKinds.Catalogued,
        KindKey = kindKey,
        Area = area,
        MetadataPolicy = LibraryMetadataPolicies.Enriched,
        YoutubeLookup = true,
        MediaTypes = [mediaType],
    };

    private async Task<Guid> QueueJobAsync(SeededAsset asset, string mediaType, IdentityJobState state)
    {
        var jobId = Guid.NewGuid();
        await _jobs.CreateAsync(new IdentityJob
        {
            Id = jobId,
            EntityId = asset.AssetId,
            EntityType = "MediaAsset",
            MediaType = mediaType,
            State = state.ToString(),
        });
        return jobId;
    }

    private async Task<SeededAsset> SeedAssetAsync(MediaType mediaType, string libraryId)
    {
        var workId = await _works.InsertStandaloneAsync(mediaType);
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using (var conn = _db.CreateConnection())
        {
            conn.Execute(
                "INSERT INTO editions (id, work_id, format_label) VALUES (@editionId, @workId, 'Test')",
                new { editionId, workId });
            conn.Execute(
                "INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status, library_id) VALUES (@assetId, @editionId, @hash, @path, 'Normal', @libraryId)",
                new
                {
                    assetId,
                    editionId,
                    hash = Guid.NewGuid().ToString("N"),
                    path = Path.Combine(_tempRoot, $"{assetId:N}.media"),
                    libraryId,
                });
        }

        await _canonicals.UpsertBatchAsync(
        [
            new CanonicalValue { EntityId = assetId, Key = "title", Value = "Some Title", LastScoredAt = DateTimeOffset.UtcNow },
        ]);
        await _arrays.SetValuesAsync(assetId, "author", [new CanonicalArrayEntry { Ordinal = 0, Value = "Some Author" }]);
        return new SeededAsset(workId, assetId);
    }

    private WikidataBridgeWorker CreateBridgeWorker()
    {
        var root = FindRepoRoot();
        var adapterConfig = JsonSerializer.Deserialize<ReconciliationProviderConfig>(
            File.ReadAllText(Path.Combine(root, "config", "providers", "wikidata_reconciliation.json")),
            new JsonSerializerOptions { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip })
            ?? throw new InvalidOperationException("Could not read wikidata_reconciliation.json");
        adapterConfig.ThrottleMs = 0;

        // Every request, from the adapter or from the Wikidata library under it, lands on the recording handler.
        var reconciler = new WikidataReconciler(
            new HttpClient(_http, disposeHandler: false),
            new WikidataReconcilerOptions { UserAgent = "Tuvima-Tests/1.0", MaxLag = 0 });
        var adapter = new ReconciliationAdapter(
            adapterConfig,
            new RecordingHttpClientFactory(_http),
            NullLogger<ReconciliationAdapter>.Instance,
            new FuzzyMatchingService(),
            reconciler: reconciler);

        var postPipeline = new PostPipelineService(
            _claims,
            _canonicals,
            new FixedScoringEngine(),
            _config,
            [],
            _reviews,
            new NoOpAutoOrganizeService(),
            NullLogger<PostPipelineService>.Instance);

        return new WikidataBridgeWorker(
            _jobs,
            new WikidataCandidateRepository(_db),
            _outcomes,
            _timeline,
            new BridgeIdHelper(_config),
            [adapter],
            _bridgeIds,
            _claims,
            _canonicals,
            new FixedScoringEngine(),
            _config,
            _works,
            new WorkClaimRouter(),
            new CatalogUpsertService(_works),
            postPipeline,
            CoverArtWorkerTestFactory.Create(_canonicals, _works),
            NullLogger<WikidataBridgeWorker>.Instance,
            arrayRepo: _arrays,
            lookupGuard: _guard);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "config", "pipelines.json")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository config folder.");
    }

    private sealed record SeededAsset(Guid WorkId, Guid AssetId);

    // ── Spies ─────────────────────────────────────────────────────────────────

    private sealed class RecordingRetailProvider : IExternalMetadataProvider
    {
        private readonly List<Guid> _entities = [];

        public IReadOnlyList<Guid> Entities
        {
            get
            {
                lock (_entities)
                {
                    return _entities.ToList();
                }
            }
        }

        public string Name => "apple_api";
        public ProviderDomain Domain => ProviderDomain.Universal;
        public IReadOnlyList<string> CapabilityTags => [];
        public Guid ProviderId => WellKnownProviders.AppleApi;
        public bool CanHandle(MediaType mediaType) => true;
        public bool CanHandle(EntityType entityType) => true;

        public Task<IReadOnlyList<ProviderClaim>> FetchAsync(ProviderLookupRequest request, CancellationToken ct = default)
        {
            lock (_entities)
            {
                _entities.Add(request.EntityId);
            }

            return Task.FromResult<IReadOnlyList<ProviderClaim>>([]);
        }
    }

    private sealed class RecordingEnrichmentService : IEnrichmentService
    {
        public int Calls { get; private set; }

        public Task RunQuickPassAsync(Guid entityId, string qid, CancellationToken ct = default)
        {
            Calls++;
            return Task.CompletedTask;
        }

        public Task RunWorkScopedPassAsync(Guid entityId, string qid, CancellationToken ct = default)
        {
            Calls++;
            return Task.CompletedTask;
        }

        public Task RunUniversePassAsync(Guid entityId, string qid, CancellationToken ct = default)
        {
            Calls++;
            return Task.CompletedTask;
        }

        public Task RunUniverseCorePassAsync(Guid entityId, string qid, CancellationToken ct = default)
        {
            Calls++;
            return Task.CompletedTask;
        }

        public Task RunUniverseEnhancerPassAsync(Guid entityId, string qid, CancellationToken ct = default)
        {
            Calls++;
            return Task.CompletedTask;
        }

        public Task RunSingleEnrichmentAsync(Guid entityId, string qid, EnrichmentType type, CancellationToken ct = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<string> _urls = [];

        public IReadOnlyList<string> Urls
        {
            get
            {
                lock (_gate)
                {
                    return _urls.ToList();
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _urls.Add(request.RequestUri?.ToString() ?? string.Empty);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class RecordingHttpClientFactory(RecordingHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => throw new NotSupportedException($"CreateClient('{name}') should not be called for a no-match retail search.");
    }

    private sealed class NoOpEventPublisher : IEventPublisher
    {
        public Task PublishAsync<TPayload>(string eventName, TPayload payload, CancellationToken ct = default)
            where TPayload : notnull
            => Task.CompletedTask;
    }

    private sealed class NoOpAutoOrganizeService : IAutoOrganizeService
    {
        public Task TryAutoOrganizeAsync(Guid assetId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<AssetRefilePlan> PlanRefileAsync(Guid assetId, AssetRefilePlacement? placement, CancellationToken ct = default)
            => Task.FromResult(AssetRefilePlan.Of(AssetRefileDisposition.AlreadyFits, string.Empty));

        public Task<AssetRefileOutcome> RefileAsync(Guid assetId, CancellationToken ct = default)
            => Task.FromResult(new AssetRefileOutcome(AssetRefileOutcomeKind.AlreadyFits, AssetRefileDisposition.AlreadyFits, string.Empty, null));
    }

    private sealed class FixedScoringEngine : IScoringEngine
    {
        public Task<ScoringResult> ScoreEntityAsync(ScoringContext context, CancellationToken ct = default)
            => Task.FromResult(new ScoringResult
            {
                EntityId = context.EntityId,
                OverallConfidence = 0.9,
                ScoredAt = DateTimeOffset.UtcNow,
                FieldScores = [],
            });

        public async Task<IReadOnlyList<ScoringResult>> ScoreBatchAsync(
            IEnumerable<ScoringContext> contexts,
            CancellationToken ct = default)
        {
            var results = new List<ScoringResult>();
            foreach (var context in contexts)
            {
                results.Add(await ScoreEntityAsync(context, ct));
            }

            return results;
        }
    }

    private sealed class LibrariesOnlyLoader : IConfigurationLoader
    {
        public LibrariesOnlyLoader(params LibraryFolderConfig[] libraries) =>
            Libraries = new LibrariesConfiguration { Libraries = [.. libraries] };

        public LibrariesConfiguration Libraries { get; set; }

        public Exception? Failure { get; set; }

        public LibrariesConfiguration LoadLibraries() => Failure is null ? Libraries : throw Failure;

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
        public void DeleteProvider(string name) { }
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
