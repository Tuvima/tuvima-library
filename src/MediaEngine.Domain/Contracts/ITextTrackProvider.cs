using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Domain.Contracts;

public interface ITextTrackProvider
{
    string Name { get; }

    /// <summary>The name shown to people in messages; the provider's name unless it declares one.</summary>
    string DisplayName => Name;

    TextTrackKind Kind { get; }

    bool IsEnabled { get; }

    bool CanHandle(MediaType mediaType);

    /// <summary>
    /// True when a download that yields nothing means the provider's files are not playable (for
    /// example subtitles in a format that cannot be converted), which the caller reports to the user.
    /// </summary>
    bool ReportsIncompatibleDownloads => false;

    TextTrackProviderAvailability GetAvailability(MediaType mediaType) =>
        !CanHandle(mediaType)
            ? new("Unsupported", $"{Name} does not support {mediaType}.")
            : IsEnabled
                ? new("Available", null)
                : new("Disabled", $"{Name} is disabled in provider settings.");

    /// <summary>
    /// The language code this source would be asked for when given a language name or code (<c>English</c>, <c>en-US</c>, <c>pt</c>), or
    /// <c>null</c> when the source cannot be asked for that language, so a search never goes out as "unknown".
    /// </summary>
    string? NormalizeSearchLanguage(string? value)
    {
        var code = value?.Trim().Split('-', '_')[0].ToLowerInvariant();
        return code is { Length: 2 or 3 } && code.All(char.IsAsciiLetter) ? code : null;
    }

    Task<IReadOnlyList<TextTrackCandidate>> SearchAsync(TextTrackLookup lookup, CancellationToken ct = default);

    Task<TextTrackDownload?> DownloadAsync(TextTrackCandidate candidate, CancellationToken ct = default);
}

public sealed record TextTrackProviderAvailability(string Status, string? Message)
{
    public bool IsAvailable => string.Equals(Status, "Available", StringComparison.OrdinalIgnoreCase);
}

public sealed record TextTrackLookup(
    MediaAsset Asset,
    MediaType MediaType,
    string? Title,
    string? Artist,
    string? Album,
    string? Year,
    string? Language,
    double? DurationSeconds,
    IReadOnlyDictionary<string, string> BridgeIds,
    SubtitleLookupContext? SubtitleContext = null);

public sealed record TextTrackCandidate(
    string Provider,
    TextTrackKind Kind,
    string SourceId,
    string? SourceUrl,
    string Language,
    string SourceFormat,
    double Confidence,
    bool IsHearingImpaired,
    double? DurationMatchScore,
    object? Payload = null,
    bool IsForced = false,
    string? ReleaseName = null,
    bool IsInstrumental = false);

public sealed record TextTrackDownload(
    TextTrackCandidate Candidate,
    string Content,
    string SourceFormat,
    string NormalizedFormat);
