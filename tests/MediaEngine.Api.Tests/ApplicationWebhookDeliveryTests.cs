using System.Net;
using System.Text.Json;
using MediaEngine.Api.Services.Approvals;
using MediaEngine.Api.Services.Events;
using MediaEngine.Api.Services.Webhooks;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Events;
using MediaEngine.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.Tests;

public sealed class ApplicationWebhookDeliveryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"webhook-delivery-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly ApplicationWebhookRepository _webhooks;
    private readonly ApplicationEventRepository _events;
    private readonly ApplicationRepository _applications;
    private readonly StubAuthorization _authorization = new();
    private readonly StubTransport _transport = new();
    private readonly Clock _clock = new();
    private readonly ApprovalService _approvals;
    private readonly ApplicationWebhookService _management;
    private readonly ApplicationWebhookDispatcher _dispatcher;
    private readonly ApplicationWebhookTestSender _sender;
    private readonly Guid _applicationId = Guid.NewGuid();

    public ApplicationWebhookDeliveryTests()
    {
        DapperConfiguration.Configure();
        _database = new(_path);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _webhooks = new(_database); _events = new(_database); _applications = new(_database);
        _approvals = new(new ApprovalRecordRepository(_database), new ProfileRepository(_database), _applications, new PermissionRegistry(), _clock);
        _management = new(_webhooks, _applications, _events, _authorization, new ApplicationEventRegistry(new PermissionRegistry()),
            new WebhookDestinationPolicy(new DnsStub()), new EphemeralDataProtectionProvider(), _clock, _approvals);
        _dispatcher = new(_webhooks, _events, _authorization, _management, _transport, _clock);
        _sender = new(_webhooks, _management, _authorization, _transport, _clock);
    }

    private async Task<ApplicationWebhookSecretResponse> CreateAsync()
    {
        await _applications.InsertApplicationAsync(new()
        {
            Id = _applicationId,
            Name = "Synthetic webhook test",
            ApplicationType = ApplicationType.ServerIntegration,
            CreatedAt = _clock.GetUtcNow(),
            UpdatedAt = _clock.GetUtcNow(),
        }, new HashSet<ApplicationPermissionId>());
        var approvalHash = _approvals.PreviewWebhook(new Uri("https://receiver.example.invalid/events"), ["ingestion.completed"], false, "").StatementHash;
        return await _management.SaveAsync(_applicationId, null,
            new("https://receiver.example.invalid/events", ["ingestion.completed"], false, true, null, approvalHash), Administrator, default);
    }

    private static readonly RequestAuthority Administrator = new(PrincipalKind.Human, true, Guid.NewGuid(), Guid.NewGuid());

    private Task<StoredApplicationEvent> EventAsync() => _events.AppendAsync(new(0, Guid.NewGuid(), "ingestion.completed", 1,
        _clock.GetUtcNow(), "synthetic-server", new("ingestion", "current"), "{\"completed\":3}"));

    [Fact]
    public async Task SecretsAreOneTimeEncryptedAndRotationCancelsOldPendingDelivery()
    {
        var created = await CreateAsync();
        Assert.False(string.IsNullOrWhiteSpace(created.SigningSecret));
        var stored = (await _webhooks.FindAsync(created.Webhook.Id))!;
        Assert.DoesNotContain(created.SigningSecret!, stored.SecretCiphertext);
        Assert.DoesNotContain(created.SigningSecret!, JsonSerializer.Serialize(await _management.ListAsync(_applicationId, default)));
        await EventAsync();
        await _dispatcher.TickAsync(default);
        Assert.Single(_transport.Calls);
        var rotated = await _management.RotateAsync(_applicationId, stored.Id, default);
        Assert.NotEqual(created.SigningSecret, rotated.SigningSecret);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _dispatcher.TickAsync(default);
        Assert.Single(_transport.Calls);
        Assert.Empty(await _webhooks.DueAsync(_clock.GetUtcNow().AddDays(1)));
    }

    [Fact]
    public async Task RevocationBeforeRetrySkipsDeliveryWithoutSendingPayload()
    {
        var created = await CreateAsync();
        await EventAsync();
        await _dispatcher.TickAsync(default);
        Assert.Single(_transport.Calls);
        _authorization.Allowed = false;
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _dispatcher.TickAsync(default);
        Assert.Single(_transport.Calls);
        Assert.Equal("Skipped: application access revoked", (await _webhooks.FindAsync(created.Webhook.Id))!.LastStatus);
    }

    [Fact]
    public async Task RetryIsBoundedAndRetainsExactEventBodyAndDeliveryId()
    {
        var created = await CreateAsync();
        var value = await EventAsync();
        for (var i = 0; i < 8; i++)
        {
            await _dispatcher.TickAsync(default);
            _clock.Advance(TimeSpan.FromHours(3));
        }
        Assert.Equal(6, _transport.Calls.Count);
        Assert.Single(_transport.Calls.Select(call => call.DeliveryId).Distinct());
        Assert.Single(_transport.Calls.Select(call => call.Body).Distinct());
        Assert.All(_transport.Calls, call => Assert.Equal(value.EventId, call.EventId));
        Assert.Empty(await _webhooks.DueAsync(_clock.GetUtcNow().AddDays(1)));
        Assert.Equal("Failed: Receiver unavailable", (await _webhooks.FindAsync(created.Webhook.Id))!.LastStatus);
    }

    [Fact]
    public async Task UnsupportedApplicationOrDeniedSubscriptionCannotCreateWebhook()
    {
        _authorization.Allowed = false;
        await Assert.ThrowsAsync<ArgumentException>(() => CreateAsync());
        Assert.Empty(await _webhooks.ListAsync(_applicationId));
        await Assert.ThrowsAsync<ArgumentException>(() => _management.ListAsync(MediaEngine.Domain.Entities.BuiltInApplicationIds.NativeClient, default));
    }

    [Fact]
    public async Task EveryTry_IsRecordedAsAnAttempt_NumberedFromOneWithItsPlainResult()
    {
        var created = await CreateAsync();
        await EventAsync();
        _transport.Next = new(false, "HTTP 503", true);
        for (var i = 0; i < 8; i++)
        {
            await _dispatcher.TickAsync(default);
            _clock.Advance(TimeSpan.FromHours(3));
        }

        var attempts = await _management.ListAttemptsAsync(_applicationId, created.Webhook.Id, 50, default);

        Assert.Equal([6, 5, 4, 3, 2, 1], attempts.Select(attempt => attempt.AttemptNumber));
        Assert.All(attempts, attempt =>
        {
            Assert.False(attempt.Delivered);
            Assert.False(attempt.IsTest);
            Assert.Equal("HTTP 503", attempt.Result);
            Assert.Equal("ingestion.completed", attempt.EventType);
        });
        Assert.Equal(attempts.OrderByDescending(attempt => attempt.AttemptedAt).Select(attempt => attempt.AttemptId), attempts.Select(attempt => attempt.AttemptId));
    }

    [Fact]
    public async Task ARetryThatSucceeds_RecordsTheFailureThenTheDelivery()
    {
        var created = await CreateAsync();
        await EventAsync();
        await _dispatcher.TickAsync(default);
        _transport.Next = new(true, "HTTP 200", false);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _dispatcher.TickAsync(default);

        var attempts = await _management.ListAttemptsAsync(_applicationId, created.Webhook.Id, null, default);

        Assert.Equal(2, attempts.Count);
        Assert.True(attempts[0].Delivered);
        Assert.Equal(2, attempts[0].AttemptNumber);
        Assert.Equal("HTTP 200", attempts[0].Result);
        Assert.False(attempts[1].Delivered);
        Assert.Equal("Receiver unavailable", attempts[1].Result);
    }

    [Fact]
    public async Task AttemptHistory_DefaultsToTheLastSixAndNeverExceedsFifty()
    {
        var created = await CreateAsync();
        for (var i = 0; i < 8; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            await _sender.SendAsync(_applicationId, created.Webhook.Id, default);
        }

        Assert.Equal(6, (await _management.ListAttemptsAsync(_applicationId, created.Webhook.Id, null, default)).Count);
        Assert.Equal(8, (await _management.ListAttemptsAsync(_applicationId, created.Webhook.Id, 500, default)).Count);
        Assert.Single(await _management.ListAttemptsAsync(_applicationId, created.Webhook.Id, 0, default));
    }

    [Fact]
    public async Task AttemptHistory_IsRefusedForAWebhookOfAnotherApplication()
    {
        var created = await CreateAsync();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => _management.ListAttemptsAsync(_applicationId, Guid.NewGuid(), null, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _management.ListAttemptsAsync(Guid.NewGuid(), created.Webhook.Id, null, default));
    }

    [Fact]
    public async Task SendTest_SendsOneSignedTestEventAndRecordsADeliveryAndATestAttempt()
    {
        var created = await CreateAsync();
        _transport.Next = new(true, "HTTP 204", false);

        var attempt = await _sender.SendAsync(_applicationId, created.Webhook.Id, default);

        Assert.True(attempt.Delivered);
        Assert.True(attempt.IsTest);
        Assert.Equal(1, attempt.AttemptNumber);
        Assert.Equal("HTTP 204", attempt.Result);
        Assert.Equal("webhook.test", attempt.EventType);
        var call = Assert.Single(_transport.Calls);
        Assert.Equal("https://receiver.example.invalid/events", call.Url);
        using var body = JsonDocument.Parse(call.Body);
        Assert.Equal("webhook.test", body.RootElement.GetProperty("event_type").GetString());
        Assert.Equal(call.EventId, body.RootElement.GetProperty("event_id").GetGuid());
        Assert.Equal("Test delivery from Library", body.RootElement.GetProperty("payload").GetProperty("message").GetString());
        var stored = (await _webhooks.FindAsync(created.Webhook.Id))!;
        Assert.Equal("HTTP 204", stored.LastStatus);
        Assert.NotNull(stored.LastSuccessAt);
        Assert.Equal(attempt.AttemptId, Assert.Single(await _management.ListAttemptsAsync(_applicationId, created.Webhook.Id, null, default)).AttemptId);
        Assert.Equal(_clock.GetUtcNow(), (await _webhooks.LastAttemptAsync())!.At);
    }

    [Fact]
    public async Task SendTest_ThatFails_IsNeverRetried_AndSendsNothingElseLater()
    {
        var created = await CreateAsync();

        var attempt = await _sender.SendAsync(_applicationId, created.Webhook.Id, default);

        Assert.False(attempt.Delivered);
        Assert.Equal("Receiver unavailable", attempt.Result);
        Assert.Equal("Failed: Receiver unavailable", (await _webhooks.FindAsync(created.Webhook.Id))!.LastStatus);
        Assert.Empty(await _webhooks.DueAsync(_clock.GetUtcNow().AddDays(1)));
        _clock.Advance(TimeSpan.FromHours(3));
        await _dispatcher.TickAsync(default);
        Assert.Single(_transport.Calls);
        Assert.Single(await _management.ListAttemptsAsync(_applicationId, created.Webhook.Id, null, default));
    }

    [Fact]
    public async Task SendTest_IsRefusedWithAPlainMessage_WhenTheWebhookIsSwitchedOff_AndSendsNothing()
    {
        var created = await CreateAsync();
        await _management.SaveAsync(_applicationId, created.Webhook.Id,
            new("https://receiver.example.invalid/events", ["ingestion.completed"], false, false, created.Webhook.Version), Administrator, default);

        var problem = await Assert.ThrowsAsync<InvalidOperationException>(() => _sender.SendAsync(_applicationId, created.Webhook.Id, default));

        Assert.Equal("Turn this webhook on to send a test.", problem.Message);
        Assert.Empty(_transport.Calls);
        Assert.Empty(await _management.ListAttemptsAsync(_applicationId, created.Webhook.Id, null, default));
    }

    [Fact]
    public async Task SendTest_IsRefused_WhenTheApplicationCanNoLongerReceiveTheWebhooksEvents()
    {
        var created = await CreateAsync();
        _authorization.Allowed = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _sender.SendAsync(_applicationId, created.Webhook.Id, default));

        Assert.Empty(_transport.Calls);
    }

    [Fact]
    public async Task SendTest_RecordsNoSecretOrBodyInTheAttempt()
    {
        var created = await CreateAsync();

        var attempt = await _sender.SendAsync(_applicationId, created.Webhook.Id, default);

        var json = JsonSerializer.Serialize(attempt);
        Assert.DoesNotContain(created.SigningSecret!, json);
        Assert.DoesNotContain("Test delivery from Library", json);
    }

    [Fact]
    public void TheTestEventType_IsNotSubscribable()
    {
        Assert.DoesNotContain(ApplicationWebhookTestSender.TestEventType, _management.EventTypes);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
    private sealed class DnsStub : IWebhookDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") });
    }
    private sealed class StubAuthorization : IApplicationEventDeliveryAuthorizer
    {
        public bool Allowed { get; set; } = true;
        public ValueTask<bool> CanSubscribeAsync(Guid id, IReadOnlyList<string> types, CancellationToken ct = default) => ValueTask.FromResult(Allowed);
        public ValueTask<bool> CanDeliverAsync(Guid id, StoredApplicationEvent value, CancellationToken ct = default) => ValueTask.FromResult(Allowed);
    }
    private sealed class StubTransport : IWebhookTransport
    {
        public List<(string Url, Guid DeliveryId, Guid EventId, string Body)> Calls { get; } = [];
        public WebhookSendResult Next { get; set; } = new(false, "Receiver unavailable", true);
        public Task<WebhookSendResult> SendAsync(string url, bool local, string secret, Guid deliveryId, Guid eventId, ReadOnlyMemory<byte> body, CancellationToken ct)
        {
            Calls.Add((url, deliveryId, eventId, System.Text.Encoding.UTF8.GetString(body.Span)));
            return Task.FromResult(Next);
        }
    }
    public void Dispose()
    {
        using (var connection = _database.CreateConnection())
        {
            SqliteConnection.ClearPool(connection);
        }

        _database.Dispose(); File.Delete(_path);
    }
}
