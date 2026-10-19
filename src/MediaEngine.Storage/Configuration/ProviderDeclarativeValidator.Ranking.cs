using System.Text.RegularExpressions;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Storage.Configuration;

internal static partial class ProviderDeclarativeValidator
{
    private static readonly string[] FactSourceKinds = ["request", "hint", "fact"];
    private static readonly string[] FieldValueKinds = [RankingValueKinds.Text, RankingValueKinds.Year, RankingValueKinds.LeadingInt];

    // -- candidate ranking ----------------------------------------------------------------------

    private static void ValidateCandidateRanking(List<CandidateRankingConfiguration>? rankings, List<string> errors)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ranking in rankings ?? [])
        {
            var path = $"candidate_ranking['{ranking.Name}']";
            AddRequired(errors, ranking.Name, $"{path}.name");
            AddUnknown(ranking.UnmappedProperties, path, errors);
            if (!string.IsNullOrWhiteSpace(ranking.Name) && !names.Add(ranking.Name))
            {
                errors.Add("candidate_ranking[].name values must be unique.");
            }

            ValidateMediaTypes(ranking.MediaTypes, $"{path}.media_types", errors);
            ValidateRanking(ranking, path, errors);
        }
    }

    private static void ValidateRanking(CandidateRankingConfiguration ranking, string path, List<string> errors)
    {
        var facts = ValidateFacts(ranking, path, errors);
        foreach (var required in ranking.RequireFacts.Where(name => !facts.ContainsKey(name)))
        {
            errors.Add($"{path}.require_facts names unknown fact '{required}'.");
        }

        var set = ranking.Candidates;
        AddUnknown(set.UnmappedProperties, $"{path}.candidates", errors);
        var candidateFields = ValidateFields(set.Fields, $"{path}.candidates.fields", errors);
        ValidateMatches(set.Match, facts, [], $"{path}.candidates.match", errors, requireAny: true);
        ValidateScore(set.Score, facts, candidateFields, $"{path}.candidates.score", errors);
        AddUnknown(set.Identity.UnmappedProperties, $"{path}.candidates.identity", errors);
        if (set.Identity.IdPaths.Count == 0)
        {
            errors.Add($"{path}.candidates.identity.id_paths must list at least one path.");
        }

        var enrichedFields = new Dictionary<string, string>(candidateFields, StringComparer.Ordinal);
        if (ranking.EnrichWhenMultiple is { } enrichment)
        {
            var enrichPath = $"{path}.enrich_when_multiple";
            AddUnknown(enrichment.UnmappedProperties, enrichPath, errors);
            AddRequired(errors, enrichment.Url, $"{enrichPath}.url");
            AddRequired(errors, enrichment.ItemPath, $"{enrichPath}.item_path");
            if (!candidateFields.ContainsKey(enrichment.KeyField))
            {
                errors.Add($"{enrichPath}.key_field must name one of the candidate fields.");
            }

            if (enrichment.Adds.Count == 0)
            {
                errors.Add($"{enrichPath}.adds must list at least one field.");
            }

            foreach (var (name, kind) in ValidateFields(enrichment.Adds, $"{enrichPath}.adds", errors))
            {
                enrichedFields[name] = kind;
            }

            ValidateUrlPlaceholders(enrichment.Url, candidateFields.Keys, $"{enrichPath}.url", errors);
            if (enrichment.Cache is not null)
            {
                AddUnknown(enrichment.Cache.UnmappedProperties, $"{enrichPath}.cache", errors);
                if (enrichment.Cache.Store != FollowUpCacheStores.Memory)
                {
                    errors.Add($"{enrichPath}.cache.store must be memory.");
                }

                ValidateUrlPlaceholders(enrichment.Cache.Key ?? string.Empty, candidateFields.Keys, $"{enrichPath}.cache.key", errors);
            }
        }

        if (ranking.PreferredRun is { } run)
        {
            ValidatePreferredRun(ranking, run, facts, $"{path}.preferred_run", errors);
        }

        for (var index = 0; index < ranking.FinalTerms.Count; index++)
        {
            ValidateTerm(ranking.FinalTerms[index], facts, enrichedFields, $"{path}.final_terms[{index}]", errors);
        }

        ValidateOrder(ranking.Order, enrichedFields.Keys, $"{path}.order", errors);
    }

    private static Dictionary<string, string> ValidateFacts(CandidateRankingConfiguration ranking, string path, List<string> errors)
    {
        var declared = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, fact) in ranking.Facts)
        {
            var factPath = $"{path}.facts['{name}']";
            AddUnknown(fact.UnmappedProperties, factPath, errors);
            if (!VariableName.IsMatch(name))
            {
                errors.Add($"{factPath} must have a lower-case name.");
            }

            if (fact.From.Count == 0)
            {
                errors.Add($"{factPath}.from must list at least one source.");
            }

            if (!Allowed(fact.As, RankingValueKinds.Text, RankingValueKinds.Year, RankingValueKinds.LeadingInt, RankingValueKinds.CleanTitle))
            {
                errors.Add($"{factPath}.as must be text, year, leading_int or clean_title.");
            }

            foreach (var source in fact.From.Concat(fact.Fallback is null ? [] : [fact.Fallback]))
            {
                var kind = source.Split(':')[0];
                var valid = source == "title_year"
                    || (FactSourceKinds.Contains(kind) && source.Contains(':') && !source.EndsWith(':'));
                if (!valid)
                {
                    errors.Add($"{factPath} source '{source}' must be title_year or kind:name with kind one of {string.Join(", ", FactSourceKinds)}.");
                }
                else if (kind == "fact" && !declared.ContainsKey(source[(source.IndexOf(':') + 1)..]))
                {
                    errors.Add($"{factPath} source '{source}' names a fact that is not declared before it.");
                }
            }

            declared[name] = fact.As;
        }

        return declared;
    }

    private static Dictionary<string, string> ValidateFields(
        Dictionary<string, RankingFieldConfiguration> fields, string path, List<string> errors)
    {
        var declared = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, field) in fields)
        {
            AddUnknown(field.UnmappedProperties, $"{path}['{name}']", errors);
            if (!VariableName.IsMatch(name))
            {
                errors.Add($"{path}['{name}'] must have a lower-case name.");
            }

            if (field.Paths.Count == 0)
            {
                errors.Add($"{path}['{name}'].paths must list at least one path.");
            }

            if (!FieldValueKinds.Contains(field.As))
            {
                errors.Add($"{path}['{name}'].as must be text, year or leading_int.");
            }

            declared[name] = field.As;
        }

        return declared;
    }

    private static void ValidateMatches(
        List<RankingMatchConfiguration> matches,
        Dictionary<string, string> facts,
        IEnumerable<string> extraValues,
        string path,
        List<string> errors,
        bool requireAny)
    {
        if (requireAny && matches.Count == 0)
        {
            errors.Add($"{path} must list at least one test.");
        }

        var known = new HashSet<string>(facts.Keys.Concat(extraValues), StringComparer.Ordinal);
        for (var index = 0; index < matches.Count; index++)
        {
            var match = matches[index];
            var matchPath = $"{path}[{index}]";
            AddUnknown(match.UnmappedProperties, matchPath, errors);
            if ((match.Fact is null) == (match.Value is null))
            {
                errors.Add($"{matchPath} needs exactly one of fact and value.");
            }

            if (match.Fact is not null && !facts.ContainsKey(match.Fact))
            {
                errors.Add($"{matchPath}.fact names unknown fact '{match.Fact}'.");
            }

            foreach (Match placeholder in Placeholder.Matches(match.Value ?? string.Empty))
            {
                if (!known.Contains(placeholder.Groups[1].Value))
                {
                    errors.Add($"{matchPath}.value uses {{{placeholder.Groups[1].Value}}}, which is not a fact or a field.");
                }
            }

            if (match.Paths.Count == 0)
            {
                errors.Add($"{matchPath}.paths must list at least one path.");
            }

            if (!Allowed(match.Compare, RankingComparisons.EquivalentText, RankingComparisons.EquivalentOrdinal, RankingComparisons.SameText))
            {
                errors.Add($"{matchPath}.compare must be equivalent_text, equivalent_ordinal or equals.");
            }
        }
    }

    private static void ValidateScore(
        RankingScoreConfiguration score,
        Dictionary<string, string> facts,
        Dictionary<string, string> fields,
        string path,
        List<string> errors)
    {
        AddUnknown(score.UnmappedProperties, path, errors);
        for (var index = 0; index < score.Terms.Count; index++)
        {
            ValidateTerm(score.Terms[index], facts, fields, $"{path}.terms[{index}]", errors);
        }
    }

    private static void ValidateTerm(
        RankingTermConfiguration term,
        Dictionary<string, string> facts,
        Dictionary<string, string> fields,
        string path,
        List<string> errors)
    {
        AddUnknown(term.UnmappedProperties, path, errors);
        if (term.Fact is not null && !facts.ContainsKey(term.Fact))
        {
            errors.Add($"{path}.fact names unknown fact '{term.Fact}'.");
        }

        if (term.Field is not null && !fields.ContainsKey(term.Field))
        {
            errors.Add($"{path}.field names unknown field '{term.Field}'.");
        }

        switch (term.Kind)
        {
            case RankingTermKinds.WordOverlap:
                RequireTermParts(term, path, errors, needFact: true, needField: true);
                break;
            case RankingTermKinds.EnglishText:
                RequireTermParts(term, path, errors, needFact: false, needField: true);
                break;
            case RankingTermKinds.TextAffinity:
                RequireTermParts(term, path, errors, needFact: true, needField: true);
                if (term.Min is <= 0 or > 1)
                {
                    errors.Add($"{path}.min must be greater than 0 and at most 1.");
                }

                break;
            case RankingTermKinds.YearBands:
                RequireTermParts(term, path, errors, needFact: true, needField: true);
                if (term.Bands is not { Count: > 0 })
                {
                    errors.Add($"{path}.bands must list at least one band.");
                }
                else if (term.Bands.Zip(term.Bands.Skip(1)).Any(pair => pair.Second.MaxDelta <= pair.First.MaxDelta))
                {
                    errors.Add($"{path}.bands must have increasing max_delta values.");
                }

                foreach (var band in term.Bands ?? [])
                {
                    AddUnknown(band.UnmappedProperties, $"{path}.bands[]", errors);
                }

                foreach (var (label, name) in new[] { ("fact", term.Fact), ("field", term.Field) })
                {
                    var kind = label == "fact"
                        ? name is not null && facts.TryGetValue(name, out var factKind) ? factKind : null
                        : name is not null && fields.TryGetValue(name, out var fieldKind) ? fieldKind : null;
                    if (kind is not null && kind != RankingValueKinds.Year)
                    {
                        errors.Add($"{path}.{label} must be a year value.");
                    }
                }

                break;
            case RankingTermKinds.LogScale:
                RequireTermParts(term, path, errors, needFact: false, needField: true);
                if (term.Factor <= 0 || term.Cap <= 0)
                {
                    errors.Add($"{path}.factor and cap must be greater than 0.");
                }

                if (term.BelowFact is not null && !facts.ContainsKey(term.BelowFact))
                {
                    errors.Add($"{path}.below_fact names unknown fact '{term.BelowFact}'.");
                }

                break;
            default:
                errors.Add($"{path}.kind '{term.Kind}' must be one of {string.Join(", ", RankingTermKindNames)}.");
                break;
        }
    }

    private static readonly string[] RankingTermKindNames =
    [
        RankingTermKinds.WordOverlap, RankingTermKinds.YearBands, RankingTermKinds.EnglishText,
        RankingTermKinds.LogScale, RankingTermKinds.TextAffinity,
    ];

    private static void RequireTermParts(
        RankingTermConfiguration term, string path, List<string> errors, bool needFact, bool needField)
    {
        if (needFact && term.Fact is null)
        {
            errors.Add($"{path}.fact is required for a {term.Kind} term.");
        }

        if (needField && term.Field is null)
        {
            errors.Add($"{path}.field is required for a {term.Kind} term.");
        }
    }

    private static void ValidatePreferredRun(
        CandidateRankingConfiguration ranking,
        RankingPreferredRunConfiguration run,
        Dictionary<string, string> facts,
        string path,
        List<string> errors)
    {
        AddUnknown(run.UnmappedProperties, path, errors);
        foreach (var name in run.WhenFacts.Where(name => !facts.ContainsKey(name)))
        {
            errors.Add($"{path}.when_facts names unknown fact '{name}'.");
        }

        var search = run.Search;
        var searchPath = $"{path}.search";
        AddUnknown(search.UnmappedProperties, searchPath, errors);
        AddRequired(errors, search.Url, $"{searchPath}.url");
        AddRequired(errors, search.ItemsPath, $"{searchPath}.items_path");
        var fields = ValidateFields(search.Fields, $"{searchPath}.fields", errors);
        ValidateUrlPlaceholders(search.Url, facts.Keys, $"{searchPath}.url", errors);
        ValidateMatches(search.Match, facts, [], $"{searchPath}.match", errors, requireAny: true);
        if (!fields.ContainsKey(search.IdField))
        {
            errors.Add($"{searchPath}.id_field must name one of the search fields.");
        }

        if (search.RejectIfBelow is { } reject)
        {
            AddUnknown(reject.UnmappedProperties, $"{searchPath}.reject_if_below", errors);
            if (!fields.ContainsKey(reject.Field) || !facts.ContainsKey(reject.Fact))
            {
                errors.Add($"{searchPath}.reject_if_below must name a search field and a fact.");
            }
        }

        ValidateScore(search.Score, facts, fields, $"{searchPath}.score", errors);
        ValidateOrder(search.Order, fields.Keys, $"{searchPath}.order", errors);
        if (search.Top < 0)
        {
            errors.Add($"{searchPath}.top must not be negative.");
        }

        var result = run.Result;
        var resultPath = $"{path}.result";
        AddUnknown(result.UnmappedProperties, resultPath, errors);
        AddRequired(errors, result.Url, $"{resultPath}.url");
        AddRequired(errors, result.ItemsPath, $"{resultPath}.items_path");
        ValidateUrlPlaceholders(result.Url, facts.Keys.Concat(fields.Keys), $"{resultPath}.url", errors);
        ValidateMatches(result.Match, facts, fields.Keys, $"{resultPath}.match", errors, requireAny: true);
    }

    private static void ValidateOrder(List<string> order, IEnumerable<string> fields, string path, List<string> errors)
    {
        var known = new HashSet<string>(fields.Append("score"), StringComparer.Ordinal);
        foreach (var key in order)
        {
            var parts = key.Split(':');
            if (parts.Length != 2 || !known.Contains(parts[0]) || !Allowed(parts[1], "asc", "desc"))
            {
                errors.Add($"{path} entry '{key}' must be field:asc or field:desc for a known field or score.");
            }
        }
    }

    private static void ValidateUrlPlaceholders(string template, IEnumerable<string> names, string path, List<string> errors)
    {
        var known = new HashSet<string>(names.Concat(BuiltInUrlPlaceholders), StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(template, @"\{([a-z_][a-z0-9_]*)\}"))
        {
            if (!known.Contains(match.Groups[1].Value))
            {
                errors.Add($"{path} uses {{{match.Groups[1].Value}}}, which is not built in or declared.");
            }
        }
    }

    // -- strategy claim rules --------------------------------------------------------------------

    private static void ValidateStrategyClaimRules(List<SearchStrategyConfig>? strategies, List<string> errors)
    {
        foreach (var strategy in strategies ?? [])
        {
            var path = $"search_strategies['{strategy.Name}']";
            if (strategy.Role is not null && !Allowed(strategy.Role, StrategyRoles.Container, StrategyRoles.Item))
            {
                errors.Add($"{path}.role must be container or item.");
            }

            foreach (var (key, alias) in strategy.ExtraFieldAliases)
            {
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(alias))
                {
                    errors.Add($"{path}.extra_field_aliases must map claim keys to non-blank names.");
                }
            }

            if (strategy.ClaimRules is not { } rules)
            {
                continue;
            }

            var rulesPath = $"{path}.claim_rules";
            AddUnknown(rules.UnmappedProperties, rulesPath, errors);
            ValidateMediaTypes(rules.MediaTypes, $"{rulesPath}.media_types", errors);
            if (rules.Drop.Concat(rules.DropNonEnglishText).Any(string.IsNullOrWhiteSpace))
            {
                errors.Add($"{rulesPath} must not list blank claim keys.");
            }

            foreach (var derived in rules.Derive)
            {
                AddUnknown(derived.UnmappedProperties, $"{rulesPath}.derive[]", errors);
                AddRequired(errors, derived.ClaimKey, $"{rulesPath}.derive[].claim_key");
                AddConfidence(derived.Confidence, $"{rulesPath}.derive[].confidence", errors);
                ValidateAddMode(derived.Add, $"{rulesPath}.derive[].add", errors);
                if (derived.From.Count == 0)
                {
                    errors.Add($"{rulesPath}.derive[].from must list at least one source.");
                }

                foreach (var source in derived.From)
                {
                    var kind = source.Split(':')[0];
                    if (!new[] { LookupSourceKinds.Claim, LookupSourceKinds.Request, LookupSourceKinds.Hint }.Contains(kind)
                        || !source.Contains(':') || source.EndsWith(':'))
                    {
                        errors.Add($"{rulesPath}.derive[].from source '{source}' must be claim:, request: or hint: followed by a name.");
                    }
                }
            }
        }
    }
}
