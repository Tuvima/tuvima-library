using MediaEngine.AI.EngineRoom;
using MediaEngine.AI.Infrastructure;
using MediaEngine.AI.Llama;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using Microsoft.Extensions.Logging;

namespace MediaEngine.AI.Features;

/// <summary>
/// LLM-powered description analysis. Gathers all available descriptions for an entity,
/// runs a single GBNF-constrained inference, and returns structured intelligence:
/// people, themes, mood, setting, time period, audience, content warnings, pace, TL;DR.
/// </summary>
public sealed class DescriptionIntelligenceService : IDescriptionIntelligenceService
{
    private static readonly AiCaller Caller = AiCaller.LibraryBackground(AiCallerIds.DescriptionIntelligence);

    private readonly IAiEngineRoom _engineRoom;
    private readonly IAiFeatureSwitches _switches;
    private readonly IVibeVocabularySource _vocabulary;
    private readonly ICanonicalValueRepository _canonicalRepo;
    private readonly ILogger<DescriptionIntelligenceService> _logger;

    public DescriptionIntelligenceService(
        IAiEngineRoom engineRoom,
        IAiFeatureSwitches switches,
        IVibeVocabularySource vocabulary,
        ICanonicalValueRepository canonicalRepo,
        ILogger<DescriptionIntelligenceService> logger)
    {
        _engineRoom = engineRoom;
        _switches = switches;
        _vocabulary = vocabulary;
        _canonicalRepo = canonicalRepo;
        _logger = logger;
    }

