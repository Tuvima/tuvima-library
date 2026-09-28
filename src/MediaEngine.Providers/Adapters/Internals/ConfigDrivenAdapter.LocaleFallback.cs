using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Providers.Models;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Adapters;

public sealed partial class ConfigDrivenAdapter
{
    private sealed record LookupPass(
        ProviderLookupRequest Request,
        string Label,
        bool TagSourceLanguage);

    private IReadOnlyList<LookupPass> BuildLookupPasses(ProviderLookupRequest request)
    {
        var passes = new List<LookupPass>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string language, string country, string label, bool tagSourceLanguage)
        {
            language = NormalizeLocalePart(language, "en");
            country = NormalizeLocalePart(country, "us");
            if (!seen.Add($"{language}|{country}"))
            {
                return;
            }

            passes.Add(new LookupPass(
                CloneRequestWithLocale(request, language, country),
                label,
                tagSourceLanguage));
        }

        void AddMarketFallbacks(string language, bool tagSourceLanguage)
        {
            var languageKey = NormalizeLocalePart(language, "en");
            if (!_config.MarketFallbacks.TryGetValue(languageKey, out var markets))
            {
                return;
            }

            foreach (var market in markets.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                Add(languageKey, market, $"{languageKey}-{market.ToUpperInvariant()} storefront fallback", tagSourceLanguage);
            }
        }

        var effectiveLanguage = ResolveEffectiveLanguage(request);
        Add(effectiveLanguage, request.Country, "primary locale", false);
        AddMarketFallbacks(effectiveLanguage, false);

        if (_config.LanguageStrategy == LanguageStrategy.Both
            && !string.Equals(effectiveLanguage, "en", StringComparison.OrdinalIgnoreCase))
        {
            Add("en", request.Country, "English fallback", true);
            AddMarketFallbacks("en", true);
        }

        return passes;
    }

    private async Task<IReadOnlyList<ProviderClaim>> ExecuteFetchPassAsync(
        IReadOnlyList<SearchStrategyConfig> strategies,
        LookupPass pass,
        CancellationToken ct)
    {
        foreach (var strategy in strategies)
        {
            if (!AllRequiredFieldsPresent(strategy, pass.Request))
            {
                continue;
            }

            try
            {
                var claims = await ExecuteStrategyAsync(strategy, pass.Request, ct).ConfigureAwait(false);
                await _healthMonitor.ReportSuccessAsync(Name, ct);
                if (claims.Count == 0)
                {
                    continue;
                }

                _logger.LogDebug(
                    "{Provider}/{Strategy} returned {Count} claims using {LocalePass}",
                    Name,
                    strategy.Name,
                    claims.Count,
                    pass.Label);
                return pass.TagSourceLanguage
                    ? claims.Select(claim => claim with { SourceLanguage = pass.Request.Language }).ToList()
                    : claims;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "{Provider}/{Strategy} failed using {LocalePass}",
                    Name,
                    strategy.Name,
                    pass.Label);
                await _healthMonitor.ReportFailureAsync(Name, ex.Message, ct);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
            {
                _logger.LogWarning(ex,
                    "{Provider}/{Strategy} parse error using {LocalePass}",
                    Name,
                    strategy.Name,
                    pass.Label);
            }
        }

        return [];
    }

    private async Task<IReadOnlyList<SearchResultItem>> ExecuteSearchPassAsync(
        IReadOnlyList<SearchStrategyConfig> strategies,
        LookupPass pass,
        int limit,
        CancellationToken ct,
        List<Exception>? searchFailures = null)
    {
        foreach (var strategy in strategies)
        {
            if (!AllRequiredFieldsPresent(strategy, pass.Request)
                || string.IsNullOrEmpty(strategy.ResultsPath))
            {
                continue;
            }

            var strategyLimit = strategy.MaxResults > 0
                ? Math.Min(limit, strategy.MaxResults)
                : limit;

            try
            {
                var results = await ExecuteSearchStrategyAsync(strategy, pass.Request, strategyLimit, ct)
                    .ConfigureAwait(false);
                if (results.Count > 0)
                {
                    return results;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException
                                       or OperationCanceledException
                                       or System.Text.Json.JsonException
                                       or InvalidOperationException)
            {
                searchFailures?.Add(ex);
                _logger.LogWarning(ex,
                    "{Provider}/{Strategy} search failed using {LocalePass}",
                    Name,
                    strategy.Name,
                    pass.Label);
            }
        }

        return [];
    }

    private static string NormalizeLocalePart(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return value.Trim()
            .Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)[0]
            .ToLowerInvariant();
    }
}
