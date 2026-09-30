using System.Text;
using System.Text.RegularExpressions;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Scores retail search results against file metadata using tiered matching.
/// Tiers 1-3 use exact ID comparison. Tier 4 uses fuzzy text matching.
/// Cross-field signals (narrator-in-description, genre overlap, etc.) provide
/// additional boost/penalty to the composite score.
/// Weights are read from <c>config/hydration.json</c> → <c>fuzzy_match_weights</c>.
/// </summary>
public sealed class RetailMatchScoringService : IRetailMatchScoringService
{
    private readonly IFuzzyMatchingService _fuzzy;
    private readonly IConfigurationLoader _configLoader;
    private readonly ICoverArtHashService? _coverArtHash;
    private readonly ILogger<RetailMatchScoringService>? _logger;

    public RetailMatchScoringService(
        IFuzzyMatchingService fuzzy,
        IConfigurationLoader configLoader,
        ICoverArtHashService? coverArtHash = null,
        ILogger<RetailMatchScoringService>? logger = null)
    {
        _fuzzy = fuzzy;
        _configLoader = configLoader;
        _coverArtHash = coverArtHash;
        _logger = logger;
    }

    public FieldMatchScores ScoreCandidate(
        IReadOnlyDictionary<string, string> fileHints,
        string? candidateTitle,
        string? candidateAuthor,
        string? candidateYear,
        MediaType mediaType,
        MatchTierConfig? matchTiers = null,
        CandidateExtendedMetadata? extendedMetadata = null,
        double structuralBonus = 0.0)
    {
        var hydration = _configLoader.LoadHydration();
        var weights = hydration.FuzzyMatchWeights;

        // ── Title score ──────────────────────────────────────────────────
        // For TV episodes, prefer episode_title so we score against the candidate
        // episode name (e.g. "Pilot") rather than the show name (e.g. "Breaking Bad").
        // Show name is still used for the upstream provider URL via ShowName/{title}
        // substitution and is verified separately via the structural S/E boost.
        double titleScore = 0.0;
        var fileTitle = mediaType == MediaType.TV
            ? (fileHints.GetValueOrDefault("episode_title") ?? fileHints.GetValueOrDefault("title"))
            : fileHints.GetValueOrDefault("title");
        var isTvSeriesLookup = mediaType == MediaType.TV
            && !fileHints.ContainsKey("episode_title")
            && fileHints.ContainsKey("show_name");
        var comicIssueIdentityMatches = IsExactComicIssueIdentity(fileHints, extendedMetadata);
        var fileTitleIsComicIssueLabel = mediaType == MediaType.Comics
            && IsGeneratedComicIssueLabel(fileTitle, fileHints);

        if (MediaEngine.Domain.Services.PlaceholderTitleDetector.IsPlaceholder(fileTitle))
        {
            return new FieldMatchScores
            {
                TitleScore = 0.0,
                AuthorScore = 0.0,
                YearScore = 0.0,
                FormatScore = 0.0,
                CrossFieldBoost = 0.0,
                CoverArtScore = 0.0,
                CompositeScore = 0.0,
            };
        }

        if (!string.IsNullOrWhiteSpace(fileTitle) && !string.IsNullOrWhiteSpace(candidateTitle))
        {
            titleScore = comicIssueIdentityMatches
                ? 1.0
                : AreEquivalentComparableText(fileTitle, candidateTitle)
                ? 1.0
                : _fuzzy.ComputeTokenSetRatio(fileTitle, candidateTitle);
            // Token-set matching treats "Solo Leveling" and the distinct
            // documentary "The Leveling of Solo Leveling" as equally exact.
            // For a show lookup, extra title words must lower the score.
            if (isTvSeriesLookup
                && !AreEquivalentComparableText(fileTitle, candidateTitle))
            {
                titleScore = Math.Min(titleScore,
                    RetailTextSimilarity.ComputeWordOverlap(fileTitle, candidateTitle));
            }
        }
        else if (comicIssueIdentityMatches)
        {
            titleScore = 1.0;
        }

        // ── Author score ─────────────────────────────────────────────────
        double authorScore = 0.0;
        // For music files, "artist" is the primary creator field, not "author".
        // For video/comics, "director" or "writer" may be the primary creator.
        // The show's own name is a useful parent hint for an episode lookup,
        // but it is not the creator of the show being matched here.
        var fileAuthor = isTvSeriesLookup ? null : RetailHints.GetCreatorHint(fileHints, mediaType);
        if (!string.IsNullOrWhiteSpace(fileAuthor) && !string.IsNullOrWhiteSpace(candidateAuthor))
        {
            var creatorListMode = _configLoader.LoadPipelines()
                .GetPipelineForMediaType(mediaType)
                .Scoring.CreatorListMode;
            authorScore = ComputeCreatorScore(fileAuthor, candidateAuthor, creatorListMode);
        }
        else if (string.IsNullOrWhiteSpace(candidateAuthor) && !string.IsNullOrWhiteSpace(fileAuthor))
        {
            // File has a creator but the provider returned no author data — 0.0 per spec
            // (weak evidence: this candidate can't be verified against the known creator).
            authorScore = 0.0;
        }
        // When BOTH file and candidate have no author data (e.g. Movies, TV, Comics where
        // creator fields are absent from file metadata), we flag this for weight redistribution
        // below — see the composite calculation.

        // ── Year score ───────────────────────────────────────────────────
        double yearScore = 0.0; // Penalised when missing
        var fileYear = RetailHints.GetYearHint(fileHints);
        candidateYear = RetailHints.NormalizeYear(candidateYear);
        if (!string.IsNullOrWhiteSpace(fileYear) && !string.IsNullOrWhiteSpace(candidateYear))
        {
            if (fileYear == candidateYear)
            {
                yearScore = 1.0;
            }
            else if (int.TryParse(fileYear, out var fy) && int.TryParse(candidateYear, out var cy))
            {
                yearScore = Math.Abs(fy - cy) <= 1 ? 0.8 : 0.3;
            }
        }

        // ── Format score (always 1.0 — strategies are media-type-scoped) ─
        double formatScore = 1.0;

        // ── Cross-field signals ──────────────────────────────────────────
        double crossFieldBoost = ComputeCrossFieldBoost(fileHints, mediaType, extendedMetadata);

        // ── Cover art similarity ─────────────────────────────────────────
        double coverBoost = 0.0;
        if (_coverArtHash is not null)
        {
            if (extendedMetadata?.CoverArtSimilarity is > 0.8)
            {
                coverBoost = 0.10; // Strong visual match — same cover, likely same edition
            }
            else if (extendedMetadata?.CoverArtSimilarity is > 0.6)
            {
                coverBoost = 0.05; // Moderate visual match — probably same work
            }
        }

        // ── Weighted composite ───────────────────────────────────────────
        var titleWeight = weights.GetValueOrDefault("title", 0.45);
        var authorWeight = weights.GetValueOrDefault("author", 0.35);
        var yearWeight = weights.GetValueOrDefault("year", 0.10);
        var formatWeight = weights.GetValueOrDefault("format", 0.10);

        // When NEITHER the file NOR the candidate carries any creator data (common for
        // Movies, TV, and Comics where "author/artist/director/writer" are absent on both
        // sides), the author weight would penalise every candidate equally and unfairly.
        // In that case, redistribute the 35% author weight proportionally to the other
        // three fields so scoring is driven entirely by title, year, and format.
        double effectiveTitleWeight = titleWeight;
        double effectiveAuthorWeight = authorWeight;
        double effectiveYearWeight = yearWeight;
        double effectiveFormatWeight = formatWeight;

        bool bothLackAuthor = string.IsNullOrWhiteSpace(fileAuthor)
                           && string.IsNullOrWhiteSpace(candidateAuthor);
        if (bothLackAuthor)
        {
            double remaining = 1.0 - authorWeight; // 0.65
            effectiveTitleWeight = titleWeight / remaining;
            effectiveYearWeight = yearWeight / remaining;
            effectiveFormatWeight = formatWeight / remaining;
            effectiveAuthorWeight = 0.0;
        }

        var composite = (titleScore * effectiveTitleWeight)
                      + (authorScore * effectiveAuthorWeight)
                      + (yearScore * effectiveYearWeight)
                      + (formatScore * effectiveFormatWeight)
                      + crossFieldBoost
                      + coverBoost
                      + structuralBonus;

        _logger?.LogDebug("RetailScoring: title={TitleScore:F2} author={AuthorScore:F2} year={YearScore:F2} cross={CrossField:F2} cover={Cover:F2} composite={Composite:F2} — file='{FileTitle}' candidate='{CandidateTitle}'",
            titleScore, authorScore, yearScore, crossFieldBoost, coverBoost, composite, fileTitle, candidateTitle);

        return new FieldMatchScores
        {
            TitleScore = titleScore,
            AuthorScore = authorScore,
            YearScore = yearScore,
            FormatScore = formatScore,
            CrossFieldBoost = crossFieldBoost,
            CoverArtScore = coverBoost,
            CompositeScore = Math.Round(Math.Clamp(composite, 0.0, 1.0), 4),
        };
    }

