using System.Collections.Concurrent;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Providers;

/// <summary>
/// The one built-in adapter for subtitle and lyrics providers. Everything that differs between
/// providers (requests, response shape, scoring, download rules, error mapping, messages) is read
/// from the <c>text_track</c> block of the provider's file, so a provider of this kind is added by
/// a file alone. Subtitles are searched as lists of releases and downloaded as files; lyrics are
/// one document per track that arrives with the search.
/// </summary>
public sealed partial class TextTrackProviderAdapter : ITextTrackProvider, IProviderCredentialConsumer
{
    private readonly ProviderConfiguration _config;
    private readonly TextTrackConfiguration _text;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IProviderResponseCacheRepository? _cache;
    private readonly IProviderHealthMonitor _health;
    private readonly ILogger<TextTrackProviderAdapter> _logger;
    private readonly IProviderRateLimiterCoordinator _rateLimiter;
    private readonly HashSet<MediaType> _mediaTypes;
    private readonly MediaType _primaryMediaType;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _emptySearches = new(StringComparer.Ordinal);

    public TextTrackProviderAdapter(
        ProviderConfiguration config,
        IHttpClientFactory httpFactory,
        IProviderHealthMonitor health,
        ILogger<TextTrackProviderAdapter> logger,
        IProviderRateLimiterCoordinator? rateLimiter = null,
        IProviderResponseCacheRepository? cache = null)
    {
        _config = config;
        _text = config.TextTrack
            ?? throw new InvalidOperationException($"Provider '{config.Name}' has no text_track block.");
        _httpFactory = httpFactory;
        _health = health;
        _logger = logger;
        _rateLimiter = rateLimiter ?? new ProviderRateLimiterCoordinator();
        _cache = cache;
        var declared = (config.CanHandle?.MediaTypes ?? [])
            .Select(name => Enum.TryParse<MediaType>(name, true, out var parsed) ? parsed : MediaType.Unknown)
            .ToList();
        _mediaTypes = declared.ToHashSet();
        _primaryMediaType = declared.FirstOrDefault();
    }

    public string Name => _config.Name;

    public TextTrackKind Kind => Enum.Parse<TextTrackKind>(_text.Kind, ignoreCase: true);

    public bool IsEnabled => _config.Enabled;

    public bool ReportsIncompatibleDownloads => _text.Download.NullMeansIncompatible;

    public bool CanHandle(MediaType mediaType) => _mediaTypes.Contains(mediaType);

    public void ApplyCredentials(IReadOnlyDictionary<string, string?> credentials)
    {
        _config.HttpClient ??= new HttpClientConfig();
        _config.HttpClient.ApiKey = credentials.GetValueOrDefault("api_key")?.Trim();
        _emptySearches.Clear();
    }

    public TextTrackProviderAvailability GetAvailability(MediaType mediaType)
    {
        if (!CanHandle(mediaType))
        {
            return new("Unsupported", $"{Name} does not support {mediaType}.");
        }

        if (!IsEnabled)
        {
            return new("Disabled", $"{Name} is disabled in provider settings.");
        }

        if (_config.RequiresApiKey && string.IsNullOrWhiteSpace(_config.HttpClient?.ApiKey))
        {
            return new("AuthenticationRequired", $"{Name} needs an API key before {_text.Noun} can be fetched.");
        }

        if (_health.IsDown(Name))
        {
            return new("ProviderUnavailable", Message(TextTrackMessages.ProviderDown, $"{Name} is temporarily unavailable."));
        }

        return new("Available", null);
    }

    public Task<IReadOnlyList<TextTrackCandidate>> SearchAsync(TextTrackLookup lookup, CancellationToken ct = default) =>
        Kind == TextTrackKind.Lyrics ? SearchLyricsAsync(lookup, ct) : SearchSubtitlesAsync(lookup, ct);

    public Task<TextTrackDownload?> DownloadAsync(TextTrackCandidate candidate, CancellationToken ct = default) =>
        _text.Download.Mode == TextTrackDownloadModes.Fetch
            ? DownloadSubtitleAsync(candidate, ct)
            : Task.FromResult(DownloadPayload(candidate));

    private static TextTrackDownload? DownloadPayload(TextTrackCandidate candidate) =>
        candidate.Payload is string content && !string.IsNullOrWhiteSpace(content)
            ? new TextTrackDownload(candidate, content, candidate.SourceFormat, candidate.SourceFormat)
            : null;

    /// <summary>A configured message with <c>{name}</c> and <c>{display_name}</c> filled in, or the fallback.</summary>
    private string Message(string key, string fallback) =>
        (_text.Messages.GetValueOrDefault(key) ?? fallback)
            .Replace("{name}", Name, StringComparison.Ordinal)
            .Replace("{display_name}", string.IsNullOrWhiteSpace(_config.DisplayName) ? Name : _config.DisplayName, StringComparison.Ordinal);
}
