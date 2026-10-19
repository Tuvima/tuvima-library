using System.Text.Json.Nodes;
using MediaEngine.Domain.Configuration;
using MediaEngine.Providers.Models;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Adapters;

public sealed partial class ConfigDrivenAdapter
{
    /// <summary>
    /// Picks one result with the provider's declared <c>candidate_ranking</c>. Returns <c>null</c>
    /// when no ranking applies or nothing fits, so the ordinary title matching takes over.
    /// </summary>
    private async Task<JsonNode?> TryRankCandidatesAsync(
        JsonArray results,
        ProviderLookupRequest request,
        CancellationToken ct)
    {
        foreach (var ranking in _config.CandidateRanking ?? [])
        {
            if (!ranking.AppliesTo(request.MediaType))
            {
                continue;
            }

            var facts = CandidateRankingEvaluator.ResolveFacts(
                ranking,
                name => ResolveRankingRequestField(request, name),
                name => request.Hints?.GetValueOrDefault(name),
                ExtractYearFromTitle(request.Title));
            if (facts is null)
            {
                continue;
            }

            var picked = await RankAsync(ranking, results, request, facts, ct).ConfigureAwait(false);
            if (picked is not null)
            {
                return picked;
            }
        }

        return null;
    }

    private static string? ResolveRankingRequestField(ProviderLookupRequest request, string name) => name switch
    {
        "title" => request.Title,
        "year" => request.Year,
        "series" => request.Series,
        _ => ResolveRequestField(request, name),
    };

