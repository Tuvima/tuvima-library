using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Jobs;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Adapters;

/// <summary>
/// Universal config-driven adapter that reads its behaviour entirely from a
/// <see cref="ProviderConfiguration"/> loaded from <c>config/providers/{name}.json</c>.
///
/// <para>
/// One instance is created per config file with <c>adapter_type: "config_driven"</c>.
/// The adapter evaluates search strategies in priority order, extracts fields via
/// JSON path expressions, and applies named transforms — all driven by data in the
/// config file. No subclass required.
/// </para>
///
/// <para>
/// Adding a new REST+JSON provider is a zero-code operation: drop a config file
/// in <c>config/providers/</c>, restart, done.
/// </para>
/// </summary>
public sealed partial class ConfigDrivenAdapter : IExternalMetadataProvider, IProviderCredentialConsumer
{
    private readonly ProviderConfiguration _config;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ConfigDrivenAdapter> _logger;
    private readonly IProviderResponseCacheRepository? _responseCache;
    private readonly IProviderHealthMonitor _healthMonitor;
    private readonly IProviderRateLimiterCoordinator _rateLimiter;
    private readonly ConcurrentDictionary<string, Lazy<Task<ComicVineVolumeFacts?>>> _comicVineVolumeFacts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<ProviderSequenceManifest?>>> _comicVineSequenceManifests =
        new(StringComparer.OrdinalIgnoreCase);

    // Parsed once at construction.
    private readonly Guid _providerId;
    private readonly HashSet<MediaType> _mediaTypes;
    private readonly HashSet<EntityType> _entityTypes;

    public ConfigDrivenAdapter(
        ProviderConfiguration config,
        IHttpClientFactory httpFactory,
        ILogger<ConfigDrivenAdapter> logger,
        IProviderHealthMonitor healthMonitor,
        IProviderResponseCacheRepository? responseCache = null,
        IProviderRateLimiterCoordinator? rateLimiter = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(httpFactory);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(healthMonitor);

        _config = config;
        _httpFactory = httpFactory;
        _responseCache = responseCache;
        _logger = logger;
        _healthMonitor = healthMonitor;
        _rateLimiter = rateLimiter ?? new ProviderRateLimiterCoordinator();

        _providerId = !string.IsNullOrEmpty(config.ProviderId)
            ? Guid.Parse(config.ProviderId)
            : Guid.NewGuid();

        // Parse can_handle filters into enum sets for fast lookup.
        _mediaTypes = ParseEnumSet<MediaType>(config.CanHandle?.MediaTypes);
        _entityTypes = ParseEnumSet<EntityType>(config.CanHandle?.EntityTypes);
    }

    // -- IExternalMetadataProvider ---------------------------------------------

    public string Name => _config.Name;

    public ProviderDomain Domain => _config.Domain;

    public IReadOnlyList<string> CapabilityTags => _config.CapabilityTags;

    public Guid ProviderId => _providerId;

    /// <inheritdoc />
    public void ApplyCredentials(IReadOnlyDictionary<string, string?> credentials)
    {
        _config.HttpClient ??= new HttpClientConfig();
        _config.HttpClient.ApiKey = credentials.GetValueOrDefault("api_key");
        _config.HttpClient.ApiKeyOverride = credentials.GetValueOrDefault("api_key_override");
        _config.HttpClient.ClientKey = credentials.GetValueOrDefault("client_key");
        _config.HttpClient.AccessToken = credentials.GetValueOrDefault("access_token");
        _config.HttpClient.Username = credentials.GetValueOrDefault("username");
        _config.HttpClient.Password = credentials.GetValueOrDefault("password");
    }

    /// <summary>Returns the configured key, preferring an administrator override.</summary>
    private string? EffectiveApiKey => !string.IsNullOrWhiteSpace(_config.HttpClient?.ApiKeyOverride)
        ? _config.HttpClient.ApiKeyOverride
        : _config.HttpClient?.ApiKey;

