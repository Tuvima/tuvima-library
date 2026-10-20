using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Adapters;

public sealed partial class ConfigDrivenAdapter
{
    private async Task<JsonNode?> NavigateToResultAsync(
        JsonNode json,
        SearchStrategyConfig strategy,
        ProviderLookupRequest request,
        CancellationToken ct)
    {
        // If no results_path, treat the whole response as the result.
        if (string.IsNullOrEmpty(strategy.ResultsPath))
        {
            return json;
        }

        var resultsNode = JsonPathEvaluator.Evaluate(json, strategy.ResultsPath);
        if (resultsNode is not JsonArray arr || arr.Count == 0)
        {
            return null;
        }

        // Apple omits ebook language and ISBN in its payloads, so candidates are
        // accepted when: the candidate's explicit language matches the file's; the
        // strategy was an exact ISBN lookup for a valid request ISBN; or there is no
        // language evidence and the file language is unknown or equals the storefront
        // language. Only a known file language that differs from the storefront still
        // requires an exact ISBN match, leaving that edition for manual selection.
        if (request.MediaType is MediaType.Books or MediaType.Audiobooks)
        {
            var language = request.FileLanguage ?? request.Language;
            var eligible = arr.Where(node => node is not null && IsEditionCompatible(node, strategy, request, language)).ToList();
            if (eligible.Count == 0)
            {
                return null;
            }
            arr = new JsonArray(eligible.Select(node => node!.DeepClone()).ToArray());
        }

        // Title + author validation: applies to ALL strategies (lookup and search).
        // Prevents wrong books from being accepted — e.g. study guides by different
        // authors, or an Apple ID lookup returning a completely different work.
        //
        // Strategy: prefer author-matched results. If no author match, fall back
        // to title-only matching (handles pen names where the listed author on
        // the retailer differs from the embedded author).
        var rankedResult = await TryRankCandidatesAsync(arr, request, ct)
            .ConfigureAwait(false);
        if (rankedResult is not null)
        {
            return rankedResult;
        }

        if (strategy.CandidateSelection is not null)
        {
            return TrySelectConfiguredCandidate(arr, strategy, request);
        }

        if (!string.IsNullOrWhiteSpace(request.Title))
        {
            // Clean the query title for matching — strip "(YYYY)" and "SxxExx" so
            // word-overlap scoring isn't penalised by filename-derived suffixes.
            var cleanedQueryTitle = CleanTitleForSearch(request.Title) ?? request.Title;

            var titlePaths = new[] { "trackName", "collectionName", "title", "name", "issue", "series.name", "series", "volumeName" };
            var authorPaths = new[] { "artistName", "author", "authors", "creator" };

            var applyDerivativeGuard = request.MediaType is MediaType.Books or MediaType.Audiobooks;
            var sourceLooksDerivative = applyDerivativeGuard
                && RetailCandidateQualityGuard.LooksDerivative(
                    request.Title,
                    genres: string.IsNullOrWhiteSpace(request.Genre) ? null : [request.Genre]);
            var scored = new List<(JsonNode Node, double TitleScore, double AuthorScore, bool Derivative)>();
            foreach (var node in arr)
            {
                if (node is null)
                {
                    continue;
                }

                // Try all title paths and keep the best score. Comic providers
                // may expose both issue-level and series-level names, and the
                // series name can be the better match for a broad query.
                var bestTitleScore = 0.0;
                string? bestNodeTitle = null;
                foreach (var tp in titlePaths)
                {
                    var val = JsonPathEvaluator.Evaluate(node, tp);
                    if (val is null)
                    {
                        continue;
                    }

                    var s = JsonPathEvaluator.GetStringValue(val);
                    if (string.IsNullOrWhiteSpace(s))
                    {
                        continue;
                    }

                    var score = ComputeWordOverlap(cleanedQueryTitle, s);
                    if (score > bestTitleScore)
                    {
                        bestTitleScore = score;
                        bestNodeTitle = s;
                    }
                }
                var nodeAuthor = ExtractFirstString(node, authorPaths);

                if (string.IsNullOrWhiteSpace(bestNodeTitle))
                {
                    continue;
                }

                var titleScore = bestTitleScore;
                var authorScore = !string.IsNullOrWhiteSpace(request.Author) && !string.IsNullOrWhiteSpace(nodeAuthor)
                    ? ComputeWordOverlap(request.Author, nodeAuthor)
                    : 0.0;

                var nodeDescription = ExtractFirstString(node, ["description", "shortDescription", "longDescription"]);
                var nodeGenre = ExtractFirstString(node, ["primaryGenreName", "genre", "genres"]);
                var derivative = applyDerivativeGuard
                    && !sourceLooksDerivative
                    && RetailCandidateQualityGuard.LooksDerivative(
                        bestNodeTitle,
                        nodeDescription,
                        string.IsNullOrWhiteSpace(nodeGenre) ? null : [nodeGenre]);

                scored.Add((node, titleScore, authorScore, derivative));
            }

            if (scored.Count == 0)
            {
                // No results had a recognisable title field — skip validation
                // and fall through to result_index selection rather than
                // rejecting all results from providers with non-standard schemas.
                var fallbackIndex = Math.Clamp(strategy.ResultIndex, 0, arr.Count - 1);
                return arr[fallbackIndex];
            }

            // Tier 1: prefer results where both author AND title match.
            var selectable = scored.Where(s => !s.Derivative).ToList();
            if (applyDerivativeGuard && !sourceLooksDerivative && selectable.Count == 0)
            {
                return null;
            }

            if (selectable.Count == 0)
            {
                selectable = scored;
            }

            var authorMatched = selectable.Where(s => s.AuthorScore >= 0.50).ToList();
            if (authorMatched.Count > 0)
            {
                return authorMatched.OrderByDescending(s => s.TitleScore).First().Node;
            }

            // Tier 2: no author match — fall back to title match (>= 0.40).
            // F1 >= 0.40 means at least moderate word overlap between query and candidate.
            // Short queries (e.g. "Batman") have low precision against longer candidate
            // titles (e.g. "Absolute Batman (2024) #1") but full coverage — 0.40 allows
            // these while still rejecting completely unrelated results.
            var bestByTitle = selectable.OrderByDescending(s => s.TitleScore).First();
            return bestByTitle.TitleScore >= 0.40 ? bestByTitle.Node : null;
        }

        var index = Math.Clamp(strategy.ResultIndex, 0, arr.Count - 1);
        return arr[index];
    }

