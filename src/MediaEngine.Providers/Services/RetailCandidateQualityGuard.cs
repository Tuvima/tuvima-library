using System.Text;
using MediaEngine.Domain;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;

namespace MediaEngine.Providers.Services;

internal static class RetailCandidateQualityGuard
{
    private static readonly string[] StrongDerivativePhrases =
    [
        "analysis",
        "book analysis",
        "summary",
        "book summary",
        "study guide",
        "study guides",
        "companion",
        "workbook",
        "commentary",
        "literary criticism",
        "criticism",
        "cliffsnotes",
        "sparknotes",
        "chapter summary",
        "reading guide",
        "review guide",
    ];

    /// <summary>Words that make a store listing a bundle of several books, not the one book in the file.</summary>
    private static readonly string[] BoxSetPhrases =
    [
        "box set",
        "boxed set",
        "boxset",
        "trilogy",
        "omnibus",
        "complete collection",
        "complete series",
        "book set",
        "books 1",
    ];

    private static readonly string[] DescriptionDerivativePhrases =
    [
        "analysis of",
        "summary of",
        "study guide",
        "companion to",
        "chapter by chapter",
        "literary criticism",
        "reading guide",
        "workbook",
    ];

    private static readonly string[] GenreDerivativePhrases =
    [
        "study aids",
        "literary criticism",
        "education",
        "reference",
    ];

    public static IReadOnlyList<string> GetRejectionReasons(
        MediaType mediaType,
        IReadOnlyDictionary<string, string> fileHints,
        string? candidateTitle,
        CandidateExtendedMetadata? metadata)
    {
        if (mediaType is not (MediaType.Books or MediaType.Audiobooks))
        {
            return [];
        }

        // A store bundle ("The Hunger Games Trilogy", "... Box Set") never stands for the single book in the
        // file, unless the file itself is named as a bundle.
        var fileTitle = fileHints.GetValueOrDefault(MetadataFieldConstants.Title);
        if (ContainsAny(Normalize(candidateTitle), BoxSetPhrases) && !ContainsAny(Normalize(fileTitle), BoxSetPhrases))
        {
            return ["box_set_candidate"];
        }

        var sourceLooksDerivative = LooksDerivative(
            fileHints.GetValueOrDefault(MetadataFieldConstants.Title),
            fileHints.GetValueOrDefault(MetadataFieldConstants.Description),
            SplitList(fileHints.GetValueOrDefault(MetadataFieldConstants.Genre)));

        if (sourceLooksDerivative)
        {
            return [];
        }

        var candidateLooksDerivative = LooksDerivative(
            candidateTitle,
            metadata?.Description,
            metadata?.Genres);

        return candidateLooksDerivative
            ? ["derivative_candidate"]
            : [];
    }

    public static bool LooksDerivative(
        string? title,
        string? description = null,
        IReadOnlyList<string>? genres = null)
    {
        var normalizedTitle = Normalize(title);
        if (ContainsAny(normalizedTitle, StrongDerivativePhrases))
        {
            return true;
        }

        var normalizedDescription = Normalize(description);
        if (ContainsAny(normalizedDescription, DescriptionDerivativePhrases))
        {
            return true;
        }

        if (genres is not null)
        {
            foreach (var genre in genres)
            {
                if (ContainsAny(Normalize(genre), GenreDerivativePhrases))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IReadOnlyList<string> SplitList(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', ';', '|')
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
                .ToList();

    private static bool ContainsAny(string value, IReadOnlyList<string> phrases)
        => !string.IsNullOrWhiteSpace(value)
           && phrases.Any(phrase => value.Contains(phrase, StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Normalize(NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
