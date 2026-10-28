using System.Net;
using System.Text.Json;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.Plugins.Sources;
using MediaEngine.Domain.Configuration;
using MediaEngine.Plugins;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Check now (Plugin architecture spec G13): a plugin's own health check, or one reach-out to a metadata source, reported in plain words
/// without changing anything.
/// </summary>
public sealed class PluginCheckNowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-plugin-checknow", Guid.NewGuid().ToString("N"));
    private readonly ConfigurationDirectoryLoader _loader;

    public PluginCheckNowTests()
    {
        var libraryRoot = Path.Combine(_root, "library");
        Directory.CreateDirectory(libraryRoot);
        _loader = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        _loader.SaveCore(new CoreConfiguration { LibraryRoot = libraryRoot });
    }

    public void Dispose()
    {
        _loader.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder another process still holds is left for the OS to clean up.
        }
    }

    [Fact]
    public async Task APluginThatIsSwitchedOff_CannotBeChecked_AndSaysSo()
    {
        var (service, _) = Build(new HealthPlugin("a.tool", "healthy"));

        var result = await service.CheckAsync("a.tool", default);

        Assert.NotNull(result);
        Assert.Equal("cannot_check", result.Result);
        Assert.Contains("switched off", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHealthyPlugin_IsWorking()
    {
        var (service, catalog) = Build(new HealthPlugin("a.tool", "healthy"));
        catalog.SetEnabled("a.tool", true);

        var result = await service.CheckAsync("a.tool", default);

        Assert.Equal("working", result!.Result);
        Assert.All(result.Steps, step => Assert.True(step.Ok));
    }

    [Fact]
    public async Task ADegradedPlugin_NeedsAttention_WithItsOwnMessage()
    {
        var (service, catalog) = Build(new HealthPlugin("a.tool", "degraded", "ffmpeg was not found."));
        catalog.SetEnabled("a.tool", true);

        var result = await service.CheckAsync("a.tool", default);

        Assert.Equal("needs_attention", result!.Result);
        Assert.Contains("ffmpeg was not found.", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APluginThatThrowsInItsCheck_NeedsAttention_WithoutLeakingTheError()
    {
        var (service, catalog) = Build(new HealthPlugin("a.tool", "throw", "secret-detail-xyz"));
        catalog.SetEnabled("a.tool", true);

        var result = await service.CheckAsync("a.tool", default);

        Assert.Equal("needs_attention", result!.Result);
        Assert.DoesNotContain("secret-detail-xyz", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownId_IsNothing()
    {
        var (service, _) = Build();

        Assert.Null(await service.CheckAsync("nope", default));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "working")]
    [InlineData(HttpStatusCode.NotFound, "working")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "needs_attention")]
    [InlineData(HttpStatusCode.TooManyRequests, "needs_attention")]
    public async Task AMetadataSource_IsReachedOnce_AndAnyAnswerBelow500IsUp(HttpStatusCode answer, string expected)
    {
        _loader.SaveProvider(new ProviderConfiguration { Name = "check_source", ProviderId = Guid.NewGuid().ToString(), Enabled = true, Endpoints = new() { ["api"] = "https://api.example.com/v1" } });
        var factory = new FixedFactory(answer);
        var (service, _) = Build(factory: factory);

        var result = await service.CheckAsync("check_source", default);

        Assert.Equal(expected, result!.Result);
        Assert.Equal(1, factory.Requests);
    }

    [Fact]
    public async Task AMetadataSourceThatCannotBeReached_NeedsAttention_InPlainWords()
    {
        _loader.SaveProvider(new ProviderConfiguration { Name = "check_source", ProviderId = Guid.NewGuid().ToString(), Enabled = true, Endpoints = new() { ["api"] = "https://api.example.com/v1" } });
        var (service, _) = Build(factory: new FixedFactory(null));

        var result = await service.CheckAsync("check_source", default);

        Assert.Equal("needs_attention", result!.Result);
        Assert.Contains("could not be reached", result.Summary, StringComparison.Ordinal);
    }

    private (PluginCheckNowService Service, PluginCatalog Catalog) Build(ITuvimaPlugin? plugin = null, FixedFactory? factory = null)
    {
        var catalog = new PluginCatalog(
            plugin is null ? [] : [plugin],
            new PluginSettingsService(_loader),
            _loader,
            AllowAllPluginApprovals.Instance,
            NullLogger<PluginCatalog>.Instance);
        var service = new PluginCheckNowService(
            catalog,
            new ProviderCapabilityCatalogue(_loader),
            _loader,
            new NoContextFactory(),
            factory ?? new FixedFactory(HttpStatusCode.OK),
            TimeProvider.System,
            NullLogger<PluginCheckNowService>.Instance);
        return (service, catalog);
    }

    private sealed class HealthPlugin(string id, string status, string? message = null) : ITuvimaPlugin
    {
        public PluginManifest Manifest { get; } = new() { Id = id, Name = id, Version = "1.0.0" };

        public IReadOnlyList<IPluginCapability> CreateCapabilities() => [new Check(status, message)];

        private sealed class Check(string status, string? message) : IPluginHealthCheck
        {
            public string Kind => PluginCapabilityKinds.HealthCheckKind;

            public Task<PluginHealthResult> GetHealthAsync(IPluginExecutionContext context, CancellationToken cancellationToken = default) =>
                status == "throw"
                    ? throw new InvalidOperationException(message)
                    : Task.FromResult(new PluginHealthResult { Status = status, Message = message });
        }
    }

    private sealed class NoContextFactory : IPluginExecutionContextFactory
    {
        public IPluginExecutionContext Create(string pluginId, string purpose) => new NoContext(pluginId);

        public IPluginExecutionContext CreateForMedia(string pluginId, string purpose, PluginMediaAssetContext asset) => new NoContext(pluginId);
    }

    private sealed class NoContext(string pluginId) : IPluginExecutionContext
    {
        public string PluginId => pluginId;

        public IReadOnlyDictionary<string, JsonElement> Settings => new Dictionary<string, JsonElement>();

        public IPluginMediaAccess Media => throw new NotSupportedException();

        public IPluginHttpClient Http => throw new NotSupportedException();

        public IPluginStorage Storage => throw new NotSupportedException();

        public IPluginToolRuntime Tools => throw new NotSupportedException();

        public IPluginAiClient Ai => throw new NotSupportedException();

        public IPluginSecrets Secrets => throw new NotSupportedException();

        public IPluginRecordStore Records => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Answers every request with the given status, or fails to connect when the status is null.</summary>
    private sealed class FixedFactory(HttpStatusCode? answer) : IHttpClientFactory
    {
        public int Requests { get; private set; }

        public HttpClient CreateClient(string name) => new(new Handler(this, answer));

        private sealed class Handler(FixedFactory owner, HttpStatusCode? answer) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Requests++;
                return answer is { } status
                    ? Task.FromResult(new HttpResponseMessage(status))
                    : throw new HttpRequestException("no route");
            }
        }
    }
}
