using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using MediaEngine.Domain.Configuration;
using MediaEngine.Providers.Models;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Adapters;

public sealed partial class ConfigDrivenAdapter
{
    /// <summary>
    /// Runs the provider's declared <c>follow_ups</c> that apply to this request, in order, each
    /// adding claims from one more lookup. A follow-up that cannot run (missing input, no key)
    /// is skipped; one that fails keeps the claims gathered before it, as its <c>on_error</c> says.
    /// </summary>
    private async Task<IReadOnlyList<ProviderClaim>> RunFollowUpsAsync(
        IReadOnlyList<ProviderClaim> claims,
        JsonNode resultNode,
        ProviderLookupRequest request,
        CancellationToken ct)
    {
        var current = claims;
        foreach (var followUp in _config.FollowUps ?? [])
        {
            if (!followUp.AppliesTo(request.MediaType))
            {
                continue;
            }

            var working = current.ToList();
            try
            {
                await RunLookupAsync(followUp, working, resultNode, resultNode, request, ct).ConfigureAwait(false);
                current = working;
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                && followUp.OnError == FollowUpErrorPolicies.LogAndContinue)
            {
                _logger.LogDebug(ex, "{Provider}: follow-up '{FollowUp}' failed for {MediaType}",
                    Name, followUp.Name, request.MediaType);
            }
        }

        return current;
    }

    private async Task RunLookupAsync(
        LookupFollowUpConfiguration lookup,
        List<ProviderClaim> claims,
        JsonNode parent,
        JsonNode resultNode,
        ProviderLookupRequest request,
        CancellationToken ct)
    {
        var variables = ResolveLookupInputs(lookup, claims, parent, resultNode, request);
        if (variables is null)
        {
            return;
        }

        var url = BuildLookupUrl(lookup, variables, request);
        if (url is null)
        {
            return;
        }

        var response = await FetchLookupAsync(lookup, url, variables, ct).ConfigureAwait(false);
        if (response is null)
        {
            return;
        }

        if (lookup.RequiredPaths.Any(path => JsonPathEvaluator.Evaluate(response, path) is null))
        {
            return;
        }

        var context = new ClaimRuleContext(Name, request.MediaType, variables);
        foreach (var rule in lookup.Rules)
        {
            if (rule.Kind == ClaimRuleKinds.PagedSequence)
            {
                await ApplyPagedSequenceAsync(claims, response, rule, lookup.Add, context, request, ct).ConfigureAwait(false);
                continue;
            }

            if (rule.Kind != ClaimRuleKinds.Lookup)
            {
                ClaimRuleEngine.Apply(claims, response, rule, lookup.Add, context);
                continue;
            }

            try
            {
                await RunLookupAsync(rule.Lookup!, claims, response, resultNode, request, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                && rule.Lookup!.OnError == FollowUpErrorPolicies.LogAndContinue)
            {
                _logger.LogDebug(ex, "{Provider}: follow-up '{FollowUp}' failed", Name, rule.Lookup.Name);
            }
        }
    }

    /// <summary>Returns the input variables, or <c>null</c> when a required one is blank.</summary>
    private Dictionary<string, string>? ResolveLookupInputs(
        LookupFollowUpConfiguration lookup,
        List<ProviderClaim> claims,
        JsonNode parent,
        JsonNode resultNode,
        ProviderLookupRequest request)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (declared, sources) in lookup.Inputs)
        {
            var optional = declared.EndsWith('?');
            var name = optional ? declared[..^1] : declared;
            var value = sources
                .Select(source => ResolveLookupSource(source, claims, parent, resultNode, request))
                .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
            if (string.IsNullOrWhiteSpace(value))
            {
                if (optional)
                {
                    continue;
                }

                return null;
            }

            variables[name] = value;
        }

