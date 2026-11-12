using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using MediaEngine.Providers.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

public sealed class StageOutcomeFactoryTests
{
    [Fact]
    public async Task ProvisionalReviewsStayHiddenUntilPromoted()
    {
        var reviewRepo = new RecordingReviewQueueRepository();
        var activityRepo = new RecordingSystemActivityRepository();
        var eventPublisher = new RecordingEventPublisher();
        var canonicalRepo = new RecordingCanonicalValueRepository();
        var factory = new StageOutcomeFactory(
            reviewRepo,
            activityRepo,
            eventPublisher,
            canonicalRepo,
            NullLogger<StageOutcomeFactory>.Instance);

        var entityId = Guid.NewGuid();
        canonicalRepo.Values.Add(new CanonicalValue
        {
            EntityId = entityId,
            Key = MetadataFieldConstants.Title,
            Value = "Fixture Title",
        });

        var provisionalId = await factory.CreateProvisionalAsync(
            entityId,
            ReviewTrigger.RetailMatchAmbiguous,
            0.62,
            "Automation can still resolve this match");

        Assert.NotNull(provisionalId);
        Assert.Empty(await reviewRepo.GetPendingAsync());
        Assert.Empty(activityRepo.Entries);
        Assert.Empty(eventPublisher.Events);

        var promoted = await factory.PromoteProvisionalAsync(entityId);

        var promotedEntry = Assert.Single(promoted);
        Assert.Equal(provisionalId.Value, promotedEntry.Id);
        Assert.NotNull(promotedEntry.ReviewReadyAt);
        Assert.NotNull(promotedEntry.AutomationCompletedAt);

        var activity = Assert.Single(activityRepo.Entries);
        Assert.Equal(SystemActionType.ReviewItemCreated, activity.ActionType);
        Assert.Equal(entityId, activity.EntityId);
        Assert.Equal("Fixture Title", activity.CollectionName);

        var published = Assert.Single(eventPublisher.Events);
        Assert.Equal(SignalREvents.ReviewItemCreated, published.EventName);

        Assert.Empty(await factory.PromoteProvisionalAsync(entityId));
        Assert.Single(activityRepo.Entries);
        Assert.Single(eventPublisher.Events);
    }

    [Fact]
    public async Task RetailAmbiguous_KeepsTheTopThreeCandidates_BestFirst_WithoutRejectedOrRepeatedOnes()
    {
        var (factory, reviewRepo) = NewFactory();
        var entityId = Guid.NewGuid();
        var best = Candidate("Dune", "2021", 0.78, "tmdb", "438631", "Ambiguous");
        var candidates = new[]
        {
            Candidate("Dune", "1984", 0.71, "tmdb", "841", "Ambiguous"),
            best,
            Candidate("Dune (repeat)", "2021", 0.77, "tmdb", "438631", "Ambiguous"),
            Candidate("Dune: Part Two", "2024", 0.74, "tmdb", "693134", "Ambiguous"),
            Candidate("Dune Drifter", "2020", 0.69, "tmdb", "99", "Ambiguous"),
            Candidate("Duna", "1999", 0.90, "tmdb", "7", "Rejected"),
        };

        await factory.CreateRetailAmbiguousAsync(entityId, "Movies", 0.78, candidates: candidates, best: best);

        var stored = ReadCandidates(Assert.Single(await reviewRepo.GetByEntityAsync(entityId)).CandidatesJson);
        Assert.Equal(["438631", "693134", "841"], stored.Select(c => c.ProviderItemId));
        Assert.Equal(["Dune", "Dune: Part Two", "Dune"], stored.Select(c => c.Title));
        Assert.Equal(0.78, stored[0].Score);
        Assert.Equal("tmdb", stored[0].ProviderName);
    }

