using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Providers;

public sealed partial class TextTrackProviderAdapter
{
    private static readonly Regex Placeholder = new(@"\{([a-z_]+)\}", RegexOptions.Compiled);

    /// <summary>The values a request template may use, taken from the lookup and its verified identities.</summary>
    private Dictionary<string, string> LookupValues(TextTrackLookup lookup)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        void Set(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                values[key] = value;
            }
        }

        Set("title", lookup.Title);
        Set("artist", lookup.Artist);
        Set("album", lookup.Album);
        Set("year", lookup.Year);
        Set("language", NormalizeLanguage(lookup.Language));
        if (lookup.DurationSeconds is > 0)
        {
            Set("duration_seconds", Math.Round(lookup.DurationSeconds.Value).ToString(CultureInfo.InvariantCulture));
        }

        foreach (var (key, value) in lookup.SubtitleContext?.ToVerifiedValues(lookup.MediaType)
            ?? new Dictionary<string, string>())
        {
            Set(key, value);
        }

        return values;
    }

    /// <summary>The first request that fits the media type and has every value it needs; <c>null</c> when none does.</summary>
    private TextTrackRequestConfiguration? SelectRequest(MediaType mediaType, IReadOnlyDictionary<string, string> values) =>
        _text.Search.Requests.FirstOrDefault(request =>
            (string.IsNullOrEmpty(request.WhenMediaType)
                || string.Equals(request.WhenMediaType, mediaType.ToString(), StringComparison.OrdinalIgnoreCase))
            && request.Needs.All(need => values.ContainsKey(need)));

    /// <summary>The path and query of a request; pairs that name a missing value are left out.</summary>
    private static string BuildPath(TextTrackRequestConfiguration request, IReadOnlyDictionary<string, string> values)
    {
        var pairs = new List<string>();
        foreach (var pair in request.Query)
        {
            var missing = false;
            var value = Placeholder.Replace(pair[1], match =>
            {
                if (values.TryGetValue(match.Groups[1].Value, out var found))
                {
                    return found;
                }

                missing = true;
                return string.Empty;
            });
            if (!missing)
            {
                pairs.Add($"{pair[0]}={Uri.EscapeDataString(value)}");
            }
        }

        return $"{request.Path}?{string.Join('&', pairs)}";
    }

    private string NormalizeLanguage(string? value)
    {
        var language = value?.Trim().Split('-', '_')[0].ToLowerInvariant();
        if (language is not null && _text.Language.Aliases.TryGetValue(language, out var aliased))
        {
            return aliased;
        }

        return language is { Length: 2 or 3 } ? language : _text.Language.Unknown;
    }

    /// <summary>The API address when it passes the host checks of the file; otherwise <c>null</c>.</summary>
    private Uri? ApiBase()
    {
        var api = _config.Endpoints.GetValueOrDefault("api")?.TrimEnd('/');
        if (!Uri.TryCreate(api, UriKind.Absolute, out var baseUri))
        {
            return null;
        }

        var hosts = _text.Api.AllowedHosts;
        if (hosts.Count == 0)
        {
            return baseUri;
        }

        return baseUri.Scheme == Uri.UriSchemeHttps
            && hosts.Contains(baseUri.Host, StringComparer.OrdinalIgnoreCase)
            && baseUri.Port == 443 && string.IsNullOrEmpty(baseUri.UserInfo)
            && baseUri.AbsolutePath == "/" && string.IsNullOrEmpty(baseUri.Query) && string.IsNullOrEmpty(baseUri.Fragment)
            ? baseUri
            : null;
    }

    /// <summary>
    /// Sends an authenticated API request. Returns <c>null</c> when the provider cannot be used
    /// (no key, marked down, address refused, redirected elsewhere, transport failure); answers with
    /// failure codes are returned for the caller to map.
    /// </summary>
    private async Task<HttpResponseMessage?> SendApiAsync(string path, CancellationToken ct)
    {
        var key = _config.HttpClient?.ApiKey?.Trim();
        if (string.IsNullOrWhiteSpace(key) || _health.IsDown(Name) || ApiBase() is not { } baseUri)
        {
            return null;
        }

        try
        {
            using var client = _httpFactory.CreateClient(Name);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            if (!string.IsNullOrWhiteSpace(_text.Api.Accept))
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(_text.Api.Accept));
            }

            var response = await _rateLimiter.ExecuteAsync(Name, _config.RateLimit,
                token => client.SendAsync(request, token), ct).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } finalUri
                && _text.Api.AllowedHosts.Count > 0
                && (finalUri.Scheme != Uri.UriSchemeHttps
                    || !_text.Api.AllowedHosts.Contains(finalUri.Host, StringComparer.OrdinalIgnoreCase)))
            {
                response.Dispose();
                return null;
            }

            var code = (int)response.StatusCode;
            if (_text.Errors.NotOutageCodes.Contains(code))
            {
                return response;
            }

            if (!response.IsSuccessStatusCode)
            {
                await _health.ReportFailureAsync(Name, $"HTTP {code}", ct).ConfigureAwait(false);
            }
            else
            {
                await _health.ReportSuccessAsync(Name, ct).ConfigureAwait(false);
            }

            return response;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "{Provider} request failed", Name);
            await _health.ReportFailureAsync(Name, "Provider request failed", ct).ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>Maps a failed API answer to a lookup error with the configured status and message.</summary>
    private async Task<TextTrackLookupException> FailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? errorCode = null;
        if (!string.IsNullOrEmpty(_text.Errors.ErrorCodePath))
        {
            try
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                errorCode = ReadPath(doc.RootElement, _text.Errors.ErrorCodePath);
            }
            catch (JsonException)
            {
                // The HTTP status stays authoritative when the body is not JSON.
            }
        }

        var code = (int)response.StatusCode;
        var status = _text.Errors.Statuses.FirstOrDefault(entry =>
            (entry.Codes.Contains(code) || (entry.MinCode is { } min && code >= min))
            && (entry.ErrorCode is null || string.Equals(entry.ErrorCode, errorCode, StringComparison.OrdinalIgnoreCase)))
            ?.Status ?? _text.Errors.DefaultStatus;

        DateTimeOffset? retryAt = response.Headers.RetryAfter?.Date;
        if (retryAt is null && response.Headers.RetryAfter?.Delta is { } delta)
        {
            retryAt = DateTimeOffset.UtcNow.Add(delta);
        }

        if (retryAt is null && !string.IsNullOrEmpty(_text.Errors.ResetHeader)
            && response.Headers.TryGetValues(_text.Errors.ResetHeader, out var resets)
            && long.TryParse(resets.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
        {
            retryAt = DateTimeOffset.FromUnixTimeSeconds(epoch);
        }

        var message = Message(status, Message(TextTrackMessages.ProviderError, $"{{display_name}} could not complete the {_text.Noun} request."));
        if (retryAt is not null && status is TextTrackMessages.QuotaExceeded or TextTrackMessages.RateLimited)
        {
            message += $" Try again after {retryAt.Value:yyyy-MM-dd HH:mm} UTC.";
        }

        return new TextTrackLookupException(status, message, retryAt);
    }

    private static string? ReadPath(JsonElement root, string path)
    {
        var current = root;
        foreach (var segment in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return ScalarText(current);
    }

    /// <summary>Text of a string or number; <c>null</c> for anything else.</summary>
    private static string? ScalarText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.ToString(),
        _ => null,
    };
}
