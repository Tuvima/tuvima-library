using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MediaEngine.Domain.Configuration;
using MediaEngine.Providers.Models;

namespace MediaEngine.Providers.Services;

/// <summary>
/// What the Library remembers about one provider's API between calls: the sign-in token and the
/// answers it has already received. Credentials are read from the provider's configuration on every
/// call (never kept here), so removing a key stops access at once, cached answers included.
/// </summary>
internal sealed partial class ProviderSession
{
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private string? _token;
    private string? _signature;
    private DateTimeOffset _expiresAt;

    [GeneratedRegex(@"\{([a-z_]+)(\?)?\}")]
    private static partial Regex Credential();

    public ConcurrentDictionary<string, (DateTimeOffset Expires, JsonNode Value)> Cache { get; } = new();

    /// <summary>The credentials a template can name, taken from the provider's configuration.</summary>
    private static Dictionary<string, string?> Credentials(ProviderConfiguration provider)
    {
        var http = provider.HttpClient;
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["api_key"] = !string.IsNullOrWhiteSpace(http?.ApiKeyOverride) ? http.ApiKeyOverride : http?.ApiKey,
            ["pin"] = http?.Pin,
            ["username"] = http?.Username,
            ["password"] = http?.Password,
            ["client_key"] = http?.ClientKey,
            ["access_token"] = http?.AccessToken,
        };
    }

    /// <summary>
    /// True when the provider is enabled and holds every credential its sign-in needs (those not
    /// marked optional with <c>?</c>). A provider without a sign-in only needs to be enabled.
    /// </summary>
    public static bool HasCredentials(ProviderConfiguration provider)
    {
        if (!provider.Enabled)
        {
            return false;
        }

        if (provider.Auth is null)
        {
            return true;
        }

        var credentials = Credentials(provider);
        return provider.Auth.Login.Body.Values
            .SelectMany(template => Credential().Matches(template).Cast<Match>())
            .Where(match => !match.Groups[2].Success)
            .All(match => !string.IsNullOrWhiteSpace(credentials.GetValueOrDefault(match.Groups[1].Value)));
    }

    /// <summary>The current token, signing in first when there is none, it is about to expire, or the credentials changed.</summary>
    public async Task<string> EnsureTokenAsync(
        ProviderConfiguration provider,
        string baseUrl,
        IHttpClientFactory httpFactory,
        IProviderRateLimiterCoordinator rateLimiter,
        CancellationToken ct)
    {
        var auth = provider.Auth ?? throw new InvalidOperationException($"{provider.Name} has no sign-in to run.");
        if (!HasCredentials(provider))
        {
            throw new InvalidOperationException($"{DisplayName(provider)} needs credentials before it can be called.");
        }

        var credentials = Credentials(provider);
        var body = new Dictionary<string, string>(StringComparer.Ordinal);
        var signature = new List<string>();
        foreach (var (key, template) in auth.Login.Body)
        {
            var match = Credential().Match(template);
            var value = match.Success ? credentials.GetValueOrDefault(match.Groups[1].Value) : template;
            signature.Add(value ?? string.Empty);
            if (match.Success && match.Groups[2].Success && string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            body[key] = value ?? string.Empty;
        }

        var currentSignature = string.Join('\u001f', signature);
        if (IsFresh(currentSignature, auth))
        {
            return _token!;
        }

        await _loginLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsFresh(currentSignature, auth))
            {
                return _token!;
            }

            using var client = httpFactory.CreateClient(provider.Name);
            using var response = await rateLimiter.ExecuteAsync(provider.Name, provider.RateLimit,
                innerCt => client.PostAsJsonAsync(baseUrl + auth.Login.Path, body, innerCt), ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var envelope = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct).ConfigureAwait(false);
            var token = JsonPathEvaluator.GetStringValue(JsonPathEvaluator.Evaluate(envelope, auth.Login.TokenPath));
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new HttpRequestException($"{DisplayName(provider)} did not return an access token.");
            }

            if (_signature != currentSignature)
            {
                Cache.Clear();
            }

            _token = token;
            _signature = currentSignature;
            // Renewing early keeps a long-running batch from expiring mid-run.
            _expiresAt = DateTimeOffset.UtcNow.AddDays(auth.Login.LifetimeDays);
            return token;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    /// <summary>Makes the current token count as expired, so the next call signs in again.</summary>
    public void Expire() => _expiresAt = DateTimeOffset.MinValue;

    private bool IsFresh(string signature, ProviderAuthConfiguration auth) =>
        _token is not null && _signature == signature
        && _expiresAt > DateTimeOffset.UtcNow.AddMinutes(auth.Login.RefreshEarlyMinutes);

    private static string DisplayName(ProviderConfiguration provider) =>
        string.IsNullOrWhiteSpace(provider.DisplayName) ? provider.Name : provider.DisplayName;
}
