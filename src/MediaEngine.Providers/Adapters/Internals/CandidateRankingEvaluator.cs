using System.Text.Json.Nodes;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Providers.Adapters;

/// <summary>A value read from the request or from a candidate: its text and, for years and counts, a number.</summary>
internal sealed record RankingValue(string? Text, double? Number)
{
    public static readonly RankingValue Empty = new(null, null);
}

/// <summary>The request facts a ranking works with.</summary>
internal sealed class RankingFacts
{
    private readonly Dictionary<string, RankingValue> _values = new(StringComparer.Ordinal);

    public void Set(string name, RankingValue value) => _values[name] = value;

    public RankingValue Get(string name) => _values.GetValueOrDefault(name) ?? RankingValue.Empty;

    public bool Has(string name) => !string.IsNullOrWhiteSpace(Get(name).Text);

    /// <summary>Text and numbers as the placeholders of a URL or match template see them.</summary>
    public IReadOnlyDictionary<string, string> AsTemplateValues() => _values
        .Where(pair => !string.IsNullOrWhiteSpace(pair.Value.Text))
        .ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Number is { } number
                ? ((long)number).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : pair.Value.Text!,
            StringComparer.Ordinal);
}

/// <summary>One candidate result with the values read from it and its running score.</summary>
internal sealed class RankedCandidate(JsonNode node, Dictionary<string, RankingValue> fields, double score)
{
    public JsonNode Node { get; } = node;

    public Dictionary<string, RankingValue> Fields { get; } = fields;

    public double Score { get; set; } = score;

    public RankingValue Field(string name) => Fields.GetValueOrDefault(name) ?? RankingValue.Empty;
}

/// <summary>
/// The pure part of candidate ranking: reading facts, matching and scoring candidates, ordering.
/// The adapter drives the lookups around it.
/// </summary>
internal static class CandidateRankingEvaluator
{
    /// <summary>Reads the ranking's facts; <c>null</c> when a required fact has no value.</summary>
    public static RankingFacts? ResolveFacts(
        CandidateRankingConfiguration ranking,
        Func<string, string?> requestField,
        Func<string, string?> hint,
        string? titleYear)
    {
        var facts = new RankingFacts();
        foreach (var (name, fact) in ranking.Facts)
        {
            var text = fact.From.Select(source => ResolveSource(source, facts, requestField, hint, titleYear))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (string.IsNullOrWhiteSpace(text) && fact.Fallback is not null)
            {
                text = ResolveSource(fact.Fallback, facts, requestField, hint, titleYear);
            }

            facts.Set(name, ToValue(text, fact.As));
        }

        return ranking.RequireFacts.All(facts.Has) ? facts : null;
    }

    private static string? ResolveSource(
        string source, RankingFacts facts, Func<string, string?> requestField, Func<string, string?> hint, string? titleYear)
    {
        if (source == "title_year")
        {
            return titleYear;
        }

        var separator = source.IndexOf(':', StringComparison.Ordinal);
        if (separator < 0)
        {
            return null;
        }

        var name = source[(separator + 1)..];
        return source[..separator] switch
        {
            "request" => requestField(name),
            "hint" => hint(name),
            "fact" => facts.Get(name).Text,
            _ => null,
        };
    }

