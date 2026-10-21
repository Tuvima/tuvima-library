using System.Net;
using System.Text.Json;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.Plugins.Declarative;
using MediaEngine.Domain.Configuration;
using MediaEngine.Plugins;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Plugins that are only JSON: AI steps, channel and guide sources, and metadata sources described in a manifest. A good file
/// loads with no code, and a malformed one is refused with a sentence that says what to change.
/// </summary>
public sealed class DeclarativePluginTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-declarative-plugins", Guid.NewGuid().ToString("N"));
    private readonly string _pluginRoot;
    private readonly PluginCatalog _catalog;

    public DeclarativePluginTests()
    {
        var libraryRoot = Path.Combine(_root, "library");
        _pluginRoot = Path.Combine(libraryRoot, ".data", "plugins");
        Directory.CreateDirectory(_pluginRoot);
        var loader = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        loader.SaveCore(new CoreConfiguration { LibraryRoot = libraryRoot });
        _catalog = new PluginCatalog([], new PluginSettingsService(loader), loader, AllowAllPluginApprovals.Instance, NullLogger<PluginCatalog>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder another process still holds is left for the OS to clean up.
        }
    }

    // ── Loading ──────────────────────────────────────────────────────────────

    [Fact]
    public void AJsonOnlyPluginWithAnAiStep_LoadsWithNoCode_SwitchedOffAndCompatible()
    {
        Add("json.summary", """
            "permissions": ["ai.infer"],
            "ai_permissions": [{ "role": "text_fast", "max_tokens": 200, "schedule": "on-demand", "resource_class": "ai" }],
            "declarative": { "ai_steps": [{ "key": "summarise", "name": "Summarise", "role": "text_fast", "prompt": "Summarise: {text}" }] }
            """);

        var plugin = Assert.IsType<PluginRegistration>(_catalog.Get("json.summary"));

        Assert.Null(plugin.LoadError);
        Assert.True(plugin.Compatibility.IsCompatible);
        Assert.False(plugin.Enabled);
        Assert.False(plugin.IsBuiltIn);
        Assert.Equal(PluginTrustLevel.Private, plugin.Trust);
        Assert.Equal("summarise", Assert.Single(plugin.Capabilities.OfType<DeclarativeAiStepCapability>()).Step.Key);
    }

    [Fact]
    public void AChannelSourcePlugin_LoadsAsAScheduledJobOnItsRefreshSchedule()
    {
        Add("json.channels", ChannelPlugin("""{ "key": "main", "name": "Main channels", "format": "m3u", "url": "https://lists.example.com/a.m3u", "refresh": "12h" }"""));

        var plugin = Assert.IsType<PluginRegistration>(_catalog.Get("json.channels"));

        Assert.Null(plugin.LoadError);
        var job = Assert.Single(plugin.Capabilities.OfType<IPluginScheduledJob>());
        Assert.Equal("channels-main", job.Key);
        Assert.Equal(TimeSpan.FromHours(12), job.DefaultSchedule.Interval);
    }

    [Fact]
    public void AMetadataSourcePlugin_IsCheckedAndListed()
    {
        Add("json.source", """
            "declarative": { "metadata_sources": [{
                "name": "my_source", "adapter_type": "config_driven", "version": "1.0",
                "endpoints": { "api": "https://api.example.com" } }] }
            """);

        var plugin = Assert.IsType<PluginRegistration>(_catalog.Get("json.source"));

        Assert.Null(plugin.LoadError);
        Assert.Equal("my_source", Assert.Single(plugin.Capabilities.OfType<DeclarativeMetadataSourceCapability>()).Source.Name);
    }

    [Theory]
    [InlineData("\"declarative\": {}", "empty")]
    [InlineData("\"declarative\": { \"surprise\": [] }", "unknown part \"surprise\"")]
    [InlineData("\"declarative\": { \"ai_steps\": {} }", "must be a list")]
    [InlineData("\"permissions\": [], \"declarative\": { \"ai_steps\": [{ \"key\": \"a\", \"name\": \"A\", \"role\": \"text_fast\", \"prompt\": \"p\" }] }", "ai.infer permission")]
    [InlineData("\"permissions\": [\"ai.infer\"], \"declarative\": { \"ai_steps\": [{ \"key\": \"a\", \"name\": \"A\", \"role\": \"text_fast\", \"prompt\": \"p\" }] }", "ai_permissions")]
    [InlineData("\"permissions\": [\"ai.infer\"], \"ai_permissions\": [{ \"role\": \"poetry\" }], \"declarative\": { \"ai_steps\": [{ \"key\": \"a\", \"name\": \"A\", \"role\": \"poetry\", \"prompt\": \"p\" }] }", "unknown role \"poetry\"")]
    [InlineData("\"permissions\": [\"ai.infer\"], \"ai_permissions\": [{ \"role\": \"text_fast\" }], \"declarative\": { \"ai_steps\": [{ \"key\": \"Bad Key\", \"name\": \"A\", \"role\": \"text_fast\", \"prompt\": \"p\" }] }", "\"key\"")]
    [InlineData("\"permissions\": [\"ai.infer\"], \"ai_permissions\": [{ \"role\": \"text_fast\" }], \"declarative\": { \"ai_steps\": [{ \"key\": \"a\", \"name\": \"A\", \"role\": \"text_fast\" }] }", "\"prompt\"")]
    [InlineData("\"permissions\": [\"ai.infer\"], \"ai_permissions\": [{ \"role\": \"text_fast\" }], \"declarative\": { \"ai_steps\": [{ \"key\": \"a\", \"name\": \"A\", \"role\": \"text_fast\", \"prompt\": \"p\", \"max_tokens\": 5 }] }", "max_tokens")]
    [InlineData("\"permissions\": [\"ai.infer\"], \"ai_permissions\": [{ \"role\": \"text_fast\" }], \"declarative\": { \"ai_steps\": [{ \"key\": \"a\", \"name\": \"A\", \"role\": \"text_fast\", \"prompt\": \"p\" }, { \"key\": \"a\", \"name\": \"B\", \"role\": \"text_fast\", \"prompt\": \"p\" }] }", "used more than once")]
    public void AMalformedFile_IsRefusedWithAPlainMessage(string fields, string expected)
    {
        Add("json.bad", fields);

        var plugin = Assert.IsType<PluginRegistration>(_catalog.Get("json.bad"));

        Assert.NotNull(plugin.LoadError);
        Assert.Contains(expected, plugin.LoadError, StringComparison.Ordinal);
        Assert.False(plugin.Enabled);
        Assert.Empty(plugin.Capabilities);
    }

    [Theory]
    [InlineData("""{ "key": "main", "name": "Main", "format": "pls", "url": "https://lists.example.com/a" }""", "unknown format \"pls\"")]
    [InlineData("""{ "key": "main", "name": "Main", "format": "m3u", "url": "http://lists.example.com/a.m3u" }""", "https://")]
    [InlineData("""{ "key": "main", "name": "Main", "format": "m3u", "url": "https://elsewhere.example.org/a.m3u" }""", "not listed under \"network\"")]
    [InlineData("""{ "key": "main", "name": "Main", "format": "m3u", "url": "https://lists.example.com/a.m3u", "refresh": "1m" }""", "refresh")]
    public void AChannelSourceThatBreaksARule_IsRefusedWithAPlainMessage(string source, string expected)
    {
        Add("json.channels-bad", ChannelPlugin(source));

        var plugin = Assert.IsType<PluginRegistration>(_catalog.Get("json.channels-bad"));

        Assert.NotNull(plugin.LoadError);
        Assert.Contains(expected, plugin.LoadError, StringComparison.Ordinal);
    }

    [Fact]
    public void AChannelSourceThatSkipsAPermission_SaysWhichOne()
    {
        Add("json.channels-perm", """
            "permissions": ["network.http"],
            "network": [{ "host": "lists.example.com", "sends": "Nothing; it only downloads the list" }],
            "declarative": { "channel_sources": [{ "key": "main", "name": "Main", "format": "m3u", "url": "https://lists.example.com/a.m3u" }] }
            """);

        var plugin = Assert.IsType<PluginRegistration>(_catalog.Get("json.channels-perm"));

        Assert.Contains("scheduled-work", plugin.LoadError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "adapter_type": "config_driven", "endpoints": { "api": "https://a.example.com" } }""", "needs a \"name\"")]
    [InlineData("""{ "name": "x", "endpoints": { "api": "https://a.example.com" } }""", "adapter_type")]
    [InlineData("""{ "name": "x", "adapter_type": "coded", "endpoints": { "api": "https://a.example.com" } }""", "adapter_type")]
    [InlineData("""{ "name": "x", "adapter_type": "config_driven", "endpoints": {} }""", "endpoints")]
    [InlineData("""{ "name": "x", "adapter_type": "config_driven", "endpoints": { "api": "http://a.example.com" } }""", "https://")]
    [InlineData("""{ "name": "x", "adapter_type": "config_driven", "weight": "heavy", "endpoints": { "api": "https://a.example.com" } }""", "not a valid metadata source")]
    public void AMetadataSourceThatBreaksARule_IsRefusedWithAPlainMessage(string source, string expected)
    {
        Add("json.source-bad", $$"""
            "declarative": { "metadata_sources": [{{source}}] }
            """);

        var plugin = Assert.IsType<PluginRegistration>(_catalog.Get("json.source-bad"));

        Assert.Contains(expected, plugin.LoadError, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatIsNotJson_DoesNotBreakTheOtherPlugins()
    {
        Add("json.summary", """
            "permissions": ["ai.infer"],
            "ai_permissions": [{ "role": "text_fast" }],
            "declarative": { "ai_steps": [{ "key": "s", "name": "S", "role": "text_fast", "prompt": "p" }] }
            """);
        var broken = Path.Combine(_pluginRoot, "json.broken");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "plugin.json"), "{ this is not json");
        _catalog.Reload();

        Assert.Null(_catalog.Get("json.summary")!.LoadError);
        Assert.Contains(_catalog.List(), plugin => plugin.Manifest.Id == "json.broken" && plugin.LoadError is not null);
    }

    // ── AI steps ─────────────────────────────────────────────────────────────

    [Fact]
    public void ThePrompt_IsFilledFromTheInputs_AndNamesTheFirstMissingOne()
    {
        var inputs = new Dictionary<string, string> { ["text"] = "A long story.", ["tone"] = "calm" };

        Assert.Equal("Summarise A long story. in a calm way", DeclarativeAiStepRunner.Fill("Summarise {text} in a {tone} way", inputs, out var none));
        Assert.Null(none);

        DeclarativeAiStepRunner.Fill("Use {text} and {other} and {more}", inputs, out var missing);
        Assert.Equal("other", missing);
    }

    [Fact]
    public async Task AStepOnAPluginThatIsSwitchedOff_IsRefused_AndAnUnknownStepIsNotFound()
    {
        Add("json.summary", """
            "permissions": ["ai.infer"],
            "ai_permissions": [{ "role": "text_fast" }],
            "declarative": { "ai_steps": [{ "key": "s", "name": "S", "role": "text_fast", "prompt": "p {text}" }] }
            """);
        var runner = new DeclarativeAiStepRunner(_catalog, gateway: null!);

        var off = await runner.RunAsync("json.summary", "s", new Dictionary<string, string>(), default);
        var unknownStep = await runner.RunAsync("json.summary", "nope", new Dictionary<string, string>(), default);
        var unknownPlugin = await runner.RunAsync("nobody", "s", new Dictionary<string, string>(), default);

        Assert.Equal(PluginAiStatus.Refused, off!.Status);
        Assert.Null(unknownStep);
        Assert.Null(unknownPlugin);
    }

    // ── Channel lists ────────────────────────────────────────────────────────

    [Fact]
    public void AnM3uList_GivesEachChannelItsNameGroupLogoAndAddress()
    {
        const string text = """
            #EXTM3U
            #EXTINF:-1 tvg-id="news.1" tvg-logo="https://img.example.com/n.png" group-title="News",News One
            https://stream.example.com/news1.m3u8
            #EXTINF:-1,Plain Channel
            https://stream.example.com/plain.m3u8
            """;

        var channels = ChannelListParsers.ParseM3u(text, out var error)!;

        Assert.Null(error);
        Assert.Equal(2, channels.Count);
        Assert.Equal(new ChannelEntry("news.1", "News One", "https://img.example.com/n.png", "News", "https://stream.example.com/news1.m3u8"), channels[0]);
        Assert.Equal("Plain Channel", channels[1].Id);
    }

    [Fact]
    public void TextThatIsNotAnM3uList_IsRefusedWithAPlainReason()
    {
        Assert.Null(ChannelListParsers.ParseM3u("<html>not a list</html>", out var error));
        Assert.Contains("#EXTM3U", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AnXmltvGuide_GivesChannelsAndProgrammesWithTheirTimes()
    {
        const string text = """
            <?xml version="1.0"?>
            <tv>
              <channel id="news.1"><display-name>News One</display-name><icon src="https://img.example.com/n.png"/></channel>
              <programme start="20261021183000 +0100" stop="20261021190000 +0100" channel="news.1"><title>Evening news</title><desc>Today.</desc></programme>
              <programme start="bad" stop="20261021190000" channel="news.1"><title>Broken time</title></programme>
            </tv>
            """;

        Assert.True(ChannelListParsers.TryParseXmltv(text, out var channels, out var programmes, out var error));

        Assert.Null(error);
        Assert.Equal("News One", Assert.Single(channels).Name);
        var programme = Assert.Single(programmes);
        Assert.Equal("Evening news", programme.Title);
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 18, 30, 0, TimeSpan.FromHours(1)), programme.Start);
    }

    [Theory]
    [InlineData("not xml at all")]
    [InlineData("<rss></rss>")]
    public void TextThatIsNotXmltv_IsRefusedWithAPlainReason(string text)
    {
        Assert.False(ChannelListParsers.TryParseXmltv(text, out _, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public async Task TheRefreshJob_FetchesParsesAndKeepsTheLatestListAsThePluginsOwnRecord()
    {
        Add("json.channels", ChannelPlugin("""{ "key": "main", "name": "Main", "format": "m3u", "url": "https://lists.example.com/a.m3u" }"""));
        var job = Assert.Single(_catalog.Get("json.channels")!.Capabilities.OfType<IPluginScheduledJob>());
        var context = new JobContext("#EXTM3U\n#EXTINF:-1,One\nhttps://s.example.com/1.m3u8\n");

        await job.RunAsync(context, default);

        var (collection, key, json) = Assert.Single(context.Saved);
        Assert.Equal("channels", collection);
        Assert.Equal("main", key);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("One", document.RootElement.GetProperty("channels")[0].GetProperty("name").GetString());
        Assert.Equal("m3u", document.RootElement.GetProperty("format").GetString());
    }

    [Fact]
    public async Task TheRefreshJob_FailsWithAPlainMessage_WhenTheAddressAnswersWithAnErrorOrTheWrongFormat()
    {
        Add("json.channels", ChannelPlugin("""{ "key": "main", "name": "Main", "format": "m3u", "url": "https://lists.example.com/a.m3u" }"""));
        var job = Assert.Single(_catalog.Get("json.channels")!.Capabilities.OfType<IPluginScheduledJob>());

        var serverError = await Assert.ThrowsAsync<InvalidOperationException>(() => job.RunAsync(new JobContext("x", HttpStatusCode.BadGateway), default));
        var wrongFormat = await Assert.ThrowsAsync<InvalidOperationException>(() => job.RunAsync(new JobContext("<html></html>"), default));

        Assert.Contains("502", serverError.Message, StringComparison.Ordinal);
        Assert.Contains("#EXTM3U", wrongFormat.Message, StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void Add(string id, string fields)
    {
        var directory = Path.Combine(_pluginRoot, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "plugin.json"), $$"""
            {
              "id": "{{id}}",
              "name": "{{id}}",
              "version": "1.0.0",
              "kind": "declarative",
              {{fields}}
            }
            """);
        _catalog.Reload();
    }

    private static string ChannelPlugin(string source) => $$"""
        "permissions": ["network.http", "scheduled-work", "plugin-records"],
        "network": [{ "host": "lists.example.com", "sends": "Nothing; it only downloads the list" }],
        "declarative": { "channel_sources": [{{source}}] }
        """;

    private sealed class JobContext(string body, HttpStatusCode status = HttpStatusCode.OK) : IPluginExecutionContext
    {
        public List<(string Collection, string Key, string Json)> Saved { get; } = [];

        public string PluginId => "json.channels";

        public IReadOnlyDictionary<string, JsonElement> Settings => new Dictionary<string, JsonElement>();

        public IPluginHttpClient Http => new FixedHttp(body, status);

        public IPluginRecordStore Records => new SavingRecords(Saved);

        public IPluginMediaAccess Media => throw new NotSupportedException();

        public IPluginStorage Storage => throw new NotSupportedException();

        public IPluginToolRuntime Tools => throw new NotSupportedException();

        public IPluginAiClient Ai => throw new NotSupportedException();

        public IPluginSecrets Secrets => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedHttp(string body, HttpStatusCode status) : IPluginHttpClient
    {
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    private sealed class SavingRecords(List<(string Collection, string Key, string Json)> saved) : IPluginRecordStore
    {
        public Task<PluginRecord?> GetAsync(string collection, string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task PutAsync(string collection, string key, string json, CancellationToken cancellationToken = default)
        {
            saved.Add((collection, key, json));
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string collection, string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<PluginRecord>> ListAsync(string collection, string? keyPrefix = null, int limit = 100, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
