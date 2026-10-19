using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Storage.Configuration;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// Bad shapes in a provider's candidate ranking, result rules, strategy claim rules and paged
/// sequences are reported when the file is loaded. Each case edits the shipped ComicVine file.
/// </summary>
public sealed class ProviderRankingValidationTests
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static ProviderConfiguration Shipped()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "MediaEngine.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return JsonSerializer.Deserialize<ProviderConfiguration>(
            File.ReadAllText(Path.Combine(dir!, "config", "providers", "comicvine.json")), s_json)!;
    }

    private static IReadOnlyList<string> Errors(Action<ProviderConfiguration> edit)
    {
        var config = Shipped();
        edit(config);
        return JsonConfigValidator.Validate(config, "providers/comicvine.json");
    }

    [Fact]
    public void ShippedFile_HasNoErrors() =>
        Assert.Empty(Errors(_ => { }));

    [Fact]
    public void RankingThatRequiresAnUndeclaredFact_IsRejected()
    {
        var errors = Errors(config => config.CandidateRanking![0].RequireFacts.Add("missing"));

        Assert.Contains("candidate_ranking['issue_in_run'].require_facts names unknown fact 'missing'.", errors);
    }

    [Fact]
    public void FactSourceThatReadsALaterFact_IsRejected()
    {
        var errors = Errors(config => config.CandidateRanking![0].Facts["series"].From.Add("fact:publisher"));

        Assert.Contains(errors, error => error.Contains("names a fact that is not declared before it", StringComparison.Ordinal));
    }

    [Fact]
    public void TermThatNamesAnUnknownFieldOrKind_IsRejected()
    {
        var errors = Errors(config =>
        {
            config.CandidateRanking![0].Candidates.Score.Terms[0].Field = "nope";
            config.CandidateRanking[0].FinalTerms[0].Kind = "magic";
        });

        Assert.Contains(errors, error => error.Contains("terms[0].field names unknown field 'nope'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("final_terms[0].kind 'magic' must be one of", StringComparison.Ordinal));
    }

    [Fact]
    public void YearBandsOutOfOrderOrOnATextField_AreRejected()
    {
        var errors = Errors(config =>
        {
            var term = config.CandidateRanking![0].Candidates.Score.Terms[1];
            term.Bands![1].MaxDelta = 0;
            term.Field = "title";
        });

        Assert.Contains(errors, error => error.Contains("bands must have increasing max_delta", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains(".field must be a year value", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownRankingProperty_IsReportedWithItsPath()
    {
        var json = JsonSerializer.Serialize(Shipped(), s_json)
            .Replace("\"require_facts\"", "\"require_fact\"", StringComparison.Ordinal);
        var config = JsonSerializer.Deserialize<ProviderConfiguration>(json, s_json)!;

        var errors = JsonConfigValidator.Validate(config, "providers/comicvine.json");

        Assert.Contains("candidate_ranking['issue_in_run'].require_fact is not a known property.", errors);
    }

    [Fact]
    public void PreferredRunWithAnUndeclaredPlaceholderOrIdField_IsRejected()
    {
        var errors = Errors(config =>
        {
            var run = config.CandidateRanking![0].PreferredRun!;
            run.Search.Url += "&x={nothing}";
            run.Search.IdField = "ghost";
        });

        Assert.Contains(errors, error => error.Contains("{nothing}", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("id_field must name one of the search fields", StringComparison.Ordinal));
    }

    [Fact]
    public void OrderKeyForAnUnknownField_IsRejected()
    {
        var errors = Errors(config => config.CandidateRanking![0].Order.Add("ghost:desc"));

        Assert.Contains(errors, error => error.Contains("entry 'ghost:desc' must be field:asc or field:desc", StringComparison.Ordinal));
    }

    [Fact]
    public void StrategyRoleAndClaimRules_AreChecked()
    {
        var errors = Errors(config =>
        {
            var strategy = config.SearchStrategies!.First(candidate => candidate.ClaimRules?.Derive.Count > 0);
            strategy.Role = "weird";
            strategy.ClaimRules!.Derive[0].From.Add("nowhere:x");
            strategy.ClaimRules.Derive[0].Confidence = 0;
        });

        Assert.Contains(errors, error => error.Contains(".role must be container or item", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("source 'nowhere:x'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("derive[].confidence must be greater than 0", StringComparison.Ordinal));
    }

    [Fact]
    public void ResultRuleThatFetches_IsRejected()
    {
        var errors = Errors(config => config.ResultRules![0].Rules[0].Kind = "lookup");

        Assert.Contains(errors, error => error.Contains("cannot be a lookup rule; results are not fetched", StringComparison.Ordinal));
    }

    [Fact]
    public void PagedSequenceWithoutTheProvidersPagedRequest_IsRejected()
    {
        var errors = Errors(config => config.SequenceManifest = null);

        Assert.Contains(errors, error => error.Contains("needs an enabled sequence_manifest request", StringComparison.Ordinal));
    }

    [Fact]
    public void PagedSequenceShapes_AreChecked()
    {
        var errors = Errors(config =>
        {
            var paged = config.FollowUps![0].Rules.First(rule => rule.Kind == ClaimRuleKinds.PagedSequence);
            paged.DateAs = "sometimes";
            paged.Container = null;
            paged.Item!.OrdinalPath = null;
            paged.OrderBy!.Add("by_vibes");
            paged.Manifest!.ContainerLabel = "{ghost}";
        });

        Assert.Contains(errors, error => error.Contains("date_as must be parsed or text", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains(".container is required", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("item.ordinal_path is required", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("order_by contains unknown key 'by_vibes'", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("{ghost}", StringComparison.Ordinal));
    }
}
