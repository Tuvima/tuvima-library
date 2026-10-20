using System.Text.RegularExpressions;
using MediaEngine.AI.Configuration;
using MediaEngine.AI.EngineRoom;
using MediaEngine.AI.Llama;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.AI.Features;

/// <summary>
/// AI-powered filename cleaning. Replaces TitleNormalizer.
/// Uses the text_quality model with GBNF grammar constraints.
/// </summary>
public sealed class SmartLabeler : ISmartLabeler
{
    private static readonly AiCaller Caller = AiCaller.Ingestion(AiCallerIds.SmartLabeling);

    private readonly IAiEngineRoom _engineRoom;
    private readonly AiSettings _settings;
    private readonly ILogger<SmartLabeler> _logger;

    public SmartLabeler(
        IAiEngineRoom engineRoom,
        AiSettings settings,
        ILogger<SmartLabeler> logger)
    {
        _engineRoom = engineRoom;
        _settings = settings;
        _logger = logger;
    }

    public async Task<CleanedSearchQuery> CleanAsync(string rawFilename, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawFilename))
        {
            return new CleanedSearchQuery { Title = string.Empty, Confidence = 0 };
        }

        // Short-circuit when the feature is disabled — use regex-based extraction
        // for season/episode patterns so the ingestion pipeline still gets structured
        // TV metadata without invoking the LLM.
        if (!_settings.Features.SmartLabeling)
        {
            var fallbackStem = Path.GetFileNameWithoutExtension(rawFilename);
            var cleaned = fallbackStem.Replace('.', ' ').Replace('_', ' ').Trim();
            var (series, season, episode) = ExtractSeasonEpisode(cleaned);

            return new CleanedSearchQuery
            {
                Title = series ?? fallbackStem,
                Season = season,
                Episode = episode,
                Confidence = 0.5,
            };
        }

        // Strip extension if present.
        var stem = Path.GetFileNameWithoutExtension(rawFilename);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = rawFilename;
        }

        try
        {
            var prompt = PromptTemplates.SmartLabelingPrompt(stem);
            var result = await _engineRoom.ExtractJsonOrThrowAsync<SmartLabelingResponse>(
                Caller,
                AiAbility.ExtractStructured,
                prompt,
                PromptTemplates.SmartLabelingGrammar,
                ct: ct);

            if (result is null || string.IsNullOrWhiteSpace(result.Title))
            {
                _logger.LogWarning("SmartLabeler returned null/empty for: {Filename}", stem);
                return new CleanedSearchQuery
                {
                    Title = stem,
                    Confidence = 0.1,
                };
            }

            // Validate year range.
            int? year = result.Year;
            if (year.HasValue && (year < 1800 || year > 2100))
            {
                year = null;
            }

            // Validate season/episode.
            int? season = result.Season;
            int? episode = result.Episode;
            if (season.HasValue && (season < 0 || season > 100))
            {
                season = null;
            }

            if (episode.HasValue && (episode < 0 || episode > 9999))
            {
                episode = null;
            }

            // Clamp confidence.
            var confidence = Math.Clamp(result.Confidence, 0.0, 1.0);

            _logger.LogInformation(
                "SmartLabeler: \"{Raw}\" → \"{Title}\" (author: {Author}, year: {Year}, confidence: {Conf:F2})",
                stem, result.Title, result.Author ?? "none", year?.ToString() ?? "none", confidence);

            return new CleanedSearchQuery
            {
                Title = result.Title.Trim(),
                Author = string.IsNullOrWhiteSpace(result.Author) ? null : result.Author.Trim(),
                Year = year,
                Season = season,
                Episode = episode,
                Confidence = confidence,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SmartLabeler failed for: {Filename}", stem);
            return new CleanedSearchQuery
            {
                Title = stem,
                Confidence = 0.1,
            };
        }
    }

    // ── Regex-based season/episode extraction ─────────────────────────────
    // Used when AI Smart Labeling is disabled — provides basic structured
    // extraction for common TV filename patterns.

    /// <summary>1x01 format.</summary>
    private static readonly Regex NxNNRegex = new(
        @"^(?<series>.+?)\s*[.\-_ ]+(?<season>\d{1,2})[Xx](?<ep1>\d{1,4})",
        RegexOptions.Compiled);

    /// <summary>Season 1 Episode 1 (verbose).</summary>
    private static readonly Regex VerboseRegex = new(
        @"^(?<series>.+?)\s*[.\-_ ]*Season\s*(?<season>\d{1,2})\s*Episode\s*(?<ep1>\d{1,4})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Extracts series title, season number, and episode number from a cleaned
    /// filename using common TV naming patterns (S01E01, 1x01, Season 1 Episode 1).
    /// Returns (null, null, null) when no pattern matches.
    /// </summary>
    private static (string? SeriesTitle, int? Season, int? Episode) ExtractSeasonEpisode(string text)
    {
        var m = EpisodePatterns.SeasonEpisode().Match(text);
        if (m.Success)
        {
            return (m.Groups["series"].Value.TrimEnd('.', '-', '_', ' '),
                    int.Parse(m.Groups["season"].Value),
                    int.Parse(m.Groups["ep1"].Value));
        }

        m = NxNNRegex.Match(text);
        if (m.Success)
        {
            return (m.Groups["series"].Value.TrimEnd('.', '-', '_', ' '),
                    int.Parse(m.Groups["season"].Value),
                    int.Parse(m.Groups["ep1"].Value));
        }

        m = VerboseRegex.Match(text);
        if (m.Success)
        {
            return (m.Groups["series"].Value.TrimEnd('.', '-', '_', ' '),
                    int.Parse(m.Groups["season"].Value),
                    int.Parse(m.Groups["ep1"].Value));
        }

        return (null, null, null);
    }

    /// <summary>Internal DTO matching the GBNF grammar output.</summary>
    private sealed class SmartLabelingResponse
    {
        public string? Title { get; set; }
        public string? Author { get; set; }
        public int? Year { get; set; }
        public int? Season { get; set; }
        public int? Episode { get; set; }
        public double Confidence { get; set; }
    }
}