    [Fact]
    public async Task RetailAmbiguous_AlwaysKeepsTheSelectedCandidate_EvenWhenOthersScoreHigher()
    {
        var (factory, reviewRepo) = NewFactory();
        var entityId = Guid.NewGuid();
        var best = Candidate("Selected", "2001", 0.60, "tmdb", "5", "Ambiguous");
        var candidates = new[]
        {
            Candidate("A", "2001", 0.80, "tmdb", "1", "Ambiguous"),
            Candidate("B", "2002", 0.79, "tmdb", "2", "Ambiguous"),
            Candidate("C", "2003", 0.78, "tmdb", "3", "Ambiguous"),
            best,
        };

        await factory.CreateRetailAmbiguousAsync(entityId, "Movies", 0.60, candidates: candidates, best: best);

        var stored = ReadCandidates(Assert.Single(await reviewRepo.GetByEntityAsync(entityId)).CandidatesJson);
        Assert.Equal(3, stored.Count);
        Assert.Contains(stored, c => c.ProviderItemId == "5");
    }

    [Fact]
    public async Task RetailAmbiguous_WhenAPendingRowExists_RefreshesItsCandidatesInsteadOfAddingAnother()
    {
        var (factory, reviewRepo) = NewFactory();
        var entityId = Guid.NewGuid();
        var first = Candidate("Old Guess", "1999", 0.70, "tmdb", "1", "Ambiguous");
        await factory.CreateRetailAmbiguousAsync(entityId, "Movies", 0.70, candidates: [first], best: first);
        var newBest = Candidate("New Guess", "2010", 0.75, "tmdb", "2", "Ambiguous");
        var second = Candidate("Other Guess", "2011", 0.72, "tmdb", "3", "Ambiguous");

        var duplicate = await factory.CreateRetailAmbiguousAsync(entityId, "Movies", 0.75, candidates: [newBest, second], best: newBest);

        Assert.Null(duplicate);
        var row = Assert.Single(await reviewRepo.GetByEntityAsync(entityId));
        Assert.Equal(["2", "3"], ReadCandidates(row.CandidatesJson).Select(c => c.ProviderItemId));
    }

    [Fact]
    public async Task RetailAmbiguous_WithoutCandidates_LeavesTheStoredCandidatesEmpty()
    {
        var (factory, reviewRepo) = NewFactory();
        var entityId = Guid.NewGuid();

        await factory.CreateRetailAmbiguousAsync(entityId, "Movies", 0.70);

        Assert.Null(Assert.Single(await reviewRepo.GetByEntityAsync(entityId)).CandidatesJson);
    }

    private static (StageOutcomeFactory Factory, RecordingReviewQueueRepository Reviews) NewFactory()
    {
        var reviews = new RecordingReviewQueueRepository();
        var factory = new StageOutcomeFactory(
            reviews,
            new RecordingSystemActivityRepository(),
            new RecordingEventPublisher(),
            new RecordingCanonicalValueRepository(),
            NullLogger<StageOutcomeFactory>.Instance);
        return (factory, reviews);
    }

    private static RetailMatchCandidate Candidate(string title, string year, double score, string provider, string itemId, string outcome) => new()
    {
        ProviderId = Guid.NewGuid(),
        ProviderName = provider,
        ProviderItemId = itemId,
        Title = title,
        Year = year,
        Creator = "Someone",
        ScoreTotal = score,
        Outcome = outcome,
        ImageUrl = $"https://image.tmdb.org/t/p/w500/{itemId}.jpg",
        BridgeIdsJson = $$"""{"tmdb_id":"{{itemId}}"}""",
    };

    private static List<RetailChoiceCandidate> ReadCandidates(string? json) =>
        System.Text.Json.JsonSerializer.Deserialize<List<RetailChoiceCandidate>>(json!)!;

    private sealed class RecordingReviewQueueRepository : IReviewQueueRepository
    {
        private readonly List<ReviewQueueEntry> _entries = [];

        public Task<Guid> InsertAsync(ReviewQueueEntry entry, CancellationToken ct = default)
        {
            _entries.Add(entry);
            return Task.FromResult(entry.Id);
        }

