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
        claims = ApplyResultRules(claims, resultNode, request);

        if (!ClaimsMatchRequest(claims, request, strategy))
        {
            return [];
        }

        return await RunFollowUpsAsync(claims, resultNode, request, ct).ConfigureAwait(false);
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
        return ApplyStrategyClaimRules(strategy, request, claims);
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
        if (strategy.Role != StrategyRoles.Container)
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
