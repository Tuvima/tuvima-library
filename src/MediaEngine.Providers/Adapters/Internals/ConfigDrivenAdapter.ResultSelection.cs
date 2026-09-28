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

        // Storefront and response locale are not evidence of an edition's language.
        // Apple omits ebook language in its search payload; without an exact ISBN
        // or explicit language evidence, leave that edition for manual selection.
        if (request.MediaType is MediaType.Books or MediaType.Audiobooks)
        {
            var language = request.FileLanguage ?? request.Language;
            var eligible = arr.Where(node => node is not null && IsEditionCompatible(node, request, language)).ToList();
            if (eligible.Count == 0) return null;
            arr = new JsonArray(eligible.Select(node => node!.DeepClone()).ToArray());
        }

        // Title + author validation: applies to ALL strategies (lookup and search).
        // Prevents wrong books from being accepted — e.g. study guides by different
        // authors, or an Apple ID lookup returning a completely different work.
        //
        // Strategy: prefer author-matched results. If no author match, fall back
        // to title-only matching (handles pen names where the listed author on
        // the retailer differs from the embedded author).
        var comicIssueResult = await TrySelectComicIssueResultAsync(arr, request, ct)
            .ConfigureAwait(false);
        if (comicIssueResult is not null)
        {
            return comicIssueResult;
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

    private bool IsEditionCompatible(JsonNode node, ProviderLookupRequest request, string? expectedLanguage)
    {
        var candidateLanguage = ExtractFirstString(node, ["language", "languageCode", "language_code"]);
        static string NormalizeLanguage(string value) => value.Trim().ToLowerInvariant().Split('-', '_')[0] switch
        {
            "eng" or "english" => "en", "fra" or "fre" or "french" => "fr",
            "deu" or "ger" or "german" => "de", "spa" or "spanish" => "es",
            "ita" or "italian" => "it", var other => other,
        };
        if (!string.IsNullOrWhiteSpace(candidateLanguage) && !string.IsNullOrWhiteSpace(expectedLanguage))
            return NormalizeLanguage(candidateLanguage) == NormalizeLanguage(expectedLanguage);

        if (!string.Equals(Name, "apple_api", StringComparison.OrdinalIgnoreCase)
            || request.MediaType != MediaType.Books) return true;

        var sourceIsbn = IsbnValidation.NormalizeValid(request.Isbn);
        var editionIsbn = IsbnValidation.NormalizeValid(ExtractFirstString(node, ["isbn", "isbn13", "isbn10"]));
        return sourceIsbn is not null && sourceIsbn == editionIsbn;
    }

    private async Task<JsonNode?> TrySelectComicIssueResultAsync(
        JsonArray arr,
        ProviderLookupRequest request,
        CancellationToken ct)
    {
        if (!string.Equals(Name, "comicvine", StringComparison.OrdinalIgnoreCase)
            || request.MediaType != MediaType.Comics)
        {
            return null;
        }

        var fileSeries = request.Series
            ?? request.Hints?.GetValueOrDefault(MetadataFieldConstants.Series);
        var fileIssue = GetComicIssueHint(request);
        if (string.IsNullOrWhiteSpace(fileSeries) || string.IsNullOrWhiteSpace(fileIssue))
        {
            return null;
        }

        var cleanedQueryTitle = !string.IsNullOrWhiteSpace(request.Title)
            ? CleanTitleForSearch(request.Title) ?? request.Title
            : fileSeries;
        var requestedYear = TryExtractYear(
            request.Year
            ?? ExtractYearFromTitle(request.Title)
            ?? request.Hints?.GetValueOrDefault("year"));
        var requestedIssueNumber = TryParseLeadingInt(fileIssue);
        var matching = new List<ComicIssueCandidate>();

        foreach (var node in arr)
        {
            if (node is null)
            {
                continue;
            }

            var candidateSeries = ExtractFirstString(node,
                ["volume.name", "series.name", "series", "volumeName", "volume"]);
            var candidateIssue = ExtractFirstString(node,
                ["issue_number", "issueNumber", "number", "issue"]);

            if (string.IsNullOrWhiteSpace(candidateSeries)
                || string.IsNullOrWhiteSpace(candidateIssue)
                || !AreEquivalentComicText(fileSeries, candidateSeries)
                || !AreEquivalentComicOrdinals(fileIssue, candidateIssue))
            {
                continue;
            }

            matching.Add(BuildComicIssueCandidate(node, cleanedQueryTitle, requestedYear));
        }

        var runScopedIssue = await TryFetchComicVineIssueFromPreferredVolumeAsync(
                fileSeries,
                fileIssue,
                requestedYear,
                requestedIssueNumber,
                request,
                ct)
            .ConfigureAwait(false);
        if (runScopedIssue is not null)
        {
            var baseRunScopedCandidate = BuildComicIssueCandidate(runScopedIssue, cleanedQueryTitle, requestedYear);
            var runScopedCandidate = baseRunScopedCandidate with
            {
                BaseScore = baseRunScopedCandidate.BaseScore + 0.35
            };
            if (!matching.Any(candidate => SameComicVineIssue(candidate.Node, runScopedCandidate.Node)))
            {
                matching.Add(runScopedCandidate);
            }
        }

        if (matching.Count == 0)
        {
            return null;
        }

        if (matching.Count > 1)
        {
            var enriched = new List<ComicIssueCandidate>(matching.Count);
            foreach (var candidate in matching)
            {
                var facts = await TryFetchComicVineVolumeFactsForSelectionAsync(candidate.VolumeId, request, ct)
                    .ConfigureAwait(false);
                enriched.Add(candidate with
                {
                    VolumeStartYear = candidate.VolumeStartYear ?? facts?.StartYear,
                    VolumeIssueCount = facts?.IssueCount,
                    Publisher = StringHelpers.FirstNonBlank(candidate.Publisher, facts?.Publisher)
                });
            }

            matching = enriched;
        }

        return matching
            .Select(item => item with
            {
                BaseScore = item.BaseScore
                    + ScoreVolumeStartYearProximity(requestedYear, item.VolumeStartYear)
                    + ScoreVolumeIssueCount(requestedIssueNumber, item.VolumeIssueCount)
                    + ScoreComicPublisherAffinity(request, item.Publisher)
            })
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.VolumeIssueCount ?? 0)
            .ThenByDescending(item => item.VolumeStartYear ?? 0)
            .ThenBy(item => item.CandidateYear ?? int.MaxValue)
            .Select(item => item.Node)
            .FirstOrDefault();
    }

    private static ComicIssueCandidate BuildComicIssueCandidate(
        JsonNode node,
        string cleanedQueryTitle,
        int? requestedYear)
    {
        var candidateTitle = ExtractFirstString(node, ["name", "title", "issue"]);
        var titleScore = string.IsNullOrWhiteSpace(candidateTitle)
            ? 0.0
            : ComputeWordOverlap(cleanedQueryTitle, candidateTitle);
        var candidateDescription = ExtractFirstString(node, ["description", "deck", "shortDescription", "longDescription"]);
        var candidateYear = TryExtractYear(
            ExtractFirstString(node, ["cover_date", "store_date", "date_added", "start_year", "year"]));
        var yearScore = ScoreYearProximity(requestedYear, candidateYear);
        var languageScore = LooksNonEnglishDescription(candidateDescription) ? -0.25 : 0.03;
        var volumeId = ExtractFirstString(node, ["volume.id", "volumeId"]);
        var volumeStartYear = TryExtractYear(ExtractFirstString(node, ["volume.start_year", "volume.startYear"]));
        var publisher = ExtractFirstString(node, ["volume.publisher.name", "publisher.name", "publisher"]);

        return new ComicIssueCandidate(
            node,
            BaseScore: 1.0 + titleScore * 0.02 + yearScore + languageScore,
            CandidateYear: candidateYear,
            VolumeId: volumeId,
            VolumeStartYear: volumeStartYear,
            VolumeIssueCount: null,
            Publisher: publisher);
    }

    private async Task<JsonNode?> TryFetchComicVineIssueFromPreferredVolumeAsync(
        string fileSeries,
        string fileIssue,
        int? requestedYear,
        int? requestedIssueNumber,
        ProviderLookupRequest request,
        CancellationToken ct)
    {
        if (!requestedIssueNumber.HasValue)
        {
            return null;
        }

        try
        {
            var volumes = await TrySelectComicVineVolumesAsync(fileSeries, requestedYear, requestedIssueNumber, request, ct)
                .ConfigureAwait(false);
            if (volumes.Count == 0)
            {
                return null;
            }

            var baseUrl = ResolveBaseUrl(request);
            var apiKey = EffectiveApiKey;
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
            {
                return null;
            }

            foreach (var volume in volumes)
            {
                var volumeId = volume.VolumeId;
                var issueUrl = $"{baseUrl.TrimEnd('/')}/issues/?api_key={Uri.EscapeDataString(apiKey)}&filter=volume:{Uri.EscapeDataString(volumeId)},issue_number:{Uri.EscapeDataString(requestedIssueNumber.Value.ToString(CultureInfo.InvariantCulture))}&format=json";
                var issueJson = await FetchComicVineJsonAsync(issueUrl, ct).ConfigureAwait(false);
                var issues = issueJson?["results"]?.AsArray();
                if (issues is null)
                {
                    continue;
                }

                var issue = issues
                    .Where(candidate => candidate is not null)
                    .FirstOrDefault(candidate =>
                    {
                        var candidateIssue = ExtractFirstString(candidate!, ["issue_number", "issueNumber", "number", "issue"]);
                        var candidateVolumeId = ExtractFirstString(candidate!, ["volume.id", "volumeId"]);
                        return !string.IsNullOrWhiteSpace(candidateIssue)
                            && AreEquivalentComicOrdinals(fileIssue, candidateIssue)
                            && string.Equals(candidateVolumeId, volumeId, StringComparison.OrdinalIgnoreCase);
                    });
                if (issue is not null)
                {
                    return issue;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(
                ex,
                "{Provider}: ComicVine run-scoped issue lookup failed for {Series} #{Issue}",
                Name,
                fileSeries,
                fileIssue);
            return null;
        }
    }

    private async Task<ComicVineVolumeSearchCandidate?> TrySelectComicVineVolumeAsync(
        string fileSeries,
        int? requestedYear,
        int? requestedIssueNumber,
        ProviderLookupRequest request,
        CancellationToken ct)
    {
        var candidates = await TrySelectComicVineVolumesAsync(
                fileSeries,
                requestedYear,
                requestedIssueNumber,
                request,
                ct)
            .ConfigureAwait(false);
        return candidates.FirstOrDefault();
    }

    private async Task<IReadOnlyList<ComicVineVolumeSearchCandidate>> TrySelectComicVineVolumesAsync(
        string fileSeries,
        int? requestedYear,
        int? requestedIssueNumber,
        ProviderLookupRequest request,
        CancellationToken ct)
    {
        var baseUrl = ResolveBaseUrl(request);
        var apiKey = EffectiveApiKey;
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            return [];
        }

        var url = $"{baseUrl.TrimEnd('/')}/search/?api_key={Uri.EscapeDataString(apiKey)}&query={Uri.EscapeDataString(fileSeries)}&resources=volume&limit=25&format=json";
        var json = await FetchComicVineJsonAsync(url, ct).ConfigureAwait(false);
        var volumes = json?["results"]?.AsArray();
        if (volumes is null)
        {
            return [];
        }

        var scored = new List<ComicVineVolumeSearchCandidate>();
        foreach (var volume in volumes)
        {
            if (volume is null)
            {
                continue;
            }

            var name = ExtractFirstString(volume, ["name", "title"]);
            if (string.IsNullOrWhiteSpace(name) || !AreEquivalentComicText(fileSeries, name))
            {
                continue;
            }

            var volumeId = ExtractFirstString(volume, ["id"]);
            if (string.IsNullOrWhiteSpace(volumeId))
            {
                continue;
            }

            var issueCount = TryParseLeadingInt(ExtractFirstString(volume, ["count_of_issues", "issue_count"]));
            if (requestedIssueNumber.HasValue && issueCount.HasValue && issueCount.Value < requestedIssueNumber.Value)
            {
                continue;
            }

            var startYear = TryExtractYear(ExtractFirstString(volume, ["start_year", "year"]));
            var publisher = ExtractFirstString(volume, ["publisher.name", "publisher"]);
            var score = 1.0
                + ScoreVolumeStartYearProximity(requestedYear, startYear)
                + ScoreVolumeIssueCount(requestedIssueNumber, issueCount)
                + ScoreComicPublisherAffinity(request, publisher)
                + ScoreLikelyOriginalComicRun(issueCount);

            scored.Add(new ComicVineVolumeSearchCandidate(volumeId, score, issueCount, startYear, publisher));
        }

        return scored
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.IssueCount ?? 0)
            .ThenBy(candidate => candidate.StartYear ?? int.MaxValue)
            .Take(6)
            .ToList();
    }

    private async Task<JsonNode?> FetchComicVineJsonAsync(string url, CancellationToken ct)
    {
        using var client = _httpFactory.CreateClient(_config.Name);
        return await _rateLimiter.ExecuteAsync(
            Name,
            _config.RateLimit,
            token => client.GetFromJsonAsync<JsonNode>(url, token),
            ct).ConfigureAwait(false);
    }

    private static bool SameComicVineIssue(JsonNode left, JsonNode right)
    {
        var leftId = ExtractFirstString(left, ["id"]);
        var rightId = ExtractFirstString(right, ["id"]);
        if (!string.IsNullOrWhiteSpace(leftId) && !string.IsNullOrWhiteSpace(rightId))
        {
            return string.Equals(leftId, rightId, StringComparison.OrdinalIgnoreCase);
        }

        var leftVolume = ExtractFirstString(left, ["volume.id", "volumeId"]);
        var rightVolume = ExtractFirstString(right, ["volume.id", "volumeId"]);
        var leftIssue = ExtractFirstString(left, ["issue_number", "issueNumber", "number", "issue"]);
        var rightIssue = ExtractFirstString(right, ["issue_number", "issueNumber", "number", "issue"]);
        return !string.IsNullOrWhiteSpace(leftVolume)
            && !string.IsNullOrWhiteSpace(rightVolume)
            && !string.IsNullOrWhiteSpace(leftIssue)
            && !string.IsNullOrWhiteSpace(rightIssue)
            && string.Equals(leftVolume, rightVolume, StringComparison.OrdinalIgnoreCase)
            && AreEquivalentComicOrdinals(leftIssue, rightIssue);
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
        var candidates = new List<(JsonNode Node, double TitleScore, double CreatorScore)>();

        foreach (var result in results)
        {
            if (result is null)
            {
                continue;
            }

            if (selection.RequireNestedSelection
                && (strategy.ReleaseSelection is null
                    || ApplyReleaseSelection(result, strategy.ReleaseSelection, request) is null))
            {
                continue;
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

            candidates.Add((result, titleScore, creatorScore));
        }

        return candidates
            .OrderByDescending(candidate => candidate.TitleScore)
            .ThenByDescending(candidate => candidate.CreatorScore)
            .Select(candidate => candidate.Node)
            .FirstOrDefault();
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

    private static bool AreEquivalentComicText(string left, string right)
        => string.Equals(NormalizeComicText(left), NormalizeComicText(right), StringComparison.Ordinal);

    private static string NormalizeComicText(string value)
    {
        var chars = StripDiacritics(value)
            .Replace("&", " and ", StringComparison.Ordinal)
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : ' ')
            .ToArray();

        return string.Join(' ', new string(chars)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool AreEquivalentComicOrdinals(string left, string right)
    {
        if (int.TryParse(ExtractLeadingDigits(left), out var leftNumber)
            && int.TryParse(ExtractLeadingDigits(right), out var rightNumber))
        {
            return leftNumber == rightNumber;
        }

        return string.Equals(left.TrimStart('0'), right.TrimStart('0'), StringComparison.OrdinalIgnoreCase)
            || string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractLeadingDigits(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var match = Regex.Match(value.Trim(), @"^\D*0*(\d+)");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static int? TryExtractYear(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = Regex.Match(value, @"(?<!\d)(19|20)\d{2}(?!\d)");
        return match.Success && int.TryParse(match.Value, out var year) ? year : null;
    }

    private static int? TryParseLeadingInt(string? value)
        => int.TryParse(ExtractLeadingDigits(value ?? string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static double ScoreYearProximity(int? requestedYear, int? candidateYear)
    {
        if (!requestedYear.HasValue || !candidateYear.HasValue)
        {
            return 0;
        }

        var delta = Math.Abs(requestedYear.Value - candidateYear.Value);
        return delta switch
        {
            0 => 0.18,
            1 => 0.10,
            <= 3 => 0.04,
            _ => -0.08,
        };
    }

    private static double ScoreVolumeStartYearProximity(int? requestedYear, int? volumeStartYear)
    {
        if (!volumeStartYear.HasValue)
        {
            return 0;
        }

        if (!requestedYear.HasValue)
        {
            return volumeStartYear.Value >= 2000 ? 0.03 : 0;
        }

        var delta = Math.Abs(requestedYear.Value - volumeStartYear.Value);
        return delta switch
        {
            0 => 0.36,
            1 => 0.22,
            <= 3 => 0.08,
            <= 8 => -0.18,
            _ => -0.45,
        };
    }

    private static double ScoreVolumeIssueCount(int? requestedIssueNumber, int? volumeIssueCount)
    {
        if (!volumeIssueCount.HasValue)
        {
            return 0;
        }

        if (requestedIssueNumber.HasValue && volumeIssueCount.Value < requestedIssueNumber.Value)
        {
            return -0.50;
        }

        if (volumeIssueCount.Value <= 1)
        {
            return -0.05;
        }

        return Math.Min(0.10, Math.Log10(volumeIssueCount.Value) * 0.05);
    }

    private static double ScoreLikelyOriginalComicRun(int? volumeIssueCount)
    {
        if (!volumeIssueCount.HasValue || volumeIssueCount.Value <= 0)
        {
            return 0;
        }

        return Math.Min(0.18, Math.Log10(volumeIssueCount.Value) * 0.09);
    }

    private static double ScoreComicPublisherAffinity(ProviderLookupRequest request, string? publisher)
    {
        var requestedPublisher = request.Hints?.GetValueOrDefault(MetadataFieldConstants.PublisherField);
        if (string.IsNullOrWhiteSpace(requestedPublisher) || string.IsNullOrWhiteSpace(publisher))
        {
            return 0;
        }

        return ComputeWordOverlap(requestedPublisher, publisher) >= 0.75 ? 0.12 : -0.03;
    }

    private static bool LooksNonEnglishDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = $" {StripDiacritics(value).ToLowerInvariant()} ";
        var markers = new[]
        {
            " der ", " die ", " das ", " und ", " eine ", " einem ", " einen ",
            " von ", " mit ", " nicht ", " fur ", " uber ", " ist ", " sich "
        };

        return markers.Count(marker => normalized.Contains(marker, StringComparison.Ordinal)) >= 3;
    }

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
    private static double ComputeWordOverlap(string query, string candidate)
    {
        var qWords = StripDiacritics(query).ToLowerInvariant()
            .Split([' ', ',', '.', '-', ':', ';', '\'', '"', '(', ')', '[', ']'],
                   StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 2)
            .ToHashSet();

        var cWords = StripDiacritics(candidate).ToLowerInvariant()
            .Split([' ', ',', '.', '-', ':', ';', '\'', '"', '(', ')', '[', ']'],
                   StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 2)
            .ToHashSet();

        if (qWords.Count == 0 || cWords.Count == 0)
        {
            return 0.0;
        }

        var coverage = (double)qWords.Count(w => cWords.Contains(w)) / qWords.Count;
        var precision = (double)cWords.Count(w => qWords.Contains(w)) / cWords.Count;

        if (coverage + precision == 0)
        {
            return 0.0;
        }

        return 2 * coverage * precision / (coverage + precision);
    }

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
