using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Models;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Calls the named API endpoints a provider file declares (<c>api</c> and <c>auth</c>), for the
/// adapter's flows and for any other part of the Library that needs one answer from a provider.
/// It knows no provider: addresses, sign-in, argument checks, paging and caching all come from the
/// provider's file, which is read again on every call so that changed credentials apply at once.
/// </summary>
public sealed class ProviderApiClient(
    IConfigurationLoader configuration,
    IHttpClientFactory httpFactory,
    IProviderRateLimiterCoordinator rateLimiter)
{
    private readonly ConcurrentDictionary<string, ProviderSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Builds the argument list of a call: <c>Args(("id", "42"))</c>.</summary>
    public static IReadOnlyDictionary<string, string> Args(params (string Name, string Value)[] arguments) =>
        arguments.ToDictionary(argument => argument.Name, argument => argument.Value, StringComparer.Ordinal);

    /// <summary>True when the provider is enabled and holds the credentials its sign-in needs.</summary>
    public bool IsConfigured(string provider) =>
        configuration.LoadProvider(provider) is { Api: not null } config && ProviderSession.HasCredentials(config);

    /// <summary>The data part of the answer, or <c>null</c> when the provider has no such record (404).</summary>
    public async Task<JsonNode?> GetAsync(
        string provider,
        string endpoint,
        IReadOnlyDictionary<string, string>? arguments = null,
        CancellationToken ct = default)
    {
        var (config, api) = Load(provider);
        var envelope = await GetEnvelopeAsync(config, api, endpoint, arguments, null, ct).ConfigureAwait(false);
        return Data(api, envelope)?.DeepClone();
    }

    /// <summary>Every item of a paged endpoint, read page by page as the endpoint's paging rules say.</summary>
    public async Task<IReadOnlyList<JsonNode>> GetAllPagesAsync(
        string provider,
        string endpoint,
        IReadOnlyDictionary<string, string>? arguments = null,
        CancellationToken ct = default)
    {
        var (config, api) = Load(provider);
        var paging = api.Endpoints.GetValueOrDefault(endpoint)?.Paging
            ?? throw new ArgumentException($"Endpoint '{endpoint}' of '{provider}' is not paged.", nameof(endpoint));
        var items = new List<JsonNode>();
        for (var page = paging.Start; page < paging.Start + paging.MaxPages; page++)
        {
            var envelope = await GetEnvelopeAsync(config, api, endpoint, arguments, page, ct).ConfigureAwait(false);
            if (JsonPathEvaluator.Evaluate(envelope, paging.ItemsPath) is not JsonArray batch || batch.Count == 0)
            {
                break;
            }

            items.AddRange(batch.Where(node => node is not null).Select(node => node!.DeepClone()));
            if (string.IsNullOrWhiteSpace(JsonPathEvaluator.GetStringValue(JsonPathEvaluator.Evaluate(envelope, paging.NextPath))))
            {
                break;
            }
        }

        return items;
    }

    private (ProviderConfiguration Config, ProviderApiConfiguration Api) Load(string provider)
    {
        var config = configuration.LoadProvider(provider)
            ?? throw new InvalidOperationException($"Provider '{provider}' has no configuration file.");
        return (config, config.Api ?? throw new InvalidOperationException($"Provider '{provider}' declares no api block."));
    }

    private static JsonNode? Data(ProviderApiConfiguration api, JsonNode? envelope) =>
        string.IsNullOrEmpty(api.DataPath) ? envelope : JsonPathEvaluator.Evaluate(envelope, api.DataPath);

    private async Task<JsonNode?> GetEnvelopeAsync(
        ProviderConfiguration config,
        ProviderApiConfiguration api,
        string endpointName,
        IReadOnlyDictionary<string, string>? arguments,
        int? page,
        CancellationToken ct)
    {
        var endpoint = api.Endpoints.GetValueOrDefault(endpointName)
            ?? throw new ArgumentException($"Provider '{config.Name}' declares no endpoint '{endpointName}'.", nameof(endpointName));
        var path = ApiEndpointPath.Build(endpointName, endpoint, arguments, page);
        var baseUrl = ResolveBaseUrl(config, api);
        var session = _sessions.GetOrAdd(config.Name, _ => new ProviderSession());

        // The credential is checked before cached answers are served: a removed key must stop access at once.
        var token = config.Auth is null
            ? null
            : await session.EnsureTokenAsync(config, baseUrl, httpFactory, rateLimiter, ct).ConfigureAwait(false);
        var cacheKey = $"{config.Name}:{path}";
        if (session.Cache.TryGetValue(cacheKey, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
        {
            return cached.Value.DeepClone();
        }

        using var client = httpFactory.CreateClient(config.Name);
        var retries = config.Auth?.RetryOnUnauthorized ?? 0;
        for (var attempt = 0; attempt <= retries; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
            if (token is not null)
            {
                Deliver(request, config.Auth!.Deliver, token);
            }

            using var response = await rateLimiter.ExecuteAsync(config.Name, config.RateLimit,
                innerCt => client.SendAsync(request, innerCt), ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt < retries)
            {
                session.Expire();
                token = await session.EnsureTokenAsync(config, baseUrl, httpFactory, rateLimiter, ct).ConfigureAwait(false);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            var envelope = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct).ConfigureAwait(false);
            if (envelope is not null)
            {
                if (session.Cache.Count >= api.CacheMaxEntries)
                {
                    session.Cache.Clear();
                }

                var minutes = endpoint.CacheMinutes ?? api.CacheMinutes;
                session.Cache[cacheKey] = (DateTimeOffset.UtcNow.AddMinutes(minutes), envelope.DeepClone());
            }

            return envelope;
        }

        return null;
    }

    private static void Deliver(HttpRequestMessage request, ProviderTokenDeliveryConfiguration delivery, string token)
    {
        var value = delivery.Format.Replace("{token}", token, StringComparison.Ordinal);
        if (string.Equals(delivery.Header, "Authorization", StringComparison.OrdinalIgnoreCase))
        {
            var space = value.IndexOf(' ', StringComparison.Ordinal);
            request.Headers.Authorization = space < 0
                ? new AuthenticationHeaderValue(value)
                : new AuthenticationHeaderValue(value[..space], value[(space + 1)..]);
            return;
        }

        request.Headers.TryAddWithoutValidation(delivery.Header, value);
    }

    /// <summary>The API address of the provider, when it is HTTPS on the default port, has no credentials in it and is on a listed host.</summary>
    private static string ResolveBaseUrl(ProviderConfiguration config, ProviderApiConfiguration api)
    {
        var value = config.Endpoints.GetValueOrDefault("api")?.TrimEnd('/');
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !api.AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The API address of '{config.Name}' must be HTTPS on one of its allowed hosts ({string.Join(", ", api.AllowedHosts)}).");
        }

        return value!;
    }
}
