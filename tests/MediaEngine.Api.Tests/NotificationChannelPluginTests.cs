using System.Text.Json;
using MediaEngine.Api.Services.Notifications;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Domain.Configuration;
using MediaEngine.Plugins;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Notification channels written as JSON (Developer tools spec WP24): a channel added as a file alone delivers a message, its
/// login comes from the secrets store, and it shows up wherever channels are listed only while its plugin is switched on.
/// </summary>
public sealed class NotificationChannelPluginTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-channel-plugins", Guid.NewGuid().ToString("N"));
    private readonly string _pluginRoot;
    private readonly PluginCatalog _catalog;
    private readonly RecordingFactory _contexts = new();
    private readonly PluginNotificationChannelSource _source;

    public NotificationChannelPluginTests()
    {
        var libraryRoot = Path.Combine(_root, "library");
        _pluginRoot = Path.Combine(libraryRoot, ".data", "plugins");
        Directory.CreateDirectory(_pluginRoot);
        var loader = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        loader.SaveCore(new CoreConfiguration { LibraryRoot = libraryRoot });
        _catalog = new PluginCatalog([], new PluginSettingsService(loader), loader, AllowAllPluginApprovals.Instance, NullLogger<PluginCatalog>.Instance);
        _source = new PluginNotificationChannelSource(_catalog, _contexts, NullLogger<PluginNotificationChannelSource>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void AChannelFromAJsonFile_ShowsInTheChannelList_OnlyWhileThePluginIsSwitchedOn()
    {
        Add("json.hook", Channel());

        Assert.Empty(_source.GetChannels());

        _catalog.SetEnabled("json.hook", true);
        var channel = Assert.Single(_source.GetChannels());
        Assert.Equal("plugin:json.hook:ntfy", channel.Id);
        Assert.Contains("Phone alert", channel.Name);
    }

    [Fact]
    public async Task ATestMessage_IsSentToTheDeclaredAddress_WithTheLoginFromTheSecretsStore()
    {
        Add("json.hook", Channel());
        _catalog.SetEnabled("json.hook", true);
        _contexts.Secrets["token"] = "s3cret-value";

        var result = await Assert.Single(_source.GetChannels())
            .SendAsync(new OutgoingNotification("backup.failed", "Backup \"failed\"", "Line one\nLine two"), [], CancellationToken.None);

        Assert.True(result.Success);
        var sent = Assert.Single(_contexts.Requests);
        Assert.Equal("https://ntfy.example.com/alerts", sent.Url);
        Assert.Equal("Bearer s3cret-value", sent.Authorization);
        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal("Backup \"failed\"", body.RootElement.GetProperty("title").GetString());
        Assert.Equal("Line one\nLine two", body.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task AMissingLogin_FailsInPlainWords_WithoutSendingAnything()
    {
        Add("json.hook", Channel());
        _catalog.SetEnabled("json.hook", true);

        var result = await Assert.Single(_source.GetChannels())
            .SendAsync(new OutgoingNotification("backup.failed", "Title", "Body"), [], CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("login", result.Error);
        Assert.Empty(_contexts.Requests);
    }

    [Fact]
    public async Task AnErrorFromTheServer_NeverCarriesTheLoginInItsMessage()
    {
        Add("json.hook", Channel());
        _catalog.SetEnabled("json.hook", true);
        _contexts.Secrets["token"] = "s3cret-value";
        _contexts.Status = System.Net.HttpStatusCode.Unauthorized;

        var result = await Assert.Single(_source.GetChannels())
            .SendAsync(new OutgoingNotification("backup.failed", "Title", "Body"), [], CancellationToken.None);

        Assert.False(result.Success);
        Assert.DoesNotContain("s3cret", result.Error);
        Assert.Contains("401", result.Error);
    }

    [Fact]
    public void TheSampleWebhookChannel_LoadsAsShipped()
    {
        var repo = AppContext.BaseDirectory;
        while (repo is not null && !Directory.Exists(Path.Combine(repo, "samples", "plugins")))
        {
            repo = Path.GetDirectoryName(repo);
        }

        var directory = Path.Combine(_pluginRoot, "tuvima.sample.webhook-channel");
        Directory.CreateDirectory(directory);
        File.Copy(Path.Combine(repo!, "samples", "plugins", "webhook-channel", "plugin.json"), Path.Combine(directory, "plugin.json"));
        _catalog.Reload();

        var plugin = Assert.IsType<PluginRegistration>(_catalog.Get("tuvima.sample.webhook-channel"));
        Assert.Null(plugin.LoadError);
        Assert.Single(plugin.Capabilities);
    }

    [Theory]
    [InlineData("""{ "key": "x", "name": "X", "url": "http://ntfy.example.com/a" }""", "https://")]
    [InlineData("""{ "key": "x", "name": "X", "url": "https://elsewhere.example.org/a" }""", "not listed under \"network\"")]
    [InlineData("""{ "key": "x", "name": "X", "url": "https://ntfy.example.com/a", "headers": { "Authorization": "Bearer {secret:other}" } }""", "not listed under \"secrets\"")]
    [InlineData("""{ "key": "x", "name": "X", "url": "https://ntfy.example.com/a", "body": "{secret:token}" }""", "Secrets can only go in headers")]
    [InlineData("""{ "key": "x", "name": "X", "url": "https://ntfy.example.com/a", "body": "{nonsense}" }""", "{title}")]
    public void AMalformedChannel_IsRefusedWithASentenceSayingWhatToChange(string channel, string expected)
    {
        Add("json.bad", Channel(channel));

        var plugin = Assert.IsType<PluginRegistration>(_catalog.Get("json.bad"));

        Assert.Contains(expected, plugin.LoadError);
    }

    private static string Channel(string? channel = null) => $$"""
        "permissions": ["network.http", "secrets.plugin"],
        "network": [{ "host": "ntfy.example.com", "sends": "The notification title and message" }],
        "secrets": [{ "name": "token", "label": "Access token", "description": "Your ntfy access token" }],
        "declarative": { "notification_channels": [{{channel ?? """
            { "key": "ntfy", "name": "Phone alert", "url": "https://ntfy.example.com/alerts",
              "headers": { "Authorization": "Bearer {secret:token}" } }
            """}}] }
        """;

    private void Add(string id, string fields)
    {
        var directory = Path.Combine(_pluginRoot, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "plugin.json"), $$"""
            { "id": "{{id}}", "name": "{{id}}", "version": "1.0.0", "kind": "declarative", {{fields}} }
            """);
        _catalog.Reload();
    }

    private sealed record SentRequest(string Url, string? Authorization, string Body);

    private sealed class RecordingFactory : IPluginExecutionContextFactory
    {
        public List<SentRequest> Requests { get; } = [];

        public Dictionary<string, string> Secrets { get; } = [];

        public System.Net.HttpStatusCode Status { get; set; } = System.Net.HttpStatusCode.OK;

        public IPluginExecutionContext Create(string pluginId, string purpose) => new Context(this);

        public IPluginExecutionContext CreateForMedia(string pluginId, string purpose, PluginMediaAssetContext asset) => throw new NotSupportedException();

        private sealed class Context(RecordingFactory owner) : IPluginExecutionContext
        {
            public string PluginId => "json.hook";

            public IReadOnlyDictionary<string, JsonElement> Settings => new Dictionary<string, JsonElement>();

            public IPluginHttpClient Http => new Http(owner);

            public IPluginSecrets Secrets => new SecretStore(owner.Secrets);

            public IPluginMediaAccess Media => throw new NotSupportedException();

            public IPluginStorage Storage => throw new NotSupportedException();

            public IPluginToolRuntime Tools => throw new NotSupportedException();

            public IPluginAiClient Ai => throw new NotSupportedException();

            public IPluginRecordStore Records => throw new NotSupportedException();

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class Http(RecordingFactory owner) : IPluginHttpClient
        {
            public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead, CancellationToken cancellationToken = default)
            {
                owner.Requests.Add(new SentRequest(
                    request.RequestUri!.ToString(),
                    request.Headers.TryGetValues("Authorization", out var values) ? values.Single() : null,
                    await request.Content!.ReadAsStringAsync(cancellationToken)));
                return new HttpResponseMessage(owner.Status);
            }
        }

        private sealed class SecretStore(Dictionary<string, string> values) : IPluginSecrets
        {
            public Task<string?> GetAsync(string name, CancellationToken cancellationToken = default) =>
                Task.FromResult(values.TryGetValue(name, out var value) ? value : null);
        }
    }
}