    private static RankingValue ToValue(string? text, string kind)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return RankingValue.Empty;
        }

        return kind switch
        {
            RankingValueKinds.Year => new RankingValue(text, ResultTextRules.ExtractYear(text)),
            RankingValueKinds.LeadingInt => new RankingValue(text, ResultTextRules.ParseLeadingInt(text)),
            RankingValueKinds.CleanTitle => new RankingValue(ConfigDrivenAdapter.CleanTitleForSearch(text) ?? text, null),
            _ => new RankingValue(text, null),
        };
    }

    /// <summary>True when the node passes every test. Both sides of a comparison must have a value.</summary>
    public static bool Matches(
        JsonNode node,
        IReadOnlyList<RankingMatchConfiguration> tests,
        RankingFacts facts,
        IReadOnlyDictionary<string, string>? extra = null)
    {
        foreach (var test in tests)
        {
            var candidate = ClaimRuleEngine.FirstText(node, test.Paths);
            var wanted = test.Fact is not null
                ? facts.Get(test.Fact).Text
                : ClaimRuleEngine.ExpandVariables(test.Value ?? string.Empty, Merge(facts, extra));
            if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(wanted))
            {
                return false;
            }

            var same = test.Compare switch
            {
                RankingComparisons.EquivalentOrdinal => ResultTextRules.AreEquivalentOrdinals(wanted, candidate),
                RankingComparisons.SameText => string.Equals(wanted, candidate, StringComparison.OrdinalIgnoreCase),
                _ => ResultTextRules.AreEquivalentText(wanted, candidate),
            };
            if (!same)
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyDictionary<string, string> Merge(RankingFacts facts, IReadOnlyDictionary<string, string>? extra)
    {
        var values = new Dictionary<string, string>(facts.AsTemplateValues(), StringComparer.Ordinal);
        foreach (var (name, value) in extra ?? new Dictionary<string, string>())
        {
            values[name] = value;
        }

        return values;
    }

    public static RankedCandidate Build(
        JsonNode node,
        IReadOnlyDictionary<string, RankingFieldConfiguration> fields,
        RankingScoreConfiguration score,
        RankingFacts facts)
    {
        var candidate = new RankedCandidate(node, ReadFields(node, fields), 0);
        candidate.Score = score.Base + score.Terms.Sum(term => Term(term, candidate, facts));
        return candidate;
    }

    public static Dictionary<string, RankingValue> ReadFields(
        JsonNode node, IReadOnlyDictionary<string, RankingFieldConfiguration> fields)
    {
        var values = new Dictionary<string, RankingValue>(StringComparer.Ordinal);
        foreach (var (name, field) in fields)
        {
            values[name] = ToValue(ClaimRuleEngine.FirstText(node, field.Paths), field.As);
        }

        return values;
    }

    public static double Term(RankingTermConfiguration term, RankedCandidate candidate, RankingFacts facts)
    {
        var field = candidate.Field(term.Field ?? string.Empty);
        switch (term.Kind)
        {
            case RankingTermKinds.WordOverlap:
                return string.IsNullOrWhiteSpace(field.Text) || string.IsNullOrWhiteSpace(facts.Get(term.Fact ?? string.Empty).Text)
                    ? 0.0
                    : ResultTextRules.ComputeWordOverlap(facts.Get(term.Fact!).Text!, field.Text) * term.Weight;
            case RankingTermKinds.EnglishText:
                return ResultTextRules.LooksNonEnglish(field.Text) ? term.Foreign : term.English;
            case RankingTermKinds.TextAffinity:
                var wanted = facts.Get(term.Fact ?? string.Empty).Text;
                if (string.IsNullOrWhiteSpace(wanted) || string.IsNullOrWhiteSpace(field.Text))
                {
                    return 0;
                }

                return ResultTextRules.ComputeWordOverlap(wanted, field.Text) >= term.Min ? term.Hit : term.Miss;
            case RankingTermKinds.YearBands:
                return YearBands(term, field, facts.Get(term.Fact ?? string.Empty));
            case RankingTermKinds.LogScale:
                return LogScale(term, field, term.BelowFact is null ? RankingValue.Empty : facts.Get(term.BelowFact));
            default:
                return 0;
        }
    }

    private static double YearBands(RankingTermConfiguration term, RankingValue field, RankingValue fact)
    {
        if (field.Number is not { } candidateYear)
        {
            return 0;
        }

        if (fact.Number is not { } requestedYear)
        {
            return term.NoFactAtOrAbove is { } threshold && candidateYear >= threshold ? term.NoFactScore : 0;
        }

        var delta = Math.Abs(requestedYear - candidateYear);
        foreach (var band in term.Bands ?? [])
        {
            if (delta <= band.MaxDelta)
            {
                return band.Score;
            }
        }

        return term.Otherwise;
    }

    private static double LogScale(RankingTermConfiguration term, RankingValue field, RankingValue below)
    {
        if (field.Number is not { } count)
        {
            return 0;
        }

        if (term.BelowFact is not null && below.Number is { } floor && count < floor)
        {
            return term.BelowScore;
        }

        if (term.AtMostCount is { } atMost)
        {
            if (count <= atMost)
            {
                return term.AtMostScore;
            }
        }
        else if (count <= 0)
        {
            return 0;
        }

        return Math.Min(term.Cap, Math.Log10(count) * term.Factor);
    }

    /// <summary>Orders best first by <c>field:desc</c> / <c>field:asc</c> keys; missing numbers sort last.</summary>
    public static List<RankedCandidate> Order(IEnumerable<RankedCandidate> candidates, IReadOnlyList<string> order)
    {
        IOrderedEnumerable<RankedCandidate>? ordered = null;
        foreach (var key in order)
        {
            var parts = key.Split(':');
            var field = parts[0];
            var descending = parts.Length > 1 && string.Equals(parts[1], "desc", StringComparison.OrdinalIgnoreCase);
            Func<RankedCandidate, double> value = field == "score"
                ? candidate => candidate.Score
                : candidate => candidate.Field(field).Number ?? (descending ? 0 : double.MaxValue);
            ordered = ordered is null
                ? descending ? candidates.OrderByDescending(value) : candidates.OrderBy(value)
                : descending ? ordered.ThenByDescending(value) : ordered.ThenBy(value);
        }

        return (ordered ?? candidates).ToList();
    }

    /// <summary>True when the two nodes are the same item by id, or by scope and ordinal when ids are missing.</summary>
    public static bool IsSameItem(JsonNode left, JsonNode right, RankingIdentityConfiguration identity)
    {
        var leftId = ClaimRuleEngine.FirstText(left, identity.IdPaths);
        var rightId = ClaimRuleEngine.FirstText(right, identity.IdPaths);
        if (!string.IsNullOrWhiteSpace(leftId) && !string.IsNullOrWhiteSpace(rightId))
        {
            return string.Equals(leftId, rightId, StringComparison.OrdinalIgnoreCase);
        }

        var leftScope = ClaimRuleEngine.FirstText(left, identity.ScopePaths);
        var rightScope = ClaimRuleEngine.FirstText(right, identity.ScopePaths);
        var leftOrdinal = ClaimRuleEngine.FirstText(left, identity.OrdinalPaths);
        var rightOrdinal = ClaimRuleEngine.FirstText(right, identity.OrdinalPaths);
        return !string.IsNullOrWhiteSpace(leftScope)
            && !string.IsNullOrWhiteSpace(rightScope)
            && !string.IsNullOrWhiteSpace(leftOrdinal)
            && !string.IsNullOrWhiteSpace(rightOrdinal)
            && string.Equals(leftScope, rightScope, StringComparison.OrdinalIgnoreCase)
            && ResultTextRules.AreEquivalentOrdinals(leftOrdinal, rightOrdinal);
    }
}
