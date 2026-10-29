using System.Collections;
using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Adapters;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Services;

namespace MediaEngine.Api.Services.Plugins.Sources;

/// <summary>Brings the running provider list in step with the provider files.</summary>
public interface IMetadataSourceRefresher
{
    void Refresh();
}

/// <summary>
/// The metadata providers the Engine is running right now. The sources that ship with the Library are built when the
/// Engine starts; the sources of switched-on JSON plugins are built here and join or leave the list while the Engine runs,
/// so a newly added source works without a restart. Everything that takes the list of providers reads this object, and a
/// read always sees the list as it stands now, never a stale copy.
/// </summary>
public sealed class LiveMetadataProviders : IReadOnlyList<IExternalMetadataProvider>, IMetadataSourceRefresher, IDisposable
{
    /// <summary>Key of the providers built at startup (the ones that ship with the Library).</summary>
    public const string StartupKey = "startup-metadata-providers";

    private static readonly JsonSerializerOptions Fingerprint = new();

    private readonly IServiceProvider _services;
    private readonly IConfigurationLoader _loader;
    private readonly ILogger<LiveMetadataProviders> _logger;
    private readonly ProviderStartupSnapshot? _snapshot;
    private readonly object _gate = new();
    private readonly Lazy<IExternalMetadataProvider[]> _startup;
    private volatile IExternalMetadataProvider[]? _current;
    private Dictionary<string, string> _fingerprints = new(StringComparer.OrdinalIgnoreCase);
    private ServiceProvider? _clients;
    private bool _disposed;

    public LiveMetadataProviders(
        IServiceProvider services,
        IConfigurationLoader loader,
        ILogger<LiveMetadataProviders> logger,
        ProviderStartupSnapshot? snapshot = null)
    {
        _services = services;
        _loader = loader;
        _logger = logger;
        _snapshot = snapshot;
        _startup = new Lazy<IExternalMetadataProvider[]>(
            () => [.. _services.GetKeyedServices<IExternalMetadataProvider>(StartupKey)],
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public int Count => Current.Length;

    public IExternalMetadataProvider this[int index] => Current[index];

    private IExternalMetadataProvider[] Current
    {
        get
        {
            var current = _current;
            if (current is not null)
            {
                return current;
            }

            Refresh();
            return _current ?? _startup.Value;
        }
    }

    public IEnumerator<IExternalMetadataProvider> GetEnumerator() => ((IEnumerable<IExternalMetadataProvider>)Current).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Reads the provider files and builds, replaces or drops the plugin sources whose file is new, changed or gone.
    /// Never throws: a source that cannot be built is left out and logged, and the rest keep working.
    /// </summary>
    public void Refresh()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var wanted = LoadPluginSources();
            var fingerprints = wanted.ToDictionary(
                config => config.Name,
                config => JsonSerializer.Serialize(config, Fingerprint),
                StringComparer.OrdinalIgnoreCase);

            if (_current is not null && FingerprintsMatch(fingerprints, _fingerprints))
            {
                return;
            }

            var previousClients = _clients;
            var built = new List<IExternalMetadataProvider>();
            ServiceProvider? clients = null;
            try
            {
                clients = BuildClients(wanted);
                var factory = clients.GetRequiredService<IHttpClientFactory>();
                foreach (var config in wanted)
                {
                    try
                    {
                        built.Add(new ConfigDrivenAdapter(
                            config,
                            factory,
                            _services.GetRequiredService<ILogger<ConfigDrivenAdapter>>(),
                            _services.GetRequiredService<IProviderHealthMonitor>(),
                            _services.GetService<IProviderResponseCacheRepository>(),
                            _services.GetService<IProviderRateLimiterCoordinator>(),
                            _services.GetService<ProviderApiClient>()));
                    }
                    catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
                    {
                        _logger.LogWarning(ex, "The metadata source {Source} could not be started and was left out", config.Name);
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                _logger.LogWarning(ex, "The plugin metadata sources could not be brought up to date; the previous list stays in use");
                clients?.Dispose();
                _current ??= [.. _startup.Value];
                return;
            }

            _clients = clients;
            _fingerprints = fingerprints;
            _current = [.. _startup.Value, .. built];
            _snapshot?.Update(built.Where(provider => IsEnabled(provider.Name)).Select(provider => provider.Name));

            if (previousClients is not null)
            {
                // Requests that started on the old clients may still be running; give them time to finish.
                _ = Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ => previousClients.Dispose(), TaskScheduler.Default);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _clients?.Dispose();
            _clients = null;
        }
    }

    private bool IsEnabled(string name) =>
        _loader.LoadAllProviders().Any(config => string.Equals(config.Name, name, StringComparison.OrdinalIgnoreCase) && config.Enabled);

    private List<ProviderConfiguration> LoadPluginSources() =>
        IsPluginSource(_loader.LoadAllProviders()).OrderBy(config => config.RegistrationOrder).ThenBy(config => config.Name, StringComparer.Ordinal).ToList();

    /// <summary>True for the config-driven sources supplied by a plugin, the ones that join the running list.</summary>
    public static IEnumerable<ProviderConfiguration> IsPluginSource(IEnumerable<ProviderConfiguration> configurations) =>
        configurations.Where(config => config.PluginId is not null
            && string.Equals(config.AdapterType, "config_driven", StringComparison.OrdinalIgnoreCase));

    private static bool FingerprintsMatch(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) && other == pair.Value);

    private static ServiceProvider BuildClients(IEnumerable<ProviderConfiguration> configurations)
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddHttpClient();
        foreach (var config in configurations)
        {
            DependencyInjection.TuvimaHttpClientRegistration.AddTuvimaHttpClient(
                collection,
                config.Name,
                TimeSpan.FromSeconds(config.HttpClient?.TimeoutSeconds ?? 10),
                config.HttpClient?.UserAgent);
        }

        return collection.BuildServiceProvider();
    }
}