        public Task<IReadOnlyList<ReviewQueueEntry>> GetPendingAsync(int limit = 50, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ReviewQueueEntry>>(
                _entries
                    .Where(e => e.Status == ReviewStatus.Pending && e.ReviewReadyAt is not null)
                    .Take(limit)
                    .ToList());

        public Task<ReviewQueueEntry?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(_entries.FirstOrDefault(e => e.Id == id));

        public Task<IReadOnlyList<ReviewQueueEntry>> GetByEntityAsync(Guid entityId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ReviewQueueEntry>>(
                _entries.Where(e => e.EntityId == entityId).ToList());

        public Task<IReadOnlyList<ReviewQueueEntry>> GetPendingByEntityAsync(Guid entityId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ReviewQueueEntry>>(
                _entries
                    .Where(e => e.EntityId == entityId
                             && e.Status == ReviewStatus.Pending
                             && e.ReviewReadyAt is not null)
                    .ToList());

        public Task UpdateStatusAsync(Guid id, string status, string? resolvedBy = null, CancellationToken ct = default)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == id);
            if (entry is not null)
            {
                entry.Status = status;
                entry.ResolvedBy = resolvedBy;
                entry.ResolvedAt = DateTimeOffset.UtcNow;
            }

            return Task.CompletedTask;
        }

        public Task<int> MarkPendingReadyByEntityAsync(Guid entityId, CancellationToken ct = default)
        {
            var now = DateTimeOffset.UtcNow;
            var rows = _entries
                .Where(e => e.EntityId == entityId
                         && e.Status == ReviewStatus.Pending
                         && e.ReviewReadyAt is null)
                .ToList();

            foreach (var row in rows)
            {
                row.ReviewReadyAt = now;
                row.AutomationCompletedAt = now;
            }

            return Task.FromResult(rows.Count);
        }

        public Task<IReadOnlyList<ReviewQueueEntry>> PromotePendingReadyByEntityAsync(
            Guid entityId,
            CancellationToken ct = default)
        {
            var now = DateTimeOffset.UtcNow;
            var rows = _entries
                .Where(e => e.EntityId == entityId
                         && e.Status == ReviewStatus.Pending
                         && e.ReviewReadyAt is null)
                .ToList();

            foreach (var row in rows)
            {
                row.ReviewReadyAt = now;
                row.AutomationCompletedAt = now;
            }

            return Task.FromResult<IReadOnlyList<ReviewQueueEntry>>(rows);
        }

        public Task<int> GetPendingCountAsync(CancellationToken ct = default)
            => Task.FromResult(_entries.Count(e => e.Status == ReviewStatus.Pending && e.ReviewReadyAt is not null));

        public Task<int> DismissAllByEntityAsync(Guid entityId, CancellationToken ct = default)
            => ResolveAllByEntityAsync(entityId, "test:dismiss", ct);

        public Task<bool> ReopenKeptAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<int> ResolveAllByEntityAsync(
            Guid entityId,
            string resolvedBy = "system:auto-organize",
            CancellationToken ct = default)
        {
            var rows = _entries
                .Where(e => e.EntityId == entityId && e.Status == ReviewStatus.Pending)
                .ToList();

            foreach (var row in rows)
            {
                row.Status = ReviewStatus.Resolved;
                row.ResolvedBy = resolvedBy;
                row.ResolvedAt = DateTimeOffset.UtcNow;
            }

            return Task.FromResult(rows.Count);
        }

        public Task<int> ResolvePendingByEntityAndTriggersAsync(
            Guid entityId,
            IReadOnlyCollection<string> triggers,
            string resolvedBy,
            CancellationToken ct = default)
        {
            var triggerSet = new HashSet<string>(triggers, StringComparer.OrdinalIgnoreCase);
            var rows = _entries
                .Where(e => e.EntityId == entityId
                         && e.Status == ReviewStatus.Pending
                         && triggerSet.Contains(e.Trigger))
                .ToList();

            foreach (var row in rows)
            {
                row.Status = ReviewStatus.Resolved;
                row.ResolvedBy = resolvedBy;
                row.ResolvedAt = DateTimeOffset.UtcNow;
            }

            return Task.FromResult(rows.Count);
        }

        public Task UpdatePendingCandidatesAsync(Guid id, string? candidatesJson, CancellationToken ct = default)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == id && e.Status == ReviewStatus.Pending);
            if (entry is not null)
            {
                entry.CandidatesJson = candidatesJson;
            }