        return variables;
    }

    private static string? ResolveLookupSource(
        string source,
        List<ProviderClaim> claims,
        JsonNode parent,
        JsonNode resultNode,
        ProviderLookupRequest request)
    {
        var separator = source.IndexOf(':', StringComparison.Ordinal);
        var kind = separator < 0 ? source : source[..separator];
        var path = separator < 0 ? string.Empty : source[(separator + 1)..];
        return kind switch
        {
            LookupSourceKinds.Claim => claims
                .FirstOrDefault(claim => string.Equals(claim.Key, path, StringComparison.OrdinalIgnoreCase))?.Value,
            LookupSourceKinds.Result => JsonPathEvaluator.GetStringValue(JsonPathEvaluator.Evaluate(resultNode, path)),
            LookupSourceKinds.Parent => JsonPathEvaluator.GetStringValue(JsonPathEvaluator.Evaluate(parent, path)),
            LookupSourceKinds.Request => ResolveRequestField(request, path),
            LookupSourceKinds.Hint => request.Hints?.GetValueOrDefault(path),
            _ => null,
        };
    }

    private string? BuildLookupUrl(
        LookupFollowUpConfiguration lookup,
        Dictionary<string, string> variables,
        ProviderLookupRequest request)
    {
        // The provider's own API address wins over the one the caller passed in.
        var baseUrl = _config.Endpoints.GetValueOrDefault("api") ?? ResolveBaseUrl(request);
        if (string.IsNullOrWhiteSpace(baseUrl)
            || (lookup.Url.Contains("{api_key}", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(EffectiveApiKey)))
        {
            return null;
        }

        return ExpandUrlTemplate(lookup.Url, baseUrl, variables)
            .Replace("{locale}", Uri.EscapeDataString(
                $"{request.Language.ToLowerInvariant()}-{request.Country.ToUpperInvariant()}"), StringComparison.Ordinal);
    }

    /// <summary>Fills <c>{base_url}</c>, <c>{api_key}</c> and the named values (URI-escaped) into a URL template.</summary>
    private string ExpandUrlTemplate(string template, string baseUrl, IReadOnlyDictionary<string, string> values)
    {
        var url = template
            .Replace("{base_url}", baseUrl.TrimEnd('/'), StringComparison.Ordinal)
            .Replace("{api_key}", Uri.EscapeDataString(EffectiveApiKey ?? string.Empty), StringComparison.Ordinal);
        foreach (var (name, value) in values)
        {
            url = url.Replace("{" + name + "}", Uri.EscapeDataString(value), StringComparison.Ordinal);
        }

        return url;
    }

    private Task<JsonNode?> FetchLookupAsync(
        LookupFollowUpConfiguration lookup,
        string url,
        Dictionary<string, string> variables,
        CancellationToken ct)
    {
        if (lookup.Cache?.Store != FollowUpCacheStores.Memory)
        {
            return FetchJsonAsync(url, useResponseCache: lookup.Cache is not null, notFoundIsError: false, ct);
        }

        var key = lookup.Cache.Key is null
            ? url
            : ClaimRuleEngine.ExpandVariables(lookup.Cache.Key, variables);
        return FetchRememberedJsonAsync(key, () => FetchJsonAsync(url, useResponseCache: false, notFoundIsError: true, ct));
    }

    /// <summary>One fetch per key for the life of the provider; a failed fetch is forgotten so it can be retried.</summary>
    private static async Task<T?> FetchRememberedAsync<T>(
        ConcurrentDictionary<string, Lazy<Task<T?>>> cache, string key, Func<Task<T?>> fetch)
        where T : class
    {
        var lazy = cache.GetOrAdd(
            key,
            _ => new Lazy<Task<T?>>(fetch, LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        catch
        {
            cache.TryRemove(key, out _);
            throw;
        }
    }

    /// <summary>A remembered JSON answer; every caller gets its own copy, so one cannot change what another reads.</summary>
    private async Task<JsonNode?> FetchRememberedJsonAsync(string key, Func<Task<JsonNode?>> fetch) =>
        (await FetchRememberedAsync(_jsonLookups, key, fetch).ConfigureAwait(false))?.DeepClone();

    private async Task<JsonNode?> FetchJsonAsync(
        string url, bool useResponseCache, bool notFoundIsError, CancellationToken ct)
    {
        var cacheKey = BuildCacheKey(url);
        var cacheTtlHours = _config.CacheTtlHours ?? 168;

        if (useResponseCache && _responseCache is not null)
        {
            var cached = await _responseCache.FindAsync(cacheKey, ct).ConfigureAwait(false);
            if (cached is not null)
            {
                return JsonNode.Parse(cached.ResponseJson);
            }
        }

        using var client = _httpFactory.CreateClient(_config.Name);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyConfiguredAuthorization(request);
        using var response = await _rateLimiter.ExecuteAsync(
            Name, _config.RateLimit, token => client.SendAsync(request, token), ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound && !notFoundIsError)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (useResponseCache && _responseCache is not null && !string.IsNullOrWhiteSpace(responseBody))
        {
            var etag = response.Headers.ETag?.Tag?.Trim('"');
            await _responseCache.UpsertAsync(
                cacheKey,
                _providerId.ToString(),
                ComputeSha256(url),
                responseBody,
                etag,
                cacheTtlHours,
                ct).ConfigureAwait(false);
        }

        return string.IsNullOrWhiteSpace(responseBody) ? null : JsonNode.Parse(responseBody);
    }

    /// <summary>Adds the bearer or basic header the provider's <c>http_client</c> asks for.</summary>
    private void ApplyConfiguredAuthorization(HttpRequestMessage request)
    {
        if (_config.HttpClient is { ApiKeyDelivery: "bearer" } && !string.IsNullOrWhiteSpace(EffectiveApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", EffectiveApiKey);
        }
        else if (_config.HttpClient is { ApiKeyDelivery: "basic" }
            && !string.IsNullOrWhiteSpace(_config.HttpClient.Username)
            && !string.IsNullOrWhiteSpace(_config.HttpClient.Password))
        {
            var credentials = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                $"{_config.HttpClient.Username}:{_config.HttpClient.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }
    }
}