    private static bool IsExactComicIssueIdentity(
        IReadOnlyDictionary<string, string> fileHints,
        CandidateExtendedMetadata? extendedMetadata)
    {
        if (extendedMetadata is null)
        {
            return false;
        }

        var fileSeries = fileHints.GetValueOrDefault(MetadataFieldConstants.Series);
        var candidateSeries = extendedMetadata.Series;
        if (string.IsNullOrWhiteSpace(fileSeries) || string.IsNullOrWhiteSpace(candidateSeries))
        {
            return false;
        }

        var fileIssue = GetComicIssueHint(fileHints);
        var candidateIssue = extendedMetadata.IssueNumber;
        if (string.IsNullOrWhiteSpace(fileIssue) || string.IsNullOrWhiteSpace(candidateIssue))
        {
            return false;
        }

        return AreEquivalentComparableText(fileSeries, candidateSeries)
            && RetailHints.AreEquivalentOrdinals(fileIssue, candidateIssue);
    }

    private static string? GetComicIssueHint(IReadOnlyDictionary<string, string> fileHints)
        => fileHints.GetValueOrDefault("issue_number")
            ?? fileHints.GetValueOrDefault(MetadataFieldConstants.SeriesPosition)
            ?? fileHints.GetValueOrDefault("issue");

