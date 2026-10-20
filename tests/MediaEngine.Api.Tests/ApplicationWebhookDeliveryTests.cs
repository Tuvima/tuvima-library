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
        public List<(Guid DeliveryId, Guid EventId, string Body)> Calls { get; } = [];
        public Task<WebhookSendResult> SendAsync(string url, bool local, string secret, Guid deliveryId, Guid eventId, ReadOnlyMemory<byte> body, CancellationToken ct)
        {
            Calls.Add((deliveryId, eventId, System.Text.Encoding.UTF8.GetString(body.Span)));
            return Task.FromResult(new WebhookSendResult(false, "Receiver unavailable", true));
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
