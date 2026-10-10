using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using MediaEngine.TestSupport;
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
        _database.Dispose();
        TestTemp.DeleteDatabase(_path);
    }
}
