using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Intelligence.Contracts;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Workers;

public sealed partial class RetailMatchWorker
{
    private static string BuildAlbumKey(Dictionary<string, string> hints)
    {
        var artist = GetMusicCreatorHint(hints);

        hints.TryGetValue(MetadataFieldConstants.Album, out var album);

        // Normalise: lowercase, trim — so "The Beatles" and "the beatles" group together.
        return $"{(artist ?? string.Empty).Trim().ToLowerInvariant()}|{(album ?? string.Empty).Trim().ToLowerInvariant()}";
    }

    private static string? GetMusicCreatorHint(IReadOnlyDictionary<string, string> hints) =>
        StringHelpers.FirstNonBlank(
            hints.GetValueOrDefault(MetadataFieldConstants.Artist),
            hints.GetValueOrDefault("album_artist"),
            hints.GetValueOrDefault(MetadataFieldConstants.Author),
            hints.GetValueOrDefault(MetadataFieldConstants.Composer));

    private static string BuildShowSeasonKey(Dictionary<string, string> hints)
    {
        hints.TryGetValue(MetadataFieldConstants.ShowName, out var showName);
        if (string.IsNullOrWhiteSpace(showName))
        {
            hints.TryGetValue(MetadataFieldConstants.Series, out showName);
        }

        hints.TryGetValue(MetadataFieldConstants.SeasonNumber, out var season);
        if (string.IsNullOrWhiteSpace(season))
        {
            hints.TryGetValue("season", out season);
        }

        return $"{(showName ?? string.Empty).Trim().ToLowerInvariant()}|{(season ?? "1").Trim()}";
    }