            return Task.CompletedTask;
        }

        public Task<int> PurgeOrphanedAsync(CancellationToken ct = default)
            => Task.FromResult(0);
    }

    private sealed class RecordingSystemActivityRepository : ISystemActivityRepository
    {
        public List<SystemActivityEntry> Entries { get; } = [];

        public Task LogAsync(SystemActivityEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SystemActivityEntry>> GetRecentAsync(int limit = 50, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SystemActivityEntry>>(Entries.Take(limit).ToList());

        public Task<int> PruneOlderThanAsync(int retentionDays, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<long> CountAsync(CancellationToken ct = default)
            => Task.FromResult((long)Entries.Count);

        public Task<IReadOnlyList<SystemActivityEntry>> GetRecentByTypesAsync(
            IReadOnlyList<string> actionTypes,
            int limit = 50,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SystemActivityEntry>>(
                Entries.Where(e => actionTypes.Contains(e.ActionType)).Take(limit).ToList());

        public Task<IReadOnlyList<SystemActivityEntry>> GetRecentByProfileAsync(
            Guid profileId,
            int limit = 50,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SystemActivityEntry>>(
                Entries.Where(e => e.ProfileId == profileId).Take(limit).ToList());

        public Task<(IReadOnlyList<SystemActivityEntry> Items, int Total)> QueryAsync(IReadOnlyList<string> actionTypes, string? search, int offset, int limit, string? mediaType, CancellationToken ct = default)
            => Task.FromResult<(IReadOnlyList<SystemActivityEntry>, int)>(([], 0));
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<(string EventName, object Payload)> Events { get; } = [];

        public Task PublishAsync<TPayload>(string eventName, TPayload payload, CancellationToken ct = default)
            where TPayload : notnull
        {
            Events.Add((eventName, payload));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCanonicalValueRepository : ICanonicalValueRepository
    {
        public List<CanonicalValue> Values { get; } = [];

        public Task UpsertBatchAsync(IReadOnlyList<CanonicalValue> values, CancellationToken ct = default)
        {
            foreach (var value in values)
            {
                Values.RemoveAll(v => v.EntityId == value.EntityId
                                    && string.Equals(v.Key, value.Key, StringComparison.OrdinalIgnoreCase));
                Values.Add(value);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CanonicalValue>> GetByEntityAsync(Guid entityId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CanonicalValue>>(
                Values.Where(v => v.EntityId == entityId).ToList());

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalValue>>> GetByEntitiesAsync(
            IReadOnlyList<Guid> entityIds,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalValue>>>(
                entityIds.ToDictionary(
                    id => id,
                    id => (IReadOnlyList<CanonicalValue>)Values.Where(v => v.EntityId == id).ToList()));

        public Task<IReadOnlyList<CanonicalValue>> GetConflictedAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CanonicalValue>>([]);

        public Task DeleteByEntityAsync(Guid entityId, CancellationToken ct = default)
        {
            Values.RemoveAll(v => v.EntityId == entityId);
            return Task.CompletedTask;
        }

        public Task DeleteByKeyAsync(Guid entityId, string key, CancellationToken ct = default)
        {
            Values.RemoveAll(v => v.EntityId == entityId
                                && string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Guid>> FindByValueAsync(string key, string value, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>(
                Values.Where(v => string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase)
                               && string.Equals(v.Value, value, StringComparison.OrdinalIgnoreCase))
                    .Select(v => v.EntityId)
                    .Distinct()
                    .ToList());

        public Task<IReadOnlyList<CanonicalValue>> FindByKeyAndPrefixAsync(
            string key,
            string prefix,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CanonicalValue>>(
                Values.Where(v => string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase)
                               && v.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .ToList());

        public Task<IReadOnlyList<Guid>> GetEntitiesNeedingEnrichmentAsync(
            string hasField,
            string missingField,
            int limit,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>([]);
    }
}
