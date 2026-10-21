using System.Net;
using System.Text;
using System.Text.Json;
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

namespace MediaEngine.Providers.Tests;

/// <summary>
/// An audiobook Apple cannot match takes the Wikidata ID of an owned, confirmed book with the same title and
/// author, and nothing is looked up outside the machine for it. Every other no-match still goes to Needs Review.
/// Uses a real temp SQLite data store, the real workers, and hand-written spies for providers and HTTP.
/// </summary>
public sealed class AudiobookOwnedBookFallbackTests : IDisposable
{
    private const string BookQid = "Q190192";

    private readonly string _tempRoot;
    private readonly DatabaseConnection _db;
    private readonly WorkRepository _works;
    private readonly IdentityJobRepository _jobs;
    private readonly MetadataClaimRepository _claims;
    private readonly CanonicalValueRepository _canonicals;
    private readonly CanonicalValueArrayRepository _arrays;
    private readonly BridgeIdRepository _bridgeIds;
    private readonly ReviewQueueRepository _reviews;
    private readonly EntityTimelineRepository _timelineEvents;
    private readonly ConfigurationDirectoryLoader _config;
    private readonly StageOutcomeFactory _outcomes;
    private readonly TimelineRecorder _timeline;
    private readonly FixedScoringEngine _scoring = new();
    private readonly RecordingHandler _http = new();

    public AudiobookOwnedBookFallbackTests()
    {
        DapperConfiguration.Configure();
        _tempRoot = Path.Combine(Path.GetTempPath(), $"tuvima_owned_book_{Guid.NewGuid():N}");
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
        _timelineEvents = new EntityTimelineRepository(_db);
        _config = new ConfigurationDirectoryLoader(Path.Combine(FindRepoRoot(), "config"));
        _outcomes = new StageOutcomeFactory(
            _reviews,
            new SystemActivityRepository(_db),
            new NoOpEventPublisher(),
            _canonicals,
            NullLogger<StageOutcomeFactory>.Instance);
        _timeline = new TimelineRecorder(_timelineEvents);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch (Exception) { /* temp store: cleanup is best effort */ }
        try { Directory.Delete(_tempRoot, recursive: true); } catch (Exception) { /* best effort */ }
    }

    // ── Retail stage ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Audiobook_WithNoRetailMatch_TakesQidOfOwnedConfirmedBookWithSameTitleAndAuthor()
    {
        await SeedBookAsync("Dune", "Frank Herbert", BookQid);
        var audiobook = await SeedAudiobookAsync("Dune", "Frank Herbert");

        await RunRetailAsync();

        var job = await _jobs.GetByIdAsync(audiobook.JobId);
        Assert.Equal(nameof(IdentityJobState.RetailMatched), job!.State);
        Assert.Null(job.SelectedCandidateId);

        // The hand-off rides on this job alone; the Wikidata stage writes the claims.
        Assert.Equal(BookQid, job.ResolvedQid);
        Assert.DoesNotContain(
            await _claims.GetByEntityAsync(audiobook.AssetId),
            c => c.ClaimKey == MetadataFieldConstants.QidResolutionMethod);

        Assert.Empty(await _reviews.GetPendingByEntityAsync(audiobook.AssetId));
        var timeline = await _timelineEvents.GetEventsByEntityAsync(audiobook.AssetId);
        Assert.DoesNotContain(timeline, e => e.EventType is "retail_matched" or "retail_no_match");
        var linked = Assert.Single(timeline, e => e.EventType == "owned_book_matched");
        Assert.Contains("owned book", linked.Detail);
        Assert.Contains("Dune", linked.Detail);
    }

    [Fact]
    public async Task Audiobook_WithNoRetailMatch_WhenBookHasSameTitleButDifferentAuthor_StillGoesToNeedsReview()
    {
        await SeedBookAsync("Dune", "Brian Herbert", BookQid);
        var audiobook = await SeedAudiobookAsync("Dune", "Frank Herbert");

        await RunRetailAsync();

        await AssertNoMatchAsync(audiobook);
    }

