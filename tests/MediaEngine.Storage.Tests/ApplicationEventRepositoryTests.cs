using System.Text.Json;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Events;
using MediaEngine.Storage;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

public sealed class ApplicationEventRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima-events-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly ApplicationEventRepository _repository;

    public ApplicationEventRepositoryTests()
    {
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _repository = new ApplicationEventRepository(_database);
    }

    [Fact]
    public async Task AppendReplayCursorBoundsAndPrunePreserveEnvelopeProvenance()
    {
        var libraryId = Guid.NewGuid();
        var first = await _repository.AppendAsync(Event("library.item_removed", "asset-1", libraryId, DateTimeOffset.UtcNow.AddDays(-10), AccountFeatureId.Watch));
        var second = await _repository.AppendAsync(Event("library.item_added", "asset-2", libraryId, DateTimeOffset.UtcNow));

        Assert.True(first.Sequence > 0);
        Assert.Equal(first.Sequence + 1, second.Sequence);
        Assert.Equal(first.Sequence, await _repository.FindSequenceAsync(first.EventId));
        var replay = Assert.Single(await _repository.ReadAfterAsync(first.Sequence, 10));
        Assert.Equal(second, replay);
        Assert.Equal(AccountFeatureId.Watch, first.Subject.FeatureId);
        Assert.Equal(AccountFeatureId.Watch, (await _repository.ReadAfterAsync(0, 1))[0].Subject.FeatureId);
        var bounds = await _repository.GetBoundsAsync();
        Assert.Equal(first.EventId, bounds.OldestEventId);
        Assert.Equal(second.EventId, bounds.LatestEventId);

        Assert.Equal(1, await _repository.PruneAsync(DateTimeOffset.UtcNow.AddDays(-7), retainNewest: 1));
        Assert.Null(await _repository.FindSequenceAsync(first.EventId));
        Assert.Equal(second.EventId, (await _repository.GetBoundsAsync()).OldestEventId);
    }

    [Fact]
    public void TransactionalOutboxRejectsUnsupportedFeatureProvenance()
    {
        var draft = Draft("library.item_added", "asset-1") with
        {
            Subject = new("media-asset", "asset-1", Guid.NewGuid(), FeatureId: AccountFeatureId.View),
        };
        using var connection = _database.CreateConnection();
        using var transaction = connection.BeginTransaction();

        Assert.Throws<ArgumentException>(() => new ApplicationEventOutboxWriter().Append(connection, transaction, draft));
    }

    [Fact]
    public async Task TransactionalOutboxRollsBackAtomicallyAndKeepsStableServerIdentity()
    {
        var writer = new ApplicationEventOutboxWriter();
        StoredApplicationEvent rolledBack;
        using (var connection = _database.CreateConnection())
        {
            using (var transaction = connection.BeginTransaction())
            {
                rolledBack = writer.Append(connection, transaction, Draft("ingestion.started", "batch-1"));
                transaction.Rollback();
            }
        }
        Assert.Null(await _repository.FindSequenceAsync(rolledBack.EventId));

        var first = AppendCommitted(writer, Draft("ingestion.started", "batch-2"));
        var second = AppendCommitted(writer, Draft("ingestion.completed", "batch-2"));
        Assert.NotEqual(first.EventId, second.EventId);
        Assert.Equal(first.ServerId, second.ServerId);
        Assert.True(Guid.TryParse(first.ServerId, out _));
    }

    private static StoredApplicationEvent Event(string type, string subjectId, Guid libraryId, DateTimeOffset occurredAt,
        AccountFeatureId? featureId = null)
    {
        using var payload = JsonDocument.Parse("{\"action\":\"changed\"}");
        return new(0, Guid.NewGuid(), type, 1, occurredAt, "test-server",
            new("media-asset", subjectId, libraryId, FeatureId: featureId), payload.RootElement.GetRawText());
    }

    private StoredApplicationEvent AppendCommitted(ApplicationEventOutboxWriter writer, ApplicationEventDraft draft)
    {
        using var connection = _database.CreateConnection();
        using var transaction = connection.BeginTransaction();
        var stored = writer.Append(connection, transaction, draft);
        transaction.Commit();
        return stored;
    }

    private static ApplicationEventDraft Draft(string type, string subjectId)
    {
        using var payload = JsonDocument.Parse("{\"status\":\"ok\"}");
        return new(type, 1, DateTimeOffset.UtcNow, new("ingestion-batch", subjectId), payload.RootElement.Clone());
    }

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }

        if (File.Exists(_path + "-wal"))
        {
            File.Delete(_path + "-wal");
        }

        if (File.Exists(_path + "-shm"))
        {
            File.Delete(_path + "-shm");
        }
    }
}
