using Dapper;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Intelligence.Contracts;
using MediaEngine.Intelligence.Models;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Providers.Workers;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using ProviderConfiguration = MediaEngine.Domain.Configuration.ProviderConfiguration;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// A file the Engine cannot match stays exactly where it is and is marked Unmatched, with no Needs Review item; a match the Engine is
/// unsure about still goes to Needs Review (Library setup spec D34). The real retail worker, guard and data store run against a hand-written
/// provider that finds nothing, or something it is not sure of.
/// </summary>
public sealed class UnmatchedNoMatchTests : IDisposable
{
    private const string MoviesLibraryId = "bbbbbbbb-0000-4000-8000-0000000000a1";
    private const string BooksLibraryId = "bbbbbbbb-0000-4000-8000-0000000000b2";
    private const string OtherVideosLibraryId = "bbbbbbbb-0000-4000-8000-0000000000c3";
    private const string OtherAudioLibraryId = "bbbbbbbb-0000-4000-8000-0000000000d4";
    private const string OtherReadingLibraryId = "bbbbbbbb-0000-4000-8000-0000000000e5";

    private readonly string _tempRoot;
    private readonly DatabaseConnection _db;
    private readonly WorkRepository _works;
    private readonly IdentityJobRepository _jobs;
    private readonly MetadataClaimRepository _claims;
    private readonly CanonicalValueRepository _canonicals;
    private readonly CanonicalValueArrayRepository _arrays;
    private readonly BridgeIdRepository _bridgeIds;
    private readonly ReviewQueueRepository _reviews;
    private readonly UnmatchedAssetRepository _unmatched;
    private readonly ConfigurationDirectoryLoader _config;
    private readonly StageOutcomeFactory _outcomes;
    private readonly TimelineRecorder _timeline;
    private readonly ConfiguredExternalLookupGuard _guard;

    public UnmatchedNoMatchTests()
    {
        DapperConfiguration.Configure();
        _tempRoot = Path.Combine(Path.GetTempPath(), $"tuvima_unmatched_worker_{Guid.NewGuid():N}");
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
        _unmatched = new UnmatchedAssetRepository(_db);
        _config = new ConfigurationDirectoryLoader(Path.Combine(FindRepoRoot(), "config"));
        var assets = new MediaAssetRepository(_db);
        _outcomes = new StageOutcomeFactory(
            _reviews,
            new SystemActivityRepository(_db),
            new NoOpEventPublisher(),
            _canonicals,
            NullLogger<StageOutcomeFactory>.Instance,
            assets,
            _config,
            _unmatched);
        _timeline = new TimelineRecorder(new EntityTimelineRepository(_db));
        _guard = new ConfiguredExternalLookupGuard(
            new LibrariesOnlyLoader(
                Library(MoviesLibraryId, "Movies", "movies", "Movies", LibraryAreas.Watch),
                Library(BooksLibraryId, "Books", "books", "Books", LibraryAreas.Read),
                Library(OtherVideosLibraryId, "Home videos", "other_videos", "Videos", LibraryAreas.Watch),
                Library(OtherAudioLibraryId, "Recordings", "other_audio", "Music", LibraryAreas.Listen),
                Library(OtherReadingLibraryId, "Loose books", "other_reading", "Books", LibraryAreas.Read)),
            assets,
            NullLogger<ConfiguredExternalLookupGuard>.Instance);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch (Exception) { /* temp store: cleanup is best effort */ }
        try { Directory.Delete(_tempRoot, recursive: true); } catch (Exception) { /* best effort */ }
    }

    [Fact]
    public async Task AMoviesFileWithNoRetailMatch_IsMarkedUnmatched_StaysAtItsPath_AndCreatesNoReviewItem()
    {
        var movie = await SeedAssetAsync(MediaType.Movies, MoviesLibraryId, "Dune.mkv");
        var provider = new StubRetailProvider("tmdb", WellKnownProviders.Tmdb);
        await QueueAsync(movie, "Movies");

        await RunWorkerAsync(provider, new FixedRetailScoring(0.0));

        Assert.Contains(movie.AssetId, provider.Entities);
        Assert.Equal(nameof(IdentityJobState.RetailNoMatch), (await _jobs.GetByEntityAsync(movie.AssetId))!.State);
        var row = await _unmatched.GetAsync(movie.AssetId);
        Assert.NotNull(row);
        Assert.Equal(MoviesLibraryId, row.LibraryId);
        Assert.Equal(UnmatchedReasons.NoMatch, row.Reason);
        Assert.False(row.KeptAsIs);
        Assert.NotNull(row.NextRetryAt);
        Assert.Empty(await _reviews.GetPendingByEntityAsync(movie.AssetId));
        Assert.True(File.Exists(movie.Path), "the file is not moved or renamed");
        Assert.Equal(movie.Path, (await new MediaAssetRepository(_db).FindByIdAsync(movie.AssetId))!.FilePathRoot);
    }