    [Fact]
    public async Task Audiobook_WithNoRetailMatch_WhenOnlyANarratorMatchesTheBookAuthor_StillGoesToNeedsReview()
    {
        await SeedBookAsync("Dune", "Scott Brick", BookQid);
        var audiobook = await SeedAudiobookAsync("Dune", "Frank Herbert", narrator: "Scott Brick");

        await RunRetailAsync();

        await AssertNoMatchAsync(audiobook);
    }

    [Fact]
    public async Task Audiobook_WithNoRetailMatch_WhenBookHasNoWikidataId_StillGoesToNeedsReview()
    {
        await SeedBookAsync("Dune", "Frank Herbert", qid: null);
        var audiobook = await SeedAudiobookAsync("Dune", "Frank Herbert");

        await RunRetailAsync();

        await AssertNoMatchAsync(audiobook);
    }

    [Theory]
    [InlineData(nameof(IdentityJobState.RetailMatchedNeedsReview))]
    [InlineData(nameof(IdentityJobState.QidNeedsReview))]
    public async Task Audiobook_WithNoRetailMatch_WhenBookIdentityIsWaitingInNeedsReview_StillGoesToNeedsReview(
        string bookJobState)
    {
        var book = await SeedBookAsync("Dune", "Frank Herbert", BookQid);
        await _jobs.CreateAsync(new IdentityJob
        {
            Id = Guid.NewGuid(),
            EntityId = book.AssetId,
            EntityType = "MediaAsset",
            MediaType = "Books",
            State = bookJobState,
        });
        var audiobook = await SeedAudiobookAsync("Dune", "Frank Herbert");

        await RunRetailAsync();

        await AssertNoMatchAsync(audiobook);
    }

    [Fact]
    public async Task Audiobook_WithNoRetailMatch_WhenBookHasAnOpenIdentityReviewItem_StillGoesToNeedsReview()
    {
        var book = await SeedBookAsync("Dune", "Frank Herbert", BookQid);
        await _reviews.InsertAsync(new ReviewQueueEntry
        {
            EntityId = book.AssetId,
            EntityType = "MediaAsset",
            Trigger = ReviewTrigger.LowConfidence,
        });
        var audiobook = await SeedAudiobookAsync("Dune", "Frank Herbert");

        await RunRetailAsync();

        await AssertNoMatchAsync(audiobook);
    }

    [Fact]
    public async Task Audiobook_WithNoRetailMatch_WhenBookIdentityWasSettledAfterItsReviewWasResolved_TakesItsQid()
    {
        var book = await SeedBookAsync("Dune", "Frank Herbert", BookQid);
        var reviewId = await _reviews.InsertAsync(new ReviewQueueEntry
        {
            EntityId = book.AssetId,
            EntityType = "MediaAsset",
            Trigger = ReviewTrigger.LowConfidence,
        });
        await _reviews.UpdateStatusAsync(reviewId, ReviewStatus.Resolved, "test");
        await _jobs.CreateAsync(new IdentityJob
        {
            Id = Guid.NewGuid(),
            EntityId = book.AssetId,
            EntityType = "MediaAsset",
            MediaType = "Books",
            State = nameof(IdentityJobState.Ready),
        });
        var audiobook = await SeedAudiobookAsync("Dune", "Frank Herbert");

        await RunRetailAsync();

        var job = await _jobs.GetByIdAsync(audiobook.JobId);
        Assert.Equal(nameof(IdentityJobState.RetailMatched), job!.State);
    }

    [Fact]
    public async Task Audiobook_WithNoRetailMatch_WhenNoBookIsOwned_StillGoesToNeedsReview()
    {
        var audiobook = await SeedAudiobookAsync("Dune", "Frank Herbert");

        await RunRetailAsync();

        await AssertNoMatchAsync(audiobook);
    }

