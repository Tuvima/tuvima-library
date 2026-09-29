using System.Globalization;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Providers;

/// <summary>SubDL v2 subtitle search. Only typed, verified movie/episode identities may reach this adapter.</summary>
public sealed class SubdlTextTrackProvider : ITextTrackProvider, IProviderCredentialConsumer
{
    private const string DefaultApi = "https://api.subdl.com";
    private const int MaximumDownloadBytes = 8 * 1024 * 1024;
    private const int MaximumArchiveEntries = 32;
    private readonly ProviderConfiguration _config;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IProviderHealthMonitor _health;
    private readonly ILogger<SubdlTextTrackProvider> _logger;
    private readonly IProviderRateLimiterCoordinator _rateLimiter;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _emptySearches = new(StringComparer.Ordinal);

    public SubdlTextTrackProvider(ProviderConfiguration config, IHttpClientFactory httpFactory,
        IProviderHealthMonitor health, ILogger<SubdlTextTrackProvider> logger,
        IProviderRateLimiterCoordinator? rateLimiter = null)
    {
        _config = config;
        _httpFactory = httpFactory;
        _health = health;
        _logger = logger;
        _rateLimiter = rateLimiter ?? new ProviderRateLimiterCoordinator();
    }

    public string Name => _config.Name;
    public TextTrackKind Kind => TextTrackKind.Subtitles;
    public bool IsEnabled => _config.Enabled;
    public bool CanHandle(MediaType mediaType) => mediaType is MediaType.Movies or MediaType.TV;

    public void ApplyCredentials(IReadOnlyDictionary<string, string?> credentials)
    {
        _config.HttpClient ??= new HttpClientConfig();
        _config.HttpClient.ApiKey = credentials.GetValueOrDefault("api_key")?.Trim();
        _emptySearches.Clear();
    }

    public TextTrackProviderAvailability GetAvailability(MediaType mediaType)
    {
        if (!CanHandle(mediaType)) return new("Unsupported", $"{Name} does not support {mediaType}.");
        if (!IsEnabled) return new("Disabled", $"{Name} is disabled in provider settings.");
        if (string.IsNullOrWhiteSpace(_config.HttpClient?.ApiKey))
            return new("AuthenticationRequired", $"{Name} needs an API key before subtitles can be fetched.");
        if (_health.IsDown(Name)) return new("ProviderUnavailable", $"{Name} is temporarily unavailable.");
        return new("Available", null);
    }

