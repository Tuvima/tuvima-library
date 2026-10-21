using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

public sealed class ApplicationWebhookRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"webhook-storage-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    public ApplicationWebhookRepositoryTests()
    {
        DapperConfiguration.Configure();
        _database = new(_path);
        _database.InitializeSchema();
        _database.RunStartupChecks();
    }

    [Fact]
    public async Task QueueAndCursorCommitTogether_AndRetryKeepsOneStableDelivery()
    {
        var repository = new ApplicationWebhookRepository(_database);
        var endpoint = NewEndpoint();
        await repository.SaveAsync(endpoint, true);
        var delivery = NewDelivery(endpoint);
        Assert.True(await repository.QueueAsync(endpoint, 1, [delivery]));
        Assert.False(await repository.QueueAsync(endpoint, 1, [NewDelivery(endpoint)]));
        Assert.Equal(1, (await repository.FindAsync(endpoint.Id))!.LastEventSequence);
        var due = Assert.Single(await repository.DueAsync(DateTimeOffset.UtcNow));
        Assert.Equal(delivery.Id, due.Id);
        due.AttemptCount = 1;
        due.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(1);
        due.UpdatedAt = DateTimeOffset.UtcNow;
        await repository.FinishAttemptAsync(due, "Receiver unavailable", false, due.UpdatedAt);
        Assert.Empty(await repository.DueAsync(DateTimeOffset.UtcNow));
        Assert.Equal(delivery.Id, Assert.Single(await repository.DueAsync(DateTimeOffset.UtcNow.AddMinutes(2))).Id);
        due.Status = "delivered";
        await repository.FinishAttemptAsync(due, "HTTP 204", true, due.UpdatedAt);
        Assert.Empty(await repository.DueAsync(DateTimeOffset.UtcNow.AddDays(1)));
        Assert.NotNull((await repository.FindAsync(endpoint.Id))!.LastSuccessAt);
    }

    [Fact]
    public async Task EndpointVersionPreventsStaleCursorOrConfigurationWrites_AndDeleteCascadesQueue()
    {
        var repository = new ApplicationWebhookRepository(_database);
        var endpoint = NewEndpoint();
        await repository.SaveAsync(endpoint, true);
        await repository.QueueAsync(endpoint, 1, [NewDelivery(endpoint)]);
        var updated = (await repository.FindAsync(endpoint.Id))!;
        updated.Url = "https://replacement.example.invalid/events";
        await repository.SaveAsync(updated, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveAsync(endpoint, false));
        Assert.False(await repository.QueueAsync(endpoint, 2, [NewDelivery(endpoint)]));
        await repository.DeleteAsync(Guid.NewGuid(), endpoint.Id);
        Assert.NotNull(await repository.FindAsync(endpoint.Id));
        await repository.DeleteAsync(endpoint.ApplicationId, endpoint.Id);
        Assert.Empty(await repository.DueAsync(DateTimeOffset.UtcNow));
        Assert.Null(await repository.FindAsync(endpoint.Id));
    }

    [Fact]
    public async Task LastAttempt_IsEmptyBeforeAnyAttempt_ThenReportsTheNewestAttemptAndItsStatus()
    {
        var repository = new ApplicationWebhookRepository(_database);
        var endpoint = NewEndpoint();
        await repository.SaveAsync(endpoint, true);
        var delivery = NewDelivery(endpoint);
        await repository.QueueAsync(endpoint, 1, [delivery]);

        Assert.Null(await repository.LastAttemptAsync());

        var attemptedAt = new DateTimeOffset(2026, 10, 19, 8, 30, 0, TimeSpan.Zero);
        var due = Assert.Single(await repository.DueAsync(DateTimeOffset.UtcNow));
        due.AttemptCount = 1;
        due.Status = "delivered";
        due.UpdatedAt = attemptedAt;
        await repository.FinishAttemptAsync(due, "HTTP 204", true, attemptedAt);

        var last = await repository.LastAttemptAsync();

        Assert.NotNull(last);
        Assert.Equal(attemptedAt, last.At);
        Assert.Equal("delivered", last.Status);
    }

    [Fact]
    public async Task Attempts_AreWrittenWithTheDeliveryUpdate_AndListedNewestFirstUpToTheLimit()
    {
        var repository = new ApplicationWebhookRepository(_database);
        var endpoint = NewEndpoint();
        await repository.SaveAsync(endpoint, true);
        var delivery = NewDelivery(endpoint);
        await repository.QueueAsync(endpoint, 1, [delivery]);
        var start = new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.Zero);
        for (var number = 1; number <= 8; number++)
        {
            delivery.AttemptCount = number;
            delivery.UpdatedAt = start.AddMinutes(number);
            await repository.FinishAttemptAsync(delivery, "HTTP 503", false, delivery.UpdatedAt,
                NewAttempt(endpoint, delivery, number, delivery.UpdatedAt, "HTTP 503"));
        }

        var last = await repository.ListAttemptsAsync(endpoint.Id, 6);

        Assert.Equal([8, 7, 6, 5, 4, 3], last.Select(attempt => attempt.AttemptNumber));
        var newest = last[0];
        Assert.Equal(delivery.Id, newest.DeliveryId);
        Assert.Equal(start.AddMinutes(8), newest.AttemptedAt);
        Assert.False(newest.Delivered);
        Assert.Equal("HTTP 503", newest.Result);
        Assert.Equal(503, newest.HttpStatus);
        Assert.False(newest.IsTest);
        Assert.Single(await repository.ListAttemptsAsync(endpoint.Id, 1));
        Assert.Empty(await repository.ListAttemptsAsync(Guid.NewGuid(), 6));
    }

    [Fact]
    public async Task ConsecutiveFailures_CountBackToTheLastDelivery_AndIgnoreTestSends()
    {
        var repository = new ApplicationWebhookRepository(_database);
        var broken = NewEndpoint();
        var recovered = NewEndpoint();
        await repository.SaveAsync(broken, true);
        await repository.SaveAsync(recovered, true);
        var start = new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.Zero);

        // "broken": one delivery, then four failures, with a successful test send in the middle that must not reset the run.
        var delivery = NewDelivery(broken);
        await repository.QueueAsync(broken, 1, [delivery]);
        for (var number = 1; number <= 5; number++)
        {
            delivery.AttemptCount = number;
            delivery.UpdatedAt = start.AddMinutes(number);
            var attempt = NewAttempt(broken, delivery, number, delivery.UpdatedAt, number == 1 ? "HTTP 204" : "HTTP 503");
            attempt.Delivered = number == 1;
            await repository.FinishAttemptAsync(delivery, attempt.Result, attempt.Delivered, delivery.UpdatedAt, attempt);
        }

        var test = NewAttempt(broken, NewFinishedDelivery(broken), 1, start.AddMinutes(3).AddSeconds(30), "HTTP 204");
        test.Delivered = true;
        test.IsTest = true;
        await repository.RecordTestAsync(NewFinishedDelivery(broken), test, "HTTP 204");

        // "recovered": failures, then a delivery.
        var second = NewDelivery(recovered);
        await repository.QueueAsync(recovered, 1, [second]);
        foreach (var (number, ok) in new[] { (1, false), (2, false), (3, true) })
        {
            second.AttemptCount = number;
            second.UpdatedAt = start.AddMinutes(number);
            var attempt = NewAttempt(recovered, second, number, second.UpdatedAt, ok ? "HTTP 204" : "HTTP 503");
            attempt.Delivered = ok;
            await repository.FinishAttemptAsync(second, attempt.Result, ok, second.UpdatedAt, attempt);
        }

        var counts = await repository.ConsecutiveFailuresAsync();

        Assert.Equal(4, counts[broken.Id]);
        Assert.False(counts.ContainsKey(recovered.Id));
    }

    [Fact]
    public async Task RecordTest_StoresAFinishedDeliveryAndATestAttempt_WithoutQueueingAnythingToRetry()
    {
        var repository = new ApplicationWebhookRepository(_database);
        var endpoint = NewEndpoint();
        await repository.SaveAsync(endpoint, true);
        var delivery = NewDelivery(endpoint);
        delivery.EventSequence = 0;
        delivery.Status = "delivered";
        delivery.AttemptCount = 1;
        var at = new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);
        delivery.UpdatedAt = at;
        var attempt = NewAttempt(endpoint, delivery, 1, at, "HTTP 204");
        attempt.Delivered = true;
        attempt.IsTest = true;
        attempt.EventType = "webhook.test";

        await repository.RecordTestAsync(delivery, attempt, "HTTP 204");

        Assert.Empty(await repository.DueAsync(at.AddDays(1)));
        var stored = Assert.Single(await repository.ListAttemptsAsync(endpoint.Id, 6));
        Assert.True(stored.IsTest);
        Assert.True(stored.Delivered);
        var webhook = (await repository.FindAsync(endpoint.Id))!;
        Assert.Equal("HTTP 204", webhook.LastStatus);
        Assert.Equal(at, webhook.LastAttemptAt);
        Assert.Equal(at, webhook.LastSuccessAt);
        Assert.Equal(at, (await repository.LastAttemptAsync())!.At);
    }

    [Fact]
    public async Task Prune_RemovesOldAttemptsButEachWebhookKeepsItsNewestTwenty()
    {
        var repository = new ApplicationWebhookRepository(_database);
        var busy = NewEndpoint();
        var quiet = NewEndpoint();
        await repository.SaveAsync(busy, true);
        await repository.SaveAsync(quiet, true);
        var oldest = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        for (var number = 1; number <= 30; number++)
        {
            await repository.RecordTestAsync(NewFinishedDelivery(busy), NewAttempt(busy, NewDelivery(busy), number, oldest.AddMinutes(number), "HTTP 500"), "Failed: HTTP 500");
        }

        await repository.RecordTestAsync(NewFinishedDelivery(quiet), NewAttempt(quiet, NewDelivery(quiet), 1, oldest, "HTTP 500"), "Failed: HTTP 500");
        var recent = new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);
        await repository.RecordTestAsync(NewFinishedDelivery(quiet), NewAttempt(quiet, NewDelivery(quiet), 2, recent, "HTTP 200"), "HTTP 200");

        await repository.PruneAsync(oldest.AddDays(7));

        var busyLeft = await repository.ListAttemptsAsync(busy.Id, 50);
        Assert.Equal(ApplicationWebhookRepository.RetainedAttemptsPerWebhook, busyLeft.Count);
        Assert.Equal(30, busyLeft[0].AttemptNumber);
        Assert.Equal(11, busyLeft[^1].AttemptNumber);
        Assert.Equal(2, (await repository.ListAttemptsAsync(quiet.Id, 50)).Count);

        await repository.PruneAsync(recent);

        Assert.Equal(ApplicationWebhookRepository.RetainedAttemptsPerWebhook, (await repository.ListAttemptsAsync(busy.Id, 50)).Count);
        Assert.Equal(2, (await repository.ListAttemptsAsync(quiet.Id, 50)).Count);
        await repository.PruneAsync(recent.AddDays(1));
        Assert.Equal(2, (await repository.ListAttemptsAsync(quiet.Id, 50)).Count);
    }

    [Fact]
    public async Task DeletingAWebhook_DeletesItsAttemptHistory()
    {
        var repository = new ApplicationWebhookRepository(_database);
        var endpoint = NewEndpoint();
        await repository.SaveAsync(endpoint, true);
        await repository.RecordTestAsync(NewFinishedDelivery(endpoint), NewAttempt(endpoint, NewDelivery(endpoint), 1, DateTimeOffset.UtcNow, "HTTP 500"), "Failed: HTTP 500");

        await repository.DeleteAsync(endpoint.ApplicationId, endpoint.Id);

        Assert.Empty(await repository.ListAttemptsAsync(endpoint.Id, 50));
    }

    [Fact]
    public async Task EventServerId_IsCreatedOnceAndStaysTheSame()
    {
        var repository = new ApplicationWebhookRepository(_database);

        var first = await repository.EventServerIdAsync();

        Assert.True(Guid.TryParse(first, out _));
        Assert.Equal(first, await repository.EventServerIdAsync());
    }

    private static ApplicationWebhookAttempt NewAttempt(ApplicationWebhook endpoint, ApplicationWebhookDelivery delivery, int number, DateTimeOffset at, string result) => new()
    {
        Id = Guid.NewGuid(),
        WebhookId = endpoint.Id,
        DeliveryId = delivery.Id,
        EventId = delivery.EventId,
        EventType = "ingestion.completed",
        AttemptNumber = number,
        AttemptedAt = at,
        Result = result,
        HttpStatus = result.StartsWith("HTTP ", StringComparison.Ordinal) ? int.Parse(result[5..]) : null,
        DurationMs = 12,
    };

    private static ApplicationWebhookDelivery NewFinishedDelivery(ApplicationWebhook endpoint)
    {
        var delivery = NewDelivery(endpoint);
        delivery.Status = "failed";
        delivery.AttemptCount = 1;
        delivery.EventSequence = 0;
        return delivery;
    }

    private static ApplicationWebhook NewEndpoint() => new()
    {
        Id = Guid.NewGuid(),
        ApplicationId = BuiltInApplicationIds.NativeClient,
        Url = "https://receiver.example.invalid/events",
        EventTypesJson = "[\"ingestion.completed\"]",
        SecretCiphertext = "encrypted-fixture",
        IsEnabled = true,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static ApplicationWebhookDelivery NewDelivery(ApplicationWebhook endpoint) => new()
    {
        Id = Guid.NewGuid(),
        WebhookId = endpoint.Id,
        WebhookVersion = endpoint.Version,
        EventId = Guid.NewGuid(),
        EventSequence = 1,
        EventJson = "{}",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1),
    };

    public void Dispose()
    {
        using (var connection = _database.CreateConnection())
        {
            SqliteConnection.ClearPool(connection);
        }

        _database.Dispose();
        File.Delete(_path);
    }
}