    [Fact]
    public async Task ABooksFileWithAnUnsureRetailMatch_StillGoesToNeedsReview_AndIsNotMarkedUnmatched()
    {
        var book = await SeedAssetAsync(MediaType.Books, BooksLibraryId, "Dune.epub");
        var provider = new StubRetailProvider("apple_api", WellKnownProviders.AppleApi)
        {
            Claims =
            [
                new ProviderClaim(MetadataFieldConstants.Title, "Dune", 0.95),
                new ProviderClaim(MetadataFieldConstants.Author, "Some Author", 0.95),
            ],
        };
        await QueueAsync(book, "Books");

        // A composite score between the ambiguous and auto-accept thresholds is a match the Engine is not sure of.
        await RunWorkerAsync(provider, new FixedRetailScoring(0.75));

        Assert.Equal(nameof(IdentityJobState.RetailMatchedNeedsReview), (await _jobs.GetByEntityAsync(book.AssetId))!.State);
        var review = Assert.Single(await _reviews.GetPendingByEntityAsync(book.AssetId));
        Assert.Equal(ReviewTrigger.RetailMatchAmbiguous, review.Trigger);
        Assert.Null(await _unmatched.GetAsync(book.AssetId));
    }

    [Fact]
    public async Task AFileInNoLibraryWithNoRetailMatch_StillGetsTheReviewItem_BecauseThereIsNoLibraryToListItUnder()
    {
        var loose = await SeedAssetAsync(MediaType.Books, libraryId: null, "Loose.epub");
        await QueueAsync(loose, "Books");

        await RunWorkerAsync(new StubRetailProvider("apple_api", WellKnownProviders.AppleApi), new FixedRetailScoring(0.0));

        Assert.Equal(nameof(IdentityJobState.RetailNoMatch), (await _jobs.GetByEntityAsync(loose.AssetId))!.State);
        Assert.Null(await _unmatched.GetAsync(loose.AssetId));
        var review = Assert.Single(await _reviews.GetPendingByEntityAsync(loose.AssetId));
        Assert.Equal(ReviewTrigger.RetailMatchFailed, review.Trigger);
    }

    [Fact]
    public async Task AFileInOtherVideosOtherAudioOrOtherReading_IsNeverLookedUp_AndNeverUnmatched()
    {
        var video = await SeedAssetAsync(MediaType.Videos, OtherVideosLibraryId, "Holiday.mp4");
        var audio = await SeedAssetAsync(MediaType.Music, OtherAudioLibraryId, "Memo.m4a");
        var reading = await SeedAssetAsync(MediaType.Books, OtherReadingLibraryId, "Notes.epub");
        await QueueAsync(video, "Videos");
        await QueueAsync(audio, "Music");
        await QueueAsync(reading, "Books");
        var provider = new StubRetailProvider("apple_api", WellKnownProviders.AppleApi);

        await RunWorkerAsync(provider, new FixedRetailScoring(0.0));

        Assert.Empty(provider.Entities);
        foreach (var file in new[] { video, audio, reading })
        {
            Assert.Null(await _unmatched.GetAsync(file.AssetId));
            Assert.Empty(await _reviews.GetPendingByEntityAsync(file.AssetId));
            Assert.Equal(nameof(IdentityJobState.ReadyWithoutUniverse), (await _jobs.GetByEntityAsync(file.AssetId))!.State);
        }
    }

