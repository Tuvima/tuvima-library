using System.Text;
using MediaEngine.AI.EngineRoom;
using MediaEngine.AI.Infrastructure;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.AI.Features;

public sealed class TasteProfiler : ITasteProfiler
{
    private const int MinimumTasteSignals = 3;
    private const int MaximumTasteSignals = 500;
    private const string PlainSummary = "Profile generated from this profile's listening, reading, and viewing history.";

    private static readonly AiCaller Caller = AiCaller.Person(AiCallerIds.TasteSummary);

    private readonly IAiEngineRoom _engineRoom;
    private readonly IAiFeatureSwitches _switches;
    private readonly ITasteProfileRepository _profiles;
    private readonly ILogger<TasteProfiler> _logger;

    public TasteProfiler(
        IAiEngineRoom engineRoom,
        IAiFeatureSwitches switches,
        ITasteProfileRepository profiles,
        ILogger<TasteProfiler> logger)
    {
        ArgumentNullException.ThrowIfNull(engineRoom);
        ArgumentNullException.ThrowIfNull(switches);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(logger);
        _engineRoom = engineRoom;
        _switches = switches;
        _profiles = profiles;
        _logger = logger;
    }

    public async Task<TasteProfileBuildResult> GetProfileAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        _logger.LogDebug("TasteProfiler: building profile-scoped taste for {UserId}", userId);
        var signals = await _profiles.GetSignalsAsync(userId, MaximumTasteSignals, ct)
            .ConfigureAwait(false);
        var inputFingerprint = ComputeInputFingerprint(signals);
        if (signals.Count < MinimumTasteSignals)
        {
            return new TasteProfileBuildResult(
                TasteProfileBuildStatus.InsufficientData,
                userId,
                Profile: null,
                signals.Count,
                inputFingerprint,
                $"At least {MinimumTasteSignals} profile interactions are required; found {signals.Count}.");
        }

        var genreWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var eraWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var typeWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var moodWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var signal in signals)
        {
            var weight = Math.Max(0.10, Math.Clamp(signal.ProgressPct, 0, 100) / 100.0);
            foreach (var genre in signal.Genres.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                Increment(genreWeights, genre.Trim().ToLowerInvariant(), weight);
            }

            foreach (var mood in signal.Moods.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                Increment(moodWeights, mood.Trim().ToLowerInvariant(), weight);
            }

            if (signal.ReleaseYear is > 0)
            {
                Increment(eraWeights, $"{(signal.ReleaseYear.Value / 10) * 10}s", weight);
            }

            if (!string.IsNullOrWhiteSpace(signal.MediaType))
            {
                Increment(typeWeights, signal.MediaType.Trim(), weight);
            }
        }

        var genreDistribution = ToDistribution(genreWeights);
        var eraDistribution = ToDistribution(eraWeights);
        var typeDistribution = ToDistribution(typeWeights);
        var moodDistribution = ToDistribution(moodWeights);
        var summary = await GenerateSummaryAsync(
            genreDistribution,
            eraDistribution,
            typeDistribution,
            ct).ConfigureAwait(false);

        var profile = new TasteProfile
        {
            UserId = userId,
            GenreDistribution = genreDistribution,
            EraPreferences = eraDistribution,
            MediaTypeMix = typeDistribution,
            MoodPreferences = moodDistribution,
            Summary = summary,
            LastUpdatedAt = DateTimeOffset.UtcNow,
        };

        return new TasteProfileBuildResult(
            TasteProfileBuildStatus.Generated,
            userId,
            profile,
            signals.Count,
            inputFingerprint);
    }

    private async Task<string> GenerateSummaryAsync(
        IReadOnlyDictionary<string, double> genres,
        IReadOnlyDictionary<string, double> eras,
        IReadOnlyDictionary<string, double> mediaTypes,
        CancellationToken ct)
    {
        // The summary sentence is the Taste summary plugin's work; the numbers above it never need Local AI.
        if (!_switches.IsOn(AiFeature.TasteSummary))
        {
            return PlainSummary;
        }

        var topGenres = string.Join(", ", genres.OrderByDescending(pair => pair.Value).Take(3).Select(pair => pair.Key));
        var topTypes = string.Join(", ", mediaTypes.OrderByDescending(pair => pair.Value).Take(3).Select(pair => pair.Key));
        var topEras = string.Join(", ", eras.OrderByDescending(pair => pair.Value).Take(2).Select(pair => pair.Key));
        var prompt = $"""
            Summarize this reader/viewer's taste profile in 1-2 sentences.
            Favorite genres: {(string.IsNullOrEmpty(topGenres) ? "mixed" : topGenres)}
            Preferred media types: {(string.IsNullOrEmpty(topTypes) ? "mixed" : topTypes)}
            Favorite eras: {(string.IsNullOrEmpty(topEras) ? "mixed" : topEras)}
            Be concise and friendly. Do not use bullet points.
            """;

        try
        {
            var summary = await _engineRoom.WriteTextOrThrowAsync(Caller, AiAbility.WriteTextQuick, prompt, ct: ct)
                .ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(summary)
                ? PlainSummary
                : summary.Trim();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TasteProfiler: summary generation failed; using a local fallback");
            return PlainSummary;
        }
    }

    private static string ComputeInputFingerprint(IReadOnlyList<TasteSignal> signals)
    {
        var payload = new StringBuilder();
        foreach (var signal in signals.OrderBy(value => value.AssetId))
        {
            payload.Append(signal.AssetId.ToString("N")).Append('|')
                .Append(signal.ProgressPct.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                .Append(signal.LastAccessed.ToUniversalTime().ToString("O")).Append('|')
                .Append(signal.MediaType).Append('|')
                .Append(signal.ReleaseYear).Append('|')
                .AppendJoin(',', signal.Genres.Order(StringComparer.OrdinalIgnoreCase)).Append('|')
                .AppendJoin(',', signal.Moods.Order(StringComparer.OrdinalIgnoreCase)).Append('\n');
        }

        // Casing contract is lowercase hex via the shared helper (previously uppercase
        // Convert.ToHexString) — this is a deliberate one-time change; it invalidates
        // previously stored fingerprints and triggers a one-time taste-profile rebuild.
        return Hashing.Sha256Hex(payload.ToString());
    }

    private static void Increment(Dictionary<string, double> values, string key, double weight) =>
        values[key] = values.GetValueOrDefault(key) + weight;

    private static IReadOnlyDictionary<string, double> ToDistribution(Dictionary<string, double> weights)
    {
        if (weights.Count == 0)
        {
            return new Dictionary<string, double>();
        }

        var total = weights.Values.Sum();
        return weights.ToDictionary(
            pair => pair.Key,
            pair => Math.Round(pair.Value / total, 4),
            StringComparer.OrdinalIgnoreCase);
    }
}
