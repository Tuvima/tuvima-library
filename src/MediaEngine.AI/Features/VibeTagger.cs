using MediaEngine.AI.EngineRoom;
using MediaEngine.AI.Infrastructure;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MediaEngine.AI.Features;

public sealed class VibeTagger : IVibeTagger
{
    private static readonly AiCaller Caller = AiCaller.LibraryBackground(AiCallerIds.VibeTags);

    private readonly IAiEngineRoom _engineRoom;
    private readonly IVibeVocabularySource _vocabulary;
    private readonly ILogger<VibeTagger> _logger;

    public VibeTagger(IAiEngineRoom engineRoom, IVibeVocabularySource vocabulary, ILogger<VibeTagger> logger)
    {
        _engineRoom = engineRoom;
        _vocabulary = vocabulary;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> TagAsync(
        string title,
        string? wikipediaSummary,
        IReadOnlyList<string> genres,
        string mediaCategory,
        CancellationToken ct = default)
    {
        // NOTE: Wikipedia summary may be in the metadata language rather than English.
        // The English vibe vocabulary still works — Llama 3.x extracts mood/atmosphere
        // across Latin-script languages. CJK input has reduced accuracy.

        var vocabulary = _vocabulary.Current.GetForCategory(mediaCategory);
        if (vocabulary.Count == 0 || string.IsNullOrWhiteSpace(wikipediaSummary))
        {
            return [];
        }

        var vocabList = string.Join(", ", vocabulary);
        var genreList = genres.Count > 0 ? string.Join(", ", genres) : "unknown";

        var prompt = $"""
            You select mood/vibe tags for media from a controlled vocabulary.
            Select 3-5 tags that best describe this work's mood and feel.
            Base your analysis on the Wikipedia summary content, NOT on who created it.
            Return ONLY a JSON array of tag strings from the vocabulary.

            Vocabulary: [{vocabList}]
            Title: {title}
            Genres: {genreList}
            Category: {mediaCategory}

            Wikipedia summary:
            {wikipediaSummary[..Math.Min(wikipediaSummary.Length, 1500)]}
            """;

        var grammar = """
            root   ::= "[" ws (string ("," ws string)*)? ws "]"
            string ::= "\"" ([^"\\] | "\\" .)* "\""
            ws     ::= [ \t\n]*
            """;

        var result = await _engineRoom.ExtractJsonOrThrowAsync<List<string>>(
            Caller, AiAbility.Classify, prompt, grammar, ct: ct);

        if (result is null || result.Count == 0)
        {
            _logger.LogDebug("VibeTagger returned empty for \"{Title}\"", title);
            return [];
        }

        // Filter to only tags in the vocabulary.
        var validTags = result
            .Where(t => vocabulary.Contains(t, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();

        _logger.LogInformation("VibeTagger: \"{Title}\" → [{Tags}]", title, string.Join(", ", validTags));
        return validTags;
    }
}
