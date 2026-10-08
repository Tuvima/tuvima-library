using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using MediaEngine.Domain.Contracts;
using System.Collections.Concurrent;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Direct TVDB v4 client. Credentials are supplied by the installation and
/// reloaded from its secret store, never compiled into the application.
/// </summary>
public sealed class TvdbRetailClient(
    IConfigurationLoader configuration,
    IHttpClientFactory httpFactory,
    IProviderRateLimiterCoordinator rateLimiter)
{
    private const string DefaultBaseUrl = "https://api4.thetvdb.com/v4";
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, JsonNode Value)> _cache = new();
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private string? _token;
    private string? _credentialSignature;
    private DateTimeOffset _tokenExpiresAt;

    public bool IsConfigured()
    {
        var provider = configuration.LoadProvider("tvdb");
        return provider?.Enabled == true
            && !string.IsNullOrWhiteSpace(provider.HttpClient?.ApiKey);
    }

    public Task<JsonNode?> SearchSeriesAsync(string query, CancellationToken ct = default) =>
        GetDataAsync($"/search?query={Uri.EscapeDataString(query)}&type=series", ct);

    public Task<JsonNode?> GetSeriesAsync(string seriesId, CancellationToken ct = default) =>
        GetDataAsync($"/series/{EscapeId(seriesId)}/extended", ct);

    public Task<JsonNode?> GetSeasonAsync(string seasonId, CancellationToken ct = default) =>
        GetDataAsync($"/seasons/{EscapeId(seasonId)}/extended", ct);

    public Task<JsonNode?> GetEpisodeAsync(string episodeId, CancellationToken ct = default) =>
        GetDataAsync($"/episodes/{EscapeId(episodeId)}/extended", ct);

    public Task<JsonNode?> GetSeriesTranslationAsync(string seriesId, string language = "eng", CancellationToken ct = default) =>
        GetDataAsync($"/series/{EscapeId(seriesId)}/translations/{EscapeLanguage(language)}", ct);

    public Task<JsonNode?> GetSeasonTranslationAsync(string seasonId, string language = "eng", CancellationToken ct = default) =>
        GetDataAsync($"/seasons/{EscapeId(seasonId)}/translations/{EscapeLanguage(language)}", ct);

    public Task<JsonNode?> GetEpisodeTranslationAsync(string episodeId, string language = "eng", CancellationToken ct = default) =>
        GetDataAsync($"/episodes/{EscapeId(episodeId)}/translations/{EscapeLanguage(language)}", ct);

    public Task<JsonNode?> GetPersonAsync(string personId, CancellationToken ct = default) =>
        GetDataAsync($"/people/{EscapeId(personId)}/extended", ct);

    public Task<JsonNode?> GetArtworkAsync(string artworkId, CancellationToken ct = default) =>
        GetDataAsync($"/artwork/{EscapeId(artworkId)}/extended", ct);

    public Task<JsonNode?> GetSeriesArtworkAsync(string seriesId, CancellationToken ct = default) =>
        GetDataAsync($"/series/{EscapeId(seriesId)}/artworks", ct);

    public Task<JsonNode?> GetArtworkTypesAsync(CancellationToken ct = default) =>
        GetDataAsync("/artwork/types", ct);

    public Task<JsonNode?> GetEpisodesAsync(
        string seriesId,
        string seasonType = "default",
        string? language = null,
        int page = 0,
        CancellationToken ct = default)
    {
        if (seasonType is not ("default" or "official" or "dvd" or "absolute"))
        {
            throw new ArgumentOutOfRangeException(nameof(seasonType));
        }
        var languagePath = string.IsNullOrWhiteSpace(language)
            ? string.Empty : $"/{Uri.EscapeDataString(language)}";
        return GetDataAsync(
            $"/series/{EscapeId(seriesId)}/episodes/{seasonType}{languagePath}?page={Math.Max(0, page)}", ct);
    }

    public async Task<IReadOnlyList<JsonNode>> GetAllEpisodesAsync(
        string seriesId,
        string seasonType = "default",
        string? language = null,
        CancellationToken ct = default)
    {
        if (seasonType is not ("default" or "official" or "dvd" or "absolute"))
        {
            throw new ArgumentOutOfRangeException(nameof(seasonType));
        }
        var languagePath = string.IsNullOrWhiteSpace(language)
            ? string.Empty : $"/{EscapeLanguage(language)}";
        var episodes = new List<JsonNode>();
        for (var page = 0; page < 100; page++)
        {
            var envelope = await GetEnvelopeAsync(
                $"/series/{EscapeId(seriesId)}/episodes/{seasonType}{languagePath}?page={page}", ct)
                .ConfigureAwait(false);
            var batch = envelope?["data"]?["episodes"]?.AsArray();
            if (batch is null || batch.Count == 0)
            {
                break;
            }
            episodes.AddRange(batch.Where(node => node is not null).Select(node => node!.DeepClone()));
            if (string.IsNullOrWhiteSpace(envelope?["links"]?["next"]?.ToString()))
            {
                break;
            }
        }
        return episodes;
    }

    public async Task<JsonNode?> GetDataAsync(string relativePath, CancellationToken ct = default)
    {
        var envelope = await GetEnvelopeAsync(relativePath, ct).ConfigureAwait(false);
        return envelope?["data"]?.DeepClone();
    }

    private async Task<JsonNode?> GetEnvelopeAsync(string relativePath, CancellationToken ct)
    {
        if (!relativePath.StartsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException("A TVDB API path must be relative to the provider host.", nameof(relativePath));
        }

        var provider = configuration.LoadProvider("tvdb");
        var baseUrl = ResolveBaseUrl(provider?.Endpoints.GetValueOrDefault("api"));
        // Validate the current credential before serving cached data. A removed
        // key must immediately disable provider access, including cache hits.
        var token = await EnsureTokenAsync(ct).ConfigureAwait(false);
        var cacheKey = $"tvdb:v4:{relativePath}";
        if (_cache.TryGetValue(cacheKey, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
        {
            return cached.Value.DeepClone();
        }
        using var client = httpFactory.CreateClient("tvdb");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + relativePath);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await rateLimiter.ExecuteAsync("tvdb", provider?.RateLimit,
                innerCt => client.SendAsync(request, innerCt), ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                _tokenExpiresAt = DateTimeOffset.MinValue;
                token = await EnsureTokenAsync(ct).ConfigureAwait(false);
                continue;
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
            response.EnsureSuccessStatusCode();
            var envelope = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct)
                .ConfigureAwait(false);
            if (envelope is not null)
            {
                if (_cache.Count >= 1000)
                {
                    _cache.Clear();
                }
                _cache[cacheKey] = (DateTimeOffset.UtcNow.Add(relativePath == "/artwork/types"
                    ? TimeSpan.FromDays(7) : TimeSpan.FromHours(1)), envelope.DeepClone());
            }
            return envelope;
        }
        return null;
    }

    private async Task<string> EnsureTokenAsync(CancellationToken ct)
    {
        var provider = configuration.LoadProvider("tvdb");
        var apiKey = provider?.Enabled == true ? provider.HttpClient?.ApiKey : null;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("TheTVDB needs an administrator-supplied project API key.");
        }
        var pin = provider?.HttpClient?.Pin;
        var signature = apiKey + "\u001f" + pin;
        if (_token is not null && _credentialSignature == signature
            && _tokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return _token;
        }

        await _loginLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is not null && _credentialSignature == signature
                && _tokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return _token;
            }

            using var client = httpFactory.CreateClient("tvdb");
            var login = new Dictionary<string, string> { ["apikey"] = apiKey };
            if (!string.IsNullOrWhiteSpace(pin))
            {
                login["pin"] = pin;
            }
            var baseUrl = ResolveBaseUrl(provider?.Endpoints.GetValueOrDefault("api"));
            using var response = await rateLimiter.ExecuteAsync("tvdb", provider?.RateLimit,
                innerCt => client.PostAsJsonAsync(baseUrl + "/login", login, innerCt), ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var envelope = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct)
                .ConfigureAwait(false);
            var token = envelope?["data"]?["token"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new HttpRequestException("TheTVDB login did not return an access token.");
            }

            if (_credentialSignature != signature)
            {
                _cache.Clear();
            }
            _token = token;
            _credentialSignature = signature;
            // TVDB documents one-month token validity. Refresh early so a
            // clock difference or a long-running batch does not expire mid-run.
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddDays(25);
            return token;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    private static string EscapeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !id.All(char.IsDigit))
        {
            throw new ArgumentException("A TVDB record ID must be numeric.", nameof(id));
        }
        return Uri.EscapeDataString(id);
    }

    private static string EscapeLanguage(string language)
    {
        if (language.Length != 3 || !language.All(char.IsLetter))
        {
            throw new ArgumentException("A TheTVDB language must be a three-letter code.", nameof(language));
        }
        return Uri.EscapeDataString(language.ToLowerInvariant());
    }

    private static string ResolveBaseUrl(string? configured)
    {
        var value = string.IsNullOrWhiteSpace(configured) ? DefaultBaseUrl : configured.TrimEnd('/');
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("api4.thetvdb.com", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath != "/v4")
        {
            throw new InvalidOperationException("TheTVDB API endpoint must be https://api4.thetvdb.com/v4.");
        }
        return value;
    }
}
