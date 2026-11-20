using System.Text.Json;
using MediaEngine.Api.Services.Notifications;
using MediaEngine.Contracts.Notifications;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>Channels added from Settings (notifications and delivery spec WP2): types as data, secrets kept out of sight, routing cleaned on removal.</summary>
public sealed class NotificationChannelServiceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_notification_channels_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly NotificationRepository _repository;
    private readonly NotificationChannelStore _store;
    private readonly NotificationChannelService _channels;
    private readonly NotificationService _notifications;
    private readonly ChannelTypeCatalogue _types = new();
    private readonly RecordingHandler _http = new();

    public NotificationChannelServiceTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _repository = new NotificationRepository(_db);
        var clock = new FixedClock(Start);
        _store = new NotificationChannelStore(_repository, new ReversibleSecrets(), clock);
        var settings = new NotificationSettingsAccessor(_repository, clock);
        var source = new AddedChannelSource(_store, _types, new HandlerFactory(_http), NullLogger<AddedChannelSource>.Instance);
        _notifications = new NotificationService(_repository, settings, [], [], [source], _types, new AccountRepository(_db), new NoEvents(), clock, NullLogger<NotificationService>.Instance);
        _channels = new NotificationChannelService(_store, _types, _notifications);
    }

    [Fact]
    public void TheTypesShippedWithTheEngine_AreDataWithPlainHelpAndAPrivacyLine()
    {
        var types = _channels.GetTypes().Types;

        Assert.Contains(types, type => type.Id == "discord");
        Assert.Contains(types, type => type.Id == "webhook");
        Assert.All(types, type =>
        {
            Assert.False(string.IsNullOrWhiteSpace(type.WhatLeaves), type.Id);
            Assert.False(string.IsNullOrWhiteSpace(type.Summary), type.Id);
            Assert.NotEmpty(type.Fields);
        });
    }

    [Fact]
    public async Task ASecondDiscordChannel_CanBeAddedTestedAndRouted()
    {
        var first = await Create("discord", "Family", ("url", "https://discord.com/api/webhooks/1/aaa"));
        var second = await Create("discord", "Friends", ("url", "https://discord.com/api/webhooks/2/bbb"));

        var settings = await _notifications.GetSettingsAsync(CancellationToken.None);
        Assert.Equal(["Family", "Friends"], settings.Channels.Where(channel => channel.Type == "discord").Select(channel => channel.Name).Order());

        var test = await _notifications.SendTestAsync(second, new NotificationRecipient(Guid.Empty, string.Empty), CancellationToken.None);

        Assert.True(test.Success, test.Message);
        var request = Assert.Single(_http.Requests);
        Assert.Equal("https://discord.com/api/webhooks/2/bbb", request.Url);
        Assert.Contains("Library test message", request.Body, StringComparison.Ordinal);

        Assert.Null(await _notifications.UpdateSettingsAsync(new UpdateNotificationSettingsRequest { Routes = { [NotificationEventIds.BackupFailed] = [first, second] } }, CancellationToken.None));
    }

    [Fact]
    public async Task TheLinkIsKeptEncrypted_AndNeverComesBackOut()
    {
        var id = await Create("discord", "Family", ("url", "https://discord.com/api/webhooks/1/topsecret"));

        var detail = await _channels.GetAsync(id, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(["url"], detail.SecretsSet);
        Assert.DoesNotContain("url", detail.Values.Keys);
        var stored = await _repository.GetSettingAsync("channels", CancellationToken.None);
        Assert.DoesNotContain("topsecret", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EditingWithoutRetypingTheLink_KeepsTheSavedOne()
    {
        var id = await Create("discord", "Family", ("url", "https://discord.com/api/webhooks/1/aaa"));

        var problem = await _channels.UpdateAsync(id, new SaveNotificationChannelRequest { Type = "discord", Name = "Renamed", Values = { ["username"] = "Home" } }, CancellationToken.None);

        Assert.Null(problem);
        var channel = Assert.Single(await _store.ListAsync(CancellationToken.None));
        Assert.Equal("Renamed", channel.Name);
        Assert.Equal("https://discord.com/api/webhooks/1/aaa", _store.Reveal(channel)["url"]);
        Assert.Equal("Home", _store.Reveal(channel)["username"]);
    }

    [Theory]
    [InlineData("discord", "", "needed")]
    [InlineData("discord", "http://discord.com/api/webhooks/1/a", "should be the link")]
    [InlineData("discord", "https://evil.example.com/api/webhooks/1/a", "should be the link")]
    [InlineData("webhook", "not a web address", "must be a full web address")]
    [InlineData("nonsense", "https://example.com", "not available")]
    public async Task AFormThatCannotWork_IsRefusedInPlainWords(string type, string url, string expected)
    {
        var (id, problem) = await _channels.CreateAsync(new SaveNotificationChannelRequest { Type = type, Name = "x", Values = { ["url"] = url } }, CancellationToken.None);

        Assert.Null(id);
        Assert.NotNull(problem);
        Assert.Contains(expected.Split(' ')[0], problem, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await _store.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RemovingAChannel_RemovesItsRoutes()
    {
        var id = await Create("webhook", "Home Assistant", ("url", "https://example.com/hooks/library"));
        await _notifications.UpdateSettingsAsync(new UpdateNotificationSettingsRequest
        {
            Routes = { [NotificationEventIds.BackupFailed] = [NotificationChannelIds.Dashboard, id] },
            ChannelDetails = { [id] = true },
        }, CancellationToken.None);

        Assert.True(await _channels.DeleteAsync(id, CancellationToken.None));

        var settings = await _notifications.GetSettingsAsync(CancellationToken.None);
        Assert.DoesNotContain(id, settings.Events.Single(item => item.Id == NotificationEventIds.BackupFailed).Channels);
        Assert.DoesNotContain(settings.Channels, channel => channel.Id == id);
    }

    [Fact]
    public async Task AFailedSend_NamesTheServiceButNeverTheLink()
    {
        _http.Status = System.Net.HttpStatusCode.Unauthorized;
        var id = await Create("webhook", "Hook", ("url", "https://example.com/hooks/very-secret-token"));

        var test = await _notifications.SendTestAsync(id, new NotificationRecipient(Guid.Empty, string.Empty), CancellationToken.None);

        Assert.False(test.Success);
        Assert.Contains("401", test.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("very-secret-token", test.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRequestBody_EscapesTheMessageSoItCanNeverBreakOutOfTheJson()
    {
        var discord = _types.Find("discord")!;
        var values = new Dictionary<string, string> { ["url"] = "https://discord.com/api/webhooks/1/aaa", ["username"] = "Library" };

        using var request = ChannelRequestRenderer.Build(discord, values, new OutgoingNotification("x", "A \"title\"", "line one\nline two \\"));
        var body = await request.Content!.ReadAsStringAsync();

        using var document = JsonDocument.Parse(body);
        Assert.Equal("**A \"title\"**\nline one\nline two \\", document.RootElement.GetProperty("content").GetString());
        Assert.Equal("Library", document.RootElement.GetProperty("username").GetString());
    }

    private async Task<string> Create(string type, string name, params (string Key, string Value)[] values)
    {
        var request = new SaveNotificationChannelRequest { Type = type, Name = name };
        foreach (var (key, value) in values)
        {
            request.Values[key] = value;
        }

        var (id, problem) = await _channels.CreateAsync(request, CancellationToken.None);
        Assert.Null(problem);
        return id!;
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private sealed class ReversibleSecrets : ISecretStore
    {
        public string Encrypt(string plaintext) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext));

        public string Decrypt(string ciphertext) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(ciphertext));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class NoEvents : MediaEngine.Domain.Events.IApplicationEventProducer
    {
        public Task PublishAsync(MediaEngine.Domain.Events.ApplicationEventDraft value, CancellationToken ct = default) => Task.CompletedTask;
    }

    internal sealed record SentRequest(string Url, string Body);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<SentRequest> Requests { get; } = [];

        public System.Net.HttpStatusCode Status { get; set; } = System.Net.HttpStatusCode.NoContent;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new SentRequest(request.RequestUri!.ToString(), request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(Status);
        }
    }

    private sealed class HandlerFactory(RecordingHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
