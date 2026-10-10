using System.Net;
using System.Text;
using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Jobs;
using MediaEngine.Providers.Adapters;
using MediaEngine.Providers.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// A provider that cannot answer must never look like "no match": the config-driven adapter
/// surfaces outages and rejected requests as typed exceptions, and only provider-side failures
/// count towards the provider being marked Down.
/// </summary>
public sealed class ProviderFailureClassificationTests
{
    [Fact]
    public async Task FetchAsync_WhenProviderIsDown_ThrowsProviderUnavailable()
    {
        var health = new RecordingHealthMonitor { Down = true };
        var handler = new SequenceHandler(_ => Json("{\"results\":[]}"));
        var adapter = CreateAdapter(handler, health);

        var ex = await Assert.ThrowsAsync<ProviderUnavailableException>(() => adapter.FetchAsync(Request()));

        Assert.Contains("Waiting for provider", ex.Message);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task FetchAsync_OnProviderSideHttpFailure_ThrowsUnavailableAndReportsFailure(HttpStatusCode status)
    {
        var health = new RecordingHealthMonitor();
        var adapter = CreateAdapter(new SequenceHandler(_ => new HttpResponseMessage(status)), health);

        await Assert.ThrowsAsync<ProviderUnavailableException>(() => adapter.FetchAsync(Request()));

        Assert.NotEmpty(health.Failures);
    }

    [Fact]
    public async Task FetchAsync_OnTimeout_ThrowsUnavailableAndReportsFailure()
    {
        var health = new RecordingHealthMonitor();
        var adapter = CreateAdapter(
            new SequenceHandler(_ => throw new TaskCanceledException("Simulated HTTP timeout.")), health);

        await Assert.ThrowsAsync<ProviderUnavailableException>(() => adapter.FetchAsync(Request()));

        Assert.NotEmpty(health.Failures);
    }

    [Fact]
    public async Task FetchAsync_OnRejectedRequest_ThrowsRejectedWithoutReportingHealthFailure()
    {
        var health = new RecordingHealthMonitor();
        var adapter = CreateAdapter(new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)), health);

        var ex = await Assert.ThrowsAsync<ProviderRequestRejectedException>(() => adapter.FetchAsync(Request()));

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Empty(health.Failures);
    }

    [Fact]
    public async Task FetchAsync_WhenRejectedAndUnavailableAreMixed_ThrowsUnavailable()
    {
        var calls = 0;
        var adapter = CreateAdapter(
            new SequenceHandler(_ => new HttpResponseMessage(
                Interlocked.Increment(ref calls) == 1 ? HttpStatusCode.BadRequest : HttpStatusCode.ServiceUnavailable)),
            new RecordingHealthMonitor());

        await Assert.ThrowsAsync<ProviderUnavailableException>(() => adapter.FetchAsync(Request()));
    }

    [Fact]
    public async Task FetchAsync_OnSuccessfulResponseWithZeroResults_ReturnsEmptyAsGenuineNoMatch()
    {
        var health = new RecordingHealthMonitor();
        var adapter = CreateAdapter(new SequenceHandler(_ => Json("{\"resultCount\":0,\"results\":[]}")), health);

        var claims = await adapter.FetchAsync(Request());

        Assert.Empty(claims);
        Assert.Empty(health.Failures);
    }

    [Fact]
    public async Task FetchAsync_WhenAnotherStrategyGetsAnAnswer_DoesNotThrowForAnEarlierFailure()
    {
        var calls = 0;
        var adapter = CreateAdapter(
            new SequenceHandler(_ => Interlocked.Increment(ref calls) == 1
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                : Json("{\"resultCount\":0,\"results\":[]}")),
            new RecordingHealthMonitor());

        var claims = await adapter.FetchAsync(Request());

        Assert.Empty(claims);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ProviderLookupRequest Request() => new()
    {
        EntityId = Guid.NewGuid(),
        EntityType = EntityType.MediaAsset,
        MediaType = MediaType.Books,
        Title = "Dune",
        Author = "Frank Herbert",
        BaseUrl = "https://itunes.apple.com",
    };

    private static ConfigDrivenAdapter CreateAdapter(SequenceHandler handler, IProviderHealthMonitor health)
    {
        var config = LoadConfig("apple_api");
        var services = new ServiceCollection();
        services.AddHttpClient(config.Name).ConfigurePrimaryHttpMessageHandler(() => handler);
        var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        return new ConfigDrivenAdapter(config, factory, NullLogger<ConfigDrivenAdapter>.Instance, health);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static ProviderConfiguration LoadConfig(string providerName)
    {
        var directory = Path.GetDirectoryName(typeof(ProviderFailureClassificationTests).Assembly.Location);
        while (directory is not null && !File.Exists(Path.Combine(directory, "MediaEngine.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        var path = Path.Combine(
            directory ?? throw new InvalidOperationException("Could not find repository root."),
            "config", "providers", $"{providerName}.json");
        return JsonSerializer.Deserialize<ProviderConfiguration>(
                   File.ReadAllText(path),
                   new JsonSerializerOptions { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip })
               ?? throw new InvalidOperationException($"Failed to deserialize {providerName}.");
    }

    private sealed class SequenceHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class RecordingHealthMonitor : IProviderHealthMonitor
    {
        public bool Down { get; init; }
        public List<string> Failures { get; } = [];

        public Task ReportSuccessAsync(string providerId, CancellationToken ct = default) => Task.CompletedTask;

        public Task ReportFailureAsync(string providerId, string reason, CancellationToken ct = default)
        {
            Failures.Add(reason);
            return Task.CompletedTask;
        }

        public bool IsDown(string providerId) => Down;

        public ProviderHealthStatus GetStatus(string providerId) =>
            Down ? ProviderHealthStatus.Down : ProviderHealthStatus.Healthy;
    }
}
