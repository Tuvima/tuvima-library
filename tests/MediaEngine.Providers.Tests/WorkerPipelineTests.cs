using System.Net;
using System.Text;
using System.Text.Json;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Intelligence;
using MediaEngine.Intelligence.Contracts;
using MediaEngine.Intelligence.Models;
using MediaEngine.Intelligence.Services;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Providers.Workers;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using MediaEngine.Storage.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
// Disambiguate ProviderConfiguration — the IConfigurationLoader uses the Storage.Models one
using ProviderConfiguration = MediaEngine.Domain.Configuration.ProviderConfiguration;

namespace MediaEngine.Providers.Tests;

public sealed class WorkerPipelineTests
{
    // ── Test 1: RetailMatchWorker auto-accepts when composite score ≥ 0.85 ──

    [Fact]
    public void WikidataOutputsCannotCorroborateLaterReconciliation()
    {
        var entity = Guid.NewGuid();
        var bridge = new BridgeIdEntry
        {
            EntityId = entity, IdType = BridgeIdKeys.MusicBrainzReleaseGroupId,
            IdValue = "wrong-previous-result", ProviderId = WellKnownProviders.Wikidata.ToString(),
        };
        var scoped = WikidataBridgeWorker.CollectScopedBridgeIdsForResolution(entity, MediaType.Music, null,
            new Dictionary<Guid, IReadOnlyList<BridgeIdEntry>> { [entity] = [bridge] });
        Assert.Empty(scoped);
        var merged = WikidataBridgeWorker.MergeCanonicalBridgeIdsForResolution(entity, MediaType.Music, null, scoped,
            [new CanonicalValue { EntityId = entity, Key = bridge.IdType, Value = bridge.IdValue,
                WinningProviderId = WellKnownProviders.Wikidata }]);
        Assert.Empty(merged);
    }

    [Fact]
    public async Task RetailMatchWorker_AutoAccepted_TransitionsToRetailMatched()
    {
        var entityId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var providerId = Guid.NewGuid();

        var jobRepo = new StubIdentityJobRepository();
        var candidateRepo = new StubRetailCandidateRepository();

        var job = new IdentityJob
        {
            Id = jobId,
            EntityId = entityId,
            EntityType = "MediaAsset",
            MediaType = "Books",
            State = "Queued",
        };
        await jobRepo.CreateAsync(job);

        var provider = new StubExternalMetadataProvider
        {
            Name = "apple_api",
            ProviderId = providerId,
            Claims =
            [
                new ProviderClaim(MetadataFieldConstants.Title, "Dune", 0.95),
                new ProviderClaim(MetadataFieldConstants.Author, "Frank Herbert", 0.95),
            ],
        };

        var retailScoring = new StubRetailMatchScoringService
        {
            Result = new FieldMatchScores
            {
                TitleScore = 0.95,
                AuthorScore = 0.90,
                YearScore = 0.0,
                FormatScore = 1.0,
                CrossFieldBoost = 0.0,
                CoverArtScore = 0.0,
                CompositeScore = 0.90,
            },
        };

        var configLoader = new StubConfigurationLoader();
        var canonicalRepo = new StubCanonicalValueRepository();
        var claimRepo = new StubMetadataClaimRepository();
        var bridgeIdRepo = new StubBridgeIdRepository();
        var scoringEngine = new StubScoringEngine();
        var outcomeFactory = CreateStubStageOutcomeFactory();
        var timeline = CreateStubTimelineRecorder();
        var batchProgress = CreateStubBatchProgressService();

        var worker = new RetailMatchWorker(
            jobRepo,
            candidateRepo,
            outcomeFactory,
            timeline,
            batchProgress,
            new[] { provider },
            retailScoring,
            claimRepo,
            canonicalRepo,
            scoringEngine,
            configLoader,
            bridgeIdRepo,
            new StubWorkRepository(),
            new WorkClaimRouter(),
            new StubHttpClientFactory(),
            null!, // PostPipelineService — not exercised in this test
            NullLogger<RetailMatchWorker>.Instance);

        var processed = await worker.PollAsync(CancellationToken.None);

        Assert.Equal(1, processed);

        var updatedJob = await jobRepo.GetByIdAsync(jobId);
        Assert.NotNull(updatedJob);
        Assert.Equal(IdentityJobState.RetailMatched.ToString(), updatedJob!.State);

        Assert.Single(candidateRepo.Candidates);
        Assert.Equal("AutoAccepted", candidateRepo.Candidates[0].Outcome);
    }

    [Fact]
    public async Task RetailMatchWorker_WeakCreatorSimilarity_CapsAutoAcceptToReview()
    {
        var entityId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var providerId = Guid.NewGuid();

        var jobRepo = new StubIdentityJobRepository();
        var candidateRepo = new StubRetailCandidateRepository();

        var job = new IdentityJob
        {
            Id = jobId,
            EntityId = entityId,
            EntityType = "MediaAsset",
            MediaType = "Books",
            State = "Queued",
        };
        await jobRepo.CreateAsync(job);

        var provider = new StubExternalMetadataProvider
        {
            Name = "apple_api",
            ProviderId = providerId,
            Claims =
            [
                new ProviderClaim(MetadataFieldConstants.Title, "Dune", 0.95),
                new ProviderClaim(MetadataFieldConstants.Author, "Wrong Author", 0.95),
            ],
        };

        var retailScoring = new StubRetailMatchScoringService
        {
            Result = new FieldMatchScores
            {
                TitleScore = 0.95,
                AuthorScore = 0.20,
                YearScore = 0.0,
                FormatScore = 1.0,
                CrossFieldBoost = 0.0,
                CoverArtScore = 0.0,
                CompositeScore = 0.95,
            },
        };

        var configLoader = new StubConfigurationLoader
        {
            Providers =
            [
                new() { Name = "apple_api", Enabled = true, ProviderId = provider.ProviderId.ToString(), Weight = 0.9 },
            ],
        };
        var canonicalRepo = new StubCanonicalValueRepository();
        await canonicalRepo.UpsertBatchAsync(
        [
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Title, Value = "Dune", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Author, Value = "Frank Herbert", LastScoredAt = DateTimeOffset.UtcNow },
        ]);
        var claimRepo = new StubMetadataClaimRepository();
        var bridgeIdRepo = new StubBridgeIdRepository();
        var scoringEngine = new StubScoringEngine();
        var outcomeFactory = CreateStubStageOutcomeFactory();
        var timeline = CreateStubTimelineRecorder();
        var batchProgress = CreateStubBatchProgressService();

        var worker = new RetailMatchWorker(
            jobRepo,
            candidateRepo,
            outcomeFactory,
            timeline,
            batchProgress,
            new[] { provider },
            retailScoring,
            claimRepo,
            canonicalRepo,
            scoringEngine,
            configLoader,
            bridgeIdRepo,
            new StubWorkRepository(),
            new WorkClaimRouter(),
            new StubHttpClientFactory(),
            null!,
            NullLogger<RetailMatchWorker>.Instance);

        var processed = await worker.PollAsync(CancellationToken.None);

        Assert.Equal(1, processed);

        var updatedJob = await jobRepo.GetByIdAsync(jobId);
        Assert.NotNull(updatedJob);
        Assert.Equal(IdentityJobState.RetailMatchedNeedsReview.ToString(), updatedJob!.State);

