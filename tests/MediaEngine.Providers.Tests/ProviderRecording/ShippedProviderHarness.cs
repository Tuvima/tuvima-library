using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Adapters;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Providers;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests.ProviderRecording;

/// <summary>
/// Builds the five shipped providers from their real files in <c>config/providers</c>
/// against a recording handler. The conversion steps of WP9 change only the bodies of
/// these methods, never the scenarios or the recorded snapshots.
/// </summary>
internal static class ShippedProviderHarness
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static ProviderConfiguration LoadShipped(string name, string? apiKey = null, string? pin = null)
    {
        var path = Path.Combine(RecordingSnapshot.FindRepoRoot(), "config", "providers", $"{name}.json");
        var config = JsonSerializer.Deserialize<ProviderConfiguration>(File.ReadAllText(path), s_json)
            ?? throw new InvalidOperationException($"Failed to deserialize config: {name}");
        config.HttpClient ??= new HttpClientConfig();
        config.HttpClient.ApiKey = apiKey;
        config.HttpClient.Pin = pin;
        // Fast limits: the recordings check requests, not pacing.
        config.RateLimit = new ProviderRateLimitConfiguration { RequestsPerSecond = 1000, Burst = 1000, MaxConcurrency = 4 };
        return config;
    }

    /// <summary>Saves the shipped file of a provider, with a key, into a test configuration folder.</summary>
    public static void SaveShipped(IConfigurationLoader loader, string name, string? apiKey, string? pin = null) =>
        loader.SaveProvider(LoadShipped(name, apiKey, pin));

    public static ConfigDrivenAdapter ConfigDriven(
        string name, RecordingHandler handler, string? apiKey = "test-key", Action<ProviderConfiguration>? tweak = null)
    {
        var config = LoadShipped(name, apiKey);
        tweak?.Invoke(config);
        return new ConfigDrivenAdapter(
            config,
            new RecordingHttpFactory(handler),
            NullLogger<ConfigDrivenAdapter>.Instance,
            NullProviderHealthMonitor.Instance,
            rateLimiter: new ProviderRateLimiterCoordinator());
    }

    public static ConfigDrivenAdapter Tmdb(RecordingHandler handler, Action<ProviderConfiguration>? tweak = null) =>
        ConfigDriven("tmdb", handler, tweak: tweak);

    public static ConfigDrivenAdapter ComicVine(RecordingHandler handler) => ConfigDriven("comicvine", handler);

    public static ITextTrackProvider Subdl(RecordingHandler handler, string? apiKey = "example-key") =>
        TextTrack("subdl", handler, apiKey);

    public static ITextTrackProvider Lrclib(RecordingHandler handler) => TextTrack("lrclib", handler, null);

    /// <summary>Builds a subtitle or lyrics provider from its shipped file.</summary>
    public static ITextTrackProvider TextTrack(
        string name, RecordingHandler handler, string? apiKey, Action<ProviderConfiguration>? tweak = null)
    {
        var config = LoadShipped(name, apiKey);
        tweak?.Invoke(config);
        return new TextTrackProviderAdapter(
            config,
            new RecordingHttpFactory(handler),
            NullProviderHealthMonitor.Instance,
            NullLogger<TextTrackProviderAdapter>.Instance,
            new ProviderRateLimiterCoordinator(),
            new NoCache());
    }

    /// <summary>TVDB lives in a temp config folder so its client can reload credentials per call.</summary>
    public sealed class TvdbRig : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"tuvima_wp9_tvdb_{Guid.NewGuid():N}");

        public TvdbRig(RecordingHandler handler, string? apiKey = "installation-key", string? pin = null)
        {
            Directory.CreateDirectory(_directory);
            var loader = new ConfigurationDirectoryLoader(_directory);
            loader.SaveProvider(LoadShipped("tvdb", apiKey, pin));
            Provider = CreateProvider(loader, handler);
        }

        public IExternalMetadataProvider Provider { get; }

        private static IExternalMetadataProvider CreateProvider(ConfigurationDirectoryLoader loader, RecordingHandler handler)
        {
            var factory = new RecordingHttpFactory(handler);
            var limiter = new ProviderRateLimiterCoordinator();
            return new ConfigDrivenAdapter(
                loader.LoadProvider("tvdb")!,
                factory,
                NullLogger<ConfigDrivenAdapter>.Instance,
                NullProviderHealthMonitor.Instance,
                rateLimiter: limiter,
                apiClient: new ProviderApiClient(loader, factory, limiter));
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { /* temp folder */ }
        }
    }

    private sealed class NoCache : IProviderResponseCacheRepository
    {
        public Task<CachedResponse?> FindAsync(string key, CancellationToken ct = default) => Task.FromResult<CachedResponse?>(null);
        public Task UpsertAsync(string key, string provider, string query, string json, string? etag, int ttl, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> FindExpiredEtagAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task RefreshExpiryAsync(string key, int ttl, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> PurgeExpiredAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> ClearAllAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<CacheStats> GetStatsAsync(CancellationToken ct = default) => Task.FromResult(new CacheStats(0, 0, null));
    }
}
