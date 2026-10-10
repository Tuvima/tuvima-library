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
    private async Task<IReadOnlyList<ProviderClaim>> ExtractAndValidateClaimsAsync(
        SearchStrategyConfig strategy,
        ProviderLookupRequest request,
        JsonNode resultNode,
        CancellationToken ct)
    {
        IReadOnlyList<ProviderClaim> claims;
        if (strategy.ReleaseSelection is not null)
        {
            var releaseNode = ApplyReleaseSelection(resultNode, strategy.ReleaseSelection, request);
            claims = ExtractClaimsWithRelease(resultNode, releaseNode, request.MediaType);
        }
        else
        {
            claims = ExtractClaims(resultNode, request.MediaType);
        }

        claims = NormalizeClaimsForStrategy(strategy, request, claims);
        claims = KeepTaggedAlbumWhenCollectionDiffers(request, claims);
        claims = EnrichComicVineCreatorClaims(claims, resultNode, request);

        if (!ClaimsMatchRequest(claims, request, strategy))
        {
            return [];
        }

        claims = await EnrichClaimsWithTmdbDetailsAsync(claims, resultNode, request, ct)
            .ConfigureAwait(false);
        claims = await EnrichClaimsWithComicVineVolumeAsync(claims, request, ct)
            .ConfigureAwait(false);

        return claims;
    }

    private async Task<IReadOnlyList<ProviderClaim>> EnrichClaimsWithComicVineVolumeAsync(
        IReadOnlyList<ProviderClaim> claims,
        ProviderLookupRequest request,
        CancellationToken ct)
    {
        if (!string.Equals(Name, "comicvine", StringComparison.OrdinalIgnoreCase)
            || request.MediaType != MediaType.Comics)
        {
            return claims;
        }

        var volumeId = claims.FirstOrDefault(claim =>
            string.Equals(claim.Key, BridgeIdKeys.ComicVineVolumeId, StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrWhiteSpace(volumeId))
        {
            return claims;
        }

        try
        {
            var facts = await TryFetchComicVineVolumeFactsAsync(volumeId, request, ct)
                .ConfigureAwait(false);
            if (facts is null)
            {
                return claims;
            }

            var enriched = claims.ToList();
            AddComicVineVolumeClaim(enriched, MetadataFieldConstants.SequenceTotal, facts.IssueCount?.ToString(CultureInfo.InvariantCulture), 0.9);
            if (enriched.Any(claim => string.Equals(claim.Key, MetadataFieldConstants.SequenceTotal, StringComparison.OrdinalIgnoreCase)))
            {
                AddComicVineVolumeClaim(enriched, MetadataFieldConstants.SequenceTotalScope, "MainSequence", 0.9);
            }

            AddComicVineVolumeClaim(enriched, MetadataFieldConstants.SeriesStartYear, facts.StartYear?.ToString(CultureInfo.InvariantCulture), 0.85);
            AddComicVineVolumeClaim(enriched, MetadataFieldConstants.PublisherField, facts.Publisher, 0.8);
            var seriesLabel = claims.FirstOrDefault(claim =>
                string.Equals(claim.Key, MetadataFieldConstants.Series, StringComparison.OrdinalIgnoreCase))?.Value
                ?? request.Series
                ?? request.Title;
            var manifest = await TryFetchComicVineSequenceManifestAsync(
                    volumeId,
                    seriesLabel,
                    facts.IssueCount,
                    request,
                    ct)
                .ConfigureAwait(false);
            if (manifest is not null)
            {
                AddComicVineVolumeClaim(
                    enriched,
                    MetadataFieldConstants.SequenceManifestJson,
                    JsonSerializer.Serialize(manifest),
                    manifest.IsAuthoritative ? 1.0 : 0.7);
            }
            return enriched;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogInformation(
                ex,
                "{Provider}: ComicVine volume enrichment failed for volume {VolumeId}; issue-level claims will be used",
                Name,
                volumeId);
            return claims;
        }
    }

    private async Task<ComicVineVolumeFacts?> TryFetchComicVineVolumeFactsAsync(
        string? volumeId,
        ProviderLookupRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(volumeId))
        {
            return null;
        }

        var lazy = _comicVineVolumeFacts.GetOrAdd(
            volumeId,
            _ => new Lazy<Task<ComicVineVolumeFacts?>>(
                () => FetchComicVineVolumeFactsCoreAsync(volumeId, request, ct),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        catch
        {
            _comicVineVolumeFacts.TryRemove(volumeId, out _);
            throw;
        }
    }

    private async Task<ComicVineVolumeFacts?> FetchComicVineVolumeFactsCoreAsync(
        string volumeId,
        ProviderLookupRequest request,
        CancellationToken ct)
    {

        var baseUrl = ResolveBaseUrl(request);
        var apiKey = EffectiveApiKey;
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var url = $"{baseUrl.TrimEnd('/')}/volume/4050-{Uri.EscapeDataString(volumeId)}/?api_key={Uri.EscapeDataString(apiKey)}&format=json";
        using var client = _httpFactory.CreateClient(_config.Name);
        var json = await _rateLimiter.ExecuteAsync(
            Name,
            _config.RateLimit,
            token => client.GetFromJsonAsync<JsonNode>(url, token),
            ct).ConfigureAwait(false);

        var volume = json?["results"];
        if (volume is null)
        {
            return null;
        }

        var issueCount = volume["count_of_issues"]?.GetValue<long?>() is { } count
            ? (int?)Convert.ToInt32(count, CultureInfo.InvariantCulture)
            : null;
        var startYear = TryExtractYear(volume["start_year"]?.GetValue<string>());
        var publisher = volume["publisher"]?["name"]?.GetValue<string>();
        return new ComicVineVolumeFacts(issueCount, startYear, publisher);
    }

    private async Task<ProviderSequenceManifest?> TryFetchComicVineSequenceManifestAsync(
        string volumeId,
        string? seriesLabel,
        int? providerIssueCount,
        ProviderLookupRequest request,
        CancellationToken ct)
    {
        var lazy = _comicVineSequenceManifests.GetOrAdd(
            volumeId,
            _ => new Lazy<Task<ProviderSequenceManifest?>>(
                () => FetchComicVineSequenceManifestCoreAsync(
                    volumeId,
                    seriesLabel,
                    providerIssueCount,
                    request,
                    ct),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        catch
        {
            _comicVineSequenceManifests.TryRemove(volumeId, out _);
            throw;
        }
    }

    private async Task<ProviderSequenceManifest?> FetchComicVineSequenceManifestCoreAsync(
        string volumeId,
        string? seriesLabel,
        int? providerIssueCount,
        ProviderLookupRequest request,
        CancellationToken ct)
    {
        var sequenceConfig = _config.SequenceManifest;
        var baseUrl = ResolveBaseUrl(request);
        var apiKey = EffectiveApiKey;
        if (sequenceConfig?.Enabled != true
            || string.IsNullOrWhiteSpace(baseUrl)
            || string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var items = new List<ProviderSequenceManifestItem>();
        int? responseTotal = null;
        var completedAllPages = false;
        for (var page = 0; page < sequenceConfig.MaxPages; page++)
        {
            var offset = page * sequenceConfig.PageSize;
            var url = sequenceConfig.UrlTemplate
                .Replace("{base_url}", baseUrl.TrimEnd('/'), StringComparison.Ordinal)
                .Replace("{api_key}", Uri.EscapeDataString(apiKey), StringComparison.Ordinal)
                .Replace("{container_id}", Uri.EscapeDataString(volumeId), StringComparison.Ordinal)
                .Replace("{field_list}", Uri.EscapeDataString(string.Join(',', sequenceConfig.Fields)), StringComparison.Ordinal)
                .Replace("{page_size}", sequenceConfig.PageSize.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{offset}", offset.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
            var json = await FetchComicVineJsonAsync(url, ct).ConfigureAwait(false);
            responseTotal ??= json?["number_of_total_results"]?.GetValue<int?>();
            var results = json?["results"]?.AsArray();
            if (results is null)
            {
                break;
            }

            foreach (var issue in results.Where(issue => issue is not null))
            {
                var id = ExtractFirstString(issue!, ["id"]);
                var ordinal = ExtractFirstString(issue!, ["issue_number"]);
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(ordinal))
                {
                    continue;
                }

                items.Add(new ProviderSequenceManifestItem
                {
                    ExternalId = id,
                    Ordinal = ordinal,
                    Title = StringHelpers.FirstNonBlank(
                        ExtractFirstString(issue!, ["name"]),
                        !string.IsNullOrWhiteSpace(seriesLabel) ? $"{seriesLabel} #{ordinal}" : null,
                        $"Issue #{ordinal}")!,
                    ReleaseDate = ExtractFirstString(issue!, ["cover_date"]),
                });
            }

            var expectedTotal = providerIssueCount ?? responseTotal;
            if (results.Count == 0
                || results.Count < sequenceConfig.PageSize
                || (expectedTotal.HasValue && items.Count >= expectedTotal.Value))
            {
                completedAllPages = results.Count < sequenceConfig.PageSize
                    || (expectedTotal.HasValue && items.Count >= expectedTotal.Value);
                break;
            }
        }

        var distinctItems = items
            .GroupBy(item => item.ExternalId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => ParseComicOrdinalForSort(item.Ordinal))
            .ThenBy(item => item.Ordinal, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctItems.Count == 0)
        {
            return null;
        }

        var total = providerIssueCount ?? responseTotal;
        var isAuthoritative = completedAllPages
            && total.HasValue
            && distinctItems.Count == total.Value;
        return new ProviderSequenceManifest
        {
            Provider = Name,
            ContainerId = $"comicvine:volume:{volumeId}",
            ContainerLabel = seriesLabel,
            ExternalIdKey = BridgeIdKeys.ComicVineId,
            MediaType = MediaType.Comics.ToString(),
            ContainerKind = sequenceConfig.ContainerKind,
            ExpectedTotal = total,
            ExpectedTotalKind = sequenceConfig.ExpectedTotalKind,
            IsAuthoritative = isAuthoritative,
            Items = distinctItems,
        };
    }

    private static double ParseComicOrdinalForSort(string value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : double.MaxValue;

    private async Task<ComicVineVolumeFacts?> TryFetchComicVineVolumeFactsForSelectionAsync(
        string? volumeId,
        ProviderLookupRequest request,
        CancellationToken ct)
    {
        try
        {
            return await TryFetchComicVineVolumeFactsAsync(volumeId, request, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(
                ex,
                "{Provider}: ComicVine volume facts unavailable while scoring candidate volume {VolumeId}",
                Name,
                volumeId);
            return null;
        }
    }

    private static void AddComicVineVolumeClaim(
        List<ProviderClaim> claims,
        string key,
        string? value,
        double confidence)
    {
        if (string.IsNullOrWhiteSpace(value)
            || claims.Any(claim => string.Equals(claim.Key, key, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        claims.Add(new ProviderClaim(key, value, confidence));
    }

    private IReadOnlyList<ProviderClaim> EnrichComicVineCreatorClaims(
        IReadOnlyList<ProviderClaim> claims,
        JsonNode resultNode,
        ProviderLookupRequest request)
    {
        if (!string.Equals(Name, "comicvine", StringComparison.OrdinalIgnoreCase)
            || request.MediaType != MediaType.Comics)
        {
            return claims;
        }

        if (JsonPathEvaluator.Evaluate(resultNode, "person_credits") is not JsonArray credits
            || credits.Count == 0)
        {
            return claims;
        }

        var enriched = claims.ToList();
        foreach (var credit in credits)
        {
            if (credit is null)
            {
                continue;
            }

            var name = ExtractFirstString(credit, ["name", "person.name", "credited_name"]);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var role = ExtractFirstString(credit, [
                "role",
                "role.name",
                "credit_type",
                "type",
                "job",
                "person_role"
            ]);
            var targetKey = ResolveComicCreatorClaimKey(role);
            if (targetKey is null)
            {
                continue;
            }

            AddDistinctClaim(enriched, targetKey, name, 0.82);
        }

        return enriched;
    }

    private static string? ResolveComicCreatorClaimKey(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return null;
        }

        var normalized = NormalizeComicText(role);
        if (normalized.Contains("writer", StringComparison.Ordinal)
            || normalized.Contains("script", StringComparison.Ordinal)
            || normalized.Contains("story", StringComparison.Ordinal))
        {
            return MetadataFieldConstants.Author;
        }

        if (normalized.Contains("artist", StringComparison.Ordinal)
            || normalized.Contains("pencil", StringComparison.Ordinal)
            || normalized.Contains("inker", StringComparison.Ordinal)
            || normalized.Contains("illustrator", StringComparison.Ordinal))
        {
            return MetadataFieldConstants.Illustrator;
        }

        return null;
    }

    private static void AddDistinctClaim(
        List<ProviderClaim> claims,
        string key,
        string? value,
        double confidence)
    {
        if (string.IsNullOrWhiteSpace(value)
            || claims.Any(claim =>
                string.Equals(claim.Key, key, StringComparison.OrdinalIgnoreCase)
                && string.Equals(claim.Value, value, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        claims.Add(new ProviderClaim(key, value, confidence));
    }

    private bool ClaimsMatchRequest(
        IReadOnlyList<ProviderClaim> claims,
        ProviderLookupRequest request,
        SearchStrategyConfig strategy)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return true;
        }

        var candidateTitle = claims.FirstOrDefault(c =>
            string.Equals(c.Key, MetadataFieldConstants.Title, StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrWhiteSpace(candidateTitle))
        {
            return true;
        }

        if (request.MediaType == MediaType.Comics && ComicClaimsMatchRequest(claims, request))
        {
            return true;
        }

        if (request.MediaType == MediaType.Comics && ComicVolumeClaimsMatchRequest(claims, request, strategy))
        {
            return true;
        }

        var titleScore = ComputeWordOverlap(
            CleanTitleForSearch(request.Title) ?? request.Title,
            CleanTitleForSearch(candidateTitle) ?? candidateTitle);
        if (titleScore >= 0.40)
        {
            return true;
        }

        var candidateAuthor = claims.FirstOrDefault(c =>
            string.Equals(c.Key, MetadataFieldConstants.Author, StringComparison.OrdinalIgnoreCase))?.Value;
        var authorScore = !string.IsNullOrWhiteSpace(request.Author) && !string.IsNullOrWhiteSpace(candidateAuthor)
            ? ComputeWordOverlap(request.Author, candidateAuthor)
            : 0.0;

        _logger.LogInformation(
            "{Provider}/{Strategy}: rejected mismatched result '{CandidateTitle}' by '{CandidateAuthor}' for requested '{Title}' by '{Author}' (title={TitleScore:F2}, author={AuthorScore:F2})",
            Name,
            strategy.Name,
            candidateTitle,
            candidateAuthor ?? "-",
            request.Title,
            request.Author ?? "-",
            titleScore,
            authorScore);

        return false;
    }

    private IReadOnlyList<ProviderClaim> NormalizeClaimsForStrategy(
        SearchStrategyConfig strategy,
        ProviderLookupRequest request,
        IReadOnlyList<ProviderClaim> claims)
    {
        claims = ApplyConfiguredClaimSafety(strategy, request, claims);

        if (!string.Equals(Name, "comicvine", StringComparison.OrdinalIgnoreCase)
            || request.MediaType != MediaType.Comics
            || (!strategy.Name.Contains("issue", StringComparison.OrdinalIgnoreCase)
                && !strategy.Name.Contains("volume", StringComparison.OrdinalIgnoreCase)))
        {
            return claims;
        }

        if (strategy.Name.Contains("volume", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeComicVineVolumeClaims(claims, request);
        }

        return claims
            .Where(claim => !string.Equals(claim.Key, MetadataFieldConstants.Description, StringComparison.OrdinalIgnoreCase))
            .Where(claim => ShouldKeepComicIssueClaim(claim, request))
            .ToList();
    }

    private static IReadOnlyList<ProviderClaim> ApplyConfiguredClaimSafety(
        SearchStrategyConfig strategy,
        ProviderLookupRequest request,
        IReadOnlyList<ProviderClaim> claims)
    {
        if (strategy.SuppressClaims.Count == 0
            && strategy.RequestClaimOverrides.Count == 0)
        {
            return claims;
        }

        var suppressed = strategy.SuppressClaims.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var overridden = strategy.RequestClaimOverrides.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalized = claims
            .Where(claim => !suppressed.Contains(claim.Key) && !overridden.Contains(claim.Key))
            .ToList();

        foreach (var (claimKey, requestField) in strategy.RequestClaimOverrides)
        {
            var value = ResolveRequestField(request, requestField);
            if (!string.IsNullOrWhiteSpace(value))
            {
                normalized.Add(new ProviderClaim(claimKey, value.Trim(), 1.0));
            }
        }

        return normalized;
    }

    private static IReadOnlyList<ProviderClaim> NormalizeComicVineVolumeClaims(
        IReadOnlyList<ProviderClaim> claims,
        ProviderLookupRequest request)
    {
        var normalized = claims
            .Where(claim => !string.Equals(claim.Key, BridgeIdKeys.ComicVineId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var volumeId = claims.FirstOrDefault(claim =>
                string.Equals(claim.Key, BridgeIdKeys.ComicVineVolumeId, StringComparison.OrdinalIgnoreCase))?.Value
            ?? claims.FirstOrDefault(claim =>
                string.Equals(claim.Key, BridgeIdKeys.ComicVineId, StringComparison.OrdinalIgnoreCase))?.Value;
        AddDistinctClaim(normalized, BridgeIdKeys.ComicVineVolumeId, volumeId, 0.95);

        var series = request.Series
            ?? request.Hints?.GetValueOrDefault(MetadataFieldConstants.Series)
            ?? claims.FirstOrDefault(claim =>
                string.Equals(claim.Key, MetadataFieldConstants.Series, StringComparison.OrdinalIgnoreCase))?.Value
            ?? claims.FirstOrDefault(claim =>
                string.Equals(claim.Key, MetadataFieldConstants.Title, StringComparison.OrdinalIgnoreCase))?.Value;
        AddDistinctClaim(normalized, MetadataFieldConstants.Series, series, 0.9);

        var issue = GetComicIssueHint(request);
        if (!string.IsNullOrWhiteSpace(issue))
        {
            AddDistinctClaim(normalized, MetadataFieldConstants.IssueNumber, issue, 0.72);
            AddDistinctClaim(normalized, MetadataFieldConstants.SeriesPosition, issue, 0.72);
        }

        return normalized;
    }

    private static bool ShouldKeepComicIssueClaim(ProviderClaim claim, ProviderLookupRequest request)
    {
        if (!string.Equals(claim.Key, MetadataFieldConstants.IssueDescription, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(claim.Value))
        {
            return false;
        }

        var preferredLanguage = StringHelpers.FirstNonBlank(request.FileLanguage, request.Language);
        var expectsEnglish = string.IsNullOrWhiteSpace(preferredLanguage)
            || preferredLanguage.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        return !expectsEnglish || !LooksNonEnglishDescription(claim.Value);
    }

    private static bool ComicClaimsMatchRequest(
        IReadOnlyList<ProviderClaim> claims,
        ProviderLookupRequest request)
    {
        var fileSeries = request.Series
            ?? request.Hints?.GetValueOrDefault(MetadataFieldConstants.Series);
        var fileIssue = GetComicIssueHint(request);
        if (string.IsNullOrWhiteSpace(fileSeries) || string.IsNullOrWhiteSpace(fileIssue))
        {
            return false;
        }

        var candidateSeries = claims.FirstOrDefault(c =>
            string.Equals(c.Key, MetadataFieldConstants.Series, StringComparison.OrdinalIgnoreCase))?.Value;
        var candidateIssue = claims.FirstOrDefault(c =>
                string.Equals(c.Key, "issue_number", StringComparison.OrdinalIgnoreCase))?.Value
            ?? claims.FirstOrDefault(c =>
                string.Equals(c.Key, MetadataFieldConstants.SeriesPosition, StringComparison.OrdinalIgnoreCase))?.Value
            ?? claims.FirstOrDefault(c =>
                string.Equals(c.Key, "issue", StringComparison.OrdinalIgnoreCase))?.Value;

        return !string.IsNullOrWhiteSpace(candidateSeries)
            && !string.IsNullOrWhiteSpace(candidateIssue)
            && AreEquivalentComicText(fileSeries, candidateSeries)
            && AreEquivalentComicOrdinals(fileIssue, candidateIssue);
    }

    private static bool ComicVolumeClaimsMatchRequest(
        IReadOnlyList<ProviderClaim> claims,
        ProviderLookupRequest request,
        SearchStrategyConfig strategy)
    {
        if (!strategy.Name.Contains("volume", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fileSeries = request.Series
            ?? request.Hints?.GetValueOrDefault(MetadataFieldConstants.Series);
        if (string.IsNullOrWhiteSpace(fileSeries))
        {
            return false;
        }

        var candidateSeries = claims.FirstOrDefault(c =>
                string.Equals(c.Key, MetadataFieldConstants.Series, StringComparison.OrdinalIgnoreCase))?.Value
            ?? claims.FirstOrDefault(c =>
                string.Equals(c.Key, MetadataFieldConstants.Title, StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrWhiteSpace(candidateSeries)
            || !AreEquivalentComicText(fileSeries, candidateSeries))
        {
            return false;
        }

        var volumeId = claims.FirstOrDefault(c =>
            string.Equals(c.Key, BridgeIdKeys.ComicVineVolumeId, StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrWhiteSpace(volumeId))
        {
            return false;
        }

        var supportingSignals = 0;
        var fileIssue = GetComicIssueHint(request);
        var requestedIssueNumber = TryParseLeadingInt(fileIssue);
        var sequenceTotal = TryParseLeadingInt(claims.FirstOrDefault(c =>
            string.Equals(c.Key, MetadataFieldConstants.SequenceTotal, StringComparison.OrdinalIgnoreCase))?.Value);
        if (requestedIssueNumber.HasValue
            && sequenceTotal.HasValue
            && sequenceTotal.Value >= requestedIssueNumber.Value)
        {
            supportingSignals++;
        }

        var requestedPublisher = request.Hints?.GetValueOrDefault(MetadataFieldConstants.PublisherField);
        var candidatePublisher = claims.FirstOrDefault(c =>
            string.Equals(c.Key, MetadataFieldConstants.PublisherField, StringComparison.OrdinalIgnoreCase))?.Value;
        if (!string.IsNullOrWhiteSpace(requestedPublisher)
            && !string.IsNullOrWhiteSpace(candidatePublisher)
            && ComputeWordOverlap(requestedPublisher, candidatePublisher) >= 0.75)
        {
            supportingSignals++;
        }

        var requestedYear = TryExtractYear(
            request.Year
            ?? ExtractYearFromTitle(request.Title)
            ?? request.Hints?.GetValueOrDefault("year"));
        var startYear = TryExtractYear(claims.FirstOrDefault(c =>
            string.Equals(c.Key, MetadataFieldConstants.SeriesStartYear, StringComparison.OrdinalIgnoreCase))?.Value);
        if (requestedYear.HasValue
            && startYear.HasValue
            && Math.Abs(requestedYear.Value - startYear.Value) <= 8)
        {
            supportingSignals++;
        }

        return supportingSignals >= 1;
    }

}