        var candidate = Assert.Single(candidateRepo.Candidates);
        Assert.Equal("Ambiguous", candidate.Outcome);
        Assert.Contains("creator_similarity_weak", candidate.ScoreBreakdownJson);
        Assert.Contains("accept_capped_to_review", candidate.ScoreBreakdownJson);
    }

    [Fact]
    public async Task RetailMatchWorker_UsesMetadataClaimAuthor_WhenCanonicalAuthorMissing()
    {
        var entityId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        var localProviderId = Guid.NewGuid();

        var jobRepo = new StubIdentityJobRepository();
        var candidateRepo = new StubRetailCandidateRepository();

        await jobRepo.CreateAsync(new IdentityJob
        {
            Id = jobId,
            EntityId = entityId,
            EntityType = "MediaAsset",
            MediaType = "Books",
            State = "Queued",
        });

        var canonicalRepo = new StubCanonicalValueRepository();
        await canonicalRepo.UpsertBatchAsync(
        [
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Title, Value = "Dune", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = "date", Value = "2005-08-02", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = BridgeIdKeys.Isbn, Value = "9780441013593", LastScoredAt = DateTimeOffset.UtcNow },
        ]);

        var claimRepo = new StubMetadataClaimRepository();
        await claimRepo.InsertBatchAsync(
        [
            new MetadataClaim
            {
                Id = Guid.NewGuid(),
                EntityId = entityId,
                ProviderId = localProviderId,
                ClaimKey = MetadataFieldConstants.Author,
                ClaimValue = "Frank Herbert",
                Confidence = 0.95,
                ClaimedAt = DateTimeOffset.UtcNow,
            },
        ]);

        var provider = new StubExternalMetadataProvider
        {
            Name = "apple_api",
            ProviderId = providerId,
            Claims =
            [
                new ProviderClaim(MetadataFieldConstants.Title, "Dune", 0.95),
                new ProviderClaim(MetadataFieldConstants.Author, "Frank Herbert", 0.95),
                new ProviderClaim(MetadataFieldConstants.Year, "2011", 0.95),
                new ProviderClaim("provider_item_id", "597944491", 0.95),
                new ProviderClaim(BridgeIdKeys.AppleBooksId, "597944491", 0.95),
            ],
        };

        var configLoader = new StubConfigurationLoader
        {
            Providers =
            [
                new() { Name = "apple_api", Enabled = true, ProviderId = provider.ProviderId.ToString(), Weight = 0.9 },
            ],
        };
        var worker = new RetailMatchWorker(
            jobRepo,
            candidateRepo,
            CreateStubStageOutcomeFactory(),
            CreateStubTimelineRecorder(),
            CreateStubBatchProgressService(),
            new[] { provider },
            new RetailMatchScoringService(
                new ExactMatchFuzzyMatchingService(),
                configLoader,
                coverArtHash: null,
                logger: null),
            claimRepo,
            canonicalRepo,
            new StubScoringEngine(),
            configLoader,
            new StubBridgeIdRepository(),
            new StubWorkRepository(),
            new WorkClaimRouter(),
            new StubHttpClientFactory(),
            null!,
            NullLogger<RetailMatchWorker>.Instance);

        var processed = await worker.PollAsync(CancellationToken.None);

        Assert.Equal(1, processed);

        var updatedJob = await jobRepo.GetByIdAsync(jobId);
        Assert.NotNull(updatedJob);
        Assert.Equal(IdentityJobState.RetailMatchedNeedsReview.ToString(), updatedJob!.State);

        var candidate = Assert.Single(candidateRepo.Candidates);
        Assert.Equal("Ambiguous", candidate.Outcome);
        Assert.InRange(candidate.ScoreTotal, 0.65, 0.8999);
        Assert.NotNull(candidate.ScoreBreakdownJson);

        using var breakdown = JsonDocument.Parse(candidate.ScoreBreakdownJson!);
        Assert.Equal(1.0, breakdown.RootElement.GetProperty("author").GetDouble());
    }

    [Fact]
    public async Task RetailMatchWorker_ComicSeriesAndIssueMatch_AutoAcceptsWithoutCreator()
    {
        var entityId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var providerId = Guid.NewGuid();

        var jobRepo = new StubIdentityJobRepository();
        var candidateRepo = new StubRetailCandidateRepository();

        await jobRepo.CreateAsync(new IdentityJob
        {
            Id = jobId,
            EntityId = entityId,
            EntityType = "MediaAsset",
            MediaType = "Comics",
            State = "Queued",
        });

        var provider = new StubExternalMetadataProvider
        {
            Name = "comicvine",
            ProviderId = providerId,
            Claims =
            [
                new ProviderClaim(MetadataFieldConstants.Title, "Batman: Year One Part 1", 0.95),
                new ProviderClaim(MetadataFieldConstants.Series, "Batman", 0.95),
                new ProviderClaim(MetadataFieldConstants.SeriesPosition, "1", 0.95),
                new ProviderClaim("issue_number", "1", 0.95),
                new ProviderClaim(BridgeIdKeys.ComicVineId, "12345", 0.95),
            ],
        };

        var configLoader = new StubConfigurationLoader
        {
            Providers =
            [
                new() { Name = "comicvine", Enabled = true, ProviderId = provider.ProviderId.ToString(), Weight = 0.9 },
            ],
        };
        var canonicalRepo = new StubCanonicalValueRepository();
        await canonicalRepo.UpsertBatchAsync(
        [
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Title, Value = "Batman: Year One Part 1", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Author, Value = "Frank Miller", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Series, Value = "Batman", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.SeriesPosition, Value = "1", LastScoredAt = DateTimeOffset.UtcNow },
        ]);

        var worker = new RetailMatchWorker(
            jobRepo,
            candidateRepo,
            CreateStubStageOutcomeFactory(),
            CreateStubTimelineRecorder(),
            CreateStubBatchProgressService(),
            new[] { provider },
            new RetailMatchScoringService(
                new ExactMatchFuzzyMatchingService(),
                configLoader,
                coverArtHash: null,
                logger: null),
            new StubMetadataClaimRepository(),
            canonicalRepo,
            new StubScoringEngine(),
            configLoader,
            new StubBridgeIdRepository(),
            new StubWorkRepository(),
            new WorkClaimRouter(),
            new StubHttpClientFactory(),
            null!,
            NullLogger<RetailMatchWorker>.Instance);

        var processed = await worker.PollAsync(CancellationToken.None);

        Assert.Equal(1, processed);

        var updatedJob = await jobRepo.GetByIdAsync(jobId);
        Assert.NotNull(updatedJob);
        Assert.Equal(IdentityJobState.RetailMatched.ToString(), updatedJob!.State);

        var candidate = Assert.Single(candidateRepo.Candidates);
        Assert.Equal("AutoAccepted", candidate.Outcome);
        using var scores = JsonDocument.Parse(candidate.ScoreBreakdownJson!);
        var fields = scores.RootElement.GetProperty("field_scores").EnumerateArray().ToArray();
        Assert.Contains(fields, row => row.GetProperty("Key").GetString() == "series" && row.GetProperty("Score").GetDouble() == 1);
        Assert.Contains(fields, row => row.GetProperty("Key").GetString() == "issue" && row.GetProperty("Score").GetDouble() == 1);
    }

    [Fact]
    public async Task RetailMatchWorker_ComicLegacyIssueNumber_TitleAnchorsSpecificIssue()
    {
        var entityId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var providerId = Guid.NewGuid();

        var jobRepo = new StubIdentityJobRepository();
        var candidateRepo = new StubRetailCandidateRepository();

        await jobRepo.CreateAsync(new IdentityJob
        {
            Id = jobId,
            EntityId = entityId,
            EntityType = "MediaAsset",
            MediaType = "Comics",
            State = "Queued",
        });

        var provider = new StubExternalMetadataProvider
        {
            Name = "comicvine",
            ProviderId = providerId,
            Claims =
            [
                new ProviderClaim(MetadataFieldConstants.Title, "Batman: Year One Part 1", 0.95),
                new ProviderClaim(MetadataFieldConstants.Series, "Batman", 0.95),
                new ProviderClaim(MetadataFieldConstants.SeriesPosition, "1", 0.95),
                new ProviderClaim("issue_number", "1", 0.95),
                new ProviderClaim(BridgeIdKeys.ComicVineId, "712097", 0.95),
            ],
        };

        var configLoader = new StubConfigurationLoader
        {
            Providers =
            [
                new() { Name = "comicvine", Enabled = true, ProviderId = provider.ProviderId.ToString(), Weight = 0.9 },
            ],
        };
        var canonicalRepo = new StubCanonicalValueRepository();
        await canonicalRepo.UpsertBatchAsync(
        [
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Title, Value = "Batman: Year One Part 1", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Author, Value = "Frank Miller", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Series, Value = "Batman", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.SeriesPosition, Value = "404", LastScoredAt = DateTimeOffset.UtcNow },
        ]);

        var worker = new RetailMatchWorker(
            jobRepo,
            candidateRepo,
            CreateStubStageOutcomeFactory(),
            CreateStubTimelineRecorder(),
            CreateStubBatchProgressService(),
            new[] { provider },
            new RetailMatchScoringService(
                new ExactMatchFuzzyMatchingService(),
                configLoader,
                coverArtHash: null,
                logger: null),
            new StubMetadataClaimRepository(),
            canonicalRepo,
            new StubScoringEngine(),
            configLoader,
            new StubBridgeIdRepository(),
            new StubWorkRepository(),
            new WorkClaimRouter(),
            new StubHttpClientFactory(),
            null!,
            NullLogger<RetailMatchWorker>.Instance);

        var processed = await worker.PollAsync(CancellationToken.None);

        Assert.Equal(1, processed);

        var updatedJob = await jobRepo.GetByIdAsync(jobId);
        Assert.NotNull(updatedJob);
        Assert.Equal(IdentityJobState.RetailNoMatch.ToString(), updatedJob!.State);

        var candidate = Assert.Single(candidateRepo.Candidates);
        Assert.Equal("Rejected", candidate.Outcome);
        Assert.True(candidate.ScoreTotal < 0.65);
    }

    [Fact]
    public async Task RetailMatchWorker_ComicExactTitleStillAnchorsWhenProviderSeriesIsMoreSpecific()
    {
        var entityId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var providerId = Guid.NewGuid();

        var jobRepo = new StubIdentityJobRepository();
        var candidateRepo = new StubRetailCandidateRepository();

        await jobRepo.CreateAsync(new IdentityJob
        {
            Id = jobId,
            EntityId = entityId,
            EntityType = "MediaAsset",
            MediaType = "Comics",
            State = "Queued",
        });

        var provider = new StubExternalMetadataProvider
        {
            Name = "comicvine",
            ProviderId = providerId,
            Claims =
            [
                new ProviderClaim(MetadataFieldConstants.Title, "Batman: Year One Part 1", 0.95),
                new ProviderClaim(MetadataFieldConstants.Series, "Batman: Year One", 0.95),
                new ProviderClaim(MetadataFieldConstants.SeriesPosition, "1", 0.95),
                new ProviderClaim("issue_number", "1", 0.95),
                new ProviderClaim(BridgeIdKeys.ComicVineId, "712097", 0.95),
            ],
        };

        var configLoader = new StubConfigurationLoader();
        var canonicalRepo = new StubCanonicalValueRepository();
        await canonicalRepo.UpsertBatchAsync(
        [
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Title, Value = "Batman: Year One Part 1", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Series, Value = "Batman", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.SeriesPosition, Value = "404", LastScoredAt = DateTimeOffset.UtcNow },
        ]);

        var worker = new RetailMatchWorker(
            jobRepo,
            candidateRepo,
            CreateStubStageOutcomeFactory(),
            CreateStubTimelineRecorder(),
            CreateStubBatchProgressService(),
            new[] { provider },
            new RetailMatchScoringService(
                new ExactMatchFuzzyMatchingService(),
                configLoader,
                coverArtHash: null,
                logger: null),
            new StubMetadataClaimRepository(),
            canonicalRepo,
            new StubScoringEngine(),
            configLoader,
            new StubBridgeIdRepository(),
            new StubWorkRepository(),
            new WorkClaimRouter(),
            new StubHttpClientFactory(),
            null!,
            NullLogger<RetailMatchWorker>.Instance);

        var processed = await worker.PollAsync(CancellationToken.None);

        Assert.Equal(1, processed);

        var updatedJob = await jobRepo.GetByIdAsync(jobId);
        Assert.NotNull(updatedJob);
        Assert.Equal(IdentityJobState.RetailNoMatch.ToString(), updatedJob!.State);

        var candidate = Assert.Single(candidateRepo.Candidates);
        Assert.Equal("Rejected", candidate.Outcome);
        Assert.True(candidate.ScoreTotal < 0.65);
    }

    [Fact]
    public async Task RetailMatchWorker_ComicSeriesAndIssueAnchor_WhenIssueTitleDiffers()
    {
        var entityId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var providerId = Guid.NewGuid();

        var jobRepo = new StubIdentityJobRepository();
        var candidateRepo = new StubRetailCandidateRepository();

        await jobRepo.CreateAsync(new IdentityJob
        {
            Id = jobId,
            EntityId = entityId,
            EntityType = "MediaAsset",
            MediaType = "Comics",
            State = "Queued",
        });

        var provider = new StubExternalMetadataProvider
        {
            Name = "comicvine",
            ProviderId = providerId,
            Claims =
            [
                new ProviderClaim(MetadataFieldConstants.Title, "Two Riders Were Approaching...", 0.95),
                new ProviderClaim(MetadataFieldConstants.Series, "Watchmen", 0.95),
                new ProviderClaim(MetadataFieldConstants.SeriesPosition, "2", 0.95),
                new ProviderClaim("issue_number", "2", 0.95),
                new ProviderClaim(BridgeIdKeys.ComicVineId, "222", 0.95),
            ],
        };

        var configLoader = new StubConfigurationLoader();
        var canonicalRepo = new StubCanonicalValueRepository();
        await canonicalRepo.UpsertBatchAsync(
        [
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Title, Value = "Watchmen #2", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Author, Value = "Alan Moore", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Series, Value = "Watchmen", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.SeriesPosition, Value = "2", LastScoredAt = DateTimeOffset.UtcNow },
        ]);

        var worker = new RetailMatchWorker(
            jobRepo,
            candidateRepo,
            CreateStubStageOutcomeFactory(),
            CreateStubTimelineRecorder(),
            CreateStubBatchProgressService(),
            new[] { provider },
            new RetailMatchScoringService(
                new ExactMatchFuzzyMatchingService(),
                configLoader,
                coverArtHash: null,
                logger: null),
            new StubMetadataClaimRepository(),
            canonicalRepo,
            new StubScoringEngine(),
            configLoader,
            new StubBridgeIdRepository(),
            new StubWorkRepository(),
            new WorkClaimRouter(),
            new StubHttpClientFactory(),
            null!,
            NullLogger<RetailMatchWorker>.Instance);

        var processed = await worker.PollAsync(CancellationToken.None);

        Assert.Equal(1, processed);

        var updatedJob = await jobRepo.GetByIdAsync(jobId);
        Assert.NotNull(updatedJob);
        Assert.Equal(IdentityJobState.RetailMatched.ToString(), updatedJob!.State);

        var candidate = Assert.Single(candidateRepo.Candidates);
        Assert.Equal("AutoAccepted", candidate.Outcome);
        using var scores = JsonDocument.Parse(candidate.ScoreBreakdownJson!);
        var fields = scores.RootElement.GetProperty("field_scores").EnumerateArray().ToArray();
        Assert.Contains(fields, row => row.GetProperty("Key").GetString() == "series" && row.GetProperty("Score").GetDouble() == 1);
        Assert.Contains(fields, row => row.GetProperty("Key").GetString() == "issue" && row.GetProperty("Score").GetDouble() == 1);
        Assert.Equal("Two Riders Were Approaching...", candidate.Title);
    }

    [Fact]
    public async Task RetailMatchWorker_MusicExactSingleTrack_AllowsAutoAcceptWithoutTrackNumber()
    {
        var entityId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var providerId = Guid.NewGuid();

        var jobRepo = new StubIdentityJobRepository();
        var candidateRepo = new StubRetailCandidateRepository();

        await jobRepo.CreateAsync(new IdentityJob
        {
            Id = jobId,
            EntityId = entityId,
            EntityType = "MediaAsset",
            MediaType = "Music",
            State = "Queued",
        });

        var canonicalRepo = new StubCanonicalValueRepository();
        await canonicalRepo.UpsertBatchAsync(
        [
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Title, Value = "Clair de Lune", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Artist, Value = "Claude Debussy", LastScoredAt = DateTimeOffset.UtcNow },
        ]);

        var searchJson = """
            {
              "resultCount": 2,
              "results": [
                {
                  "wrapperType": "track",
                  "kind": "song",
                  "artistId": 267973,
                  "collectionId": 358198030,
                  "trackId": 358198482,
                  "artistName": "The Philadelphia Orchestra & Eugene Ormandy",
                  "collectionName": "Sweet Dreams",
                  "trackName": "Clair De Lune",
                  "trackCount": 16,
                  "trackNumber": 7,
                  "trackTimeMillis": 298846,
                  "releaseDate": "1989-08-04T12:00:00Z",
                  "primaryGenreName": "Classical",
                  "artworkUrl100": "https://example.test/sweet-100x100bb.jpg"
                },
                {
                  "wrapperType": "track",
                  "kind": "song",
                  "artistId": 219163,
                  "collectionId": 444967120,
                  "trackId": 444967148,
                  "artistName": "Claude Debussy",
                  "collectionName": "Clair de Lune - Single",
                  "trackName": "Clair de Lune",
                  "trackCount": 1,
                  "trackNumber": 1,
                  "trackTimeMillis": 218149,
                  "releaseDate": "2011-06-16T12:00:00Z",
                  "primaryGenreName": "Instrumental",
                  "artworkUrl100": "https://example.test/clair-100x100bb.jpg"
                }
              ]
            }
            """;

        var lookupJson = """
            {
              "resultCount": 2,
              "results": [
                {
                  "wrapperType": "collection",
                  "collectionType": "Album",
                  "artistId": 219163,
                  "collectionId": 444967120,
                  "artistName": "Claude Debussy",
                  "collectionName": "Clair de Lune - Single",
                  "trackCount": 1,
                  "releaseDate": "2011-06-16T12:00:00Z",
                  "primaryGenreName": "Instrumental"
                },
                {
                  "wrapperType": "track",
                  "kind": "song",
                  "artistId": 219163,
                  "collectionId": 444967120,
                  "trackId": 444967148,
                  "artistName": "Claude Debussy",
                  "collectionName": "Clair de Lune - Single",
                  "trackName": "Clair de Lune",
                  "trackCount": 1,
                  "trackNumber": 1,
                  "trackTimeMillis": 218149,
                  "releaseDate": "2011-06-16T12:00:00Z",
                  "primaryGenreName": "Instrumental",
                  "artworkUrl100": "https://example.test/clair-100x100bb.jpg"
                }
              ]
            }
            """;

        var configLoader = CreateAppleMusicIdentityConfig();
        var metadataClaims = new StubMetadataClaimRepository();
        var worker = new RetailMatchWorker(
            jobRepo,
            candidateRepo,
            CreateStubStageOutcomeFactory(),
            CreateStubTimelineRecorder(),
            CreateStubBatchProgressService(),
            [
                new StubExternalMetadataProvider
                {
                    Name = "apple_api",
                    ProviderId = providerId,
                    Claims = [],
                },
            ],
            new RetailMatchScoringService(
                new ExactMatchFuzzyMatchingService(),
                configLoader,
                coverArtHash: null,
                logger: null),
            metadataClaims,
            canonicalRepo,
            new StubScoringEngine(),
            configLoader,
            new StubBridgeIdRepository(),
            new StubWorkRepository(),
            new WorkClaimRouter(),
            new RoutingHttpClientFactory(request =>
            {
                var url = request.RequestUri?.ToString() ?? string.Empty;
                var body = url.Contains("/lookup?", StringComparison.OrdinalIgnoreCase)
                    ? lookupJson
                    : searchJson;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
            }),
            null!,
            NullLogger<RetailMatchWorker>.Instance);

        var processed = await worker.PollAsync(CancellationToken.None);

        Assert.Equal(1, processed);

        var updatedJob = await jobRepo.GetByIdAsync(jobId);
        Assert.NotNull(updatedJob);
        Assert.Equal(IdentityJobState.RetailMatched.ToString(), updatedJob!.State);

        var candidate = Assert.Single(candidateRepo.Candidates);
        Assert.Equal("AutoAccepted", candidate.Outcome);
        Assert.Contains("\"single_track_release\":true", candidate.ScoreBreakdownJson);
        Assert.DoesNotContain("requires_track_number_or_duration_corroboration", candidate.ScoreBreakdownJson);
        Assert.Contains(metadataClaims.Claims, claim =>
            claim.ClaimKey == MetadataFieldConstants.DurationField
            && claim.ClaimValue == "3.636");
    }

    [Fact]
    public async Task RetailMatchWorker_MusicExactTrackWithAlbum_AllowsAutoAcceptWithoutTrackNumber()
    {
        var entityId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var providerId = Guid.NewGuid();

        var jobRepo = new StubIdentityJobRepository();
        var candidateRepo = new StubRetailCandidateRepository();

        await jobRepo.CreateAsync(new IdentityJob
        {
            Id = jobId,
            EntityId = entityId,
            EntityType = "MediaAsset",
            MediaType = "Music",
            State = "Queued",
        });

        var canonicalRepo = new StubCanonicalValueRepository();
        await canonicalRepo.UpsertBatchAsync(
        [
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Title, Value = "La Vie en rose", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Artist, Value = "Édith Piaf", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Album, Value = "La Vie en rose", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Year, Value = "1947", LastScoredAt = DateTimeOffset.UtcNow },
        ]);

        const string SearchJson = """
            {
              "resultCount": 1,
              "results": [
                {
                  "wrapperType": "track",
                  "kind": "song",
                  "artistId": 82055,
                  "collectionId": 1708269212,
                  "trackId": 1708269213,
                  "artistName": "Édith Piaf",
                  "collectionName": "La vie en rose",
                  "trackName": "La vie en rose",
                  "trackCount": 33,
                  "trackNumber": 8,
                  "releaseDate": "1987-01-01T08:00:00Z",
                  "primaryGenreName": "French Pop",
                  "artworkUrl100": "https://example.test/piaf-100x100bb.jpg"
                }
              ]
            }
            """;

        const string LookupJson = """
            {
              "resultCount": 2,
              "results": [
                {
                  "wrapperType": "collection",
                  "collectionType": "Album",
                  "artistId": 82055,
                  "collectionId": 1708269212,
                  "artistName": "Édith Piaf",
                  "collectionName": "La vie en rose",
                  "trackCount": 33,
                  "releaseDate": "1987-01-01T08:00:00Z",
                  "primaryGenreName": "French Pop"
                },
                {
                  "wrapperType": "track",
                  "kind": "song",
                  "artistId": 82055,
                  "collectionId": 1708269212,
                  "trackId": 1708269213,
                  "artistName": "Édith Piaf",
                  "collectionName": "La vie en rose",
                  "trackName": "La vie en rose",
                  "trackCount": 33,
                  "trackNumber": 8,
                  "releaseDate": "1987-01-01T08:00:00Z",
                  "primaryGenreName": "French Pop",
                  "artworkUrl100": "https://example.test/piaf-100x100bb.jpg"
                }
              ]
            }
            """;

        var configLoader = CreateAppleMusicIdentityConfig();
        var worker = new RetailMatchWorker(
            jobRepo,
            candidateRepo,
            CreateStubStageOutcomeFactory(),
            CreateStubTimelineRecorder(),
            CreateStubBatchProgressService(),
            [
                new StubExternalMetadataProvider
                {
                    Name = "apple_api",
                    ProviderId = providerId,
                    Claims = [],
                },
            ],
            new RetailMatchScoringService(
                new ExactMatchFuzzyMatchingService(),
                configLoader,
                coverArtHash: null,
                logger: null),
            new StubMetadataClaimRepository(),
            canonicalRepo,
            new StubScoringEngine(),
            configLoader,
            new StubBridgeIdRepository(),
            new StubWorkRepository(),
            new WorkClaimRouter(),
            new RoutingHttpClientFactory(request =>
            {
                var url = request.RequestUri?.ToString() ?? string.Empty;
                var body = url.Contains("/lookup?", StringComparison.OrdinalIgnoreCase)
                    ? LookupJson
                    : SearchJson;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
            }),
            null!,
            NullLogger<RetailMatchWorker>.Instance);

        var processed = await worker.PollAsync(CancellationToken.None);

        Assert.Equal(1, processed);

        var updatedJob = await jobRepo.GetByIdAsync(jobId);
        Assert.NotNull(updatedJob);
        Assert.Equal(IdentityJobState.RetailMatched.ToString(), updatedJob!.State);

        var candidate = Assert.Single(candidateRepo.Candidates);
        Assert.Equal("AutoAccepted", candidate.Outcome);
        Assert.Contains("\"album_corroborates\":true", candidate.ScoreBreakdownJson);
        Assert.Contains("\"strong_canonical_track_identity\":true", candidate.ScoreBreakdownJson);
        Assert.DoesNotContain("requires_track_number_or_duration_corroboration", candidate.ScoreBreakdownJson);
    }

    [Fact]
    public async Task RetailMatchWorker_MusicExactSingleTrackSearchHit_BypassesAlbumLookup()
    {
        var entityId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var providerId = Guid.NewGuid();

        var jobRepo = new StubIdentityJobRepository();
        var candidateRepo = new StubRetailCandidateRepository();

        await jobRepo.CreateAsync(new IdentityJob
        {
            Id = jobId,
            EntityId = entityId,
            EntityType = "MediaAsset",
            MediaType = "Music",
            State = "Queued",
        });

        var canonicalRepo = new StubCanonicalValueRepository();
        await canonicalRepo.UpsertBatchAsync(
        [
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Title, Value = "Clair de Lune", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Composer, Value = "Claude Debussy", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Album, Value = "Suite bergamasque", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.TrackNumber, Value = "3", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Year, Value = "1905", LastScoredAt = DateTimeOffset.UtcNow },
        ]);

        var searchJson = """
            {
              "resultCount": 1,
              "results": [
                {
                  "wrapperType": "track",
                  "kind": "song",
                  "artistId": 219163,
                  "collectionId": 444967120,
                  "trackId": 444967148,
                  "artistName": "Claude Debussy",
                  "collectionName": "Clair de Lune - Single",
                  "trackName": "Clair de Lune",
                  "trackCount": 1,
                  "trackNumber": 1,
                  "trackTimeMillis": 218149,
                  "releaseDate": "2011-06-16T12:00:00Z",
                  "primaryGenreName": "Instrumental",
                  "artworkUrl100": "https://example.test/clair-100x100bb.jpg"
                }
              ]
            }
            """;

        var lookupJson = """
            {
              "resultCount": 0,
              "results": []
            }
            """;

        var configLoader = CreateAppleMusicIdentityConfig();
        var worker = new RetailMatchWorker(
            jobRepo,
            candidateRepo,
            CreateStubStageOutcomeFactory(),
            CreateStubTimelineRecorder(),
            CreateStubBatchProgressService(),
            [
                new StubExternalMetadataProvider
                {
                    Name = "apple_api",
                    ProviderId = providerId,
                    Claims = [],
                },
            ],
            new RetailMatchScoringService(
                new ExactMatchFuzzyMatchingService(),
                configLoader,
                coverArtHash: null,
                logger: null),
            new StubMetadataClaimRepository(),
            canonicalRepo,
            new StubScoringEngine(),
            configLoader,
            new StubBridgeIdRepository(),
            new StubWorkRepository(),
            new WorkClaimRouter(),
            new RoutingHttpClientFactory(request =>
            {
                var url = request.RequestUri?.ToString() ?? string.Empty;
                var body = url.Contains("/lookup?", StringComparison.OrdinalIgnoreCase)
                    ? lookupJson
                    : searchJson;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
            }),
            null!,
            NullLogger<RetailMatchWorker>.Instance);

        var processed = await worker.PollAsync(CancellationToken.None);

        Assert.Equal(1, processed);

        var updatedJob = await jobRepo.GetByIdAsync(jobId);
        Assert.NotNull(updatedJob);
        Assert.Equal(IdentityJobState.RetailMatchedNeedsReview.ToString(), updatedJob!.State);

        var candidate = Assert.Single(candidateRepo.Candidates);
        Assert.Equal("Ambiguous", candidate.Outcome);
        Assert.Contains("\"single_track_release\":true", candidate.ScoreBreakdownJson);
        Assert.Contains("\"strong_single_track_identity\":true", candidate.ScoreBreakdownJson);
    }

    [Fact]
    public async Task RetailMatchWorker_MusicSingleTrack_IgnoresWrongCompilationBeforeAlbumSearch()
    {
        var entityId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var providerId = Guid.NewGuid();

        var jobRepo = new StubIdentityJobRepository();
        var candidateRepo = new StubRetailCandidateRepository();

        await jobRepo.CreateAsync(new IdentityJob
        {
            Id = jobId,
            EntityId = entityId,
            EntityType = "MediaAsset",
            MediaType = "Music",
            State = "Queued",
        });

        var canonicalRepo = new StubCanonicalValueRepository();
        await canonicalRepo.UpsertBatchAsync(
        [
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Title, Value = "Beauty and the Beast", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Artist, Value = "David Bowie", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Album, Value = "Heroes", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.TrackNumber, Value = "2", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Year, Value = "1977", LastScoredAt = DateTimeOffset.UtcNow },
        ]);

        var wrongTrackJson = """
            {
              "resultCount": 1,
              "results": [
                {
                  "wrapperType": "track",
                  "kind": "song",
                  "artistId": 551695,
                  "collectionId": 696590528,
                  "trackId": 696592230,
                  "artistName": "David Bowie",
                  "collectionName": "The Platinum Collection",
                  "trackName": "Beauty and the Beast",
                  "trackCount": 57,
                  "trackNumber": 15,
                  "trackTimeMillis": 215000,
                  "releaseDate": "2005-11-07T12:00:00Z",
                  "primaryGenreName": "Rock",
                  "artworkUrl100": "https://example.test/platinum-100x100bb.jpg"
                }
              ]
            }
            """;

        var albumSearchJson = """
            {
              "resultCount": 1,
              "results": [
                {
                  "wrapperType": "collection",
                  "collectionType": "Album",
                  "artistId": 551695,
                  "collectionId": 12345,
                  "artistName": "David Bowie",
                  "collectionName": "Heroes",
                  "trackCount": 10,
                  "releaseDate": "1977-10-14T12:00:00Z",
                  "primaryGenreName": "Rock"
                }
              ]
            }
            """;

        var albumLookupJson = """
            {
              "resultCount": 2,
              "results": [
                {
                  "wrapperType": "collection",
                  "collectionType": "Album",
                  "artistId": 551695,
                  "collectionId": 12345,
                  "artistName": "David Bowie",
                  "collectionName": "Heroes",
                  "trackCount": 10,
                  "releaseDate": "1977-10-14T12:00:00Z",
                  "primaryGenreName": "Rock"
                },
                {
                  "wrapperType": "track",
                  "kind": "song",
                  "artistId": 551695,
                  "collectionId": 12345,
                  "trackId": 99902,
                  "artistName": "David Bowie",
                  "collectionName": "Heroes",
                  "trackName": "Beauty and the Beast",
                  "trackCount": 10,
                  "trackNumber": 2,
                  "trackTimeMillis": 215000,
                  "releaseDate": "1977-10-14T12:00:00Z",
                  "primaryGenreName": "Rock",
                  "artworkUrl100": "https://example.test/heroes-100x100bb.jpg"
                }
              ]
            }
            """;

        var requests = new List<string>();
        var configLoader = CreateAppleMusicIdentityConfig();
        var worker = new RetailMatchWorker(
            jobRepo,
            candidateRepo,
            CreateStubStageOutcomeFactory(),
            CreateStubTimelineRecorder(),
            CreateStubBatchProgressService(),
            [
                new StubExternalMetadataProvider
                {
                    Name = "apple_api",
                    ProviderId = providerId,
                    Claims = [],
                },
            ],
            new RetailMatchScoringService(
                new ExactMatchFuzzyMatchingService(),
                configLoader,
                coverArtHash: null,
                logger: null),
            new StubMetadataClaimRepository(),
            canonicalRepo,
            new StubScoringEngine(),
            configLoader,
            new StubBridgeIdRepository(),
            new StubWorkRepository(),
            new WorkClaimRouter(),
            new RoutingHttpClientFactory(request =>
            {
                var url = request.RequestUri?.ToString() ?? string.Empty;
                requests.Add(url);

                var body = url.Contains("/lookup?", StringComparison.OrdinalIgnoreCase)
                    ? albumLookupJson
                    : url.Contains("entity=album", StringComparison.OrdinalIgnoreCase)
                        ? albumSearchJson
                        : wrongTrackJson;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
            }),
            null!,
            NullLogger<RetailMatchWorker>.Instance);

        var processed = await worker.PollAsync(CancellationToken.None);

        Assert.Equal(1, processed);

        var updatedJob = await jobRepo.GetByIdAsync(jobId);
        Assert.NotNull(updatedJob);
        Assert.Equal(IdentityJobState.RetailMatched.ToString(), updatedJob!.State);

        var candidate = Assert.Single(candidateRepo.Candidates);
        Assert.Equal("AutoAccepted", candidate.Outcome);
        Assert.Contains("\"album_corroborates\":true", candidate.ScoreBreakdownJson);
        Assert.Contains("entity=album", string.Join("\n", requests), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lookup?id=12345", string.Join("\n", requests), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lookup?id=696590528", string.Join("\n", requests), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RetailMatchWorker_MusicTrackSearch_TriesArtistQueryBeforeAlbumBiasedQuery()
    {
        var entityId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var providerId = Guid.NewGuid();

        var jobRepo = new StubIdentityJobRepository();
        var candidateRepo = new StubRetailCandidateRepository();

        await jobRepo.CreateAsync(new IdentityJob
        {
            Id = jobId,
            EntityId = entityId,
            EntityType = "MediaAsset",
            MediaType = "Music",
            State = "Queued",
        });

        var canonicalRepo = new StubCanonicalValueRepository();
        await canonicalRepo.UpsertBatchAsync(
        [
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Title, Value = "Clair de Lune", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Composer, Value = "Claude Debussy", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Album, Value = "Suite bergamasque", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.TrackNumber, Value = "3", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Year, Value = "1905", LastScoredAt = DateTimeOffset.UtcNow },
        ]);

        var exactSingleJson = """
            {
              "resultCount": 1,
              "results": [
                {
                  "wrapperType": "track",
                  "kind": "song",
                  "artistId": 219163,
                  "collectionId": 444967120,
                  "trackId": 444967148,
                  "artistName": "Claude Debussy",
                  "collectionName": "Clair de Lune - Single",
                  "trackName": "Clair de Lune",
                  "trackCount": 1,
                  "trackNumber": 1,
                  "trackTimeMillis": 218149,
                  "releaseDate": "2011-06-16T12:00:00Z",
                  "primaryGenreName": "Instrumental",
                  "artworkUrl100": "https://example.test/clair-100x100bb.jpg"
                }
              ]
            }
            """;

        var albumBiasedJson = """
            {
              "resultCount": 1,
              "results": [
                {
                  "wrapperType": "track",
                  "kind": "song",
                  "artistId": 267973,
                  "collectionId": 358198030,
                  "trackId": 358198482,
                  "artistName": "The Philadelphia Orchestra & Eugene Ormandy",
                  "collectionName": "Sweet Dreams",
                  "trackName": "Clair De Lune",
                  "trackCount": 16,
                  "trackNumber": 7,
                  "trackTimeMillis": 298846,
                  "releaseDate": "1989-08-04T12:00:00Z",
                  "primaryGenreName": "Classical",
                  "artworkUrl100": "https://example.test/sweet-100x100bb.jpg"
                }
              ]
            }
            """;

        var requests = new List<string>();
        var configLoader = CreateAppleMusicIdentityConfig();
        var worker = new RetailMatchWorker(
            jobRepo,
            candidateRepo,
            CreateStubStageOutcomeFactory(),
            CreateStubTimelineRecorder(),
            CreateStubBatchProgressService(),
            [
                new StubExternalMetadataProvider
                {
                    Name = "apple_api",
                    ProviderId = providerId,
                    Claims = [],
                },
            ],
            new RetailMatchScoringService(
                new ExactMatchFuzzyMatchingService(),
                configLoader,
                coverArtHash: null,
                logger: null),
            new StubMetadataClaimRepository(),
            canonicalRepo,
            new StubScoringEngine(),
            configLoader,
            new StubBridgeIdRepository(),
            new StubWorkRepository(),
            new WorkClaimRouter(),
            new RoutingHttpClientFactory(request =>
            {
                var url = request.RequestUri?.ToString() ?? string.Empty;
                requests.Add(url);

                var body = url.Contains("Suite%20bergamasque", StringComparison.OrdinalIgnoreCase)
                    ? albumBiasedJson
                    : exactSingleJson;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
            }),
            null!,
            NullLogger<RetailMatchWorker>.Instance);

        var processed = await worker.PollAsync(CancellationToken.None);

        Assert.Equal(1, processed);

        var updatedJob = await jobRepo.GetByIdAsync(jobId);
        Assert.NotNull(updatedJob);
        Assert.Equal(IdentityJobState.RetailMatchedNeedsReview.ToString(), updatedJob!.State);

        var candidate = Assert.Single(candidateRepo.Candidates);
        Assert.Equal("Ambiguous", candidate.Outcome);
        Assert.Contains("\"single_track_release\":true", candidate.ScoreBreakdownJson);
        Assert.Single(requests, url => url.Contains("/search?", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(requests, url => url.Contains("Suite%20bergamasque", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RetailMatchWorker_MusicGroupedTracks_UsesCollectionConsensusAcrossQueuedAlbum()
    {
        var providerId = Guid.NewGuid();
        var tracks = new[]
        {
            new { EntityId = Guid.NewGuid(), JobId = Guid.NewGuid(), Title = "Bohemian Rhapsody", TrackNumber = "11" },
            new { EntityId = Guid.NewGuid(), JobId = Guid.NewGuid(), Title = "You're My Best Friend", TrackNumber = "4" },
            new { EntityId = Guid.NewGuid(), JobId = Guid.NewGuid(), Title = "Death on Two Legs", TrackNumber = "1" },
        };

        var jobRepo = new StubIdentityJobRepository();
        var candidateRepo = new StubRetailCandidateRepository();
        var canonicalRepo = new StubCanonicalValueRepository();

        foreach (var track in tracks)
        {
            await jobRepo.CreateAsync(new IdentityJob
            {
                Id = track.JobId,
                EntityId = track.EntityId,
                EntityType = "MediaAsset",
                MediaType = "Music",
                State = "Queued",
            });

            await canonicalRepo.UpsertBatchAsync(
            [
                new CanonicalValue { EntityId = track.EntityId, Key = MetadataFieldConstants.Title, Value = track.Title, LastScoredAt = DateTimeOffset.UtcNow },
                new CanonicalValue { EntityId = track.EntityId, Key = MetadataFieldConstants.Artist, Value = "Queen", LastScoredAt = DateTimeOffset.UtcNow },
                new CanonicalValue { EntityId = track.EntityId, Key = MetadataFieldConstants.Album, Value = "A Night at the Opera", LastScoredAt = DateTimeOffset.UtcNow },
                new CanonicalValue { EntityId = track.EntityId, Key = MetadataFieldConstants.TrackNumber, Value = track.TrackNumber, LastScoredAt = DateTimeOffset.UtcNow },
                new CanonicalValue { EntityId = track.EntityId, Key = MetadataFieldConstants.Year, Value = "1975", LastScoredAt = DateTimeOffset.UtcNow },
            ]);
        }

        var wrongCollectionSearchJson = """
            {
              "resultCount": 1,
              "results": [
                {
                  "wrapperType": "track",
                  "kind": "song",
                  "artistId": 3296287,
                  "collectionId": 1440650428,
                  "trackId": 1440650781,
                  "artistName": "Queen",
                  "collectionName": "Greatest Hits",
                  "trackName": "Bohemian Rhapsody",
                  "trackCount": 17,
                  "trackNumber": 1,
                  "trackTimeMillis": 354320,
                  "releaseDate": "1981-10-26T12:00:00Z",
                  "primaryGenreName": "Rock",
                  "artworkUrl100": "https://example.test/hits-100x100bb.jpg"
                }
              ]
            }
            """;

        var correctCollectionSearchJson = """
            {
              "resultCount": 1,
              "results": [
                {
                  "wrapperType": "track",
                  "kind": "song",
                  "artistId": 3296287,
                  "collectionId": 1440806041,
                  "trackId": 1440806519,
                  "artistName": "Queen",
                  "collectionName": "A Night at the Opera",
                  "trackName": "Death On Two Legs (Dedicated To...)",
                  "trackCount": 12,
                  "trackNumber": 1,
                  "trackTimeMillis": 223960,
                  "releaseDate": "1975-11-21T12:00:00Z",
                  "primaryGenreName": "Rock",
                  "artworkUrl100": "https://example.test/opera-100x100bb.jpg"
                }
              ]
            }
            """;

        var correctLookupJson = """
            {
              "resultCount": 4,
              "results": [
                {
                  "wrapperType": "collection",
                  "collectionType": "Album",
                  "artistId": 3296287,
                  "collectionId": 1440806041,
                  "artistName": "Queen",
                  "collectionName": "A Night at the Opera",
                  "trackCount": 12,
                  "releaseDate": "1975-11-21T12:00:00Z",
                  "primaryGenreName": "Rock"
                },
                {
                  "wrapperType": "track",
                  "kind": "song",
                  "artistId": 3296287,
                  "collectionId": 1440806041,
                  "trackId": 1440806519,
                  "artistName": "Queen",
                  "collectionName": "A Night at the Opera",
                  "trackName": "Death On Two Legs (Dedicated To...)",
                  "trackCount": 12,
                  "trackNumber": 1,
                  "trackTimeMillis": 223960,
                  "releaseDate": "1975-11-21T12:00:00Z",
                  "primaryGenreName": "Rock",
                  "artworkUrl100": "https://example.test/opera-100x100bb.jpg"
                },
                {
                  "wrapperType": "track",
                  "kind": "song",
                  "artistId": 3296287,
                  "collectionId": 1440806041,
                  "trackId": 1440806522,
                  "artistName": "Queen",
                  "collectionName": "A Night at the Opera",
                  "trackName": "You're My Best Friend",
                  "trackCount": 12,
                  "trackNumber": 4,
                  "trackTimeMillis": 170680,
                  "releaseDate": "1975-11-21T12:00:00Z",
                  "primaryGenreName": "Rock",
                  "artworkUrl100": "https://example.test/opera-100x100bb.jpg"
                },
                {
                  "wrapperType": "track",
                  "kind": "song",
                  "artistId": 3296287,
                  "collectionId": 1440806041,
                  "trackId": 1440806529,
                  "artistName": "Queen",
                  "collectionName": "A Night at the Opera",
                  "trackName": "Bohemian Rhapsody",
                  "trackCount": 12,
                  "trackNumber": 11,
                  "trackTimeMillis": 354320,
                  "releaseDate": "1975-11-21T12:00:00Z",
                  "primaryGenreName": "Rock",
                  "artworkUrl100": "https://example.test/opera-100x100bb.jpg"
                }
              ]
            }
            """;

        var wrongLookupJson = """
            {
              "resultCount": 3,
              "results": [
                {
                  "wrapperType": "collection",
                  "collectionType": "Album",
                  "artistId": 3296287,
                  "collectionId": 1440650428,
                  "artistName": "Queen",
                  "collectionName": "Greatest Hits",
                  "trackCount": 17,
                  "releaseDate": "1981-10-26T12:00:00Z",
                  "primaryGenreName": "Rock"
                },
                {
                  "wrapperType": "track",
                  "kind": "song",
                  "artistId": 3296287,
                  "collectionId": 1440650428,
                  "trackId": 1440650781,
                  "artistName": "Queen",
                  "collectionName": "Greatest Hits",
                  "trackName": "Bohemian Rhapsody",
                  "trackCount": 17,
                  "trackNumber": 1,
                  "trackTimeMillis": 354320,
                  "releaseDate": "1981-10-26T12:00:00Z",
                  "primaryGenreName": "Rock",
                  "artworkUrl100": "https://example.test/hits-100x100bb.jpg"
                },
                {
                  "wrapperType": "track",
                  "kind": "song",
                  "artistId": 3296287,
                  "collectionId": 1440650428,
                  "trackId": 1440650790,
                  "artistName": "Queen",
                  "collectionName": "Greatest Hits",
                  "trackName": "You're My Best Friend",
                  "trackCount": 17,
                  "trackNumber": 10,
                  "trackTimeMillis": 170680,
                  "releaseDate": "1981-10-26T12:00:00Z",
                  "primaryGenreName": "Rock",
                  "artworkUrl100": "https://example.test/hits-100x100bb.jpg"
                }
              ]
            }
            """;

        var requests = new List<string>();
        var configLoader = CreateAppleMusicIdentityConfig();
        var worker = new RetailMatchWorker(
            jobRepo,
            candidateRepo,
            CreateStubStageOutcomeFactory(),
            CreateStubTimelineRecorder(),
            CreateStubBatchProgressService(),
            [
                new StubExternalMetadataProvider
                {
                    Name = "apple_api",
                    ProviderId = providerId,
                    Claims = [],
                },
            ],
            new RetailMatchScoringService(
                new FuzzyMatchingService(),
                configLoader,
                coverArtHash: null,
                logger: null),
            new StubMetadataClaimRepository(),
            canonicalRepo,
            new StubScoringEngine(),
            configLoader,
            new StubBridgeIdRepository(),
            new StubWorkRepository(),
            new WorkClaimRouter(),
            new RoutingHttpClientFactory(request =>
            {
                var url = request.RequestUri?.ToString() ?? string.Empty;
                requests.Add(url);

                string body;
                if (url.Contains("/lookup?", StringComparison.OrdinalIgnoreCase))
                {
                    body = url.Contains("id=1440806041", StringComparison.OrdinalIgnoreCase)
                        ? correctLookupJson
                        : wrongLookupJson;
                }
                else if (url.Contains("Bohemian%20Rhapsody", StringComparison.OrdinalIgnoreCase))
                {
                    body = wrongCollectionSearchJson;
                }
                else
                {
                    body = correctCollectionSearchJson;
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
            }),
            null!,
            NullLogger<RetailMatchWorker>.Instance);

        var processed = await worker.PollAsync(CancellationToken.None);

        Assert.Equal(3, processed);
        Assert.Equal(3, candidateRepo.Candidates.Count);
        Assert.Contains(requests, url => url.Contains("/lookup?id=1440806041", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(requests, url => url.Contains("/lookup?id=1440650428", StringComparison.OrdinalIgnoreCase));

        foreach (var track in tracks)
        {
            var updatedJob = await jobRepo.GetByIdAsync(track.JobId);
            Assert.NotNull(updatedJob);
            Assert.Equal(IdentityJobState.RetailMatched.ToString(), updatedJob!.State);
        }

        Assert.All(candidateRepo.Candidates, candidate => Assert.Equal("AutoAccepted", candidate.Outcome));
    }

    [Fact]
    public async Task RetailMatchWorker_MusicBrainzFirstPipeline_UsesConfiguredProviderOrder()
    {
        var entityId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var albumRootId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var musicBrainzProviderId = Guid.NewGuid();
        var appleProviderId = Guid.NewGuid();

        var jobRepo = new StubIdentityJobRepository();
        var candidateRepo = new StubRetailCandidateRepository();

        await jobRepo.CreateAsync(new IdentityJob
        {
            Id = jobId,
            EntityId = entityId,
            EntityType = "MediaAsset",
            MediaType = "Music",
            State = "Queued",
        });

        var musicBrainz = new StubExternalMetadataProvider
        {
            Name = "musicbrainz",
            ProviderId = musicBrainzProviderId,
            Claims =
            [
                new ProviderClaim(MetadataFieldConstants.Title, "Bohemian Rhapsody", 0.95),
                new ProviderClaim(MetadataFieldConstants.Author, "Queen", 0.95),
                new ProviderClaim(MetadataFieldConstants.Album, "A Night at the Opera", 0.95),
                new ProviderClaim(BridgeIdKeys.MusicBrainzRecordingId, "mb-recording-1", 1.0),
            ],
        };
        var apple = new StubExternalMetadataProvider
        {
            Name = "apple_api",
            ProviderId = appleProviderId,
            Claims =
            [
                new ProviderClaim(MetadataFieldConstants.Title, "Bohemian Rhapsody", 0.90),
                new ProviderClaim(MetadataFieldConstants.Author, "Queen", 0.90),
                new ProviderClaim(MetadataFieldConstants.Album, "A Night at the Opera", 0.90),
                new ProviderClaim(MetadataFieldConstants.CoverUrl, "https://example.invalid/cover.jpg", 0.90),
                new ProviderClaim(BridgeIdKeys.AppleMusicId, "1440806768", 0.95),
                new ProviderClaim(BridgeIdKeys.AppleMusicCollectionId, "1440806041", 0.95),
            ],
        };

        var retailScoring = new StubRetailMatchScoringService();
        retailScoring.Results.Enqueue(new FieldMatchScores
        {
            TitleScore = 0.97,
            AuthorScore = 0.95,
            YearScore = 0.0,
            FormatScore = 1.0,
            CompositeScore = 0.96,
        });
        retailScoring.Results.Enqueue(new FieldMatchScores
        {
            TitleScore = 0.99,
            AuthorScore = 0.99,
            YearScore = 0.0,
            FormatScore = 1.0,
            CompositeScore = 0.99,
        });

        var configLoader = new StubConfigurationLoader
        {
            PipelineConfiguration = new PipelineConfiguration
            {
                Pipelines = new Dictionary<string, MediaTypePipeline>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Music"] = new()
                    {
                        Strategy = ProviderStrategy.Sequential,
                        Providers =
                        [
                            new PipelineProviderEntry { Rank = 1, Name = "musicbrainz", Purpose = "identity" },
                            new PipelineProviderEntry
                            {
                                Rank = 2,
                                Name = "apple_api",
                                Purpose = "enrichment",
                                RequiresIdentity = true,
                                AcceptedActions = ["apple-album-manifest"],
                            },
                        ],
                    },
                },
            },
            Providers =
            [
                new() { Name = "musicbrainz", Enabled = true, ProviderId = musicBrainzProviderId.ToString(), Weight = 0.9 },
                new() { Name = "apple_api", Enabled = true, ProviderId = appleProviderId.ToString(), Weight = 0.9 },
            ],
        };

        var canonicalRepo = new StubCanonicalValueRepository();
        await canonicalRepo.UpsertBatchAsync(
        [
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Title, Value = "Bohemian Rhapsody", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Artist, Value = "Queen", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Album, Value = "A Night at the Opera", LastScoredAt = DateTimeOffset.UtcNow },
        ]);

        var worker = new RetailMatchWorker(
            jobRepo,
            candidateRepo,
            CreateStubStageOutcomeFactory(),
            CreateStubTimelineRecorder(),
            CreateStubBatchProgressService(),
            new[] { musicBrainz, apple },
            retailScoring,
            new StubMetadataClaimRepository(),
            canonicalRepo,
            new StubScoringEngine(),
            configLoader,
            new StubBridgeIdRepository(),
            new StubWorkRepository
            {
                Lineage = new WorkLineage(
                    entityId,
                    Guid.NewGuid(),
                    workId,
                    albumRootId,
                    albumRootId,
                    WorkKind.Child,
                    MediaType.Music),
            },
            new WorkClaimRouter(),
            new RoutingHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "results": [
                        { "wrapperType": "collection", "collectionId": 1440806041 },
                        { "wrapperType": "track", "kind": "song", "trackName": "Death on Two Legs", "trackNumber": 1, "discNumber": 1, "trackTimeMillis": 223000, "trackId": 1440806042 },
                        { "wrapperType": "track", "kind": "song", "trackName": "Bohemian Rhapsody", "trackNumber": 11, "discNumber": 1, "trackTimeMillis": 354000, "trackId": 1440806768 }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            }),
            null!,
            NullLogger<RetailMatchWorker>.Instance);

        var processed = await worker.PollAsync(CancellationToken.None);

        Assert.Equal(1, processed);
        Assert.Single(musicBrainz.Requests);
        Assert.Single(apple.Requests);
        Assert.Equal(["musicbrainz", "apple_api"], candidateRepo.Candidates.Select(candidate => candidate.ProviderName).ToArray());
        Assert.Equal([1, 2], candidateRepo.Candidates.Select(candidate => candidate.Rank).ToArray());
        Assert.NotNull(apple.Requests[0].PriorProviderBridgeIds);
        Assert.Contains(BridgeIdKeys.MusicBrainzRecordingId, apple.Requests[0].PriorProviderBridgeIds!.Keys);
        var manifest = Assert.Single(canonicalRepo.Values, value =>
            value.EntityId == albumRootId
            && value.Key == MetadataFieldConstants.ChildEntitiesJson);
        Assert.Contains("\"provider_collection_id\":\"1440806041\"", manifest.Value, StringComparison.Ordinal);
        Assert.Contains("\"Bohemian Rhapsody\"", manifest.Value, StringComparison.Ordinal);
        Assert.Contains(canonicalRepo.Values, value =>
            value.EntityId == albumRootId
            && value.Key == MetadataFieldConstants.TrackCount
            && value.Value == "2");
        var musicBrainzCandidate = Assert.Single(candidateRepo.Candidates, candidate => candidate.ProviderName == "musicbrainz");
        var appleCandidate = Assert.Single(candidateRepo.Candidates, candidate => candidate.ProviderName == "apple_api");
        Assert.True(appleCandidate.ScoreTotal > musicBrainzCandidate.ScoreTotal);

        var updatedJob = await jobRepo.GetByIdAsync(jobId);
        Assert.NotNull(updatedJob);
        Assert.Equal(IdentityJobState.RetailMatched.ToString(), updatedJob!.State);
        Assert.Equal(musicBrainzCandidate.Id, updatedJob.SelectedCandidateId);
    }

    [Fact]
    public async Task RetailMatchWorker_MusicEnrichmentPersistsAfterAcceptedIdentityWithoutOwningIdentity()
    {
        var entityId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var musicBrainzProviderId = Guid.NewGuid();
        var appleProviderId = Guid.NewGuid();

        var jobRepo = new StubIdentityJobRepository();
        var candidateRepo = new StubRetailCandidateRepository();
        var claimRepo = new StubMetadataClaimRepository();
        var canonicalRepo = new StubCanonicalValueRepository();
        var bridgeRepo = new StubBridgeIdRepository();

        await jobRepo.CreateAsync(new IdentityJob
        {
            Id = jobId,
            EntityId = entityId,
            EntityType = "MediaAsset",
            MediaType = "Music",
            State = "Queued",
        });

        var musicBrainz = new StubExternalMetadataProvider
        {
            Name = "musicbrainz",
            ProviderId = musicBrainzProviderId,
            Claims =
            [
                new ProviderClaim(MetadataFieldConstants.Title, "Example Track", 0.95),
                new ProviderClaim(MetadataFieldConstants.Author, "Example Artist", 0.95),
                new ProviderClaim(MetadataFieldConstants.Album, "Example Album", 0.95),
                new ProviderClaim(BridgeIdKeys.MusicBrainzRecordingId, "mb-recording-1", 1.0),
            ],
        };
        var apple = new StubExternalMetadataProvider
        {
            Name = "apple_api",
            ProviderId = appleProviderId,
            Claims =
            [
                new ProviderClaim(MetadataFieldConstants.Title, "Example Track", 0.90),
                new ProviderClaim(MetadataFieldConstants.Author, "Example Artist", 0.90),
                new ProviderClaim(MetadataFieldConstants.CoverUrl, "https://example.invalid/cover.jpg", 0.90),
                new ProviderClaim(MetadataFieldConstants.Genre, "Rock", 0.70),
                new ProviderClaim(BridgeIdKeys.AppleMusicId, "1440806768", 0.95),
            ],
        };

        var retailScoring = new StubRetailMatchScoringService();
        retailScoring.Results.Enqueue(new FieldMatchScores
        {
            TitleScore = 0.98,
            AuthorScore = 0.96,
            CompositeScore = 0.96,
            FormatScore = 1.0,
        });
        retailScoring.Results.Enqueue(new FieldMatchScores
        {
            TitleScore = 0.99,
            AuthorScore = 0.98,
            CompositeScore = 0.40,
            FormatScore = 1.0,
        });

        var configLoader = new StubConfigurationLoader
        {
            PipelineConfiguration = new PipelineConfiguration
            {
                Pipelines = new Dictionary<string, MediaTypePipeline>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Music"] = new()
                    {
                        Strategy = ProviderStrategy.Sequential,
                        Providers =
                        [
                            new PipelineProviderEntry { Rank = 1, Name = "musicbrainz", Purpose = "identity" },
                            new PipelineProviderEntry { Rank = 2, Name = "apple_api", Purpose = "enrichment", RequiresIdentity = true },
                        ],
                    },
                },
            },
            Providers =
            [
                new() { Name = "musicbrainz", Enabled = true, ProviderId = musicBrainzProviderId.ToString(), Weight = 0.9 },
                new() { Name = "apple_api", Enabled = true, ProviderId = appleProviderId.ToString(), Weight = 0.9 },
            ],
        };

        await canonicalRepo.UpsertBatchAsync(
        [
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Title, Value = "Example Track", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Artist, Value = "Example Artist", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Album, Value = "Example Album", LastScoredAt = DateTimeOffset.UtcNow },
        ]);

        var worker = new RetailMatchWorker(
            jobRepo,
            candidateRepo,
            CreateStubStageOutcomeFactory(),
            CreateStubTimelineRecorder(),
            CreateStubBatchProgressService(),
            new[] { musicBrainz, apple },
            retailScoring,
            claimRepo,
            canonicalRepo,
            new StubScoringEngine(),
            configLoader,
            bridgeRepo,
            new StubWorkRepository(),
            new WorkClaimRouter(),
            new StubHttpClientFactory(),
            null!,
            NullLogger<RetailMatchWorker>.Instance);

        var processed = await worker.PollAsync(CancellationToken.None);

        Assert.Equal(1, processed);
        var musicBrainzCandidate = Assert.Single(candidateRepo.Candidates, candidate => candidate.ProviderName == "musicbrainz");
        var appleCandidate = Assert.Single(candidateRepo.Candidates, candidate => candidate.ProviderName == "apple_api");
        Assert.Equal("Rejected", appleCandidate.Outcome);

        var updatedJob = await jobRepo.GetByIdAsync(jobId);
        Assert.NotNull(updatedJob);
        Assert.Equal(IdentityJobState.RetailMatched.ToString(), updatedJob!.State);
        Assert.Equal(musicBrainzCandidate.Id, updatedJob.SelectedCandidateId);

        Assert.Contains(claimRepo.Claims, claim =>
            claim.EntityId == entityId
            && claim.ProviderId == appleProviderId
            && claim.ClaimKey == MetadataFieldConstants.CoverUrl);
        Assert.Contains(bridgeRepo.Entries, entry =>
            entry.EntityId == entityId
            && entry.IdType == BridgeIdKeys.AppleMusicId
            && entry.IdValue == "1440806768");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetailMatchWorker_MusicEnrichmentProviderBecomesIdentityFallbackWhenPrimaryIsNotAccepted(
        bool primaryReturnsAmbiguous)
    {
        var entityId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var musicBrainzProviderId = Guid.NewGuid();
        var appleProviderId = Guid.NewGuid();

        var jobRepo = new StubIdentityJobRepository();
        var candidateRepo = new StubRetailCandidateRepository();

        await jobRepo.CreateAsync(new IdentityJob
        {
            Id = jobId,
            EntityId = entityId,
            EntityType = "MediaAsset",
            MediaType = "Music",
            State = "Queued",
        });

        var musicBrainz = new StubExternalMetadataProvider
        {
            Name = "musicbrainz",
            ProviderId = musicBrainzProviderId,
            Claims = primaryReturnsAmbiguous
                ?
                [
                    new ProviderClaim(MetadataFieldConstants.Title, "Example Track", 0.95),
                    new ProviderClaim(MetadataFieldConstants.Author, "Example Artist", 0.95),
                    new ProviderClaim(BridgeIdKeys.MusicBrainzRecordingId, "mb-recording-1", 1.0),
                ]
                : [],
        };
        var apple = new StubExternalMetadataProvider
        {
            Name = "apple_api",
            ProviderId = appleProviderId,
            Claims =
            [
                new ProviderClaim(MetadataFieldConstants.Title, "Example Track", 0.95),
                new ProviderClaim(MetadataFieldConstants.Author, "Example Artist", 0.95),
                new ProviderClaim(MetadataFieldConstants.CoverUrl, "https://example.invalid/cover.jpg", 0.95),
                new ProviderClaim(BridgeIdKeys.AppleMusicId, "12345", 0.95),
            ],
        };

        var retailScoring = new StubRetailMatchScoringService
        {
            Result = new FieldMatchScores
            {
                TitleScore = 1.0,
                AuthorScore = 1.0,
                YearScore = 0.0,
                FormatScore = 1.0,
                CompositeScore = 0.99,
            },
        };
        if (primaryReturnsAmbiguous)
        {
            retailScoring.Results.Enqueue(new FieldMatchScores
            {
                TitleScore = 1.0,
                AuthorScore = 1.0,
                FormatScore = 1.0,
                CompositeScore = 0.75,
            });
            retailScoring.Results.Enqueue(retailScoring.Result);
        }

        var configLoader = new StubConfigurationLoader
        {
            PipelineConfiguration = new PipelineConfiguration
            {
                Pipelines = new Dictionary<string, MediaTypePipeline>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Music"] = new()
                    {
                        Strategy = ProviderStrategy.Sequential,
                        Providers =
                        [
                            new PipelineProviderEntry { Rank = 1, Name = "musicbrainz", Purpose = "identity" },
                            new PipelineProviderEntry
                            {
                                Rank = 2,
                                Name = "apple_api",
                                Purpose = "enrichment",
                                RequiresIdentity = true,
                                UseAsIdentityFallback = true,
                            },
                        ],
                    },
                },
            },
            Providers =
            [
                new() { Name = "musicbrainz", Enabled = true, ProviderId = musicBrainzProviderId.ToString(), Weight = 0.9 },
                new() { Name = "apple_api", Enabled = true, ProviderId = appleProviderId.ToString(), Weight = 0.9 },
            ],
        };

        var canonicalRepo = new StubCanonicalValueRepository();
        await canonicalRepo.UpsertBatchAsync(
        [
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Title, Value = "Example Track", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Artist, Value = "Example Artist", LastScoredAt = DateTimeOffset.UtcNow },
        ]);

        var worker = new RetailMatchWorker(
            jobRepo,
            candidateRepo,
            CreateStubStageOutcomeFactory(),
            CreateStubTimelineRecorder(),
            CreateStubBatchProgressService(),
            new[] { musicBrainz, apple },
            retailScoring,
            new StubMetadataClaimRepository(),
            canonicalRepo,
            new StubScoringEngine(),
            configLoader,
            new StubBridgeIdRepository(),
            new StubWorkRepository(),
            new WorkClaimRouter(),
            new StubHttpClientFactory(),
            null!,
            NullLogger<RetailMatchWorker>.Instance);

        var processed = await worker.PollAsync(CancellationToken.None);

        Assert.Equal(1, processed);
        Assert.Single(musicBrainz.Requests);
        Assert.Single(apple.Requests);
        var appleCandidate = Assert.Single(
            candidateRepo.Candidates,
            candidate => candidate.ProviderName == "apple_api");
        Assert.Equal("apple_api", appleCandidate.ProviderName);
        Assert.Equal("AutoAccepted", appleCandidate.Outcome);
        if (primaryReturnsAmbiguous)
        {
            Assert.Contains(candidateRepo.Candidates, candidate =>
                candidate.ProviderName == "musicbrainz"
                && candidate.Outcome == "Ambiguous");
        }

        var updatedJob = await jobRepo.GetByIdAsync(jobId);
        Assert.NotNull(updatedJob);
        Assert.Equal(IdentityJobState.RetailMatched.ToString(), updatedJob!.State);
        Assert.Equal(appleCandidate.Id, updatedJob.SelectedCandidateId);
    }

    [Fact]
    public async Task RetailMatchWorker_MusicProviderMissEndsAsRetailNoMatch()
    {
        var entityId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        var jobRepo = new StubIdentityJobRepository();

        await jobRepo.CreateAsync(new IdentityJob
        {
            Id = jobId,
            EntityId = entityId,
            EntityType = "MediaAsset",
            MediaType = "Music",
            State = "Queued",
        });

        var musicBrainz = new StubExternalMetadataProvider
        {
            Name = "musicbrainz",
            ProviderId = providerId,
            Claims = [],
        };
        var configLoader = new StubConfigurationLoader
        {
            PipelineConfiguration = new PipelineConfiguration
            {
                Pipelines = new Dictionary<string, MediaTypePipeline>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Music"] = new()
                    {
                        Strategy = ProviderStrategy.Sequential,
                        Providers =
                        [
                            new PipelineProviderEntry
                            {
                                Rank = 1,
                                Name = "musicbrainz",
                                Purpose = "identity",
                            },
                        ],
                    },
                },
            },
            Providers =
            [
                new()
                {
                    Name = "musicbrainz",
                    Enabled = true,
                    ProviderId = providerId.ToString(),
                    Weight = 0.9,
                },
            ],
        };
        var canonicalRepo = new StubCanonicalValueRepository();
        await canonicalRepo.UpsertBatchAsync(
        [
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Title, Value = "Unknown Track", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Artist, Value = "Unknown Artist", LastScoredAt = DateTimeOffset.UtcNow },
        ]);

        var worker = new RetailMatchWorker(
            jobRepo,
            new StubRetailCandidateRepository(),
            CreateStubStageOutcomeFactory(),
            CreateStubTimelineRecorder(),
            CreateStubBatchProgressService(),
            [musicBrainz],
            new StubRetailMatchScoringService(),
            new StubMetadataClaimRepository(),
            canonicalRepo,
            new StubScoringEngine(),
            configLoader,
            new StubBridgeIdRepository(),
            new StubWorkRepository(),
            new WorkClaimRouter(),
            new StubHttpClientFactory(),
            null!,
            NullLogger<RetailMatchWorker>.Instance);

        var processed = await worker.PollAsync(CancellationToken.None);

        Assert.Equal(1, processed);
        var updatedJob = await jobRepo.GetByIdAsync(jobId);
        Assert.NotNull(updatedJob);
        Assert.Equal(IdentityJobState.RetailNoMatch.ToString(), updatedJob!.State);
    }

    [Fact]
    public async Task RetailMatchWorker_ConfiguredAcceptedTransition_RetriesEarlierProviderWithAcceptedClaims()
    {
        var entityId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var musicBrainzProviderId = Guid.NewGuid();
        var appleProviderId = Guid.NewGuid();
        var jobRepo = new StubIdentityJobRepository();
        var candidateRepo = new StubRetailCandidateRepository();

        await jobRepo.CreateAsync(new IdentityJob
        {
            Id = jobId,
            EntityId = entityId,
            EntityType = "MediaAsset",
            MediaType = "Music",
            State = "Queued",
        });

        var musicBrainz = new StubExternalMetadataProvider
        {
            Name = "musicbrainz",
            ProviderId = musicBrainzProviderId,
        };
        musicBrainz.ClaimResults.Enqueue([]);
        musicBrainz.ClaimResults.Enqueue(
        [
            new ProviderClaim(MetadataFieldConstants.Title, "Canonical Track", 0.95),
            new ProviderClaim(MetadataFieldConstants.Author, "Canonical Artist", 0.95),
            new ProviderClaim(MetadataFieldConstants.Album, "Canonical Album", 0.95),
            new ProviderClaim(BridgeIdKeys.MusicBrainzRecordingId, "mb-recording-2", 1.0),
        ]);

        var apple = new StubExternalMetadataProvider
        {
            Name = "apple_api",
            ProviderId = appleProviderId,
            Claims =
            [
                new ProviderClaim(MetadataFieldConstants.Title, "Canonical Track", 0.95),
                new ProviderClaim(MetadataFieldConstants.Author, "Canonical Artist", 0.95),
                new ProviderClaim(MetadataFieldConstants.Artist, "Canonical Artist", 0.95),
                new ProviderClaim(MetadataFieldConstants.Album, "Canonical Album", 0.95),
                new ProviderClaim(BridgeIdKeys.AppleMusicId, "67890", 0.95),
            ],
        };

        var configLoader = new StubConfigurationLoader
        {
            PipelineConfiguration = new PipelineConfiguration
            {
                Pipelines = new Dictionary<string, MediaTypePipeline>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Music"] = new()
                    {
                        Strategy = ProviderStrategy.Sequential,
                        MaxProviderAttempts = 3,
                        Providers =
                        [
                            new PipelineProviderEntry { Rank = 1, Name = "musicbrainz", Purpose = "identity" },
                            new PipelineProviderEntry
                            {
                                Rank = 2,
                                Name = "apple_api",
                                Purpose = "enrichment",
                                RequiresIdentity = true,
                                UseAsIdentityFallback = true,
                                AcceptedTransition = new AcceptedProviderTransitionConfiguration
                                {
                                    When = "identity-fallback-accepted",
                                    Provider = "musicbrainz",
                                    MaxAttempts = 1,
                                    HintFields =
                                    [
                                        MetadataFieldConstants.Title,
                                        MetadataFieldConstants.Author,
                                        MetadataFieldConstants.Artist,
                                        MetadataFieldConstants.Album,
                                    ],
                                },
                            },
                        ],
                    },
                },
            },
            Providers =
            [
                new() { Name = "musicbrainz", Enabled = true, ProviderId = musicBrainzProviderId.ToString(), Weight = 0.9 },
                new() { Name = "apple_api", Enabled = true, ProviderId = appleProviderId.ToString(), Weight = 0.9 },
            ],
        };
        var canonicalRepo = new StubCanonicalValueRepository();
        await canonicalRepo.UpsertBatchAsync(
        [
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Title, Value = "Raw Track", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Artist, Value = "Raw Artist", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Album, Value = "Raw Album", LastScoredAt = DateTimeOffset.UtcNow },
        ]);

        var worker = new RetailMatchWorker(
            jobRepo,
            candidateRepo,
            CreateStubStageOutcomeFactory(),
            CreateStubTimelineRecorder(),
            CreateStubBatchProgressService(),
            [musicBrainz, apple],
            new StubRetailMatchScoringService
            {
                Result = new FieldMatchScores
                {
                    TitleScore = 1.0,
                    AuthorScore = 1.0,
                    FormatScore = 1.0,
                    CompositeScore = 0.99,
                },
            },
            new StubMetadataClaimRepository(),
            canonicalRepo,
            new StubScoringEngine(),
            configLoader,
            new StubBridgeIdRepository(),
            new StubWorkRepository(),
            new WorkClaimRouter(),
            new StubHttpClientFactory(),
            null!,
            NullLogger<RetailMatchWorker>.Instance);

        Assert.Equal(1, await worker.PollAsync(CancellationToken.None));
        Assert.Equal(2, musicBrainz.Requests.Count);
        Assert.Single(apple.Requests);
        Assert.Equal("Canonical Track", musicBrainz.Requests[1].Title);
        Assert.Equal("Canonical Artist", musicBrainz.Requests[1].Artist);
        Assert.Equal("Canonical Album", musicBrainz.Requests[1].Album);

        var updatedJob = await jobRepo.GetByIdAsync(jobId);
        var reconciledCandidate = Assert.Single(candidateRepo.Candidates, candidate =>
            candidate.ProviderName == "musicbrainz" && candidate.Outcome == "AutoAccepted");
        Assert.Equal(reconciledCandidate.Id, updatedJob!.SelectedCandidateId);
    }

    [Fact]
    public async Task RetailMatchWorker_TvWithOnlyTmdbStaysQueuedWithoutLegacyFallback()
    {
        var entityId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var jobs = new StubIdentityJobRepository();
        await jobs.CreateAsync(new IdentityJob
        {
            Id = jobId, EntityId = entityId, EntityType = "MediaAsset", MediaType = "TV", State = "Queued",
        });
        var canonical = new StubCanonicalValueRepository();
        await canonical.UpsertBatchAsync(
        [
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.ShowName, Value = "Fixture Show", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Title, Value = "Pilot", LastScoredAt = DateTimeOffset.UtcNow },
        ]);
        var loader = new StubConfigurationLoader
        {
            Providers = [new ProviderConfiguration { Name = "tmdb", Enabled = true, HttpClient = new HttpClientConfig { ApiKey = "test-key" } }],
        };
        var provider = new StubExternalMetadataProvider { Name = "tmdb", ProviderId = Guid.NewGuid(), Claims = [] };
        var requests = new List<string>();
        var assets = new StubEntityAssetRepository();
        var worker = new RetailMatchWorker(
            jobs, new StubRetailCandidateRepository(), CreateStubStageOutcomeFactory(), CreateStubTimelineRecorder(), CreateStubBatchProgressService(),
            [provider], new RetailMatchScoringService(new ExactMatchFuzzyMatchingService(), loader, coverArtHash: null, logger: null),
            new StubMetadataClaimRepository(), canonical, new StubScoringEngine(), loader,
            new StubBridgeIdRepository(), new StubWorkRepository(), new WorkClaimRouter(),
            new RoutingHttpClientFactory(request =>
            {
                requests.Add(request.RequestUri!.ToString());
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }), null!, NullLogger<RetailMatchWorker>.Instance, entityAssetRepo: assets);

        Assert.Equal(1, await worker.PollAsync(CancellationToken.None));
        Assert.Equal("Queued", (await jobs.GetByIdAsync(jobId))!.State);
        Assert.Empty(provider.Requests);
        Assert.Empty(requests);
        Assert.Empty(assets.Assets);
    }
    [Fact]
    public async Task RetailMatchWorker_OutcomePriorityPrefersAmbiguousCandidateOverRejectedHighScore()
    {
        var entityId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        var jobRepo = new StubIdentityJobRepository();
        var candidateRepo = new StubRetailCandidateRepository();

        await jobRepo.CreateAsync(new IdentityJob
        {
            Id = jobId,
            EntityId = entityId,
            EntityType = "MediaAsset",
            MediaType = "Books",
            State = "Queued",
        });

        var providers = new IExternalMetadataProvider[]
        {
            new StubExternalMetadataProvider
            {
                Name = "apple_api",
                ProviderId = Guid.NewGuid(),
                Claims =
                [
                    new ProviderClaim(MetadataFieldConstants.Title, "Weak Text Strong Cover", 0.95),
                    new ProviderClaim(MetadataFieldConstants.Author, "Mismatch", 0.95),
                ],
            },
            new StubExternalMetadataProvider
            {
                Name = "openlibrary",
                ProviderId = Guid.NewGuid(),
                Claims =
                [
                    new ProviderClaim(MetadataFieldConstants.Title, "Dune", 0.95),
                    new ProviderClaim(MetadataFieldConstants.Author, "Frank Herbert", 0.95),
                ],
            },
        };

        var retailScoring = new StubRetailMatchScoringService();
        retailScoring.Results.Enqueue(new FieldMatchScores
        {
            TitleScore = 0.40,
            AuthorScore = 0.10,
            YearScore = 0.0,
            FormatScore = 1.0,
            CrossFieldBoost = 0.0,
            CoverArtScore = 0.30,
            CompositeScore = 0.92,
        });
        retailScoring.Results.Enqueue(new FieldMatchScores
        {
            TitleScore = 0.80,
            AuthorScore = 0.70,
            YearScore = 0.0,
            FormatScore = 1.0,
            CrossFieldBoost = 0.0,
            CoverArtScore = 0.0,
            CompositeScore = 0.70,
        });

        var configLoader = new StubConfigurationLoader
        {
            Providers =
            [
                new() { Name = "apple_api", Enabled = true, ProviderId = providers[0].ProviderId.ToString(), Weight = 0.9 },
                new() { Name = "openlibrary", Enabled = true, ProviderId = providers[1].ProviderId.ToString(), Weight = 0.9 },
            ],
        };
        var canonicalRepo = new StubCanonicalValueRepository();
        await canonicalRepo.UpsertBatchAsync(
        [
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Title, Value = "Dune", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = entityId, Key = MetadataFieldConstants.Author, Value = "Frank Herbert", LastScoredAt = DateTimeOffset.UtcNow },
        ]);

        var worker = new RetailMatchWorker(
            jobRepo,
            candidateRepo,
            CreateStubStageOutcomeFactory(),
            CreateStubTimelineRecorder(),
            CreateStubBatchProgressService(),
            providers,
            retailScoring,
            new StubMetadataClaimRepository(),
            canonicalRepo,
            new StubScoringEngine(),
            configLoader,
            new StubBridgeIdRepository(),
            new StubWorkRepository(),
            new WorkClaimRouter(),
            new StubHttpClientFactory(),
            null!,
            NullLogger<RetailMatchWorker>.Instance);

        await worker.PollAsync(CancellationToken.None);

        var updatedJob = await jobRepo.GetByIdAsync(jobId);
        Assert.NotNull(updatedJob);
        Assert.Equal(IdentityJobState.RetailMatchedNeedsReview.ToString(), updatedJob!.State);

        Assert.Equal(2, candidateRepo.Candidates.Count);
        Assert.Equal("Rejected", candidateRepo.Candidates[0].Outcome);
        Assert.Contains("cover_cannot_rescue_weak_text", candidateRepo.Candidates[0].ScoreBreakdownJson);
        Assert.Equal("Ambiguous", candidateRepo.Candidates[1].Outcome);
        Assert.Equal(candidateRepo.Candidates[1].Id, updatedJob.SelectedCandidateId);
    }

    // ── Test 2: RetailMatchWorker no match → RetailNoMatch, WikidataBridge skips ──

    [Fact]
    public async Task RetailMatchWorker_NoMatch_TransitionsToRetailNoMatch_WikidataSkips()
    {
        var entityId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        var jobRepo = new StubIdentityJobRepository();
        var candidateRepo = new StubRetailCandidateRepository();
        var wikidataCandidateRepo = new StubWikidataCandidateRepository();

        var job = new IdentityJob
        {
            Id = jobId,
            EntityId = entityId,
            EntityType = "MediaAsset",
            MediaType = "Books",
            State = "Queued",
        };
        await jobRepo.CreateAsync(job);

        // Provider returns empty claims
        var provider = new StubExternalMetadataProvider
        {
            Name = "apple_api",
            ProviderId = Guid.NewGuid(),
            Claims = [],
        };

        var configLoader = new StubConfigurationLoader();
        var canonicalRepo = new StubCanonicalValueRepository();
        var claimRepo = new StubMetadataClaimRepository();
        var bridgeIdRepo = new StubBridgeIdRepository();
        var scoringEngine = new StubScoringEngine();
        var retailScoring = new StubRetailMatchScoringService();
        var outcomeFactory = CreateStubStageOutcomeFactory();
        var timeline = CreateStubTimelineRecorder();
        var batchProgress = CreateStubBatchProgressService();

        var retailWorker = new RetailMatchWorker(
            jobRepo,
            candidateRepo,
            outcomeFactory,
            timeline,
            batchProgress,
            new[] { provider },
            retailScoring,
            claimRepo,
            canonicalRepo,
            scoringEngine,
            configLoader,
            bridgeIdRepo,
            new StubWorkRepository(),
            new WorkClaimRouter(),
            new StubHttpClientFactory(),
            null!, // PostPipelineService — not exercised in this test
            NullLogger<RetailMatchWorker>.Instance);

        await retailWorker.PollAsync(CancellationToken.None);

        var updatedJob = await jobRepo.GetByIdAsync(jobId);
        Assert.NotNull(updatedJob);
        Assert.Equal(IdentityJobState.RetailNoMatch.ToString(), updatedJob!.State);

        // Now try WikidataBridgeWorker — it should find 0 jobs because it only
        // leases RetailMatched/RetailMatchedNeedsReview, never RetailNoMatch.
        var bridgeIdHelper = new BridgeIdHelper(configLoader);
        var bridgeWorker = new WikidataBridgeWorker(
            jobRepo,
            wikidataCandidateRepo,
            outcomeFactory,
            timeline,
            bridgeIdHelper,
            new[] { provider },
            bridgeIdRepo,
            claimRepo,
            canonicalRepo,
            scoringEngine,
            configLoader,
            new StubWorkRepository(),
            new WorkClaimRouter(),
            new CatalogUpsertService(new StubWorkRepository()),
            new StubIngestionBatchRepository(),
            null!, // PostPipelineService — not exercised because no jobs are leased
            CoverArtWorkerTestFactory.Create(canonicalRepo, new StubWorkRepository()),
            NullLogger<WikidataBridgeWorker>.Instance);

        var bridgeProcessed = await bridgeWorker.PollAsync(CancellationToken.None);
        Assert.Equal(0, bridgeProcessed);
    }

    // ── Test 3: WikidataBridgeWorker with no ReconciliationAdapter → QidNoMatch ──

    [Fact]
    public async Task WikidataBridgeWorker_NoReconAdapter_TransitionsToQidNoMatch()
    {
        var entityId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        var jobRepo = new StubIdentityJobRepository();
        var wikidataCandidateRepo = new StubWikidataCandidateRepository();

        // Seed job directly in RetailMatched state
        var job = new IdentityJob
        {
            Id = jobId,
            EntityId = entityId,
            EntityType = "MediaAsset",
            MediaType = "Books",
            State = IdentityJobState.RetailMatched.ToString(),
        };
        await jobRepo.CreateAsync(job);

        var configLoader = new StubConfigurationLoader();
        var canonicalRepo = new StubCanonicalValueRepository();
        var claimRepo = new StubMetadataClaimRepository();
        var bridgeIdRepo = new StubBridgeIdRepository();
        var scoringEngine = new StubScoringEngine();
        var outcomeFactory = CreateStubStageOutcomeFactory();
        var timeline = CreateStubTimelineRecorder();
        var bridgeIdHelper = new BridgeIdHelper(configLoader);

        // No ReconciliationAdapter in the provider list — only a plain stub
        var plainProvider = new StubExternalMetadataProvider
        {
            Name = "stub_provider",
            ProviderId = Guid.NewGuid(),
            Claims = [],
        };

        var worker = new WikidataBridgeWorker(
            jobRepo,
            wikidataCandidateRepo,
            outcomeFactory,
            timeline,
            bridgeIdHelper,
            new IExternalMetadataProvider[] { plainProvider },
            bridgeIdRepo,
            claimRepo,
            canonicalRepo,
            scoringEngine,
            configLoader,
            new StubWorkRepository(),
            new WorkClaimRouter(),
            new CatalogUpsertService(new StubWorkRepository()),
            new StubIngestionBatchRepository(),
            null!, // PostPipelineService — retained-retail organization not under test here
            CoverArtWorkerTestFactory.Create(canonicalRepo, new StubWorkRepository()),
            NullLogger<WikidataBridgeWorker>.Instance);

        var processed = await worker.PollAsync(CancellationToken.None);

        Assert.Equal(1, processed);

        var updatedJob = await jobRepo.GetByIdAsync(jobId);
        Assert.NotNull(updatedJob);
        Assert.Equal(IdentityJobState.QidNoMatch.ToString(), updatedJob!.State);
        Assert.Contains("No reconciliation adapter", updatedJob.LastError);
    }

    [Fact]
    public async Task WikidataBridgeWorker_NoReconAdapter_EmitsBatchProgressOnce()
    {
        var entityId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var dbPath = Path.Combine(Path.GetTempPath(), $"bridge-progress-{Guid.NewGuid():N}.db");

        try
        {
            var jobRepo = new StubIdentityJobRepository();
            await jobRepo.CreateAsync(new IdentityJob
            {
                Id = jobId,
                EntityId = entityId,
                EntityType = "MediaAsset",
                MediaType = "Books",
                State = IdentityJobState.RetailMatched.ToString(),
                IngestionRunId = batchId,
            });

            var wikidataCandidateRepo = new StubWikidataCandidateRepository();
            var configLoader = new StubConfigurationLoader();
            var canonicalRepo = new StubCanonicalValueRepository();
            var claimRepo = new StubMetadataClaimRepository();
            var bridgeIdRepo = new StubBridgeIdRepository();
            var scoringEngine = new StubScoringEngine();
            var outcomeFactory = CreateStubStageOutcomeFactory();
            var timeline = CreateStubTimelineRecorder();
            var bridgeIdHelper = new BridgeIdHelper(configLoader);
            var batchRepo = new RecordingIngestionBatchRepository(new IngestionBatch
            {
                Id = batchId,
                FilesTotal = 1,
                StartedAt = DateTimeOffset.UtcNow,
                Status = "running",
            });
            var eventPublisher = new RecordingEventPublisher();
            using var db = new DatabaseConnection(dbPath);
            db.InitializeSchema();
            db.RunStartupChecks();
            var batchProgress = new BatchProgressService(
                batchRepo,
                eventPublisher,
                NullLogger<BatchProgressService>.Instance);

            var plainProvider = new StubExternalMetadataProvider
            {
                Name = "stub_provider",
                ProviderId = Guid.NewGuid(),
                Claims = [],
            };

            var worker = new WikidataBridgeWorker(
                jobRepo,
                wikidataCandidateRepo,
                outcomeFactory,
                timeline,
                bridgeIdHelper,
                new IExternalMetadataProvider[] { plainProvider },
                bridgeIdRepo,
                claimRepo,
                canonicalRepo,
                scoringEngine,
                configLoader,
                new StubWorkRepository(),
                new WorkClaimRouter(),
                new CatalogUpsertService(new StubWorkRepository()),
                batchRepo,
                null!,
                CoverArtWorkerTestFactory.Create(canonicalRepo, new StubWorkRepository()),
                NullLogger<WikidataBridgeWorker>.Instance,
                batchProgress);

            await worker.PollAsync(CancellationToken.None);

            Assert.Equal(1, eventPublisher.CountFor(SignalREvents.BatchProgress));
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public void WikidataBridgeWorker_ComicLookupHints_UsesSeriesTitleAndFallbackWriter()
    {
        var entityId = Guid.NewGuid();
        var canonicals = new List<CanonicalValue>
        {
            new() { EntityId = entityId, Key = MetadataFieldConstants.Title, Value = "Chapter One", LastScoredAt = DateTimeOffset.UtcNow },
            new() { EntityId = entityId, Key = MetadataFieldConstants.Series, Value = "Saga", LastScoredAt = DateTimeOffset.UtcNow },
            new() { EntityId = entityId, Key = MetadataFieldConstants.SeriesPosition, Value = "7", LastScoredAt = DateTimeOffset.UtcNow },
            new() { EntityId = entityId, Key = "writer", Value = "Brian K. Vaughan", LastScoredAt = DateTimeOffset.UtcNow },
            new() { EntityId = entityId, Key = MetadataFieldConstants.Language, Value = "ja", LastScoredAt = DateTimeOffset.UtcNow },
        };

        var hints = WikidataBridgeWorker.BuildLookupHints(MediaType.Comics, canonicals);

        Assert.Equal("Saga", hints.TitleHint);
        Assert.Equal("Brian K. Vaughan", hints.AuthorHint);
        Assert.Null(hints.AlbumHint);
        Assert.Null(hints.ArtistHint);
        Assert.Equal("Saga", hints.SeriesHint);
        Assert.Equal("ja", hints.LanguageHint);
        Assert.Equal("7", hints.IssueNumber);
    }

    [Fact]
    public void WikidataBridgeWorker_ComicLookupHints_UsesParentTitleWhenSeriesClaimMissing()
    {
        var childWorkId = Guid.NewGuid();
        var parentWorkId = Guid.NewGuid();
        var canonicals = new List<CanonicalValue>
        {
            new() { EntityId = childWorkId, Key = MetadataFieldConstants.Title, Value = "Saga #2", LastScoredAt = DateTimeOffset.UtcNow },
            new() { EntityId = childWorkId, Key = "issue_number", Value = "2", LastScoredAt = DateTimeOffset.UtcNow },
            new() { EntityId = parentWorkId, Key = MetadataFieldConstants.Title, Value = "Saga", LastScoredAt = DateTimeOffset.UtcNow },
        };

        var hints = WikidataBridgeWorker.BuildLookupHints(MediaType.Comics, canonicals, parentWorkId);

        Assert.Equal("Saga", hints.TitleHint);
        Assert.Equal("Saga", hints.SeriesHint);
        Assert.Equal("2", hints.IssueNumber);
    }

    [Fact]
    public void WikidataBridgeWorker_BridgeYearHint_DropsComicIssueYearForSeriesLookup()
    {
        Assert.Null(WikidataBridgeWorker.BuildBridgeYearHint(MediaType.Comics, "Batman", "1987"));
        Assert.Equal("1987", WikidataBridgeWorker.BuildBridgeYearHint(MediaType.Comics, null, "1987"));
        Assert.Equal("2005", WikidataBridgeWorker.BuildBridgeYearHint(MediaType.Movies, "Batman in film", "2005"));
    }

    [Theory]
    [InlineData(MediaType.TV, "bridge_id", "tv_show_rollup")]
    [InlineData(MediaType.Music, "music_album", "music_album_rollup")]
    [InlineData(MediaType.Movies, "bridge_id", "bridge")]
    [InlineData(MediaType.Books, "retail_text", "retail_text")]
    public void WikidataBridgeWorker_QidResolutionMethod_ReportsContainerRollups(
        MediaType mediaType,
        string matchedBy,
        string expected)
    {
        Assert.Equal(expected, WikidataBridgeWorker.ResolveQidResolutionMethod(mediaType, matchedBy));
    }

    [Fact]
    public void WikidataBridgeWorker_AudiobookLookupHints_FallbackToArtistAndAlbum()
    {
        var entityId = Guid.NewGuid();
        var canonicals = new List<CanonicalValue>
        {
            new() { EntityId = entityId, Key = MetadataFieldConstants.Title, Value = "Harry Potter and the Philosopher's Stone", LastScoredAt = DateTimeOffset.UtcNow },
            new() { EntityId = entityId, Key = MetadataFieldConstants.Artist, Value = "J.K. Rowling", LastScoredAt = DateTimeOffset.UtcNow },
            new() { EntityId = entityId, Key = MetadataFieldConstants.Album, Value = "Harry Potter", LastScoredAt = DateTimeOffset.UtcNow },
            new() { EntityId = entityId, Key = MetadataFieldConstants.Year, Value = "2024", LastScoredAt = DateTimeOffset.UtcNow },
        };

        var hints = WikidataBridgeWorker.BuildLookupHints(MediaType.Audiobooks, canonicals);

        Assert.Equal("Harry Potter and the Philosopher's Stone", hints.TitleHint);
        Assert.Equal("J.K. Rowling", hints.AuthorHint);
        Assert.Equal("Harry Potter", hints.SeriesHint);
        Assert.Equal("2024", hints.YearHint);
    }

    [Fact]
    public void WikidataBridgeWorker_CollectsScopedWorkBridgeIdsForResolution()
    {
        var assetId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var trackWorkId = Guid.NewGuid();
        var albumWorkId = Guid.NewGuid();
        var lineage = new WorkLineage(
            assetId,
            editionId,
            trackWorkId,
            albumWorkId,
            albumWorkId,
            WorkKind.Child,
            MediaType.Music);

        var allBridgeIds = new Dictionary<Guid, IReadOnlyList<BridgeIdEntry>>
        {
            [assetId] =
            [
                new()
                {
                    EntityId = assetId,
                    IdType = BridgeIdKeys.Isbn13,
                    IdValue = "legacy-asset-id",
                },
            ],
            [trackWorkId] =
            [
                new()
                {
                    EntityId = trackWorkId,
                    IdType = BridgeIdKeys.AppleMusicId,
                    IdValue = "1708269213",
                },
            ],
            [albumWorkId] =
            [
                new()
                {
                    EntityId = albumWorkId,
                    IdType = BridgeIdKeys.AppleMusicCollectionId,
                    IdValue = "1708269212",
                },
                new()
                {
                    EntityId = albumWorkId,
                    IdType = BridgeIdKeys.AppleMusicId,
                    IdValue = "wrong-scope-track-id",
                },
            ],
        };

        var bridgeIds = WikidataBridgeWorker.CollectScopedBridgeIdsForResolution(
            assetId,
            MediaType.Music,
            lineage,
            allBridgeIds);

        Assert.Contains(bridgeIds, id =>
            id.EntityId == assetId
            && id.IdType == BridgeIdKeys.Isbn13
            && id.IdValue == "legacy-asset-id");
        Assert.Contains(bridgeIds, id =>
            id.EntityId == trackWorkId
            && id.IdType == BridgeIdKeys.AppleMusicId
            && id.IdValue == "1708269213");
        Assert.Contains(bridgeIds, id =>
            id.EntityId == albumWorkId
            && id.IdType == BridgeIdKeys.AppleMusicCollectionId
            && id.IdValue == "1708269212");
        Assert.DoesNotContain(bridgeIds, id => id.IdValue == "wrong-scope-track-id");
    }

    // ── Test 4: QuickHydrationWorker queues Stage 3 after quick hydration ──

    [Fact]
    public void WikidataBridgeWorker_MergesCanonicalBridgeIdsForResolution()
    {
        var assetId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var trackWorkId = Guid.NewGuid();
        var albumWorkId = Guid.NewGuid();
        var lineage = new WorkLineage(
            assetId,
            editionId,
            trackWorkId,
            albumWorkId,
            albumWorkId,
            WorkKind.Child,
            MediaType.Music);

        var bridgeIds = new List<BridgeIdEntry>
        {
            new()
            {
                EntityId = trackWorkId,
                IdType = BridgeIdKeys.AppleMusicId,
                IdValue = "726378511",
            },
        };
        var canonicals = new List<CanonicalValue>
        {
            new()
            {
                EntityId = assetId,
                Key = BridgeIdKeys.Isbn,
                Value = "9782070612758",
                LastScoredAt = DateTimeOffset.UtcNow,
            },
            new()
            {
                EntityId = albumWorkId,
                Key = BridgeIdKeys.AppleMusicCollectionId,
                Value = "726378402",
                LastScoredAt = DateTimeOffset.UtcNow,
            },
            new()
            {
                EntityId = albumWorkId,
                Key = BridgeIdKeys.AppleMusicId,
                Value = "wrong-scope-track-id",
                LastScoredAt = DateTimeOffset.UtcNow,
            },
        };

        var merged = WikidataBridgeWorker.MergeCanonicalBridgeIdsForResolution(
            assetId,
            MediaType.Music,
            lineage,
            bridgeIds,
            canonicals);

        Assert.Contains(merged, id =>
            id.EntityId == trackWorkId
            && id.IdType == BridgeIdKeys.AppleMusicId
            && id.IdValue == "726378511");
        Assert.Contains(merged, id =>
            id.EntityId == assetId
            && id.IdType == BridgeIdKeys.Isbn
            && id.IdValue == "9782070612758");
        Assert.Contains(merged, id =>
            id.EntityId == albumWorkId
            && id.IdType == BridgeIdKeys.AppleMusicCollectionId
            && id.IdValue == "726378402");
        Assert.DoesNotContain(merged, id => id.IdValue == "wrong-scope-track-id");
    }

    [Fact]
    public async Task QuickHydrationWorker_QueuesStage3AfterQuickHydration()
    {
        var entityId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var qid = "Q190159";

        var jobRepo = new StubIdentityJobRepository();
        var enrichment = new StubEnrichmentService();

        var job = new IdentityJob
        {
            Id = jobId,
            EntityId = entityId,
            EntityType = "MediaAsset",
            MediaType = "Books",
            State = IdentityJobState.QidResolved.ToString(),
            ResolvedQid = qid,
        };
        await jobRepo.CreateAsync(job);

        var configLoader = new StubConfigurationLoader();
        var claimRepo = new StubMetadataClaimRepository();
        var canonicalRepo = new StubCanonicalValueRepository();
        var scoringEngine = new StubScoringEngine();
        var reviewRepo = new StubReviewQueueRepository();
        var organizer = new StubAutoOrganizeService();
        var batchProgress = CreateStubBatchProgressService();
        var universeScheduler = new StubUniverseEnrichmentScheduler();

        var postPipeline = new PostPipelineService(
            claimRepo,
            canonicalRepo,
            scoringEngine,
            configLoader,
            Array.Empty<IExternalMetadataProvider>(),
            reviewRepo,
            organizer,
            batchProgress,
            NullLogger<PostPipelineService>.Instance);

        var collectionRepo = new NoOpCollectionRepository();
        var collectionAssignment = new CollectionAssignmentService(
            collectionRepo,
            canonicalRepo,
            new StubWorkRepository(),
            NullLogger<CollectionAssignmentService>.Instance);
        var collectionFinalization = new CollectionFinalizationService(
            collectionAssignment,
            new ParentCollectionResolver(collectionRepo, NullLogger<ParentCollectionResolver>.Instance),
            new StubSystemActivityRepository(),
            NullLogger<CollectionFinalizationService>.Instance);

        var worker = new QuickHydrationWorker(
            jobRepo,
            enrichment,
            collectionFinalization,
            postPipeline,
            canonicalRepo,
            collectionRepo,
            universeScheduler,
            configLoader,
            NullLogger<QuickHydrationWorker>.Instance);

        var processed = await worker.PollAsync(CancellationToken.None);

        Assert.Equal(1, processed);

        var updatedJob = await jobRepo.GetByIdAsync(jobId);
        Assert.NotNull(updatedJob);
        Assert.Equal(IdentityJobState.UniverseEnriching.ToString(), updatedJob!.State);

        Assert.Single(enrichment.Calls);
        Assert.Equal(entityId, enrichment.Calls[0].EntityId);
        Assert.Equal(qid, enrichment.Calls[0].Qid);
        Assert.Single(universeScheduler.Requests);
        Assert.Equal(entityId, universeScheduler.Requests[0].EntityId);
        Assert.Equal(qid, universeScheduler.Requests[0].WorkQid);
    }

    [Fact]
    public async Task PostPipeline_RetainedRetailIdentity_SupersedesBridgeOnlyReview()
    {
        var entityId = Guid.NewGuid();
        var reviewRepo = new StubReviewQueueRepository();
        var service = new PostPipelineService(
            new StubMetadataClaimRepository(),
            new StubCanonicalValueRepository(),
            new StubScoringEngine { OverallConfidence = 0.95 },
            new StubConfigurationLoader(),
            Array.Empty<IExternalMetadataProvider>(),
            reviewRepo,
            new StubAutoOrganizeService(),
            CreateStubBatchProgressService(),
            NullLogger<PostPipelineService>.Instance);

        await service.EvaluateAndOrganizeAsync(
            entityId,
            Guid.NewGuid(),
            wikidataQid: null,
            ingestionRunId: null,
            CancellationToken.None,
            retainedRetailIdentity: true);

        var resolution = Assert.Single(reviewRepo.ResolveCalls);
        Assert.Equal(entityId, resolution.EntityId);
        Assert.Equal("system:retained-retail-identity", resolution.ResolvedBy);
        Assert.Contains(ReviewTrigger.WikidataBridgeFailed, resolution.Triggers);
        Assert.Contains(ReviewTrigger.MissingQid, resolution.Triggers);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Factory helpers for concrete helpers that require repo dependencies
    // ══════════════════════════════════════════════════════════════════════

    private static StageOutcomeFactory CreateStubStageOutcomeFactory()
    {
        return new StageOutcomeFactory(
            new StubReviewQueueRepository(),
            new StubSystemActivityRepository(),
            new StubEventPublisher(),
            new StubCanonicalValueRepository(),
            NullLogger<StageOutcomeFactory>.Instance);
    }

    private static TimelineRecorder CreateStubTimelineRecorder()
    {
        return new TimelineRecorder(new StubEntityTimelineRepository());
    }

    private static BatchProgressService CreateStubBatchProgressService()
    {
        return new BatchProgressService(
            new StubIngestionBatchRepository(),
            new StubEventPublisher(),
            NullLogger<BatchProgressService>.Instance);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    // ══════════════════════════════════════════════════════════════════════
    //  Stub implementations
    // ══════════════════════════════════════════════════════════════════════

    // ── StubIdentityJobRepository ────────────────────────────────────────

    private sealed class StubIdentityJobRepository : IIdentityJobRepository
    {
        private readonly List<IdentityJob> _jobs = [];

        public Task CreateAsync(IdentityJob job, CancellationToken ct = default)
        {
            _jobs.Add(job);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<IdentityJob>> LeaseNextAsync(
            string workerName,
            IReadOnlyList<IdentityJobState> states,
            int batchSize,
            TimeSpan leaseDuration,
            IReadOnlyList<string>? excludeRunIds = null,
            CancellationToken ct = default)
        {
            var stateStrings = states.Select(s => s.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var excluded = excludeRunIds is { Count: > 0 }
                ? new HashSet<string>(excludeRunIds, StringComparer.OrdinalIgnoreCase)
                : null;
            var matches = _jobs
                .Where(j => stateStrings.Contains(j.State)
                         && (excluded is null || j.IngestionRunId is null || !excluded.Contains(j.IngestionRunId.ToString()!)))
                .Take(batchSize)
                .ToList();

            foreach (var j in matches)
            {
                j.LeaseOwner = workerName;
                j.LeaseExpiresAt = DateTimeOffset.UtcNow.Add(leaseDuration);
            }

            return Task.FromResult<IReadOnlyList<IdentityJob>>(matches);
        }

        public Task UpdateStateAsync(Guid jobId, IdentityJobState newState, string? error = null, CancellationToken ct = default)
        {
            var job = _jobs.FirstOrDefault(j => j.Id == jobId);
            if (job is not null)
            {
                job.State = newState.ToString();
                job.LastError = error;
                job.UpdatedAt = DateTimeOffset.UtcNow;
            }
            return Task.CompletedTask;
        }

        public Task SetSelectedCandidateAsync(Guid jobId, Guid candidateId, CancellationToken ct = default)
        {
            var job = _jobs.FirstOrDefault(j => j.Id == jobId);
            if (job is not null)
            {
                job.SelectedCandidateId = candidateId;
            }

            return Task.CompletedTask;
        }

        public Task SetResolvedQidAsync(Guid jobId, string qid, CancellationToken ct = default)
        {
            var job = _jobs.FirstOrDefault(j => j.Id == jobId);
            if (job is not null)
            {
                job.ResolvedQid = qid;
            }

            return Task.CompletedTask;
        }

        public Task<IdentityJob?> GetByIdAsync(Guid jobId, CancellationToken ct = default)
            => Task.FromResult(_jobs.FirstOrDefault(j => j.Id == jobId));

        public Task<IdentityJob?> GetByEntityAsync(Guid entityId, CancellationToken ct = default)
            => Task.FromResult(_jobs.FirstOrDefault(j => j.EntityId == entityId));

        public Task<IReadOnlyList<IdentityJob>> GetStaleAsync(TimeSpan age, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IdentityJob>>([]);

        public Task<IReadOnlyList<IdentityJob>> GetByStateAsync(IdentityJobState state, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IdentityJob>>(
                _jobs.Where(j => j.State == state.ToString()).Take(limit).ToList());

        public Task<IReadOnlyDictionary<string, int>> GetStateCountsByRunAsync(Guid ingestionRunId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());

        public Task<IReadOnlyDictionary<string, int>> GetPendingStage1CountsByRunAsync(
            IReadOnlyList<string> ingestionRunIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());

        public Task<int> ReclaimStuckJobsAsync(
            IdentityJobState processingState,
            TimeSpan stuckThreshold,
            CancellationToken ct = default)
            => Task.FromResult(0);

        public Task ReleaseLeaseAsync(Guid jobId, CancellationToken ct = default)
        {
            var job = _jobs.FirstOrDefault(j => j.Id == jobId);
            if (job is not null)
            {
                job.LeaseOwner = null;
                job.LeaseExpiresAt = null;
            }
            return Task.CompletedTask;
        }

        public Task<int> CountActiveAsync(CancellationToken ct = default)
            => Task.FromResult(_jobs.Count(j =>
                j.State != IdentityJobState.Ready.ToString() &&
                j.State != IdentityJobState.ReadyWithoutUniverse.ToString() &&
                j.State != IdentityJobState.Failed.ToString()));
    }

    // ── StubRetailCandidateRepository ────────────────────────────────────

    private sealed class StubRetailCandidateRepository : IRetailCandidateRepository
    {
        public List<RetailMatchCandidate> Candidates { get; } = [];

        public Task InsertBatchAsync(IReadOnlyList<RetailMatchCandidate> candidates, CancellationToken ct = default)
        {
            Candidates.AddRange(candidates);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RetailMatchCandidate>> GetByJobAsync(Guid jobId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RetailMatchCandidate>>(
                Candidates.Where(c => c.JobId == jobId).ToList());

        public Task<RetailMatchCandidate?> GetSelectedAsync(Guid jobId, CancellationToken ct = default)
            => Task.FromResult(Candidates.FirstOrDefault(c => c.JobId == jobId && c.Outcome == "AutoAccepted"));

        public Task<RetailMatchCandidate?> GetByIdAsync(Guid candidateId, CancellationToken ct = default)
            => Task.FromResult(Candidates.FirstOrDefault(c => c.Id == candidateId));
    }

    // ── StubWikidataCandidateRepository ──────────────────────────────────

    private sealed class StubWikidataCandidateRepository : IWikidataCandidateRepository
    {
        public List<WikidataBridgeCandidate> Candidates { get; } = [];

        public Task InsertBatchAsync(IReadOnlyList<WikidataBridgeCandidate> candidates, CancellationToken ct = default)
        {
            Candidates.AddRange(candidates);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WikidataBridgeCandidate>> GetByJobAsync(Guid jobId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WikidataBridgeCandidate>>(
                Candidates.Where(c => c.JobId == jobId).ToList());

        public Task<WikidataBridgeCandidate?> GetSelectedAsync(Guid jobId, CancellationToken ct = default)
            => Task.FromResult(Candidates.FirstOrDefault(c => c.JobId == jobId && c.Outcome == "AutoAccepted"));

        public Task<WikidataBridgeCandidate?> GetByIdAsync(Guid candidateId, CancellationToken ct = default)
            => Task.FromResult(Candidates.FirstOrDefault(c => c.Id == candidateId));
    }

    // ── StubExternalMetadataProvider ─────────────────────────────────────

    private sealed class StubExternalMetadataProvider : IExternalMetadataProvider
    {
        public string Name { get; set; } = "stub_provider";
        public ProviderDomain Domain => ProviderDomain.Universal;
        public IReadOnlyList<string> CapabilityTags => [];
        public Guid ProviderId { get; set; } = Guid.NewGuid();
        public IReadOnlyList<ProviderClaim> Claims { get; set; } = [];
        public Queue<IReadOnlyList<ProviderClaim>> ClaimResults { get; } = new();
        public List<ProviderLookupRequest> Requests { get; } = [];

        public bool CanHandle(MediaType mediaType) => true;
        public bool CanHandle(EntityType entityType) => true;

        public Task<IReadOnlyList<ProviderClaim>> FetchAsync(ProviderLookupRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(ClaimResults.Count > 0 ? ClaimResults.Dequeue() : Claims);
        }
    }

    // ── StubRetailMatchScoringService ────────────────────────────────────

    private sealed class StubRetailMatchScoringService : IRetailMatchScoringService
    {
        public Queue<FieldMatchScores> Results { get; } = new();

        public FieldMatchScores Result { get; set; } = new()
        {
            CompositeScore = 0.0,
        };

        public FieldMatchScores ScoreCandidate(
            IReadOnlyDictionary<string, string> fileHints,
            string? candidateTitle,
            string? candidateAuthor,
            string? candidateYear,
            MediaType mediaType,
            MatchTierConfig? matchTiers = null,
            CandidateExtendedMetadata? extendedMetadata = null,
            double structuralBonus = 0.0)
            => Results.Count > 0 ? Results.Dequeue() : Result;
    }

    private sealed class ExactMatchFuzzyMatchingService : IFuzzyMatchingService
    {
        public double ComputeTokenSetRatio(string a, string b) =>
            string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;

        public double ComputePartialRatio(string a, string b) =>
            string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;

        public FieldMatchResult ScoreCandidate(LocalMetadata local, CandidateMetadata candidate) =>
            new()
            {
                TitleScore = 1.0,
                AuthorScore = 1.0,
                YearScore = 1.0,
                CompositeScore = 1.0,
            };
    }

    // ── StubConfigurationLoader ─────────────────────────────────────────

    private static StubConfigurationLoader CreateAppleMusicIdentityConfig() => new()
    {
        PipelineConfiguration = new PipelineConfiguration
        {
            Pipelines = new Dictionary<string, MediaTypePipeline>(StringComparer.OrdinalIgnoreCase)
            {
                ["Music"] = new()
                {
                    Strategy = ProviderStrategy.Waterfall,
                    Providers =
                    [
                        new PipelineProviderEntry { Rank = 1, Name = "apple_api", Purpose = "identity" },
                    ],
                },
            },
        },
        Providers =
        [
            new() { Name = "apple_api", Enabled = true, ProviderId = WellKnownProviders.AppleApi.ToString(), Weight = 0.9 },
        ],
    };

    private sealed class StubConfigurationLoader : IConfigurationLoader
    {
        public PipelineConfiguration? PipelineConfiguration { get; init; }
        public IReadOnlyList<ProviderConfiguration> Providers { get; init; } = [];
        public HydrationSettings Hydration { get; init; } = new();

        public PipelineConfiguration LoadPipelines() => RetailMatrixFixture.WithMatrices(PipelineConfiguration ?? new()
        {
            Pipelines = new Dictionary<string, MediaTypePipeline>(StringComparer.OrdinalIgnoreCase)
            {
                ["Books"] = new()
                {
                    Strategy = ProviderStrategy.Waterfall,
                    Providers =
                    [
                        new PipelineProviderEntry { Rank = 1, Name = "apple_api", Purpose = "identity" },
                        new PipelineProviderEntry { Rank = 2, Name = "openlibrary", Purpose = "identity" },
                    ],
                },
                ["Comics"] = new()
                {
                    Strategy = ProviderStrategy.Waterfall,
                    Providers =
                    [
                        new PipelineProviderEntry { Rank = 1, Name = "comicvine", Purpose = "identity" },
                    ],
                },
                ["Music"] = new()
                {
                    Strategy = ProviderStrategy.Waterfall,
                    Providers =
                    [
                        new PipelineProviderEntry { Rank = 1, Name = "apple_api", Purpose = "identity" },
                    ],
                },
            },
        });

        public HydrationSettings LoadHydration() => Hydration;
        public IReadOnlyList<ProviderConfiguration> LoadAllProviders() =>
            Providers.Count > 0
                ? Providers
                :
                [
                    new() { Name = "apple_api", Enabled = true, ProviderId = WellKnownProviders.AppleApi.ToString(), Weight = 0.9 },
                    new() { Name = "comicvine", Enabled = true, ProviderId = WellKnownProviders.ComicVine.ToString(), Weight = 0.9 },
                    new() { Name = "tmdb", Enabled = true, ProviderId = WellKnownProviders.Tmdb.ToString(), Weight = 0.9 },
                ];
        public ScoringSettings LoadScoring() => new();
        public T? LoadConfig<T>(string subdirectory, string name) where T : class => default;

        // Remaining methods throw — not called during tests
        public CoreConfiguration LoadCore() => new();
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

    // ── StubCanonicalValueRepository ────────────────────────────────────

    private sealed class StubCanonicalValueRepository : ICanonicalValueRepository
    {
        public List<CanonicalValue> Values { get; } = [];

        public Task<IReadOnlyList<CanonicalValue>> GetByEntityAsync(Guid entityId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CanonicalValue>>(Values.Where(v => v.EntityId == entityId).ToList());

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalValue>>> GetByEntitiesAsync(IReadOnlyList<Guid> entityIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalValue>>>(
                entityIds.ToDictionary(
                    id => id,
                    id => (IReadOnlyList<CanonicalValue>)Values.Where(v => v.EntityId == id).ToList()));

        public Task UpsertBatchAsync(IReadOnlyList<CanonicalValue> values, CancellationToken ct = default)
        {
            foreach (var value in values)
            {
                Values.RemoveAll(v => v.EntityId == value.EntityId && string.Equals(v.Key, value.Key, StringComparison.OrdinalIgnoreCase));
                Values.Add(value);
            }

            return Task.CompletedTask;
        }

        public Task DeleteByEntityAsync(Guid entityId, CancellationToken ct = default)
        {
            Values.RemoveAll(v => v.EntityId == entityId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CanonicalValue>> GetConflictedAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CanonicalValue>>([]);

        public Task DeleteByKeyAsync(Guid entityId, string key, CancellationToken ct = default)
        {
            Values.RemoveAll(v => v.EntityId == entityId && string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Guid>> FindByValueAsync(string key, string value, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>(
                Values.Where(v => string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase)
                               && string.Equals(v.Value, value, StringComparison.OrdinalIgnoreCase))
                      .Select(v => v.EntityId)
                      .Distinct()
                      .ToList());

        public Task<IReadOnlyList<CanonicalValue>> FindByKeyAndPrefixAsync(string key, string prefix, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CanonicalValue>>([]);

        public Task<IReadOnlyList<Guid>> GetEntitiesNeedingEnrichmentAsync(string hasField, string missingField, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>([]);
    }

    // ── StubMetadataClaimRepository ─────────────────────────────────────

    private sealed class StubMetadataClaimRepository : IMetadataClaimRepository
    {
        public List<MetadataClaim> Claims { get; } = [];

        public Task InsertBatchAsync(IReadOnlyList<MetadataClaim> claims, CancellationToken ct = default)
        {
            Claims.AddRange(claims);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MetadataClaim>> GetByEntityAsync(Guid entityId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MetadataClaim>>(Claims.Where(c => c.EntityId == entityId).ToList());

        public Task DeleteByEntityAsync(Guid entityId, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    // ── StubBridgeIdRepository ──────────────────────────────────────────

    private sealed class StubBridgeIdRepository : IBridgeIdRepository
    {
        public List<BridgeIdEntry> Entries { get; set; } = [];

        public Task<IReadOnlyList<BridgeIdEntry>> GetByEntityAsync(Guid entityId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<BridgeIdEntry>>(Entries.Where(e => e.EntityId == entityId).ToList());

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<BridgeIdEntry>>> GetByEntitiesAsync(IReadOnlyList<Guid> entityIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<BridgeIdEntry>>>(
                entityIds.ToDictionary(
                    id => id,
                    id => (IReadOnlyList<BridgeIdEntry>)Entries.Where(e => e.EntityId == id).ToList()));

        public Task UpsertBatchAsync(IReadOnlyList<BridgeIdEntry> entries, CancellationToken ct = default)
        {
            Entries.AddRange(entries);
            return Task.CompletedTask;
        }

        public Task<BridgeIdEntry?> FindAsync(Guid entityId, string idType, CancellationToken ct = default)
            => Task.FromResult(Entries.FirstOrDefault(e => e.EntityId == entityId && e.IdType == idType));

        public Task<IReadOnlyList<BridgeIdEntry>> FindByValueAsync(string idType, string idValue, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<BridgeIdEntry>>(
                Entries.Where(e => e.IdType == idType && e.IdValue == idValue).ToList());

        public Task UpsertAsync(BridgeIdEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task DeleteByEntityAsync(Guid entityId, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    // ── StubScoringEngine ───────────────────────────────────────────────

    private sealed class StubScoringEngine : IScoringEngine
    {
        public double OverallConfidence { get; set; } = 0.90;

        public Task<ScoringResult> ScoreEntityAsync(ScoringContext context, CancellationToken ct = default)
            => Task.FromResult(new ScoringResult
            {
                EntityId = context.EntityId,
                OverallConfidence = OverallConfidence,
                ScoredAt = DateTimeOffset.UtcNow,
                FieldScores = [],
            });

        public Task<IReadOnlyList<ScoringResult>> ScoreBatchAsync(IEnumerable<ScoringContext> contexts, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ScoringResult>>(
                contexts.Select(c => ScoreEntityAsync(c, ct).Result).ToList());
    }

    // ── StubEnrichmentService ───────────────────────────────────────────

    private sealed class StubEnrichmentService : IEnrichmentService
    {
        public List<(Guid EntityId, string Qid)> Calls { get; } = [];

        public Task RunQuickPassAsync(Guid entityId, string qid, CancellationToken ct = default)
        {
            Calls.Add((entityId, qid));
            return Task.CompletedTask;
        }

        public Task RunUniversePassAsync(Guid entityId, string qid, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RunWorkScopedPassAsync(Guid entityId, string qid, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RunUniverseCorePassAsync(Guid entityId, string qid, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RunUniverseEnhancerPassAsync(Guid entityId, string qid, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RunSingleEnrichmentAsync(Guid entityId, string qid, EnrichmentType type, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class StubUniverseEnrichmentScheduler : IUniverseEnrichmentScheduler
    {
        public List<UniverseEnrichmentRequest> Requests { get; } = [];

        public ValueTask QueueInlineAsync(UniverseEnrichmentRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return ValueTask.CompletedTask;
        }

        public void TriggerManualSweep()
        {
        }
    }

    // ── StubReviewQueueRepository ───────────────────────────────────────

    private sealed class StubReviewQueueRepository : IReviewQueueRepository
    {
        public List<(Guid EntityId, IReadOnlyCollection<string> Triggers, string ResolvedBy)> ResolveCalls { get; } = [];

        public Task<Guid> InsertAsync(ReviewQueueEntry entry, CancellationToken ct = default)
            => Task.FromResult(entry.Id);

        public Task<IReadOnlyList<ReviewQueueEntry>> GetPendingAsync(int limit = 50, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ReviewQueueEntry>>([]);

        public Task<ReviewQueueEntry?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<ReviewQueueEntry?>(null);

        public Task<IReadOnlyList<ReviewQueueEntry>> GetByEntityAsync(Guid entityId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ReviewQueueEntry>>([]);

        public Task<IReadOnlyList<ReviewQueueEntry>> GetPendingByEntityAsync(Guid entityId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ReviewQueueEntry>>([]);

        public Task UpdateStatusAsync(Guid id, string status, string? resolvedBy = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<int> MarkPendingReadyByEntityAsync(Guid entityId, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<IReadOnlyList<ReviewQueueEntry>> PromotePendingReadyByEntityAsync(Guid entityId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ReviewQueueEntry>>([]);

        public Task<int> GetPendingCountAsync(CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<int> DismissAllByEntityAsync(Guid entityId, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<int> ResolveAllByEntityAsync(Guid entityId, string resolvedBy = "system:auto-organize", CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<int> ResolvePendingByEntityAndTriggersAsync(Guid entityId, IReadOnlyCollection<string> triggers, string resolvedBy, CancellationToken ct = default)
        {
            ResolveCalls.Add((entityId, triggers, resolvedBy));
            return Task.FromResult(0);
        }

        public Task<int> PurgeOrphanedAsync(CancellationToken ct = default)
            => Task.FromResult(0);
    }

    // ── StubSystemActivityRepository ────────────────────────────────────

    private sealed class StubSystemActivityRepository : ISystemActivityRepository
    {
        public Task LogAsync(SystemActivityEntry entry, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<SystemActivityEntry>> GetRecentAsync(int limit = 50, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SystemActivityEntry>>([]);

        public Task<int> PruneOlderThanAsync(int retentionDays, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<long> CountAsync(CancellationToken ct = default)
            => Task.FromResult(0L);

        public Task<IReadOnlyList<SystemActivityEntry>> GetByRunIdAsync(Guid runId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SystemActivityEntry>>([]);

        public Task<IReadOnlyList<SystemActivityEntry>> GetRecentByTypesAsync(IReadOnlyList<string> actionTypes, int limit = 50, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SystemActivityEntry>>([]);

        public Task<IReadOnlyList<SystemActivityEntry>> GetRecentByProfileAsync(Guid profileId, int limit = 50, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SystemActivityEntry>>([]);
    }

    // ── StubEventPublisher ──────────────────────────────────────────────

    private sealed class StubEventPublisher : IEventPublisher
    {
        public Task PublishAsync<TPayload>(string eventName, TPayload payload, CancellationToken ct = default)
            where TPayload : notnull
            => Task.CompletedTask;
    }

    // ── StubEntityTimelineRepository ────────────────────────────────────

    private sealed class StubEntityTimelineRepository : IEntityTimelineRepository
    {
        public Task InsertEventAsync(EntityEvent evt, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task InsertEventsAsync(IReadOnlyList<EntityEvent> events, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<EntityEvent>> GetEventsByEntityAsync(Guid entityId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EntityEvent>>([]);

        public Task<EntityEvent?> GetLatestEventAsync(Guid entityId, int stage, CancellationToken ct = default)
            => Task.FromResult<EntityEvent?>(null);

        public Task<IReadOnlyList<EntityEvent>> GetCurrentPipelineStateAsync(Guid entityId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EntityEvent>>([]);

        public Task<EntityEvent?> GetEventByIdAsync(Guid eventId, CancellationToken ct = default)
            => Task.FromResult<EntityEvent?>(null);

        public Task InsertFieldChangesAsync(IReadOnlyList<EntityFieldChange> changes, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<EntityFieldChange>> GetFieldChangesByEventAsync(Guid eventId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EntityFieldChange>>([]);

        public Task<IReadOnlyList<EntityFieldChange>> GetFieldHistoryAsync(Guid entityId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EntityFieldChange>>([]);

        public Task<IReadOnlyList<EntityFieldChange>> GetFieldHistoryAsync(Guid entityId, string field, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EntityFieldChange>>([]);

        public Task<IReadOnlyList<EntityFieldChange>> GetFileOriginalsForEventAsync(Guid eventId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EntityFieldChange>>([]);

        public Task<IReadOnlyDictionary<Guid, EntityEvent>> GetLatestStage2EventsAsync(IReadOnlyList<Guid> entityIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<Guid, EntityEvent>>(new Dictionary<Guid, EntityEvent>());

        public Task<int> CullOldEventsAsync(TimeSpan retention, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task DeleteByEntityAsync(Guid entityId, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    // ── StubIngestionBatchRepository ────────────────────────────────────

    private sealed class StubIngestionBatchRepository : IIngestionBatchRepository
    {
        public Task CreateAsync(IngestionBatch batch, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task UpdateCountsAsync(Guid id, int filesTotal, int filesProcessed, int filesIdentified, int filesReview, int filesNoMatch, int filesFailed, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task CompleteAsync(Guid id, string status, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task IncrementCounterAsync(Guid id, BatchCounterColumn column, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IngestionBatch?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<IngestionBatch?>(null);

        public Task<IReadOnlyList<IngestionBatch>> GetRecentAsync(int limit = 20, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IngestionBatch>>([]);

        public Task<int> GetNeedsAttentionCountAsync(CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<IngestionBatchProgressSnapshot> GetProgressSnapshotAsync(Guid batchId, CancellationToken ct = default)
            => Task.FromResult(new IngestionBatchProgressSnapshot());
    }

    // ── StubAutoOrganizeService ─────────────────────────────────────────

    private sealed class StubAutoOrganizeService : IAutoOrganizeService
    {
        public Task TryAutoOrganizeAsync(Guid assetId, CancellationToken ct = default, Guid? ingestionRunId = null)
            => Task.CompletedTask;
    }

    // ── NoOpCollectionRepository ─────────────────────────────────────────────────

    private sealed class RecordingIngestionBatchRepository : IIngestionBatchRepository
    {
        private readonly IngestionBatch _batch;

        public RecordingIngestionBatchRepository(IngestionBatch batch)
            => _batch = batch;

        public Task CreateAsync(IngestionBatch batch, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task UpdateCountsAsync(Guid id, int filesTotal, int filesProcessed, int filesIdentified, int filesReview, int filesNoMatch, int filesFailed, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task CompleteAsync(Guid id, string status, CancellationToken ct = default)
        {
            _batch.Status = status;
            _batch.CompletedAt = DateTimeOffset.UtcNow;
            return Task.CompletedTask;
        }

        public Task IncrementCounterAsync(Guid id, BatchCounterColumn column, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IngestionBatch?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(id == _batch.Id ? _batch : null);

        public Task<IReadOnlyList<IngestionBatch>> GetRecentAsync(int limit = 20, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IngestionBatch>>([_batch]);

        public Task<int> GetNeedsAttentionCountAsync(CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<IngestionBatchProgressSnapshot> GetProgressSnapshotAsync(Guid batchId, CancellationToken ct = default)
            => Task.FromResult(new IngestionBatchProgressSnapshot());
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);

        public Task PublishAsync<TPayload>(string eventName, TPayload payload, CancellationToken ct = default)
            where TPayload : notnull
        {
            _counts[eventName] = CountFor(eventName) + 1;
            return Task.CompletedTask;
        }

        public int CountFor(string eventName)
            => _counts.TryGetValue(eventName, out var count) ? count : 0;
    }

    private sealed class NoOpCollectionRepository : ICollectionRepository
    {
        public Task<IReadOnlyList<Collection>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Collection>>([]);
        public Task<Collection?> FindByDisplayNameAsync(string displayName, CancellationToken ct = default) => Task.FromResult<Collection?>(null);
        public Task<Collection?> FindByRelationshipQidAsync(string relType, string qid, CancellationToken ct = default) => Task.FromResult<Collection?>(null);
        public Task<Guid> UpsertAsync(Collection collection, CancellationToken ct = default) => Task.FromResult(collection.Id);
        public Task InsertRelationshipsAsync(IReadOnlyList<CollectionRelationship> relationships, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Guid?> GetWorkIdByMediaAssetAsync(Guid mediaAssetId, CancellationToken ct = default) => Task.FromResult<Guid?>(null);
        public Task<IReadOnlyList<Guid>> GetWorkLineageIdsByMediaAssetAsync(Guid mediaAssetId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Guid>>([]);
        public Task<string?> FindCollectionNameByWorkIdAsync(Guid workId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task AssignWorkToCollectionAsync(Guid workId, Guid collectionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task MergeCollectionsAsync(Guid keepCollectionId, Guid mergeCollectionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetUniverseMismatchAsync(Guid workId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateWorkWikidataStatusAsync(Guid workId, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateWorkWikidataMatchStateAsync(Guid workId, string status, string? source = null, bool? locked = null, string? wikidataQid = null, string? rejectedQidsJson = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> PruneOrphanedHierarchyAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<IReadOnlyList<Collection>> GetChildCollectionsAsync(Guid parentCollectionId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Collection>>([]);
        public Task SetParentCollectionAsync(Guid collectionId, Guid? parentCollectionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Collection?> FindParentCollectionByRelationshipAsync(string qid, CancellationToken ct = default) => Task.FromResult<Collection?>(null);
        public Task<IReadOnlyList<Guid>> FindCollectionIdsByFranchiseQidAsync(string qid, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Guid>>([]);
        public Task<IReadOnlyList<CollectionRelationship>> GetRelationshipsAsync(Guid collectionId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CollectionRelationship>>([]);
        public Task<Collection?> GetByIdAsync(Guid collectionId, CancellationToken ct = default) => Task.FromResult<Collection?>(null);
        public Task<IReadOnlyList<Collection>> GetByIdsAsync(IEnumerable<Guid> collectionIds, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Collection>>([]);
        public Task<Collection?> FindByQidAsync(string qid, CancellationToken ct = default) => Task.FromResult<Collection?>(null);
        public Task<Edition?> FindEditionByQidAsync(string wikidataQid, CancellationToken ct = default) => Task.FromResult<Edition?>(null);
        public Task<Edition> CreateEditionAsync(Guid workId, string? formatLabel, string? wikidataQid, CancellationToken ct = default) => Task.FromResult(new Edition { Id = Guid.NewGuid(), WorkId = workId });
        public Task UpdateMatchLevelAsync(Guid workId, string matchLevel, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Collection>> GetByTypeAsync(string collectionType, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Collection>>([]);
        public Task<IReadOnlyList<Collection>> GetManagedCollectionsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Collection>>([]);
        public Task<Dictionary<string, int>> GetCountsByTypeAsync(CancellationToken ct = default) => Task.FromResult(new Dictionary<string, int>());
        public Task<IReadOnlyList<CollectionItem>> GetCollectionItemsAsync(Guid collectionId, int limit = 20, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CollectionItem>>([]);
        public Task<int> GetCollectionItemCountAsync(Guid collectionId, CancellationToken ct = default) => Task.FromResult(0);
        public Task<Dictionary<Guid, int>> GetCollectionItemCountsAsync(IEnumerable<Guid> collectionIds, CancellationToken ct = default) => Task.FromResult(new Dictionary<Guid, int>());
        public Task UpdateCollectionArtworkAsync(Guid collectionId, string slot, string? localPath, string? mimeType, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateCollectionEnabledAsync(Guid collectionId, bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateCollectionFeaturedAsync(Guid collectionId, bool featured, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddCollectionItemAsync(CollectionItem item, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveCollectionItemAsync(Guid itemId, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReorderCollectionItemsAsync(Guid collectionId, IReadOnlyList<Guid> itemIds, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Collection>> GetContentGroupsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Collection>>([]);
        public Task<Collection?> GetCollectionWithWorksAsync(Guid collectionId, CancellationToken ct = default) => Task.FromResult<Collection?>(null);
        public Task<IReadOnlyList<Collection>> GetCollectionsWithWorksAsync(IEnumerable<Guid> collectionIds, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Collection>>([]);
        public Task<Guid?> GetCollectionIdByWorkIdAsync(Guid workId, CancellationToken ct = default) => Task.FromResult<Guid?>(null);
        public Task<Collection?> FindByRuleHashAsync(string ruleHash, CancellationToken ct = default) => Task.FromResult<Collection?>(null);
        public Task<int> CountCollectionBackfillCandidatesAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<IReadOnlyList<CollectionBackfillCandidate>> GetCollectionBackfillCandidatesAsync(int limit, Guid? afterWorkId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CollectionBackfillCandidate>>([]);
        public Task<IReadOnlyList<Collection>> GetAllCollectionsForLocationAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Collection>>([]);
    }

    /// <summary>
    /// Stub IHttpClientFactory — never called in tests that use non-Music/TV media types.
    /// </summary>
    private sealed class StubHttpClientFactory : System.Net.Http.IHttpClientFactory
    {
        public System.Net.Http.HttpClient CreateClient(string name)
            => throw new NotSupportedException(
                $"StubHttpClientFactory.CreateClient('{name}') should not be called in unit tests.");
    }

    private sealed class RoutingHttpClientFactory : System.Net.Http.IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public RoutingHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responder)
            => _responder = responder;

        public System.Net.Http.HttpClient CreateClient(string name)
            => new(new RoutingHttpMessageHandler(_responder), disposeHandler: true);
    }

    private sealed class RoutingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public RoutingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }

    private sealed class StubEntityAssetRepository : IEntityAssetRepository
    {
        public List<EntityAsset> Assets { get; } = [];

        public Task<IReadOnlyList<EntityAsset>> GetByEntityAsync(string entityId, string? assetType = null, CancellationToken ct = default)
        {
            var assets = Assets
                .Where(asset => string.Equals(asset.EntityId, entityId, StringComparison.OrdinalIgnoreCase)
                    && (assetType is null || string.Equals(asset.AssetTypeValue, assetType, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            return Task.FromResult<IReadOnlyList<EntityAsset>>(assets);
        }

        public Task<IReadOnlyList<EntityAsset>> GetByEntitiesAsync(
            IReadOnlyCollection<string> entityIds,
            string? assetType = null,
            CancellationToken ct = default)
        {
            var entitySet = entityIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var assets = Assets
                .Where(asset => entitySet.Contains(asset.EntityId)
                    && (assetType is null || string.Equals(asset.AssetTypeValue, assetType, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            return Task.FromResult<IReadOnlyList<EntityAsset>>(assets);
        }

        public Task<EntityAsset?> FindByIdAsync(Guid assetId, CancellationToken ct = default)
            => Task.FromResult(Assets.FirstOrDefault(asset => asset.Id == assetId));

        public Task UpsertAsync(EntityAsset asset, CancellationToken ct = default)
        {
            Assets.RemoveAll(existing => existing.Id == asset.Id);
            Assets.Add(asset);
            return Task.CompletedTask;
        }

        public Task SetPreferredAsync(Guid assetId, CancellationToken ct = default)
        {
            var target = Assets.FirstOrDefault(asset => asset.Id == assetId);
            if (target is null)
            {
                return Task.CompletedTask;
            }

            foreach (var asset in Assets.Where(asset =>
                         string.Equals(asset.EntityId, target.EntityId, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(asset.AssetTypeValue, target.AssetTypeValue, StringComparison.OrdinalIgnoreCase)))
            {
                asset.IsPreferred = asset.Id == assetId;
            }

            return Task.CompletedTask;
        }

        public Task DeleteByEntityAsync(string entityId, CancellationToken ct = default)
        {
            Assets.RemoveAll(asset => string.Equals(asset.EntityId, entityId, StringComparison.OrdinalIgnoreCase));
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid assetId, CancellationToken ct = default)
        {
            Assets.RemoveAll(asset => asset.Id == assetId);
            return Task.CompletedTask;
        }

        public Task<EntityAsset?> GetPreferredAsync(string entityId, string assetType, CancellationToken ct = default)
            => Task.FromResult(Assets.FirstOrDefault(asset =>
                string.Equals(asset.EntityId, entityId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(asset.AssetTypeValue, assetType, StringComparison.OrdinalIgnoreCase)
                && asset.IsPreferred));

        public Task<IReadOnlyList<EntityAsset>> GetPreferredByEntitiesAsync(IReadOnlyCollection<string> entityIds, CancellationToken ct = default)
        {
            var entitySet = entityIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var assets = Assets
                .Where(asset => entitySet.Contains(asset.EntityId) && asset.IsPreferred)
                .ToList();
            return Task.FromResult<IReadOnlyList<EntityAsset>>(assets);
        }

        public Task<IReadOnlyList<EntityAsset>> GetPreferredArtworkAsync(CancellationToken ct = default)
        {
            var assets = Assets
                .Where(asset => asset.IsPreferred
                    && string.Equals(asset.AssetClassValue, "Artwork", StringComparison.OrdinalIgnoreCase))
                .ToList();
            return Task.FromResult<IReadOnlyList<EntityAsset>>(assets);
        }
    }

    private sealed class StubImageCacheRepository : IImageCacheRepository
    {
        private readonly Dictionary<string, string> _byHash = new(StringComparer.OrdinalIgnoreCase);

        public Task<string?> FindByHashAsync(string contentHash, CancellationToken ct = default)
            => Task.FromResult(_byHash.GetValueOrDefault(contentHash));

        public Task InsertAsync(string contentHash, string filePath, string? sourceUrl = null, CancellationToken ct = default)
        {
            _byHash[contentHash] = filePath;
            return Task.CompletedTask;
        }

        public Task<bool> IsUserOverrideAsync(string contentHash, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<string?> FindBySourceUrlAsync(string sourceUrl, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task SetUserOverrideAsync(string contentHash, bool isOverride, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SetPerceptualHashAsync(string contentHash, ulong phash, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<ulong?> GetPerceptualHashAsync(string contentHash, CancellationToken ct = default)
            => Task.FromResult<ulong?>(null);
    }

    /// <summary>
    /// Stub IWorkRepository — every method returns a no-op default. The
    /// pipeline tests don't exercise the asset → work lineage path, so
    /// returning null from <see cref="GetLineageByAssetAsync"/> short-circuits
    /// the Phase 3b routing helper without affecting test outcomes.
    /// </summary>
    private sealed class StubWorkRepository : IWorkRepository
    {
        public WorkLineage? Lineage { get; init; }

        public Task<Guid?> FindParentByKeyAsync(MediaType mediaType, string parentKey, CancellationToken ct = default)
            => Task.FromResult<Guid?>(null);

        public Task<Guid> GetOrCreateParentAsync(MediaType mediaType, string parentKey, Guid? grandparentWorkId, int? ordinal, double? ordinalSort = null, CancellationToken ct = default)
            => Task.FromResult(Guid.NewGuid());

        public Task<Guid?> FindChildByOrdinalAsync(Guid parentWorkId, int ordinal, CancellationToken ct = default)
            => Task.FromResult<Guid?>(null);

        public Task<Guid?> FindChildByOrdinalSortAsync(Guid parentWorkId, double ordinalSort, CancellationToken ct = default)
            => Task.FromResult<Guid?>(null);

        public Task<Guid?> FindChildByTitleAsync(Guid parentWorkId, string title, CancellationToken ct = default)
            => Task.FromResult<Guid?>(null);

        public Task<Guid?> FindByExternalIdentifierAsync(string scheme, string value, CancellationToken ct = default)
            => Task.FromResult<Guid?>(null);

        public Task<Guid> InsertParentAsync(MediaType mediaType, string parentKey, Guid? grandparentWorkId, int? ordinal, CancellationToken ct = default)
            => Task.FromResult(Guid.NewGuid());

        public Task<Guid> InsertChildAsync(MediaType mediaType, Guid parentWorkId, int? ordinal, CancellationToken ct = default)
            => Task.FromResult(Guid.NewGuid());

        public Task<Guid> GetOrCreateChildAsync(MediaType mediaType, Guid parentWorkId, int? ordinal, double? ordinalSort = null, CancellationToken ct = default)
            => Task.FromResult(Guid.NewGuid());

        public Task UpdateOrdinalSortAsync(Guid workId, double? ordinalSort, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<Guid> InsertStandaloneAsync(MediaType mediaType, CancellationToken ct = default)
            => Task.FromResult(Guid.NewGuid());

        public Task<Guid> InsertCatalogChildAsync(MediaType mediaType, Guid parentWorkId, int? ordinal, IReadOnlyDictionary<string, string>? externalIdentifiers, CancellationToken ct = default)
            => Task.FromResult(Guid.NewGuid());

        public Task PromoteCatalogToOwnedAsync(Guid workId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task WriteExternalIdentifiersAsync(Guid workId, IReadOnlyDictionary<string, string> identifiers, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<WorkLineage?> GetLineageByAssetAsync(Guid assetId, CancellationToken ct = default)
            => Task.FromResult(Lineage);

        public Task<ConfirmedSiblingWorkQid?> FindConfirmedSiblingQidAsync(MediaType sourceMediaType, IReadOnlyList<MediaType> candidateMediaTypes, string title, string? creator, Guid? excludeWorkId = null, CancellationToken ct = default)
            => Task.FromResult<ConfirmedSiblingWorkQid?>(null);
    }
}