    [Fact]
    public async Task Book_WithNoRetailMatch_StillGoesToNeedsReview_EvenWhenAnOwnedAudiobookHasAWikidataId()
    {
        await SeedAudiobookAsync("Dune", "Frank Herbert", qid: BookQid, queued: false);
        var book = await SeedBookAsync("Dune", "Frank Herbert", qid: null, queued: true);

        await RunRetailAsync();

        var job = await _jobs.GetByIdAsync(book.JobId!.Value);
        Assert.Equal(nameof(IdentityJobState.RetailNoMatch), job!.State);
        var review = Assert.Single(await _reviews.GetPendingByEntityAsync(book.AssetId));
        Assert.Equal(ReviewTrigger.RetailMatchFailed, review.Trigger);
        Assert.DoesNotContain(
            await _claims.GetByEntityAsync(book.AssetId),
            c => c.ClaimKey == MetadataFieldConstants.QidResolutionMethod);
    }

    // ── Wikidata stage ────────────────────────────────────────────────────────

    [Fact]
    public async Task Audiobook_HandedOffFromOwnedBook_FinalisesWithThatQid_WithoutAnyOutsideLookup()
    {
        await SeedBookAsync("Dune", "Frank Herbert", BookQid);
        var audiobook = await SeedAudiobookAsync("Dune", "Frank Herbert");
        await RunRetailAsync();

        var processed = await RunBridgeAsync();

        Assert.Equal(1, processed);
        var job = await _jobs.GetByIdAsync(audiobook.JobId);
        Assert.Equal(nameof(IdentityJobState.QidResolved), job!.State);
        Assert.Equal(BookQid, job.ResolvedQid);

        var claims = await _claims.GetByEntityAsync(audiobook.AssetId);
        Assert.All(claims.Where(c => c.ClaimKey == BridgeIdKeys.WikidataQid), c => Assert.Equal(BookQid, c.ClaimValue));
        Assert.Contains(claims, c => c.ClaimKey == BridgeIdKeys.WikidataQid);
        Assert.Equal(
            QidResolutionMethods.OwnedBookNoRetail,
            Assert.Single(claims, c => c.ClaimKey == MetadataFieldConstants.QidResolutionMethod).ClaimValue);

        var resolved = Assert.Single(
            await _timelineEvents.GetEventsByEntityAsync(audiobook.AssetId),
            e => e.EventType == "wikidata_bridge_resolved");
        Assert.DoesNotContain("owned_book_no_retail", resolved.Detail);
        Assert.Contains("owned book", resolved.Detail);

        var candidate = Assert.Single(await new WikidataCandidateRepository(_db).GetByJobAsync(audiobook.JobId));
        Assert.Equal(BookQid, candidate.Qid);
        Assert.Equal(QidResolutionMethods.OwnedBookNoRetail, candidate.MatchedBy);

        // No search, bridge-ID or text lookup went out: the only requests read the chosen QID itself
        // (hydration of the resolved Work), as for any resolved Work.
        Assert.NotEmpty(_http.Urls);
        Assert.All(_http.Urls, url =>
        {
            Assert.Contains("action=wbgetentities", url, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"ids={BookQid}", url, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Audiobook_RetailMatchedWithACandidate_IgnoresAnOwnedBookHandoffAndLooksUpAsUsual()
    {
        var audiobook = await SeedAudiobookAsync("Dune", "Frank Herbert", queued: false);
        await SeedIsbnAndRetailCandidateAsync(audiobook);
        await _jobs.SetResolvedQidAsync(audiobook.JobId, BookQid);

        await RunBridgeAsync();

        // The lookups the hand-off would have skipped did go out for this retail-matched job.
        Assert.NotEmpty(_http.Urls);
        var candidates = await new WikidataCandidateRepository(_db).GetByJobAsync(audiobook.JobId);
        Assert.DoesNotContain(candidates, c => c.MatchedBy == QidResolutionMethods.OwnedBookNoRetail);
    }

    [Fact]
    public async Task Audiobook_RematchedByTheUserAfterAnOwnedBookHandoff_DoesTheNormalLookups()
    {
        await SeedBookAsync("Dune", "Frank Herbert", BookQid);
        var audiobook = await SeedAudiobookAsync("Dune", "Frank Herbert");
        await RunRetailAsync();
        await _bridgeIds.UpsertBatchAsync(
        [
            new BridgeIdEntry { EntityId = audiobook.AssetId, IdType = BridgeIdKeys.Isbn13, IdValue = "9780441172719" },
        ]);

        // What a rematch in Editing Mode does: resume the job with the retail stage skipped. The job is
        // built fresh, so it carries neither the earlier hand-off nor a stored one after the resume.
        var rematch = new IdentityJob
        {
            EntityId = audiobook.AssetId,
            EntityType = "MediaAsset",
            MediaType = "Audiobooks",
            State = nameof(IdentityJobState.RetailMatched),
        };
        rematch.Id = await _jobs.CreateOrResumeUserResolutionAsync(rematch);
        Assert.Equal(audiobook.JobId, rematch.Id);

        await CreateBridgeWorker().ProcessJobAsync(rematch, CancellationToken.None);

        Assert.Contains(_http.Urls, url => !url.Contains($"ids={BookQid}", StringComparison.OrdinalIgnoreCase));
        var candidates = await new WikidataCandidateRepository(_db).GetByJobAsync(audiobook.JobId);
        Assert.DoesNotContain(candidates, c => c.MatchedBy == QidResolutionMethods.OwnedBookNoRetail);
    }

    private async Task SeedIsbnAndRetailCandidateAsync(SeededAudiobook audiobook)
    {
        await _bridgeIds.UpsertBatchAsync(
        [
            new BridgeIdEntry { EntityId = audiobook.AssetId, IdType = BridgeIdKeys.Isbn13, IdValue = "9780441172719" },
        ]);
        var candidateId = Guid.NewGuid();
        await new RetailCandidateRepository(_db).InsertBatchAsync(
        [
            new RetailMatchCandidate
            {
                Id = candidateId,
                JobId = audiobook.JobId,
                ProviderId = WellKnownProviders.AppleApi,
                ProviderName = "apple_api",
                Title = "Dune",
                Creator = "Frank Herbert",
                ScoreTotal = 0.95,
                Outcome = "AutoAccepted",
            },
        ]);
        await _jobs.SetSelectedCandidateAsync(audiobook.JobId, candidateId);
        await _jobs.UpdateStateAsync(audiobook.JobId, IdentityJobState.RetailMatched);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task AssertNoMatchAsync(SeededAudiobook audiobook)
    {
        var job = await _jobs.GetByIdAsync(audiobook.JobId);
        Assert.Equal(nameof(IdentityJobState.RetailNoMatch), job!.State);
        var review = Assert.Single(await _reviews.GetPendingByEntityAsync(audiobook.AssetId));
        Assert.Equal(ReviewTrigger.RetailMatchFailed, review.Trigger);
        Assert.DoesNotContain(
            await _claims.GetByEntityAsync(audiobook.AssetId),
            c => c.ClaimKey == MetadataFieldConstants.QidResolutionMethod);
        var timeline = await _timelineEvents.GetEventsByEntityAsync(audiobook.AssetId);
        Assert.Contains(timeline, e => e.EventType == "retail_no_match");
        Assert.DoesNotContain(timeline, e => e.EventType == "owned_book_matched");
    }

    private async Task RunRetailAsync()
    {
        var worker = new RetailMatchWorker(
            _jobs,
            new RetailCandidateRepository(_db),
            _outcomes,
            _timeline,
            [new NoMatchRetailProvider()],
            new RetailMatchScoringService(new FuzzyMatchingService(), _config),
            _claims,
            _canonicals,
            _scoring,
            _config,
            _bridgeIds,
            _works,
            new WorkClaimRouter(),
            new ThrowingHttpClientFactory(),
            null!, // PostPipelineService: only reached after a retail match
            NullLogger<RetailMatchWorker>.Instance,
            arrayRepo: _arrays);

        await worker.PollAsync(CancellationToken.None);
    }

    private Task<int> RunBridgeAsync() => CreateBridgeWorker().PollAsync(CancellationToken.None);

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
            _scoring,
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
            _scoring,
            _config,
            _works,
            new WorkClaimRouter(),
            new CatalogUpsertService(_works),
            postPipeline,
            CoverArtWorkerTestFactory.Create(_canonicals, _works),
            NullLogger<WikidataBridgeWorker>.Instance,
            arrayRepo: _arrays);
    }

    private sealed record SeededBook(Guid WorkId, Guid AssetId, Guid? JobId);

    private sealed record SeededAudiobook(Guid WorkId, Guid AssetId, Guid JobId);

    private async Task<SeededBook> SeedBookAsync(string title, string author, string? qid, bool queued = false)
    {
        var (workId, assetId) = await SeedOwnedWorkAsync(MediaType.Books, title, author, qid);
        Guid? jobId = null;
        if (queued)
        {
            jobId = Guid.NewGuid();
            await _jobs.CreateAsync(new IdentityJob
            {
                Id = jobId.Value,
                EntityId = assetId,
                EntityType = "MediaAsset",
                MediaType = "Books",
                State = nameof(IdentityJobState.Queued),
            });
        }

        return new SeededBook(workId, assetId, jobId);
    }

    private async Task<SeededAudiobook> SeedAudiobookAsync(
        string title,
        string author,
        string? narrator = null,
        string? qid = null,
        bool queued = true)
    {
        var (workId, assetId) = await SeedOwnedWorkAsync(MediaType.Audiobooks, title, author, qid, narrator);
        var jobId = Guid.NewGuid();
        await _jobs.CreateAsync(new IdentityJob
        {
            Id = jobId,
            EntityId = assetId,
            EntityType = "MediaAsset",
            MediaType = "Audiobooks",
            // A job that is not queued is parked where the retail worker will not pick it up.
            State = queued ? nameof(IdentityJobState.Queued) : nameof(IdentityJobState.RetailMatched),
        });
        return new SeededAudiobook(workId, assetId, jobId);
    }

    private async Task<(Guid WorkId, Guid AssetId)> SeedOwnedWorkAsync(
        MediaType mediaType,
        string title,
        string author,
        string? qid,
        string? narrator = null)
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
                "INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status) VALUES (@assetId, @editionId, @hash, @path, 'Normal')",
                new
                {
                    assetId,
                    editionId,
                    hash = Guid.NewGuid().ToString("N"),
                    path = Path.Combine(_tempRoot, $"{assetId:N}.media"),
                });
        }

        var scalars = new List<CanonicalValue>
        {
            new() { EntityId = assetId, Key = MetadataFieldConstants.Title, Value = title, LastScoredAt = DateTimeOffset.UtcNow },
        };
        if (qid is not null)
        {
            scalars.Add(new CanonicalValue { EntityId = assetId, Key = BridgeIdKeys.WikidataQid, Value = qid, LastScoredAt = DateTimeOffset.UtcNow });
        }

        await _canonicals.UpsertBatchAsync(scalars);
        await _arrays.SetValuesAsync(
            assetId,
            MetadataFieldConstants.Author,
            [new CanonicalArrayEntry { Ordinal = 0, Value = author }]);
        if (narrator is not null)
        {
            await _arrays.SetValuesAsync(
                assetId,
                MetadataFieldConstants.Narrator,
                [new CanonicalArrayEntry { Ordinal = 0, Value = narrator }]);
        }

        return (workId, assetId);
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

    // ── Spies ─────────────────────────────────────────────────────────────────

    private sealed class NoMatchRetailProvider : IExternalMetadataProvider
    {
        public string Name => "apple_api";
        public ProviderDomain Domain => ProviderDomain.Universal;
        public IReadOnlyList<string> CapabilityTags => [];
        public Guid ProviderId => WellKnownProviders.AppleApi;
        public bool CanHandle(MediaType mediaType) => true;
        public bool CanHandle(EntityType entityType) => true;

        public Task<IReadOnlyList<ProviderClaim>> FetchAsync(ProviderLookupRequest request, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ProviderClaim>>([]);
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
            => throw new NotSupportedException($"CreateClient('{name}') should not be called for audiobooks.");
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
}
