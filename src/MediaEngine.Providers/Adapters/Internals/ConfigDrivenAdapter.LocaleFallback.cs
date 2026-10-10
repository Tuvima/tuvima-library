using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
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
            language = NormalizeLanguagePart(language, "en");
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
            var languageKey = NormalizeLanguagePart(language, "en");
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
        FetchOutcome outcome,
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
                outcome.Responded = true;
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
                await RecordTransportFailureAsync(ex, strategy.Name, $"failed using {pass.Label}", outcome, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
            {
                outcome.Responded = true;
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

    /// <summary>Normalises a country part (two-letter, lowercase). Not for languages; see <see cref="NormalizeLanguagePart"/>.</summary>
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

    // Language values already reported as unrecognised, so the Debug note is logged once per value.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _loggedLanguageFallbacks =
        new(StringComparer.OrdinalIgnoreCase);

    // Specific culture names (e.g. "en-US", "pt-BR") used to confirm a {lang}-{country} pair is real.
    private static readonly Lazy<HashSet<string>> SpecificCultureNames = new(() =>
        System.Globalization.CultureInfo.GetCultures(System.Globalization.CultureTypes.SpecificCultures)
            .Select(culture => culture.Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Normalises a language value ("English", "eng", "en-US", ...) to an ISO 639-1 code.
    /// Unrecognised values fall back to <paramref name="fallback"/> (logged once at Debug).
    /// </summary>
    private string NormalizeLanguagePart(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var code = LanguageCodeNormalizer.ToIso6391(value);
        if (code is not null)
        {
            return code;
        }

        if (_loggedLanguageFallbacks.TryAdd(value.Trim(), 0))
        {
            _logger.LogDebug(
                "{Provider}: language '{Language}' is not a recognised language; using '{Fallback}'",
                Name,
                value,
                fallback);
        }

        return fallback;
    }

    /// <summary>
    /// The <c>{lang}</c> value substituted into a URL template. When the template pairs it
    /// with <c>{country}</c> (<c>lang_COUNTRY</c> / <c>lang-COUNTRY</c>) the pair must be a
    /// real culture; otherwise the language falls back to English.
    /// </summary>
    private string ResolveTemplateLanguage(ProviderLookupRequest request, string template)
    {
        var language = NormalizeLanguagePart(request.Language, "en");
        var pairsWithCountry = template.Contains("{lang}_{country}", StringComparison.OrdinalIgnoreCase)
            || template.Contains("{lang}-{country}", StringComparison.OrdinalIgnoreCase);
        return pairsWithCountry ? ApplyLanguageCountryPairFallback(language, request.Country) : language;
    }

    /// <summary>
    /// The language the request's storefront locale resolves to — the <c>{lang}</c> value
    /// that Apple-style <c>lang={lang}_{country}</c> templates actually send.
    /// </summary>
    private string ResolveStorefrontLanguage(ProviderLookupRequest request) =>
        ApplyLanguageCountryPairFallback(NormalizeLanguagePart(request.Language, "en"), request.Country);

    private string ApplyLanguageCountryPairFallback(string language, string? country)
    {
        var pair = $"{language}-{(country ?? string.Empty).Trim().ToUpperInvariant()}";
        var known = SpecificCultureNames.Value;

        // No culture data available (invariant globalization): cannot validate, so leave as-is.
        if (known.Count == 0 || known.Contains(pair))
        {
            return language;
        }

        if (_loggedLanguageFallbacks.TryAdd(pair, 0))
        {
            _logger.LogDebug(
                "{Provider}: locale '{Pair}' is not a known culture; using 'en' for the language",
                Name,
                pair);
        }

        return "en";
    }
}
