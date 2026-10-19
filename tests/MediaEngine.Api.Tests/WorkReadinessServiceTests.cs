using Dapper;
using MediaEngine.Api.Services;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Api.Tests;

public sealed class WorkReadinessServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly RecordingPublisher _publisher = new();
    private readonly ListLogger _logger = new();
    private readonly StubBypassLibraries _bypass = new();
    private readonly WorkReadinessService _service;

    public WorkReadinessServiceTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_ready_service_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _service = new WorkReadinessService(new WorkReadinessRepository(_db), _bypass, _publisher, TimeProvider.System, _logger);
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(path); } catch (IOException) { /* temp database cleanup is best effort */ }
        }
    }

    [Fact]
    public async Task AWorkThatBecomesReady_IsAnnouncedOnceWithItsRealWorkId_AndNotAgainOnTheNextPoll()
    {
        var (workId, assetId) = SeedBook("Dune");
        SetJob(assetId, "Queued", DateTimeOffset.UtcNow.AddSeconds(-10));
        SetCanonical(assetId, "cover_state", "present");
        await _service.BackfillAsync(CancellationToken.None);
        Assert.Empty(_publisher.Published);

        SetJob(assetId, "RetailMatched", DateTimeOffset.UtcNow);
        await _service.PollAsync(CancellationToken.None);

        var published = Assert.Single(_publisher.Published);
        Assert.Equal(SignalREvents.MediaAdded, published.EventName);
        var added = Assert.IsType<MediaAddedEvent>(published.Payload);
        Assert.Equal(workId, added.WorkId);
        Assert.NotEqual(assetId, added.WorkId);
        Assert.Equal(assetId, added.AssetId);
        Assert.Equal("Books", added.MediaType);
        Assert.Equal("Dune", added.Title);
        Assert.NotNull(added.CollectionId);

        await _service.PollAsync(CancellationToken.None);
        await _service.SweepAsync(CancellationToken.None);

        Assert.Single(_publisher.Published);
    }

    [Fact]
    public async Task WorksThatAreAlreadyReadyAtStartup_AreMarkedWithoutBeingAnnounced()
    {
        var (workId, assetId) = SeedBook("Existing");
        SetJob(assetId, "Ready", DateTimeOffset.UtcNow);
        SetCanonical(assetId, "cover_state", "present");

        await _service.BackfillAsync(CancellationToken.None);

        Assert.Empty(_publisher.Published);
        Assert.NotNull(ReadyAt(workId));
    }

    [Fact]
    public async Task AWorkThatLeavesReady_IsLoggedAndNothingNewIsPublished()
    {
        var (workId, assetId) = SeedBook("Dune");
        SetJob(assetId, "Queued", DateTimeOffset.UtcNow.AddSeconds(-10));
        SetCanonical(assetId, "cover_state", "present");
        await _service.BackfillAsync(CancellationToken.None);
        SetJob(assetId, "RetailMatched", DateTimeOffset.UtcNow);
        await _service.PollAsync(CancellationToken.None);
        Assert.Single(_publisher.Published);

        SetJob(assetId, "RetailMatchedNeedsReview", DateTimeOffset.UtcNow.AddSeconds(2));
        await _service.PollAsync(CancellationToken.None);

        Assert.Single(_publisher.Published);
        Assert.Null(ReadyAt(workId));
        Assert.Contains(_logger.Messages, message => message.Contains($"Work {workId} is no longer ready (Books)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AWorkWaitingForArtwork_LogsHowLongItWaitedAfterBeingMatched()
    {
        var (workId, assetId) = SeedBook("Dune");
        SetJob(assetId, "Queued", DateTimeOffset.UtcNow.AddSeconds(-10));
        await _service.BackfillAsync(CancellationToken.None);

        SetJob(assetId, "RetailMatched", DateTimeOffset.UtcNow);
        await _service.PollAsync(CancellationToken.None);
        Assert.Empty(_publisher.Published);

        SetCanonical(assetId, "cover_state", "present", DateTimeOffset.UtcNow.AddSeconds(1));
        await _service.PollAsync(CancellationToken.None);

        Assert.Single(_publisher.Published);
        Assert.Contains(
            _logger.Messages,
            message => message.StartsWith($"Work {workId} is ready (Books) ", StringComparison.Ordinal)
                && message.EndsWith("s after it was matched", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALocalOnlyLibraryWork_BecomesReadyWithoutAJob_AndStopsBeingReadyWhenThePolicyChanges()
    {
        const string library = "33333333-cccc-4ccc-8ccc-333333333333";
        _bypass.LibraryIds = [library];
        await _service.BackfillAsync(CancellationToken.None);

        var (workId, assetId) = SeedBook("Local Book");
        using (var conn = _db.CreateConnection())
        {
            conn.Execute("UPDATE media_assets SET library_id = @library WHERE id = @assetId", new { library, assetId });
        }

        SetCanonical(assetId, "cover_state", "present", DateTimeOffset.UtcNow.AddSeconds(1));
        await _service.PollAsync(CancellationToken.None);

        Assert.Equal(workId, Assert.IsType<MediaAddedEvent>(Assert.Single(_publisher.Published).Payload).WorkId);

        // The library goes back to normal identity: the next full pass takes the mark away.
        _bypass.LibraryIds = [];
        await _service.SweepAsync(CancellationToken.None);

        Assert.Null(ReadyAt(workId));
    }

    [Fact]
    public async Task AWorkThatIsReadyThenInReviewThenReadyAgain_IsAnnouncedOnlyOnce()
    {
        var (workId, assetId) = SeedBook("Dune");
        SetJob(assetId, "Queued", DateTimeOffset.UtcNow.AddSeconds(-10));
        SetCanonical(assetId, "cover_state", "present");
        await _service.BackfillAsync(CancellationToken.None);

        SetJob(assetId, "RetailMatched", DateTimeOffset.UtcNow);
        await _service.PollAsync(CancellationToken.None);
        Assert.Single(_publisher.Published);

        SetJob(assetId, "RetailMatchedNeedsReview", DateTimeOffset.UtcNow.AddSeconds(1));
        await _service.PollAsync(CancellationToken.None);
        Assert.Null(ReadyAt(workId));

        SetJob(assetId, "RetailMatched", DateTimeOffset.UtcNow.AddSeconds(2));
        await _service.PollAsync(CancellationToken.None);

        Assert.NotNull(ReadyAt(workId));
        Assert.Single(_publisher.Published);
    }

    [Fact]
    public async Task AnAnnouncementThatFailsOnce_IsRetriedOnTheNextPass_AndPublishedExactlyOnce()
    {
        var (workId, assetId) = SeedBook("Dune");
        SetJob(assetId, "Queued", DateTimeOffset.UtcNow.AddSeconds(-10));
        SetCanonical(assetId, "cover_state", "present");
        await _service.BackfillAsync(CancellationToken.None);
        _publisher.FailTimes = 1;

        SetJob(assetId, "RetailMatched", DateTimeOffset.UtcNow);
        await _service.PollAsync(CancellationToken.None);

        Assert.Empty(_publisher.Published);
        Assert.NotNull(ReadyAt(workId));
        Assert.Contains(_logger.Messages, message => message.Contains("could not be published", StringComparison.Ordinal));

        await _service.PollAsync(CancellationToken.None);
        await _service.PollAsync(CancellationToken.None);

        var added = Assert.IsType<MediaAddedEvent>(Assert.Single(_publisher.Published).Payload);
        Assert.Equal(workId, added.WorkId);
    }

    [Fact]
    public async Task AWorkThatIsNoLongerWaitingForArtwork_IsForgottenOnTheNextFullSweep()
    {
        var (workId, assetId) = SeedBook("Dune");
        SetJob(assetId, "Queued", DateTimeOffset.UtcNow.AddSeconds(-10));
        await _service.BackfillAsync(CancellationToken.None);
        SetJob(assetId, "RetailMatched", DateTimeOffset.UtcNow);
        await _service.PollAsync(CancellationToken.None);

        // It goes into review, so it is no longer waiting for artwork; the sweep forgets when it was matched.
        SetJob(assetId, "RetailMatchedNeedsReview", DateTimeOffset.UtcNow.AddSeconds(1));
        await _service.SweepAsync(CancellationToken.None);
        SetJob(assetId, "RetailMatched", DateTimeOffset.UtcNow.AddSeconds(2));
        SetCanonical(assetId, "cover_state", "present", DateTimeOffset.UtcNow.AddSeconds(2));
        await _service.SweepAsync(CancellationToken.None);

        Assert.Contains(_logger.Messages, message => message == $"Work {workId} is ready (Books)");
    }

    private (Guid WorkId, Guid AssetId) SeedBook(string title)
    {
        var collectionId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute(
            """
            INSERT INTO collections (id, created_at) VALUES (@collectionId, datetime('now'));
            INSERT INTO works (id, collection_id, media_type) VALUES (@workId, @collectionId, 'Books');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
            VALUES (@assetId, @editionId, @hash, @path, 'Normal');
            """,
            new { collectionId, workId, editionId, assetId, hash = $"hash_{assetId:N}", path = $"/library/{assetId:N}.epub" });
        SetCanonical(assetId, "title", title);
        return (workId, assetId);
    }

    private void SetJob(Guid assetId, string state, DateTimeOffset updatedAt)
    {
        var at = updatedAt.ToString("O");
        using var conn = _db.CreateConnection();
        var updated = conn.Execute(
            "UPDATE identity_jobs SET state = @state, updated_at = @at WHERE entity_id = @assetId",
            new { state, at, assetId });
        if (updated == 0)
        {
            conn.Execute(
                """
                INSERT INTO identity_jobs (id, entity_id, entity_type, media_type, state, created_at, updated_at)
                VALUES (@id, @assetId, 'MediaAsset', 'Books', @state, @at, @at)
                """,
                new { id = Guid.NewGuid(), assetId, state, at });
        }
    }

    private void SetCanonical(Guid entityId, string key, string value, DateTimeOffset? at = null)
    {
        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT OR REPLACE INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@entityId, @key, @value, @at)",
            new { entityId, key, value, at = (at ?? DateTimeOffset.UtcNow).ToString("O") });
    }

    private string? ReadyAt(Guid workId)
    {
        using var conn = _db.CreateConnection();
        return conn.ExecuteScalar<string?>("SELECT ready_at FROM works WHERE id = @workId", new { workId });
    }

    private sealed class StubBypassLibraries : IIdentityBypassLibraries
    {
        public IReadOnlyCollection<string> LibraryIds { get; set; } = [];

        public IReadOnlyCollection<string> GetLibraryIds() => LibraryIds;
    }

    private sealed class RecordingPublisher : IEventPublisher
    {
        public List<(string EventName, object Payload)> Published { get; } = [];

        public int FailTimes { get; set; }

        public Task PublishAsync<TPayload>(string eventName, TPayload payload, CancellationToken ct = default)
            where TPayload : notnull
        {
            if (FailTimes > 0)
            {
                FailTimes--;
                throw new InvalidOperationException("The hub is not reachable.");
            }

            Published.Add((eventName, payload));
            return Task.CompletedTask;
        }
    }

    private sealed class ListLogger : ILogger<WorkReadinessService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