    private static bool IsGeneratedComicIssueLabel(
        string? title,
        IReadOnlyDictionary<string, string> fileHints)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var series = fileHints.GetValueOrDefault(MetadataFieldConstants.Series);
        var issue = GetComicIssueHint(fileHints);
        if (string.IsNullOrWhiteSpace(series) || string.IsNullOrWhiteSpace(issue))
        {
            return false;
        }

        var normalizedTitle = RetailTextSimilarity.NormalizeComparableText(
            Regex.Replace(title, @"\(\d{4}\)\s*$", string.Empty));
        var normalizedSeries = RetailTextSimilarity.NormalizeComparableText(series);
        if (string.IsNullOrWhiteSpace(normalizedTitle) || string.IsNullOrWhiteSpace(normalizedSeries))
        {
            return false;
        }

        if (!int.TryParse(RetailHints.ExtractLeadingDigits(issue), out var issueNumber))
        {
            return false;
        }

        var pattern = $"^{Regex.Escape(normalizedSeries)}\\s+(?:issue\\s+|no\\s+|number\\s+)?0*{issueNumber}$";
        return Regex.IsMatch(normalizedTitle, pattern, RegexOptions.IgnoreCase);
    }

    private static bool AreEquivalentComparableText(string left, string right)
    {
        return string.Equals(
            RetailTextSimilarity.NormalizeComparableText(left),
            RetailTextSimilarity.NormalizeComparableText(right),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Computes an additive boost (positive or negative) from cross-field signals.
    /// These signals cross-reference file metadata against the candidate's extended
    /// metadata (description, publisher, duration, genres, language).
    /// </summary>
    private double ComputeCrossFieldBoost(
        IReadOnlyDictionary<string, string> fileHints,
        MediaType mediaType,
        CandidateExtendedMetadata? ext)
    {
        if (ext is null)
        {
            return 0.0;
        }

        double boost = 0.0;
        var description = ext.Description;
        var descLower = description?.ToLowerInvariant();

        // ── Narrator found in description (+0.10, audiobooks only) ────────
        if (mediaType is MediaType.Audiobooks && !string.IsNullOrWhiteSpace(descLower))
        {
            var narrator = fileHints.GetValueOrDefault("narrator");
            if (!string.IsNullOrWhiteSpace(narrator) && descLower.Contains(narrator.ToLowerInvariant()))
            {
                boost += 0.10;
            }
        }

        // ── Author found in description (+0.08, books/audiobooks) ────────
        if (mediaType is MediaType.Books or MediaType.Audiobooks && !string.IsNullOrWhiteSpace(descLower))
        {
            var author = fileHints.GetValueOrDefault("author");
            if (!string.IsNullOrWhiteSpace(author) && descLower.Contains(author.ToLowerInvariant()))
            {
                boost += 0.08;
            }
        }

        // ── Series name found in description (+0.08) ─────────────────────
        if (!string.IsNullOrWhiteSpace(descLower))
        {
            var series = fileHints.GetValueOrDefault("series");
            if (!string.IsNullOrWhiteSpace(series) && descLower.Contains(series.ToLowerInvariant()))
            {
                boost += 0.08;
            }
        }

        // ── Publisher matches (+0.05, books) ─────────────────────────────
        if (mediaType is MediaType.Books && !string.IsNullOrWhiteSpace(ext.Publisher))
        {
            var filePublisher = fileHints.GetValueOrDefault("publisher");
            if (!string.IsNullOrWhiteSpace(filePublisher))
            {
                var ratio = _fuzzy.ComputeTokenSetRatio(filePublisher, ext.Publisher);
                if (ratio >= 0.85)
                {
                    boost += 0.05;
                }
            }
        }

        // ── Page count within 10% (+0.05, books) ────────────────────────
        if (mediaType is MediaType.Books && ext.PageCount.HasValue)
        {
            var filePages = fileHints.GetValueOrDefault("page_count") ?? fileHints.GetValueOrDefault("word_count");
            if (!string.IsNullOrWhiteSpace(filePages) && int.TryParse(filePages, out var fp) && fp > 0)
            {
                var diff = Math.Abs(fp - ext.PageCount.Value) / (double)Math.Max(fp, ext.PageCount.Value);
                if (diff <= 0.10)
                {
                    boost += 0.05;
                }
            }
        }

        // ── Duration within 15% (+0.05, audiobooks) ─────────────────────
        if (mediaType is MediaType.Audiobooks && ext.DurationSeconds.HasValue)
        {
            var fileDur = fileHints.GetValueOrDefault("duration_sec");
            if (!string.IsNullOrWhiteSpace(fileDur) && double.TryParse(fileDur, out var fd) && fd > 0)
            {
                var diff = Math.Abs(fd - ext.DurationSeconds.Value) / Math.Max(fd, ext.DurationSeconds.Value);
                if (diff <= 0.15)
                {
                    boost += 0.05;
                }
                else if (diff > 0.50)
                {
                    boost -= 0.10; // Duration wildly different — penalty
                }
            }
        }

        // ── Genre overlap (+0.05) ────────────────────────────────────────
        if (ext.Genres is { Count: > 0 })
        {
            var fileGenre = fileHints.GetValueOrDefault("genre");
            if (!string.IsNullOrWhiteSpace(fileGenre))
            {
                var fileGenres = fileGenre.Split(',', ';', '|')
                    .Select(g => g.Trim().ToLowerInvariant())
                    .Where(g => g.Length > 0)
                    .ToHashSet();

                var candidateGenres = ext.Genres
                    .Select(g => g.Trim().ToLowerInvariant())
                    .ToHashSet();

                if (fileGenres.Overlaps(candidateGenres))
                {
                    boost += 0.05;
                }
            }
        }

        // ── Language matches (+0.05) or mismatch (-0.10) ────────────────
        if (!string.IsNullOrWhiteSpace(ext.Language))
        {
            var fileLang = fileHints.GetValueOrDefault("language");
            if (!string.IsNullOrWhiteSpace(fileLang))
            {
                var fileLangNorm = fileLang.Split('-', '_')[0].ToLowerInvariant();
                var candLangNorm = ext.Language.Split('-', '_')[0].ToLowerInvariant();

                if (string.Equals(fileLangNorm, candLangNorm, StringComparison.Ordinal))
                {
                    boost += 0.05;
                }
                else
                {
                    boost -= 0.10;
                }
            }
        }

        return boost;
    }

    private double ComputeCreatorScore(
        string fileCreator,
        string candidateCreator,
        string creatorListMode)
    {
        if (HaveSameCreatorTokens(fileCreator, candidateCreator))
        {
            return 1.0;
        }

        var fileCreators = RetailHints.SplitAuthors(fileCreator);
        var candidateCreators = RetailHints.SplitAuthors(candidateCreator);
        if (fileCreators.Count == 1 && candidateCreators.Count == 1)
        {
            return _fuzzy.ComputeTokenSetRatio(fileCreator, candidateCreator);
        }

        // Creator lists must be compared member-by-member. Token-set similarity alone
        // treats a shorter list as a perfect subset of a longer one, so a candidate such
        // as "J. R. R. Tolkien & Rafat Allam" would otherwise score 1.0 against Tolkien.
        int matched = 0;
        var usedCandidates = new HashSet<int>();
        foreach (var fileCreatorPart in fileCreators)
        {
            double bestMatch = 0.0;
            int bestIndex = -1;
            for (int i = 0; i < candidateCreators.Count; i++)
            {
                if (usedCandidates.Contains(i))
                {
                    continue;
                }

                var similarity = AreEquivalentComparableText(fileCreatorPart, candidateCreators[i])
                    ? 1.0
                    : _fuzzy.ComputeTokenSetRatio(fileCreatorPart, candidateCreators[i]);
                if (similarity > bestMatch)
                {
                    bestMatch = similarity;
                    bestIndex = i;
                }
            }

            if (bestMatch >= 0.70 && bestIndex >= 0)
            {
                matched++;
                usedCandidates.Add(bestIndex);
            }
        }

        var denominator = string.Equals(
            creatorListMode,
            "local-primary-containment",
            StringComparison.OrdinalIgnoreCase)
            ? fileCreators.Count
            : Math.Max(fileCreators.Count, candidateCreators.Count);
        return (double)matched / denominator;
    }

    private static bool HaveSameCreatorTokens(string left, string right)
    {
        var leftTokens = GetCreatorTokens(left);
        var rightTokens = GetCreatorTokens(right);

        return leftTokens.SetEquals(rightTokens);
    }

    private static HashSet<string> GetCreatorTokens(string value)
    {
        var words = RetailTextSimilarity.NormalizeComparableText(value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !string.Equals(token, "and", StringComparison.Ordinal))
            .ToList();
        var tokens = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < words.Count; i++)
        {
            if (words[i].Length == 1)
            {
                var initials = new StringBuilder();
                while (i < words.Count && words[i].Length == 1)
                {
                    initials.Append(words[i]);
                    i++;
                }

                tokens.Add(initials.ToString());
                i--;
                continue;
            }

            tokens.Add(words[i]);
        }

        return tokens;
    }
}