    [Fact]
    public async Task ARetryThatMatches_TakesTheFileOutOfUnmatched()
    {
        var book = await SeedAssetAsync(MediaType.Books, BooksLibraryId, "Dune.epub");
        await QueueAsync(book, "Books");
        await RunWorkerAsync(new StubRetailProvider("apple_api", WellKnownProviders.AppleApi), new FixedRetailScoring(0.0));
        Assert.NotNull(await _unmatched.GetAsync(book.AssetId));

        // The next lookup (a new job, as a retry queues one) finds a match.
        await QueueAsync(book, "Books");
        var provider = new StubRetailProvider("apple_api", WellKnownProviders.AppleApi)
        {
            Claims =
            [
                new ProviderClaim(MetadataFieldConstants.Title, "Dune", 0.95),
                new ProviderClaim(MetadataFieldConstants.Author, "Some Author", 0.95),
            ],
        };
        await RunWorkerAsync(provider, new FixedRetailScoring(0.99));

        Assert.Null(await _unmatched.GetAsync(book.AssetId));
        Assert.Empty(await _reviews.GetPendingByEntityAsync(book.AssetId));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task RunWorkerAsync(StubRetailProvider provider, IRetailMatchScoringService scoring)
    {
        var worker = new RetailMatchWorker(
            _jobs,
            new RetailCandidateRepository(_db),
            _outcomes,
            _timeline,
            [provider],
            scoring,
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
        await worker.PollAsync(CancellationToken.None);
    }

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

    private async Task QueueAsync(SeededAsset asset, string mediaType) =>
        await _jobs.CreateAsync(new IdentityJob
        {
            Id = Guid.NewGuid(),
            EntityId = asset.AssetId,
            EntityType = "MediaAsset",
            MediaType = mediaType,
            State = nameof(IdentityJobState.Queued),
        });

    private async Task<SeededAsset> SeedAssetAsync(MediaType mediaType, string? libraryId, string fileName)
    {
        var workId = await _works.InsertStandaloneAsync(mediaType);
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var path = Path.Combine(_tempRoot, fileName);
        await File.WriteAllTextAsync(path, "media");
        using (var conn = _db.CreateConnection())
        {
            conn.Execute(
                "INSERT INTO editions (id, work_id, format_label) VALUES (@editionId, @workId, 'Test')",
                new { editionId, workId });
            conn.Execute(
                "INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status, library_id) VALUES (@assetId, @editionId, @hash, @path, 'Normal', @libraryId)",
                new { assetId, editionId, hash = Guid.NewGuid().ToString("N"), path, libraryId });
        }

        await _canonicals.UpsertBatchAsync(
        [
            new CanonicalValue { EntityId = assetId, Key = "title", Value = "Some Title", LastScoredAt = DateTimeOffset.UtcNow },
        ]);
        await _arrays.SetValuesAsync(assetId, "author", [new CanonicalArrayEntry { Ordinal = 0, Value = "Some Author" }]);
        return new SeededAsset(workId, assetId, path);
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

    private sealed record SeededAsset(Guid WorkId, Guid AssetId, string Path);

    // ── Spies ─────────────────────────────────────────────────────────────────

    private sealed class StubRetailProvider(string name, Guid providerId) : IExternalMetadataProvider
    {
        private readonly List<Guid> _entities = [];

        public IReadOnlyList<ProviderClaim> Claims { get; init; } = [];

        public IReadOnlyList<Guid> Entities => _entities.ToList();

        public string Name => name;
        public ProviderDomain Domain => ProviderDomain.Universal;
        public IReadOnlyList<string> CapabilityTags => [];
        public Guid ProviderId => providerId;
        public bool CanHandle(MediaType mediaType) => true;
        public bool CanHandle(EntityType entityType) => true;

        public Task<IReadOnlyList<ProviderClaim>> FetchAsync(ProviderLookupRequest request, CancellationToken ct = default)
        {
            _entities.Add(request.EntityId);
            return Task.FromResult(Claims);
        }
    }

    private sealed class FixedRetailScoring(double composite) : IRetailMatchScoringService
    {
        public FieldMatchScores ScoreCandidate(
            IReadOnlyDictionary<string, string> fileHints,
            string? candidateTitle,
            string? candidateAuthor,
            string? candidateYear,
            MediaType mediaType,
            MatchTierConfig? matchTiers = null,
            CandidateExtendedMetadata? extendedMetadata = null,
            double structuralBonus = 0.0)
            => new() { TitleScore = composite, AuthorScore = composite, YearScore = 0.0, FormatScore = 1.0, CompositeScore = composite };
    }

    private sealed class NoOpEventPublisher : IEventPublisher
    {
        public Task PublishAsync<TPayload>(string eventName, TPayload payload, CancellationToken ct = default)
            where TPayload : notnull
            => Task.CompletedTask;
    }

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => throw new NotSupportedException($"CreateClient('{name}') should not be called for a no-match retail search.");
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
