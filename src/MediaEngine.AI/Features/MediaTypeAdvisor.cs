using MediaEngine.AI.EngineRoom;
using MediaEngine.AI.Infrastructure;
using MediaEngine.AI.Llama;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.AI.Features;

/// <summary>
/// AI-powered media type classification. Replaces heuristic disambiguation
/// in AudioProcessor and VideoProcessor.
/// </summary>
public sealed class MediaTypeAdvisor : IMediaTypeAdvisor
{
    private static readonly AiCaller Caller = AiCaller.Ingestion(AiCallerIds.TypeLogic);

    private readonly IAiEngineRoom _engineRoom;
    private readonly IAiFeatureSwitches _switches;
    private readonly ILogger<MediaTypeAdvisor> _logger;

    public MediaTypeAdvisor(
        IAiEngineRoom engineRoom,
        IAiFeatureSwitches switches,
        ILogger<MediaTypeAdvisor> logger)
    {
        _engineRoom = engineRoom;
        _switches = switches;
        _logger = logger;
    }

    public async Task<MediaTypeCandidate> ClassifyAsync(
        string filename,
        string? container,
        double? durationSeconds,
        int? bitrate,
        string? genre,
        bool hasChapters,
        string? folderPath,
        CancellationToken ct = default)
    {
        // Short-circuit when the feature is disabled.
        if (!_switches.IsOn(AiFeature.TypeLogic))
        {
            return new MediaTypeCandidate
            {
                Type = MediaType.Unknown,
                Confidence = 0,
                Reason = "AI type classification disabled",
            };
        }

        try
        {
            var prompt = PromptTemplates.MediaTypePrompt(
                filename, container, durationSeconds, bitrate, genre, hasChapters, folderPath);

            var result = await _engineRoom.ExtractJsonOrThrowAsync<MediaTypeResponse>(
                Caller,
                AiAbility.Classify,
                prompt,
                PromptTemplates.MediaTypeGrammar,
                ct: ct);

            if (result is null || string.IsNullOrWhiteSpace(result.Type))
            {
                _logger.LogWarning("MediaTypeAdvisor returned null for: {Filename}", filename);
                return new MediaTypeCandidate
                {
                    Type = MediaType.Unknown,
                    Confidence = 0.1,
                    Reason = "AI classification returned empty result",
                };
            }

            var mediaType = MediaTypeParser.Parse(result.Type);
            var confidence = Math.Clamp(result.Confidence, 0.0, 1.0);

            _logger.LogInformation(
                "MediaTypeAdvisor: {Filename} → {Type} ({Conf:F2}) — {Reason}",
                filename, mediaType, confidence, result.Reason ?? "no reason");

            return new MediaTypeCandidate
            {
                Type = mediaType,
                Confidence = confidence,
                Reason = result.Reason ?? $"AI classified as {mediaType}",
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MediaTypeAdvisor failed for: {Filename}", filename);
            return new MediaTypeCandidate
            {
                Type = MediaType.Unknown,
                Confidence = 0.1,
                Reason = $"AI classification error: {ex.Message}",
            };
        }
    }

    /// <summary>Internal DTO matching the GBNF grammar output.</summary>
    private sealed class MediaTypeResponse
    {
        public string? Type { get; set; }
        public double Confidence { get; set; }
        public string? Reason { get; set; }
    }
}