    public async Task<IReadOnlyList<TextTrackCandidate>> SearchAsync(TextTrackLookup lookup, CancellationToken ct = default)
    {
        if (!CanHandle(lookup.MediaType) || lookup.SubtitleContext is null) return [];
        var availability = GetAvailability(lookup.MediaType);
        if (!availability.IsAvailable)
            throw new SubdlLookupException(availability.Status,
                availability.Message ?? "SubDL is unavailable for subtitle search.");

        var context = lookup.SubtitleContext;
        var query = new List<string>();
        var expectedType = lookup.MediaType == MediaType.TV ? "tv" : "movie";
        string? expectedTmdb;
        if (lookup.MediaType == MediaType.TV)
        {
            var episode = context.TmdbEpisode;
            if (episode is null || episode.SeasonNumber < 0 || episode.EpisodeNumber < 1
                || !TryPositiveId(episode.ShowId, out expectedTmdb)) return [];
            query.Add($"tmdb_id={expectedTmdb}");
            query.Add("type=tv");
            query.Add($"season={episode.SeasonNumber.ToString(CultureInfo.InvariantCulture)}");
            query.Add($"episode={episode.EpisodeNumber.ToString(CultureInfo.InvariantCulture)}");
        }
        else
        {
            var movie = context.Movie;
            if (movie is null) return [];
            if (TryPositiveId(movie.TmdbMovieId, out expectedTmdb))
                query.Add($"tmdb_id={expectedTmdb}");
            else if (IsImdbId(movie.ImdbId))
            {
                expectedTmdb = null;
                query.Add($"imdb_id={Uri.EscapeDataString(movie.ImdbId!)}");
            }
            else return [];
            query.Add("type=movie");
        }

        var language = NormalizeLanguage(lookup.Language);
        query.Add($"languages={Uri.EscapeDataString(language)}");
        query.Add("unpack=1");
        var path = $"/api/v2/subtitles/search?{string.Join('&', query)}";
        if (_emptySearches.TryGetValue(path, out var nextAttempt) && nextAttempt > DateTimeOffset.UtcNow)
            return [];
        using var response = await SendApiAsync(path, ct).ConfigureAwait(false);
        if (response is null)
            throw new SubdlLookupException("ProviderUnavailable", "SubDL could not be reached.");
        if (!response.IsSuccessStatusCode)
            throw await FailureAsync(response, ct).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;
            if (!IsSuccessful(root) || !TryArray(root, "results", out var results)
                || !TryArray(root, "subtitles", out var subtitles))
                throw new SubdlLookupException("ProviderError", "SubDL returned an incomplete search response.");
            if (results.GetArrayLength() == 0 || subtitles.GetArrayLength() == 0)
            {
                _emptySearches[path] = DateTimeOffset.UtcNow.AddMinutes(15);
                return [];
            }
            if (!MatchesTitle(root, expectedType, expectedTmdb, context.Movie?.ImdbId))
                throw new SubdlLookupException("IdentityMismatch", "SubDL returned subtitles for a different movie or show.");

            var candidates = new List<TextTrackCandidate>();
            foreach (var subtitle in subtitles.EnumerateArray().Take(30))
                AddCandidates(subtitle, lookup, language, candidates);

            var ranked = candidates.OrderByDescending(candidate => candidate.Confidence).ToList();
            // Two equally plausible releases cannot be resolved by popularity or API ordering.
            if (ranked.Count > 1 && ranked[0].Confidence - ranked[1].Confidence < 0.025)
                throw new SubdlLookupException("AmbiguousMatch", "Several SubDL subtitles match equally well; no automatic choice was made.");
            if (ranked.Count == 0)
                _emptySearches[path] = DateTimeOffset.UtcNow.AddMinutes(15);
            return ranked.Take(3).ToList();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "{Provider} returned an invalid subtitle response", Name);
            await _health.ReportFailureAsync(Name, "Invalid subtitle response", ct).ConfigureAwait(false);
            throw new SubdlLookupException("ProviderError", "SubDL returned an invalid search response.");
        }
    }

    public async Task<TextTrackDownload?> DownloadAsync(TextTrackCandidate candidate, CancellationToken ct = default)
    {
        if (candidate.Payload is not DownloadSelection selection) return null;
        var availability = GetAvailability(MediaType.Movies);
        if (!availability.IsAvailable)
            throw new SubdlLookupException(availability.Status,
                availability.Message ?? "SubDL is unavailable for subtitle download.");
        try
        {
            // An unpacked file has an explicit episode and language. For a simple result, v2's
            // format=file endpoint only succeeds when there is one unambiguous subtitle file.
            var url = selection.FileUrl;
            if (url is null)
            {
                using var response = await SendApiAsync($"/api/v2/subtitles/{Uri.EscapeDataString(selection.SubtitleId)}/download?format=file", ct)
                    .ConfigureAwait(false);
                if (response is null)
                    throw new SubdlLookupException("ProviderUnavailable", "SubDL could not be reached.");
                if (!response.IsSuccessStatusCode)
                    throw await FailureAsync(response, ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                url = ReadString(doc.RootElement, "url") ?? ReadString(doc.RootElement, "link")
                    ?? (TryObject(doc.RootElement, "data", out var data) ? ReadString(data, "url") : null);
            }
            if (!TryDownloadUri(url, out var downloadUri)) return null;
            using var client = _httpFactory.CreateClient(Name);
            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUri);
            using var responseFile = await _rateLimiter.ExecuteAsync(Name, _config.RateLimit,
                token => client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token), ct)
                .ConfigureAwait(false);
            if (responseFile.RequestMessage?.RequestUri is { } finalFileUri
                && (!TryDownloadUri(finalFileUri.ToString(), out _))) return null;
            if (!responseFile.IsSuccessStatusCode || responseFile.Content.Headers.ContentLength > MaximumDownloadBytes)
                return null;
            await using var stream = await responseFile.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var bytes = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(bytes, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaximumDownloadBytes) return null;
                buffer.Write(bytes, 0, read);
            }
            var payload = buffer.ToArray();
            if (payload.Length == 0) return null;
            var fileName = selection.FileName;
            if (IsZip(payload))
            {
                var extracted = ExtractSingleSafeEntry(payload, selection);
                if (extracted is null) return null;
                (payload, fileName) = extracted.Value;
            }
            var format = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
            if (format is not ("srt" or "vtt" or "ass" or "ssa")) return null;
            var content = DecodeText(payload);
            if (string.IsNullOrWhiteSpace(content)) return null;
            var normalized = MediaEngine.Providers.Services.SubtitleNormalizer.NormalizeToWebVtt(content, format);
            if (!normalized.Contains(" --> ", StringComparison.Ordinal)) return null;
            await _health.ReportSuccessAsync(Name, ct).ConfigureAwait(false);
            return new(candidate, content, format, "vtt");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (SubdlLookupException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        {
            _logger.LogWarning(ex, "{Provider} subtitle download failed", Name);
            await _health.ReportFailureAsync(Name, "Subtitle download failed", ct).ConfigureAwait(false);
            throw new SubdlLookupException("ProviderUnavailable", "SubDL could not complete the subtitle download.");
        }
    }

    private void AddCandidates(JsonElement item, TextTrackLookup lookup, string requestedLanguage,
        List<TextTrackCandidate> candidates)
    {
        var subtitleId = ReadString(item, "n_id") ?? ReadString(item, "nId");
        if (string.IsNullOrWhiteSpace(subtitleId) || !IsSafeId(subtitleId)) return;
        var episode = lookup.SubtitleContext?.TmdbEpisode;
        var release = ReadString(item, "release_name") ?? ReadString(item, "name");
        var files = TryArray(item, "unpack_files", out var unpack) ? unpack : default;
        if (files.ValueKind == JsonValueKind.Array && files.GetArrayLength() > 0)
        {
            foreach (var file in files.EnumerateArray().Take(MaximumArchiveEntries))
            {
                if (episode is not null && !EpisodeMatches(file, episode.SeasonNumber, episode.EpisodeNumber)) continue;
                var fileName = ReadString(file, "name");
                var fileId = ReadString(file, "file_n_id") ?? ReadString(file, "file_id");
                var url = ReadString(file, "url");
                if (fileId is null || !IsSafeId(fileId) || !TryDownloadUri(url, out _)
                    || !SupportedFile(fileName)) continue;
                var language = NormalizeLanguage(ReadString(file, "language") ?? ReadString(item, "language")
                    ?? ReadString(item, "lang"));
                if (language != requestedLanguage) continue;
                var hi = ReadBool(file, "hi") ?? ReadBool(item, "hi") ?? false;
                AddCandidate(subtitleId, fileId, fileName!, url, release, language, hi, lookup, candidates);
            }
        }
        else
        {
            if (episode is not null && !EpisodeMatches(item, episode.SeasonNumber, episode.EpisodeNumber)) return;
            var fileName = ReadString(item, "name");
            if (!SupportedFile(fileName)) return;
            var language = NormalizeLanguage(ReadString(item, "language") ?? ReadString(item, "lang"));
            if (language != requestedLanguage) return;
            AddCandidate(subtitleId, null, fileName!, null, release, language,
                ReadBool(item, "hi") ?? false, lookup, candidates);
        }
    }

    private void AddCandidate(string subtitleId, string? fileId, string fileName, string? fileUrl,
        string? release, string language, bool hearingImpaired, TextTrackLookup lookup,
        List<TextTrackCandidate> candidates)
    {
        var releaseScore = ReleaseScore(Path.GetFileNameWithoutExtension(lookup.Asset.FilePathRoot), release);
        var format = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        var score = 0.82 + releaseScore * 0.1 + (format == "vtt" ? 0.015 : format == "srt" ? 0.01 : 0);
        if (hearingImpaired) score -= 0.03;
        candidates.Add(new TextTrackCandidate(Name, Kind,
            fileId is null ? subtitleId : $"{subtitleId}/{fileId}",
            $"https://subdl.com/subtitle/{Uri.EscapeDataString(subtitleId)}", language,
            format, score, hearingImpaired, null,
            new DownloadSelection(subtitleId, fileName, fileUrl), ReleaseName: release));
    }

    private async Task<HttpResponseMessage?> SendApiAsync(string path, CancellationToken ct)
    {
        var key = _config.HttpClient?.ApiKey?.Trim();
        if (string.IsNullOrWhiteSpace(key) || _health.IsDown(Name)) return null;
        var api = _config.Endpoints.GetValueOrDefault("api")?.TrimEnd('/') ?? DefaultApi;
        if (!Uri.TryCreate(api, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(baseUri.Host, "api.subdl.com", StringComparison.OrdinalIgnoreCase)
            || baseUri.Port != 443 || !string.IsNullOrEmpty(baseUri.UserInfo)
            || baseUri.AbsolutePath != "/" || !string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment)) return null;
        try
        {
            using var client = _httpFactory.CreateClient(Name);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var response = await _rateLimiter.ExecuteAsync(Name, _config.RateLimit,
                token => client.SendAsync(request, token), ct).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } finalApiUri
                && (finalApiUri.Scheme != Uri.UriSchemeHttps
                    || !string.Equals(finalApiUri.Host, "api.subdl.com", StringComparison.OrdinalIgnoreCase)))
            {
                response.Dispose();
                return null;
            }
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return response; // A bad key or quota is not a provider outage.
            if (!response.IsSuccessStatusCode)
                await _health.ReportFailureAsync(Name, $"HTTP {(int)response.StatusCode}", ct).ConfigureAwait(false);
            else
                await _health.ReportSuccessAsync(Name, ct).ConfigureAwait(false);
            return response;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "{Provider} request failed", Name);
            await _health.ReportFailureAsync(Name, "Provider request failed", ct).ConfigureAwait(false);
            return null;
        }
    }

    private static async Task<SubdlLookupException> FailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? code = null;
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (TryObject(doc.RootElement, "error", out var error)) code = ReadString(error, "code");
        }
        catch (JsonException) { /* HTTP status remains authoritative. */ }
        var status = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "AuthenticationRequired",
            HttpStatusCode.TooManyRequests when string.Equals(code, "quota_exceeded", StringComparison.OrdinalIgnoreCase)
                => "QuotaExceeded",
            HttpStatusCode.TooManyRequests => "RateLimited",
            >= HttpStatusCode.InternalServerError => "ProviderUnavailable",
            _ => "ProviderError"
        };
        DateTimeOffset? retryAt = response.Headers.RetryAfter?.Date;
        if (retryAt is null && response.Headers.RetryAfter?.Delta is { } delta)
            retryAt = DateTimeOffset.UtcNow.Add(delta);
        if (retryAt is null && response.Headers.TryGetValues("X-RateLimit-Reset", out var resets)
            && long.TryParse(resets.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
            retryAt = DateTimeOffset.FromUnixTimeSeconds(epoch);
        var message = status switch
        {
            "AuthenticationRequired" => "The SubDL API key was rejected.",
            "QuotaExceeded" => "The SubDL daily quota has been exhausted.",
            "RateLimited" => "SubDL is rate limiting subtitle requests.",
            "ProviderUnavailable" => "SubDL is temporarily unavailable.",
            _ => "SubDL could not complete the subtitle request."
        };
        if (retryAt is not null && status is "QuotaExceeded" or "RateLimited")
            message += $" Try again after {retryAt.Value:yyyy-MM-dd HH:mm} UTC.";
        return new SubdlLookupException(status, message, retryAt);
    }

    private static bool MatchesTitle(JsonElement root, string type, string? tmdbId, string? imdbId)
    {
        if (!TryArray(root, "results", out var results)) return false;
        return results.EnumerateArray().Any(result =>
            string.Equals(ReadString(result, "type"), type, StringComparison.OrdinalIgnoreCase)
            && (tmdbId is not null
                ? string.Equals(ReadString(result, "tmdb_id"), tmdbId, StringComparison.Ordinal)
                : string.Equals(ReadString(result, "imdb_id"), imdbId, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool EpisodeMatches(JsonElement item, int season, int episode)
    {
        var itemSeason = ReadInt(item, "season");
        var itemEpisode = ReadInt(item, "episode");
        // Season packs and ranges require an explicit unpacked member with exact coordinates.
        return itemSeason == season && itemEpisode == episode
            && ReadBool(item, "full_season") != true
            && (!ReadInt(item, "episode_from").HasValue || ReadInt(item, "episode_from") == episode)
            && (!ReadInt(item, "episode_end").HasValue || ReadInt(item, "episode_end") == episode);
    }

    private static (byte[] Data, string Name)? ExtractSingleSafeEntry(byte[] zip, DownloadSelection selection)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        if (archive.Entries.Count is 0 or > MaximumArchiveEntries) return null;
        var entries = archive.Entries.Where(entry => SupportedFile(entry.FullName)
            && !entry.FullName.Contains('/') && !entry.FullName.Contains('\\')
            && !entry.FullName.Contains("..", StringComparison.Ordinal)
            && entry.Length is > 0 and <= MaximumDownloadBytes
            && entry.Length <= Math.Max(entry.CompressedLength, 1) * 100).ToList();
        var selected = entries.Where(entry => string.Equals(entry.Name, selection.FileName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (selected.Count != 1) return null;
        using var input = selected[0].Open();
        using var output = new MemoryStream();
        input.CopyTo(output);
        if (output.Length > MaximumDownloadBytes) return null;
        return (output.ToArray(), selected[0].Name);
    }

    private static bool IsZip(byte[] data) => data.Length >= 4 && data[0] == 0x50 && data[1] == 0x4b
        && data[2] == 0x03 && data[3] == 0x04;
    private static string DecodeText(byte[] data) =>
        data.Length >= 2 && data[0] == 0xff && data[1] == 0xfe
            ? Encoding.Unicode.GetString(data, 2, data.Length - 2)
            : Encoding.UTF8.GetString(data).TrimStart('\uFEFF');
    private static bool SupportedFile(string? name) => name is not null
        && Path.GetExtension(name).ToLowerInvariant() is ".srt" or ".vtt" or ".ass" or ".ssa";
    private static bool IsSuccessful(JsonElement root) => ReadBool(root, "status") != false;
    private static bool TryArray(JsonElement node, string key, out JsonElement value)
    {
        value = default;
        return node.ValueKind == JsonValueKind.Object && node.TryGetProperty(key, out value)
            && value.ValueKind == JsonValueKind.Array;
    }
    private static bool TryObject(JsonElement node, string key, out JsonElement value)
    {
        value = default;
        return node.ValueKind == JsonValueKind.Object && node.TryGetProperty(key, out value)
            && value.ValueKind == JsonValueKind.Object;
    }
    private static string? ReadString(JsonElement node, string key) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(key, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind == JsonValueKind.Number ? value.ToString() : null
            : null;
    private static int? ReadInt(JsonElement node, string key) =>
        int.TryParse(ReadString(node, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    private static bool? ReadBool(JsonElement node, string key) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(key, out var value)
            ? value.ValueKind == JsonValueKind.True ? true : value.ValueKind == JsonValueKind.False ? false : null
            : null;
    private static bool TryPositiveId(string? value, out string id)
    {
        id = value?.Trim() ?? "";
        return long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0;
    }
    private static bool IsImdbId(string? value) => value is { Length: >= 3 and <= 12 }
        && value.StartsWith("tt", StringComparison.Ordinal) && value[2..].All(char.IsAsciiDigit);
    private static bool IsSafeId(string value) => value.Length <= 128
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static string NormalizeLanguage(string? value)
    {
        var language = value?.Trim().Split('-', '_')[0].ToLowerInvariant();
        return language switch { "english" => "en", "french" => "fr", "spanish" => "es", "german" => "de",
            "portuguese" => "pt", "italian" => "it", "arabic" => "ar", "persian" or "farsi" => "fa",
            "japanese" => "ja", "korean" => "ko", "chinese" => "zh", _ => language is { Length: 2 or 3 } ? language : "und" };
    }
    private static bool TryDownloadUri(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.StartsWith("/subtitle/", StringComparison.Ordinal)) value = "https://dl.subdl.com" + value;
        return Uri.TryCreate(value, UriKind.Absolute, out uri!) && uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Host, "dl.subdl.com", StringComparison.OrdinalIgnoreCase)
            && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
            && uri.AbsolutePath.StartsWith("/subtitle/", StringComparison.Ordinal)
            && !Uri.UnescapeDataString(uri.AbsolutePath).Contains("..", StringComparison.Ordinal)
            && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
    }
    private static double ReleaseScore(string? local, string? remote)
    {
        if (string.IsNullOrWhiteSpace(local) || string.IsNullOrWhiteSpace(remote)) return 0;
        var localWords = Tokenize(local);
        var remoteWords = Tokenize(remote);
        if (localWords.Count == 0 || remoteWords.Count == 0) return 0;
        return (double)localWords.Intersect(remoteWords).Count() / localWords.Union(remoteWords).Count();
    }
    private static HashSet<string> Tokenize(string value) => new(value.ToLowerInvariant()
        .Split(['.', '_', '-', ' ', '[', ']', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
        .Where(word => word.Length > 1), StringComparer.Ordinal);

    private sealed record DownloadSelection(string SubtitleId, string FileName, string? FileUrl);
}

public sealed class SubdlLookupException(string status, string message, DateTimeOffset? retryAt = null) : Exception(message)
{
    public string Status { get; } = status;
    public DateTimeOffset? RetryAt { get; } = retryAt;
}