    private (string Language, string MusicCountry, string RegionCountry) GetConfiguredLocale()
    {
        var core = GetExecutionSnapshot().Core;
        var rawLanguage = string.IsNullOrWhiteSpace(core.Language.Metadata)
            ? "en"
            : core.Language.Metadata.Trim();
        var language = rawLanguage
            .Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)[0]
            .ToLowerInvariant();
        var regionCountry = string.IsNullOrWhiteSpace(core.Country)
            ? "US"
            : core.Country.Trim().ToUpperInvariant();
        return (language, regionCountry.ToLowerInvariant(), regionCountry);
    }

    private static CandidateExtendedMetadata BuildCandidateExtendedMetadata(
        IReadOnlyList<ProviderClaim> claims)
    {
        static string? First(IReadOnlyList<ProviderClaim> claims, params string[] keys)
        {
            foreach (var key in keys)
            {
                var value = claims.FirstOrDefault(c =>
                    string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }

        var genre = First(claims, MetadataFieldConstants.Genre);
        var pageCountValue = First(claims, MetadataFieldConstants.PageCount);
        var durationSecondsValue = First(claims, "duration_sec", "duration_seconds");
        var durationMinutesValue = First(claims, MetadataFieldConstants.DurationField);

        int? pageCount = int.TryParse(pageCountValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPages)
            ? parsedPages
            : null;
        double? durationSeconds = double.TryParse(durationSecondsValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSeconds)
            ? parsedSeconds
            : double.TryParse(durationMinutesValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedMinutes)
                ? parsedMinutes * 60d
                : null;

        return new CandidateExtendedMetadata
        {
            Signals = claims.GroupBy(claim => claim.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase),
            Description = First(claims, MetadataFieldConstants.Description),
            Publisher = First(claims, MetadataFieldConstants.PublisherField, "publisher"),
            Genres = string.IsNullOrWhiteSpace(genre)
                ? null
                : genre.Split(',', ';', '|')
                    .Select(part => part.Trim())
                    .Where(part => part.Length > 0)
                    .ToArray(),
            Language = First(claims, "language"),
            Series = First(claims, MetadataFieldConstants.Series),
            IssueNumber = First(claims, "issue_number", MetadataFieldConstants.SeriesPosition, "issue"),
            PageCount = pageCount,
            DurationSeconds = durationSeconds,
        };
    }

    private static (double StructuralBonus, Dictionary<string, object?> Evidence) ComputeSingleItemStructuralSignal(
        MediaType mediaType,
        IReadOnlyDictionary<string, string> fileHints,
        IReadOnlyList<ProviderClaim> claims)
    {
        var evidence = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        double structuralBonus = 0.0;

        var exactBridgeMatches = claims
            .Where(c => BridgeIdHelper.IsBridgeId(c.Key) && !string.IsNullOrWhiteSpace(c.Value))
            .Count(c => fileHints.TryGetValue(c.Key, out var fileValue)
                && string.Equals(fileValue?.Trim(), c.Value.Trim(), StringComparison.OrdinalIgnoreCase));

        if (exactBridgeMatches > 0)
        {
            structuralBonus += 0.35;
            evidence["exact_bridge_id_matches"] = exactBridgeMatches;
        }

        return (structuralBonus, evidence);
    }

    private static bool AreEquivalentOrdinals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        if (int.TryParse(left, out var leftNumber) && int.TryParse(right, out var rightNumber))
        {
            return leftNumber == rightNumber;
        }

        return string.Equals(left.TrimStart('0'), right.TrimStart('0'), StringComparison.OrdinalIgnoreCase)
            || string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TitleContainsSeriesAnchor(string? title, string? series)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(series))
        {
            return false;
        }

        var normalizedTitle = RetailTextSimilarity.NormalizeComparableText(title);
        var normalizedSeries = RetailTextSimilarity.NormalizeComparableText(series);
        if (string.IsNullOrWhiteSpace(normalizedTitle) || string.IsNullOrWhiteSpace(normalizedSeries))
        {
            return false;
        }

        return normalizedTitle.Contains(normalizedSeries, StringComparison.Ordinal);
    }

    private static bool TryGetDurationSeconds(IReadOnlyDictionary<string, string> fileHints, out double seconds)
    {
        if (TryGetNumericSeconds(fileHints.GetValueOrDefault("duration_sec"), false, out seconds))
        {
            return true;
        }

        return TryParseFlexibleDuration(fileHints.GetValueOrDefault(MetadataFieldConstants.DurationField), out seconds);
    }

    private static bool TryGetDurationSeconds(long? milliseconds, out double seconds)
    {
        seconds = 0.0;
        if (milliseconds is not > 0)
        {
            return false;
        }

        seconds = milliseconds.Value / 1000.0;
        return true;
    }

    private static bool TryParseFlexibleDuration(string? value, out double seconds)
    {
        if (TryGetNumericSeconds(value, true, out seconds))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(value)
            && TimeSpan.TryParse(value, out var timeSpan)
            && timeSpan.TotalSeconds > 0)
        {
            seconds = timeSpan.TotalSeconds;
            return true;
        }

        seconds = 0.0;
        return false;
    }

    private static bool TryParseOrdinal(string? value, out int ordinal)
    {
        ordinal = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var digits = new string(value.Where(char.IsDigit).ToArray());
        return !string.IsNullOrWhiteSpace(digits)
            && int.TryParse(digits, out ordinal)
            && ordinal > 0;
    }

    private sealed record MusicGroupTrackSearchEvidence(
        Guid EntityId,
        string Title,
        MediaEngine.Providers.Services.AppleTrackSearchMatch Match);

    private sealed record MusicGroupCollectionSelection(
        string CollectionId,
        int SupportCount,
        int AlbumExactCount,
        double TotalAlbumScore,
        double TotalScore);

    private static MusicGroupCollectionSelection SelectBestMusicGroupCollection(
        IReadOnlyList<MusicGroupTrackSearchEvidence> evidence)
    {
        return evidence
            .GroupBy(e => e.Match.CollectionId, StringComparer.Ordinal)
            .Select(group => new MusicGroupCollectionSelection(
                CollectionId: group.Key,
                SupportCount: group.Count(),
                AlbumExactCount: group.Count(e => e.Match.AlbumExact),
                TotalAlbumScore: Math.Round(group.Sum(e => e.Match.AlbumScore), 4),
                TotalScore: Math.Round(group.Sum(e => e.Match.Score), 4)))
            .OrderByDescending(candidate => candidate.SupportCount)
            .ThenByDescending(candidate => candidate.AlbumExactCount)
            .ThenByDescending(candidate => candidate.TotalAlbumScore)
            .ThenByDescending(candidate => candidate.TotalScore)
            .First();
    }

    private static bool IsStrongMusicGroupCollectionSelection(
        MusicGroupCollectionSelection selection,
        int queuedTrackCount)
    {
        if (queuedTrackCount <= 1)
        {
            return selection.AlbumExactCount > 0 || selection.TotalAlbumScore >= 0.92;
        }

        return selection.SupportCount >= Math.Min(2, queuedTrackCount)
               && (selection.AlbumExactCount > 0
                   || selection.TotalAlbumScore / Math.Max(1, selection.SupportCount) >= 0.92);
    }

    private static bool IsStrongMusicTrackAlbumAnchor(
        MediaEngine.Providers.Services.AppleTrackSearchMatch match,
        string? album)
    {
        if (string.IsNullOrWhiteSpace(album))
        {
            return false;
        }

        return match.AlbumExact || match.AlbumScore >= 0.92;
    }

    private static bool TryGetNumericSeconds(string? value, bool preferMillisecondsForLargeValues, out double seconds)
    {
        seconds = 0.0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!double.TryParse(value, out var raw) || raw <= 0)
        {
            return false;
        }

        seconds = preferMillisecondsForLargeValues && raw > 20000
            ? raw / 1000.0
            : raw;

        return seconds > 0;
    }

    private static bool DurationsCorroborate(double fileDurationSeconds, double candidateDurationSeconds)
    {
        if (fileDurationSeconds <= 0 || candidateDurationSeconds <= 0)
        {
            return false;
        }

        var absoluteDiff = Math.Abs(fileDurationSeconds - candidateDurationSeconds);
        var relativeDiff = absoluteDiff / Math.Max(fileDurationSeconds, candidateDurationSeconds);
        return absoluteDiff <= 5 || relativeDiff <= 0.15;
    }

    private static int GetOutcomeRank(string outcome) => outcome switch
    {
        "AutoAccepted" => 2,
        "Ambiguous" => 1,
        _ => 0,
    };

    private static bool IsBetterCandidate(RetailMatchCandidate candidate, RetailMatchCandidate? currentBest)
    {
        if (currentBest is null)
        {
            return true;
        }

        var candidateRank = GetOutcomeRank(candidate.Outcome);
        var bestRank = GetOutcomeRank(currentBest.Outcome);
        if (candidateRank != bestRank)
        {
            return candidateRank > bestRank;
        }

        if (Math.Abs(candidate.ScoreTotal - currentBest.ScoreTotal) > 0.0001)
        {
            return candidate.ScoreTotal > currentBest.ScoreTotal;
        }

        return candidate.Rank < currentBest.Rank;
    }

    private static RetailMatchCandidate? SelectIdentityCandidateWhenConfigured(
        IReadOnlyList<RetailMatchCandidate> candidates,
        RetailMatchCandidate? currentBest,
        MediaTypePipeline pipeline)
    {
        if (candidates.Count == 0 || pipeline.Providers.Count == 0)
        {
            return currentBest;
        }

        var identityProviders = pipeline.Providers
            .Where(provider => IsIdentityPurpose(provider.Purpose))
            .OrderBy(provider => provider.Rank)
            .Select(provider => provider.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (identityProviders.Count == 0)
        {
            return currentBest;
        }

        RetailMatchCandidate? identityBest = null;
        foreach (var candidate in candidates)
        {
            if (!identityProviders.Contains(candidate.ProviderName)
                || GetOutcomeRank(candidate.Outcome) == 0)
            {
                continue;
            }

            if (IsBetterCandidate(candidate, identityBest))
            {
                identityBest = candidate;
            }
        }

        if (identityBest?.Outcome == "AutoAccepted")
        {
            return identityBest;
        }

        var fallbackIdentityProviders = pipeline.Providers
            .Where(provider => provider.UseAsIdentityFallback)
            .OrderBy(provider => provider.Rank)
            .Select(provider => provider.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        RetailMatchCandidate? fallbackBest = null;
        foreach (var candidate in candidates)
        {
            if (!fallbackIdentityProviders.Contains(candidate.ProviderName)
                || GetOutcomeRank(candidate.Outcome) == 0)
            {
                continue;
            }

            if (IsBetterCandidate(candidate, fallbackBest))
            {
                fallbackBest = candidate;
            }
        }

        if (fallbackBest?.Outcome == "AutoAccepted")
        {
            return fallbackBest;
        }

        return identityBest ?? fallbackBest ?? currentBest;
    }

    private static bool IsIdentityPurpose(string? purpose) =>
        string.Equals(purpose, "identity", StringComparison.OrdinalIgnoreCase);

    private static bool IsEnrichmentPurpose(string? purpose) =>
        string.Equals(purpose, "enrichment", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldPersistProviderClaims(
        RetailDecision decision,
        PipelineProviderEntry? pipelineEntry,
        bool acceptedIdentity,
        FieldMatchScores retailScore,
        IReadOnlyList<ProviderClaim> claims)
    {
        if (decision.Outcome != "Rejected")
        {
            return true;
        }

        if (!acceptedIdentity
            || pipelineEntry?.RequiresIdentity != true
            || !IsEnrichmentPurpose(pipelineEntry.Purpose))
        {
            return false;
        }

        if (!claims.Any(IsEnrichmentClaim))
        {
            return false;
        }

        return retailScore.TitleScore >= 0.85
               && retailScore.AuthorScore >= 0.75;
    }

    private static bool IsEnrichmentClaim(ProviderClaim claim)
        => claim.Key is MetadataFieldConstants.CoverUrl
            or MetadataFieldConstants.Genre
            or MetadataFieldConstants.Year
            or MetadataFieldConstants.TrackNumber
            or MetadataFieldConstants.DurationField
            or BridgeIdKeys.AppleMusicId
            or BridgeIdKeys.AppleMusicCollectionId
            or BridgeIdKeys.AppleArtistId
            or "disc_number"
            or "disc_count"
            or "track_count";

}