    private async Task<JsonNode?> RankAsync(
        CandidateRankingConfiguration ranking,
        JsonArray results,
        ProviderLookupRequest request,
        RankingFacts facts,
        CancellationToken ct)
    {
        var set = ranking.Candidates;
        var candidates = results
            .Where(node => node is not null && CandidateRankingEvaluator.Matches(node, set.Match, facts))
            .Select(node => CandidateRankingEvaluator.Build(node!, set.Fields, set.Score, facts))
            .ToList();

        if (ranking.PreferredRun is not null
            && await TryFindInPreferredRunAsync(ranking, request, facts, ct).ConfigureAwait(false) is { } found)
        {
            var extra = CandidateRankingEvaluator.Build(found, set.Fields, set.Score, facts);
            extra.Score += ranking.PreferredRun.Result.ScoreBonus;
            if (!candidates.Any(candidate => CandidateRankingEvaluator.IsSameItem(candidate.Node, extra.Node, set.Identity)))
            {
                candidates.Add(extra);
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        if (candidates.Count > 1 && ranking.EnrichWhenMultiple is { } enrichment)
        {
            foreach (var candidate in candidates)
            {
                await EnrichCandidateAsync(enrichment, request, candidate, ct).ConfigureAwait(false);
            }
        }

        foreach (var candidate in candidates)
        {
            candidate.Score += ranking.FinalTerms.Sum(term => CandidateRankingEvaluator.Term(term, candidate, facts));
        }

        return CandidateRankingEvaluator.Order(candidates, ranking.Order).FirstOrDefault()?.Node;
    }

    /// <summary>Searches for the runs that fit and looks the requested item up in each, best run first.</summary>
    private async Task<JsonNode?> TryFindInPreferredRunAsync(
        CandidateRankingConfiguration ranking,
        ProviderLookupRequest request,
        RankingFacts facts,
        CancellationToken ct)
    {
        var run = ranking.PreferredRun!;
        if (!run.WhenFacts.All(name => HasRankingValue(ranking, facts, name)))
        {
            return null;
        }

        try
        {
            var runs = await FindRunsAsync(run.Search, request, facts, ct).ConfigureAwait(false);
            var baseUrl = ResolveBaseUrl(request);
            if (runs.Count == 0 || string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(EffectiveApiKey))
            {
                return null;
            }

            foreach (var candidate in runs)
            {
                var values = TemplateValues(facts, candidate);
                var json = await FetchJsonAsync(
                    ExpandUrlTemplate(run.Result.Url, baseUrl, values), useResponseCache: false, notFoundIsError: true, ct)
                    .ConfigureAwait(false);
                if (JsonPathEvaluator.Evaluate(json, run.Result.ItemsPath) is not JsonArray items)
                {
                    continue;
                }

                var item = items.FirstOrDefault(node =>
                    node is not null && CandidateRankingEvaluator.Matches(node, run.Result.Match, facts, values));
                if (item is not null)
                {
                    return item;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "{Provider}: preferred-run lookup failed for {Ranking}", Name, ranking.Name);
            return null;
        }
    }

    private async Task<IReadOnlyList<RankedCandidate>> FindRunsAsync(
        RankingRunSearchConfiguration search,
        ProviderLookupRequest request,
        RankingFacts facts,
        CancellationToken ct)
    {
        var baseUrl = ResolveBaseUrl(request);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(EffectiveApiKey))
        {
            return [];
        }

        var json = await FetchJsonAsync(
            ExpandUrlTemplate(search.Url, baseUrl, facts.AsTemplateValues()), useResponseCache: false, notFoundIsError: true, ct)
            .ConfigureAwait(false);
        if (JsonPathEvaluator.Evaluate(json, search.ItemsPath) is not JsonArray items)
        {
            return [];
        }

        var runs = new List<RankedCandidate>();
        foreach (var node in items)
        {
            if (node is null || !CandidateRankingEvaluator.Matches(node, search.Match, facts))
            {
                continue;
            }

            var run = CandidateRankingEvaluator.Build(node, search.Fields, search.Score, facts);
            if (string.IsNullOrWhiteSpace(run.Field(search.IdField).Text)
                || IsRejected(search.RejectIfBelow, run, facts))
            {
                continue;
            }

            runs.Add(run);
        }

        var ordered = CandidateRankingEvaluator.Order(runs, search.Order);
        return search.Top > 0 ? ordered.Take(search.Top).ToList() : ordered;
    }

    private static bool IsRejected(RankingRejectConfiguration? reject, RankedCandidate run, RankingFacts facts) =>
        reject is not null
        && facts.Get(reject.Fact).Number is { } floor
        && run.Field(reject.Field).Number is { } count
        && count < floor;

    private async Task EnrichCandidateAsync(
        RankingEnrichmentConfiguration enrichment,
        ProviderLookupRequest request,
        RankedCandidate candidate,
        CancellationToken ct)
    {
        var key = candidate.Field(enrichment.KeyField).Text;
        JsonNode? item = null;
        if (!string.IsNullOrWhiteSpace(key))
        {
            try
            {
                var baseUrl = ResolveBaseUrl(request);
                var values = candidate.Fields
                    .Where(pair => !string.IsNullOrWhiteSpace(pair.Value.Text))
                    .ToDictionary(pair => pair.Key, pair => pair.Value.Text!, StringComparer.Ordinal);
                if (!string.IsNullOrWhiteSpace(baseUrl) && !string.IsNullOrWhiteSpace(EffectiveApiKey))
                {
                    var url = ExpandUrlTemplate(enrichment.Url, baseUrl, values);
                    var response = enrichment.Cache?.Store == FollowUpCacheStores.Memory
                        ? await FetchRememberedAsync(
                            ClaimRuleEngine.ExpandVariables(enrichment.Cache.Key ?? url, values),
                            () => FetchJsonAsync(url, useResponseCache: false, notFoundIsError: true, ct)).ConfigureAwait(false)
                        : await FetchJsonAsync(url, useResponseCache: false, notFoundIsError: true, ct).ConfigureAwait(false);
                    item = JsonPathEvaluator.Evaluate(response, enrichment.ItemPath);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "{Provider}: candidate details unavailable while ranking {Key}", Name, key);
            }
        }

        if (item is null)
        {
            return;
        }

        var read = CandidateRankingEvaluator.ReadFields(item, enrichment.Adds);
        foreach (var (name, add) in enrichment.Adds)
        {
            var existing = candidate.Field(name);
            var hasExisting = existing.Number is not null || !string.IsNullOrWhiteSpace(existing.Text);
            if (!(add.OnlyIfMissing && hasExisting))
            {
                candidate.Fields[name] = read[name];
            }
        }
    }

    private static bool HasRankingValue(CandidateRankingConfiguration ranking, RankingFacts facts, string name)
    {
        var value = facts.Get(name);
        return ranking.Facts.TryGetValue(name, out var fact)
            && fact.As is RankingValueKinds.Year or RankingValueKinds.LeadingInt
                ? value.Number is not null
                : !string.IsNullOrWhiteSpace(value.Text);
    }

    private static Dictionary<string, string> TemplateValues(RankingFacts facts, RankedCandidate candidate)
    {
        var values = new Dictionary<string, string>(facts.AsTemplateValues(), StringComparer.Ordinal);
        foreach (var (name, value) in candidate.Fields)
        {
            if (!string.IsNullOrWhiteSpace(value.Text))
            {
                values[name] = value.Text;
            }
        }

        return values;
    }
}