    public async Task<DescriptionIntelligenceResult?> AnalyzeAsync(
        Guid entityId,
        string mediaCategory,
        CancellationToken ct = default)
    {
        if (!_switches.IsOn(AiFeature.DescriptionIntelligence))
        {
            _logger.LogDebug("[DESCRIPTION-INTEL] Feature disabled — skipping for {Id}", entityId);
            return null;
        }

        try
        {
            // NOTE: Descriptions are in the configured metadata language. For best AI accuracy,
            // the hydration pipeline should request English Wikipedia summaries (Len) alongside
            // the metadata-language version. For now, the LLM handles non-English input with
            // reduced accuracy — Llama 3.x has multilingual understanding for Latin scripts.

            // Gather all available descriptions from canonical values.
            var canonicals = await _canonicalRepo.GetByEntityAsync(entityId, ct).ConfigureAwait(false);
            if (canonicals is null || canonicals.Count == 0)
            {
                return null;
            }

            var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cv in canonicals)
            {
                if (string.Equals(cv.Key, "plot_summary", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(cv.Value))
                {
                    descriptions["Wikipedia Plot"] = cv.Value;
                }
                else if (string.Equals(cv.Key, "description", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(cv.Value))
                {
                    descriptions["Description"] = cv.Value;
                }
            }

            if (descriptions.Count == 0)
            {
                _logger.LogDebug("[DESCRIPTION-INTEL] No descriptions available for {Id}", entityId);
                return null;
            }

            // Build combined description text with source labels.
            // Priority: Wikipedia plot (richest) → Description (Apple API/Wikipedia extract)
            // Truncate per source, cap total at 2000 chars to stay within the 3B model's
            // context window (4096 tokens) and allow enough room for the ~400-token output.
            var parts = new List<string>();
            int remaining = 2000;

            foreach (var (label, text) in descriptions.OrderByDescending(d =>
                d.Key.Contains("Plot", StringComparison.OrdinalIgnoreCase) ? 2 : 1))
            {
                var maxLen = label.Contains("Plot", StringComparison.OrdinalIgnoreCase) ? 1200 : 600;
                var truncated = text.Length > maxLen ? text[..maxLen] + "..." : text;
                if (truncated.Length > remaining)
                {
                    truncated = truncated[..remaining];
                }

                if (truncated.Length > 0)
                {
                    parts.Add($"[{label}]: {truncated}");
                    remaining -= truncated.Length;
                }
                if (remaining <= 0)
                {
                    break;
                }
            }

            var combinedDescriptions = string.Join("\n\n", parts);

            // Get title from canonicals.
            var title = canonicals.FirstOrDefault(c =>
                string.Equals(c.Key, "title", StringComparison.OrdinalIgnoreCase))?.Value ?? "Unknown";

            // Get mood vocabulary for this media category.
            var moodVocab = _vocabulary.Current.GetForCategory(mediaCategory);

            // Logical workloads share the one text artifact selected by resource profile.
            const AiAbility ability = AiAbility.WriteTextLong;

            _logger.LogInformation(
                "[DESCRIPTION-INTEL] Analyzing {Title} ({Category}) — {DescLen} chars of descriptions, ability={Ability}",
                title, mediaCategory, combinedDescriptions.Length, ability);

            // ── Pass 1: Vocabulary extraction (themes, mood, setting, etc.) ──
            var vocabPrompt = PromptTemplates.DescriptionIntelligencePrompt(
                title, mediaCategory, moodVocab, combinedDescriptions);

            var vocabResult = await _engineRoom.ExtractJsonOrThrowAsync<VocabularyResponse>(
                Caller,
                ability,
                vocabPrompt,
                PromptTemplates.DescriptionIntelligenceGrammar,
                ct: ct);

            if (vocabResult is null)
            {
                _logger.LogWarning("[DESCRIPTION-INTEL] Pass 1 (vocabulary) returned null for {Title}", title);
                return null;
            }

            // ── Pass 2: People extraction (separate, simpler grammar) ────────
            var validPeople = new List<ExtractedPersonRef>();
            try
            {
                var peoplePrompt = PromptTemplates.DescriptionIntelligencePeoplePrompt(
                    title, combinedDescriptions);

                var peopleResult = await _engineRoom.ExtractJsonOrThrowAsync<PeopleResponse>(
                    Caller,
                    ability,
                    peoplePrompt,
                    PromptTemplates.DescriptionIntelligencePeopleGrammar,
                    ct: ct);

                if (peopleResult?.People is not null)
                {
                    validPeople = peopleResult.People
                        .Where(p => !string.IsNullOrWhiteSpace(p.Name) && p.Name.Contains(' '))
                        .Select(p => new ExtractedPersonRef(
                            p.Name.Trim(),
                            NormalizeRole(p.Role),
                            ClaimConfidence.AiDescription))
                        .ToList();
                }
            }
            catch (Exception ex) when (ex is not AiRequestDeferredException)
            {
                _logger.LogDebug(ex, "[DESCRIPTION-INTEL] Pass 2 (people) failed for {Title} — continuing without", title);
            }

            // ── Validate and build result ────────────────────────────────────
            var validMood = (vocabResult.Mood ?? [])
                .Where(m => moodVocab.Contains(m, StringComparer.OrdinalIgnoreCase))
                .Take(3)
                .ToList();

            var intelligence = new DescriptionIntelligenceResult
            {
                People = validPeople,
                Themes = (vocabResult.Themes ?? []).Take(5).ToList(),
                Mood = validMood,
                Setting = string.IsNullOrWhiteSpace(vocabResult.Setting) ? null : vocabResult.Setting.Trim(),
                TimePeriod = string.IsNullOrWhiteSpace(vocabResult.TimePeriod) ? null : vocabResult.TimePeriod.Trim(),
                Audience = string.IsNullOrWhiteSpace(vocabResult.Audience) ? null : vocabResult.Audience.Trim(),
                ContentWarnings = (vocabResult.ContentWarnings ?? []).Take(5).ToList(),
                Pace = string.IsNullOrWhiteSpace(vocabResult.Pace) ? null : vocabResult.Pace.Trim(),
                Tldr = string.IsNullOrWhiteSpace(vocabResult.Tldr) ? null : vocabResult.Tldr.Trim(),
            };

            _logger.LogInformation(
                "[DESCRIPTION-INTEL] {Title}: {People} people, {Themes} themes, {Mood} mood, setting={Setting}, tldr={HasTldr}",
                title, intelligence.People.Count, intelligence.Themes.Count, intelligence.Mood.Count,
                intelligence.Setting ?? "none", intelligence.Tldr is not null);

            return intelligence;
        }
        catch (AiRequestDeferredException)
        {
            // The machine became busy: the batch skips this run and tries again later, as it did before the engine room.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DESCRIPTION-INTEL] Failed for entity {Id}", entityId);
            return null;
        }
    }

    private static string NormalizeRole(string? role) => (role?.Trim().ToLowerInvariant()) switch
    {
        "narrator" => "Narrator",
        "translator" => "Translator",
        "editor" => "Editor",
        "director" => "Director",
        "cast" => "Actor",
        "host" => "Host",
        "producer" => "Producer",
        "author" => "Author",
        _ => "Author",
    };

    /// <summary>Pass 1 DTO: vocabulary fields (no people).</summary>
    private sealed class VocabularyResponse
    {
        public List<string>? Themes { get; set; }
        public List<string>? Mood { get; set; }
        public string? Setting { get; set; }
        public string? TimePeriod { get; set; }
        public string? Audience { get; set; }
        public List<string>? ContentWarnings { get; set; }
        public string? Pace { get; set; }
        public string? Tldr { get; set; }
    }

    /// <summary>Pass 2 DTO: people extraction.</summary>
    private sealed class PeopleResponse
    {
        public List<PersonDto>? People { get; set; }
    }

    private sealed class PersonDto
    {
        public string Name { get; set; } = "";
        public string? Role { get; set; }
    }
}