    private bool IsEditionCompatible(
        JsonNode node,
        SearchStrategyConfig strategy,
        ProviderLookupRequest request,
        string? expectedLanguage)
    {
        var candidateLanguage = ExtractFirstString(node, ["language", "languageCode", "language_code"]);
        static string NormalizeLanguage(string value) =>
            LanguageCodeNormalizer.ToIso6391(value) ?? value.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(candidateLanguage) && !string.IsNullOrWhiteSpace(expectedLanguage))
        {
            return NormalizeLanguage(candidateLanguage) == NormalizeLanguage(expectedLanguage);
        }

        if (!string.Equals(Name, "apple_api", StringComparison.OrdinalIgnoreCase)
            || request.MediaType != MediaType.Books)
        {
            return true;
        }

        var sourceIsbn = IsbnValidation.NormalizeValid(request.Isbn);

        // A strategy keyed on the ISBN (e.g. isbn_lookup) returns ISBN-exact editions by
        // definition, even though Apple's payload carries no isbn or language field.
        if (sourceIsbn is not null
            && strategy.RequiredFields.Any(field => string.Equals(field, "isbn", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // No language/ISBN evidence on the candidate: accept when the file language is
        // unknown or is the language of the storefront that was queried.
        var fileLanguage = LanguageCodeNormalizer.ToIso6391(expectedLanguage);
        if (fileLanguage is null
            || string.Equals(fileLanguage, ResolveStorefrontLanguage(request), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var editionIsbn = IsbnValidation.NormalizeValid(ExtractFirstString(node, ["isbn", "isbn13", "isbn10"]));
        return sourceIsbn is not null && sourceIsbn == editionIsbn;
    }

    private JsonNode? TrySelectConfiguredCandidate(
        JsonArray results,
        SearchStrategyConfig strategy,
        ProviderLookupRequest request)
    {
        var selection = strategy.CandidateSelection!;
        var requestedTitle = CleanTitleForSearch(request.Title) ?? request.Title;
        var requestedCreator = selection.CreatorRequestFields
            .Select(field => ResolveRequestField(request, field))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var candidates = new List<(JsonNode Node, double TitleScore, double CreatorScore, int AlbumRank, JsonNode? Release)>();

        // A music search returns many distinct recordings that share one title (the studio
        // recording, live takes, compilation-only duplicates). The file's album tag is the
        // tiebreaker between them, so the recording that sits on the tagged album wins over
        // an equally titled one whose only releases are elsewhere.
        var taggedAlbum = request.MediaType == MediaType.Music ? GetRequestedAlbum(request) : null;

        foreach (var result in results)
        {
            if (result is null)
            {
                continue;
            }

            var albumRank = 0;
            var selectedRelease = strategy.ReleaseSelection is null
                ? null
                : ApplyReleaseSelection(result, strategy.ReleaseSelection, request);
            if (selection.RequireNestedSelection)
            {
                if (selectedRelease is null)
                {
                    continue;
                }

                albumRank = RankReleaseAgainstTaggedAlbum(taggedAlbum, selectedRelease);
            }

            if (!PassesRequestFilters(result, selection.RequestFilters, request))
            {
                continue;
            }

            var candidateTitle = ExtractFirstString(result, selection.TitlePaths.ToArray());
            var titleScore = !string.IsNullOrWhiteSpace(requestedTitle)
                && !string.IsNullOrWhiteSpace(candidateTitle)
                    ? ComputeWordOverlap(requestedTitle, candidateTitle)
                    : 0;
            if (!string.IsNullOrWhiteSpace(requestedTitle)
                && titleScore < selection.MinimumTitleScore)
            {
                continue;
            }

            var candidateCreator = ExtractFirstString(result, selection.CreatorPaths.ToArray());
            var creatorScore = !string.IsNullOrWhiteSpace(requestedCreator)
                && !string.IsNullOrWhiteSpace(candidateCreator)
                    ? ComputeWordOverlap(requestedCreator, candidateCreator)
                    : 0;
            if (!string.IsNullOrWhiteSpace(requestedCreator)
                && !string.IsNullOrWhiteSpace(candidateCreator)
                && creatorScore < selection.MinimumCreatorScore)
            {
                continue;
            }

            candidates.Add((result, titleScore, creatorScore, albumRank, selectedRelease));
        }

        // Equally scored candidates (the studio recording and its remaster or compilation
        // duplicates) must not be chosen by the provider's result order, which varies between
        // calls: prefer the one whose chosen release sorts first under the strategy's own
        // release order (the original release), then the lowest provider id.
        var releaseSort = strategy.ReleaseSelection?.Sort;
        var disambiguationPath = strategy.ReleaseSelection?.DisambiguationPath;
        return candidates
            .OrderByDescending(candidate => candidate.TitleScore)
            .ThenByDescending(candidate => candidate.CreatorScore)
            .ThenByDescending(candidate => candidate.AlbumRank)
            .ThenBy(candidate => candidate, Comparer<(JsonNode Node, double TitleScore, double CreatorScore, int AlbumRank, JsonNode? Release)>
                .Create((left, right) => CompareEquallyScoredCandidates(
                    left.Node, left.Release, right.Node, right.Release, releaseSort, disambiguationPath)))
            .Select(candidate => candidate.Node)
            .FirstOrDefault();
    }

    private static int CompareEquallyScoredCandidates(
        JsonNode left, JsonNode? leftRelease, JsonNode right, JsonNode? rightRelease,
        IReadOnlyList<SelectionSort>? releaseSort, string? disambiguationPath)
    {
        if (leftRelease is not null && rightRelease is not null)
        {
            var byRelease = CompareBySelectionSort(leftRelease, rightRelease, releaseSort, disambiguationPath);
            if (byRelease != 0)
            {
                return byRelease;
            }
        }
        else if (leftRelease is not null || rightRelease is not null)
        {
            return leftRelease is null ? 1 : -1;
        }

        return string.CompareOrdinal(ExtractFirstString(left, ["id"]), ExtractFirstString(right, ["id"]));
    }

    /// <summary>
    /// 2 when the selected release is the tagged album's track list, 1 when it is the same album
    /// under a different edition label (MusicBrainz keeps "Deluxe", "Bonus Track Version" and
    /// "2015 Remaster" out of a release title), 0 otherwise or when the file has no album tag.
    /// </summary>
    private int RankReleaseAgainstTaggedAlbum(string? taggedAlbum, JsonNode selectedRelease)
    {
        if (string.IsNullOrWhiteSpace(taggedAlbum))
        {
            return 0;
        }

        var releaseTitle = ExtractFirstString(selectedRelease, [GetReleaseAlbumTitlePath()]);
        if (IsStrongAlbumMatch(taggedAlbum, releaseTitle))
        {
            return 2;
        }

        return MusicAlbumIdentity.IsSameBaseAlbum(taggedAlbum, releaseTitle) ? 1 : 0;
    }

    private static bool PassesRequestFilters(
        JsonNode candidate,
        IReadOnlyList<RequestCandidateFilterConfig> filters,
        ProviderLookupRequest request)
    {
        foreach (var filter in filters)
        {
            var requested = ResolveRequestField(request, filter.RequestField);
            if (string.IsNullOrWhiteSpace(requested))
            {
                if (filter.Required)
                {
                    return false;
                }

                continue;
            }

            var values = filter.CandidatePaths
                .Select(path => ExtractFirstString(candidate, [path]))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToList();
            if (values.Count == 0 || !values.Any(value => RequestFilterMatches(requested, value, filter)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool RequestFilterMatches(
        string requested,
        string candidate,
        RequestCandidateFilterConfig filter) =>
        filter.Operator.ToLowerInvariant() switch
        {
            "album_identity" => MusicAlbumIdentity.IsSameTrackList(requested, candidate),
            "normalized_similarity" => ComputeWordOverlap(requested, candidate) >= filter.MinimumScore,
            _ => string.Equals(
                RetailTextSimilarity.NormalizeComparableText(requested),
                RetailTextSimilarity.NormalizeComparableText(candidate),
                StringComparison.Ordinal)
        };

    private static string? GetRequestedAlbum(ProviderLookupRequest request)
        => request.Album
           ?? request.Hints?.GetValueOrDefault(MetadataFieldConstants.Album);

    private static bool IsStrongAlbumMatch(string? requestedAlbum, string? candidateAlbum)
    {
        if (string.IsNullOrWhiteSpace(requestedAlbum) || string.IsNullOrWhiteSpace(candidateAlbum))
        {
            return false;
        }

        return MusicAlbumIdentity.IsSameTrackList(requestedAlbum, candidateAlbum);
    }

    private static string? GetComicIssueHint(ProviderLookupRequest request)
        => request.Hints?.GetValueOrDefault("issue_number")
            ?? request.Hints?.GetValueOrDefault(MetadataFieldConstants.SeriesPosition)
            ?? request.Hints?.GetValueOrDefault("issue");

    private static bool AreEquivalentComicText(string left, string right) =>
        ResultTextRules.AreEquivalentText(left, right);

    private static bool AreEquivalentComicOrdinals(string left, string right) =>
        ResultTextRules.AreEquivalentOrdinals(left, right);

    private static int? TryExtractYear(string? value) => ResultTextRules.ExtractYear(value);

    private static int? TryParseLeadingInt(string? value) => ResultTextRules.ParseLeadingInt(value);

    /// <summary>
    /// Extracts the first non-empty string value from a JSON node by trying multiple paths.
    /// </summary>
    private static string? ExtractFirstString(JsonNode node, string[] paths)
    {
        foreach (var path in paths)
        {
            var val = JsonPathEvaluator.Evaluate(node, path);
            if (val is not null)
            {
                var s = JsonPathEvaluator.GetStringValue(val);
                if (!string.IsNullOrWhiteSpace(s))
                {
                    return s;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Word-overlap similarity (0.0–1.0). Compares normalized word sets,
    /// returning harmonic mean of coverage and precision (F1 score).
    /// </summary>
    private static double ComputeWordOverlap(string query, string candidate) =>
        ResultTextRules.ComputeWordOverlap(query, candidate);

    /// <summary>
    /// Strips diacritical marks from text — e.g. "Shogun" ? "Shogun", "Für Elise" ? "Fur Elise".
    /// Uses Unicode decomposition to separate base characters from combining marks.
    /// </summary>
    private static string StripDiacritics(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    // -- Nested release selection --------------------------------------------

    /// <summary>
    /// Selects the best sub-result from a nested array within the matched result node.
    /// Used for MusicBrainz-style APIs where a recording contains multiple releases
    /// and the adapter needs to pick the best one (e.g. original studio album with artwork).
    /// </summary>
}