    public bool CanHandle(MediaType mediaType) =>
        _mediaTypes.Count == 0 || mediaType == MediaType.Unknown || _mediaTypes.Contains(mediaType);

    public bool CanHandle(EntityType entityType) =>
        _entityTypes.Count == 0 || _entityTypes.Contains(entityType);

    public async Task<IReadOnlyList<ProviderClaim>> FetchAsync(
        ProviderLookupRequest request,
        CancellationToken ct = default)
    {
        if (!CanHandle(request.MediaType) || !CanHandle(request.EntityType))
        {
            return [];
        }

        // A provider known to be down is not a "no match": surface it so callers keep the work
        // queued as "Waiting for provider" instead of classifying it as permanently unmatched.
        if (_healthMonitor.IsDown(Name))
        {
            _logger.LogDebug("{Provider} is known to be down — waiting for provider", Name);
            throw new ProviderUnavailableException("Waiting for provider");
        }

        // Short-circuit when an API key is required but not configured.
        if (_config.RequiresApiKey
            && string.IsNullOrWhiteSpace(EffectiveApiKey)
            && (string.IsNullOrWhiteSpace(_config.HttpClient?.Username)
                || string.IsNullOrWhiteSpace(_config.HttpClient?.Password)))
        {
            _logger.LogWarning(
                "{Provider}: requires an API key but none is configured — skipping. "
                + "Set 'api_key' in the provider's http_client config.",
                Name);
            return [];
        }

        var strategies = FilterStrategiesByMediaType(
            _config.SearchStrategies, request.MediaType)
            ?.OrderBy(s => s.Priority)
            .ToList();

        if (strategies is null or { Count: 0 })
        {
            _logger.LogDebug("{Provider} has no search strategies configured", Name);
            return [];
        }

        // Resolve the effective language based on the provider's language strategy.
        var effectiveLang = ResolveEffectiveLanguage(request);
        var effectiveRequest = string.Equals(effectiveLang, request.Language, StringComparison.OrdinalIgnoreCase)
            ? request
            : CloneRequestWithLanguage(request, effectiveLang);

        var outcome = new FetchOutcome();

        foreach (var strategy in strategies)
        {
            // Check required fields are present.
            if (!AllRequiredFieldsPresent(strategy, effectiveRequest))
            {
                _logger.LogDebug(
                    "{Provider}/{Strategy}: skipped — missing required fields",
                    Name, strategy.Name);
                continue;
            }

            try
            {
                var claims = await ExecuteStrategyAsync(strategy, effectiveRequest, ct)
                    .ConfigureAwait(false);
                outcome.Responded = true;

                if (claims.Count > 0)
                {
                    _logger.LogDebug(
                        "{Provider}/{Strategy}: returned {Count} claims",
                        Name, strategy.Name, claims.Count);
                    await _healthMonitor.ReportSuccessAsync(Name, ct);
                    return claims;
                }

                _logger.LogInformation(
                    "{Provider}/{Strategy}: zero results from API, trying next strategy",
                    Name, strategy.Name);
                // Provider responded but had no match — still healthy.
                await _healthMonitor.ReportSuccessAsync(Name, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                await RecordTransportFailureAsync(ex, strategy.Name, "failed, trying next strategy", outcome, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
            {
                // The provider answered; an unreadable body is not an availability problem.
                outcome.Responded = true;
                _logger.LogWarning(ex,
                    "{Provider}/{Strategy}: parse error, trying next strategy",
                    Name, strategy.Name);
            }
        }

        // "Both" strategy: if the metadata-language pass found nothing, retry in English.
        if (_config.LanguageStrategy == LanguageStrategy.Both
            && !string.Equals(effectiveLang, "en", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("{Provider}: 'both' strategy — retrying in English", Name);
            var englishRequest = CloneRequestWithLanguage(request, "en");

            foreach (var strategy in strategies)
            {
                if (!AllRequiredFieldsPresent(strategy, englishRequest))
                {
                    continue;
                }

                try
                {
                    var claims = await ExecuteStrategyAsync(strategy, englishRequest, ct)
                        .ConfigureAwait(false);
                    outcome.Responded = true;

                    if (claims.Count > 0)
                    {
                        // Tag claims with source language since they came from English fallback.
                        await _healthMonitor.ReportSuccessAsync(Name, ct);
                        return claims.Select(c => c with { SourceLanguage = "en" }).ToList();
                    }

                    // Provider responded — still healthy even with no match.
                    await _healthMonitor.ReportSuccessAsync(Name, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
                {
                    await RecordTransportFailureAsync(ex, strategy.Name, "English fallback failed", outcome, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
                {
                    outcome.Responded = true;
                    _logger.LogWarning(ex,
                        "{Provider}/{Strategy}: English fallback parse error",
                        Name, strategy.Name);
                }
            }
        }

        foreach (var pass in BuildLookupPasses(request).Where(pass =>
                     !string.Equals(pass.Request.Country, request.Country, StringComparison.OrdinalIgnoreCase)))
        {
            var claims = await ExecuteFetchPassAsync(strategies, pass, outcome, ct).ConfigureAwait(false);
            if (claims.Count > 0)
            {
                return claims;
            }
        }

        // Nothing produced claims. If no strategy got any answer and at least one failed in transit,
        // this is an availability/request problem, not a genuine "no match".
        if (!outcome.Responded && outcome.Failures.Count > 0)
        {
            throw outcome.Failures.All(failure => failure is ProviderRequestRejectedException)
                ? outcome.Failures[0]
                : outcome.Failures.OfType<ProviderUnavailableException>().First();
        }

        return [];
    }

    /// <summary>Tracks what the provider answered while <see cref="FetchAsync"/> tried its strategies.</summary>
    private sealed class FetchOutcome
    {
        /// <summary>True once any strategy received a readable response (even with zero results).</summary>
        public bool Responded { get; set; }

        /// <summary>
        /// Transport-level failures: <see cref="ProviderRequestRejectedException"/> for non-429 4xx
        /// responses, <see cref="ProviderUnavailableException"/> for everything else.
        /// </summary>
        public List<HttpRequestException> Failures { get; } = [];
    }

    /// <summary>
    /// Logs a failed provider request and records how it should be classified. Only provider-side
    /// failures (timeouts, connection errors, 5xx, 429) count towards the provider being Down;
    /// a rejected request (non-429 4xx) means our request was wrong, not that the provider is.
    /// </summary>
    private async Task RecordTransportFailureAsync(
        Exception ex,
        string strategyName,
        string context,
        FetchOutcome outcome,
        CancellationToken ct)
    {
        if (ex is HttpRequestException { StatusCode: { } status }
            && (int)status is >= 400 and < 500
            && status != System.Net.HttpStatusCode.TooManyRequests)
        {
            _logger.LogWarning(ex,
                "{Provider}/{Strategy}: {Context} — request rejected (HTTP {StatusCode})",
                Name, strategyName, context, (int)status);
            outcome.Failures.Add(new ProviderRequestRejectedException(
                $"{Name} rejected the request (HTTP {(int)status})", status, ex));
            return;
        }

        _logger.LogWarning(ex, "{Provider}/{Strategy}: {Context}", Name, strategyName, context);
        await _healthMonitor.ReportFailureAsync(Name, ex.Message, ct).ConfigureAwait(false);
        outcome.Failures.Add(new ProviderUnavailableException(
            $"{Name} is unavailable: {ex.Message}", ex, (ex as HttpRequestException)?.StatusCode));
    }

    /// <summary>
    /// Searches the provider and returns up to <paramref name="limit"/> result candidates,
    /// each with enough context for the user to visually identify a match (title, description,
    /// year, thumbnail, provider item ID).
    ///
    /// Reuses the same URL building and HTTP infrastructure as <see cref="FetchAsync"/>,
    /// but iterates the results array instead of picking a single result.
    /// </summary>
    public async Task<IReadOnlyList<SearchResultItem>> SearchAsync(
        ProviderLookupRequest request,
        int limit = 25,
        CancellationToken ct = default)
    {
        if (!CanHandle(request.MediaType) || !CanHandle(request.EntityType))
        {
            return [];
        }

        // Short-circuit when an API key is required but not configured.
        if (_config.RequiresApiKey
            && string.IsNullOrWhiteSpace(EffectiveApiKey)
            && (string.IsNullOrWhiteSpace(_config.HttpClient?.Username)
                || string.IsNullOrWhiteSpace(_config.HttpClient?.Password)))
        {
            _logger.LogWarning(
                "{Provider}: requires an API key but none is configured — skipping search.",
                Name);
            return [];
        }

        var strategies = FilterStrategiesByMediaType(
            _config.SearchStrategies, request.MediaType)
            ?.OrderBy(s => s.Priority)
            .ToList();

        if (strategies is null or { Count: 0 })
        {
            return [];
        }

        // Resolve the effective language based on the provider's language strategy.
        var effectiveLang = ResolveEffectiveLanguage(request);
        var effectiveRequest = string.Equals(effectiveLang, request.Language, StringComparison.OrdinalIgnoreCase)
            ? request
            : CloneRequestWithLanguage(request, effectiveLang);

        // Use the lesser of caller limit, strategy max_results, and a hard cap of 50.
        var searchFailures = new List<Exception>();

        foreach (var strategy in strategies)
        {
            if (!AllRequiredFieldsPresent(strategy, effectiveRequest))
            {
                continue;
            }

            // Strategies without a results_path return a single object — not useful
            // for multi-result search. Skip to the next strategy.
            if (string.IsNullOrEmpty(strategy.ResultsPath))
            {
                continue;
            }

            var effectiveLimit = Math.Clamp(limit, 1, 50);
            // Per-strategy cap.
            if (strategy.MaxResults > 0)
            {
                effectiveLimit = Math.Min(effectiveLimit, strategy.MaxResults);
            }

            try
            {
                var results = await ExecuteSearchStrategyAsync(strategy, effectiveRequest, effectiveLimit, ct)
                    .ConfigureAwait(false);

                if (results.Count > 0)
                {
                    return results;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException)
            {
                searchFailures.Add(ex);
                _logger.LogWarning(ex,
                    "{Provider}/{Strategy}: search failed, trying next strategy",
                    Name, strategy.Name);
            }
        }

        // "Both" strategy: if the metadata-language pass found nothing, retry in English.
        if (_config.LanguageStrategy == LanguageStrategy.Both
            && !string.Equals(effectiveLang, "en", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("{Provider}: 'both' strategy — retrying search in English", Name);
            var englishRequest = CloneRequestWithLanguage(request, "en");

            foreach (var strategy in strategies)
            {
                if (!AllRequiredFieldsPresent(strategy, englishRequest))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(strategy.ResultsPath))
                {
                    continue;
                }

                var strategyLimit = limit;
                if (strategy.MaxResults > 0)
                {
                    strategyLimit = Math.Min(strategyLimit, strategy.MaxResults);
                }

                try
                {
                    var results = await ExecuteSearchStrategyAsync(strategy, englishRequest, strategyLimit, ct)
                        .ConfigureAwait(false);

                    if (results.Count > 0)
                    {
                        return results;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException)
                {
                    searchFailures.Add(ex);
                    _logger.LogWarning(ex,
                            "{Provider}/{Strategy}: English fallback search failed",
                            Name, strategy.Name);
                }
            }
        }

        foreach (var pass in BuildLookupPasses(request).Where(pass =>
                     !string.Equals(pass.Request.Country, request.Country, StringComparison.OrdinalIgnoreCase)))
        {
            var results = await ExecuteSearchPassAsync(strategies, pass, limit, ct, searchFailures).ConfigureAwait(false);
            if (results.Count > 0)
            {
                return results;
            }
        }

        if (searchFailures.Count > 0)
        {
            throw new AggregateException("Retail provider search could not complete.", searchFailures);
        }
        return [];
    }

    /// <summary>
    /// Executes a search strategy and extracts multiple result items from the response array.
    /// </summary>
}
