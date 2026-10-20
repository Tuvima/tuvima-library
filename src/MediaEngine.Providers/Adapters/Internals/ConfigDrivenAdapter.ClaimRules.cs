using System.Text.Json.Nodes;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Models;

namespace MediaEngine.Providers.Adapters;

public sealed partial class ConfigDrivenAdapter
{
    /// <summary>
    /// Applies the strategy's declared <c>claim_rules</c>: drops claims, removes text that reads as
    /// another language, then derives new claims from what the strategy produced and the request.
    /// </summary>
    private static IReadOnlyList<ProviderClaim> ApplyStrategyClaimRules(
        SearchStrategyConfig strategy,
        ProviderLookupRequest request,
        IReadOnlyList<ProviderClaim> claims)
    {
        if (strategy.ClaimRules is not { } rules || !rules.AppliesTo(request.MediaType))
        {
            return claims;
        }

        var dropped = rules.Drop.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var foreignGuarded = rules.DropNonEnglishText.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectsEnglish = ExpectsEnglish(request);
        var result = claims
            .Where(claim => !dropped.Contains(claim.Key))
            .Where(claim => !foreignGuarded.Contains(claim.Key)
                || (!string.IsNullOrWhiteSpace(claim.Value)
                    && (!expectsEnglish || !ResultTextRules.LooksNonEnglish(claim.Value))))
            .ToList();

        foreach (var derived in rules.Derive)
        {
            var value = derived.From
                .Select(source => ResolveDerivedSource(source, claims, request))
                .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
            ClaimRuleEngine.AddClaim(result, derived.ClaimKey, value, derived.Confidence, derived.Add);
        }

        return result;
    }

    private static bool ExpectsEnglish(ProviderLookupRequest request)
    {
        var preferred = StringHelpers.FirstNonBlank(request.FileLanguage, request.Language);
        return string.IsNullOrWhiteSpace(preferred) || preferred.StartsWith("en", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveDerivedSource(string source, IReadOnlyList<ProviderClaim> claims, ProviderLookupRequest request)
    {
        var separator = source.IndexOf(':', StringComparison.Ordinal);
        if (separator < 0)
        {
            return null;
        }

        var name = source[(separator + 1)..];
        return source[..separator] switch
        {
            LookupSourceKinds.Claim => claims
                .FirstOrDefault(claim => string.Equals(claim.Key, name, StringComparison.OrdinalIgnoreCase))?.Value,
            LookupSourceKinds.Request => ResolveRankingRequestField(request, name),
            LookupSourceKinds.Hint => request.Hints?.GetValueOrDefault(name),
            _ => null,
        };
    }

    /// <summary>Applies the provider's <c>result_rules</c> to the matched result, adding claims from it.</summary>
    private IReadOnlyList<ProviderClaim> ApplyResultRules(
        IReadOnlyList<ProviderClaim> claims,
        JsonNode resultNode,
        ProviderLookupRequest request)
    {
        var current = claims;
        foreach (var group in _config.ResultRules ?? [])
        {
            if (!group.AppliesTo(request.MediaType))
            {
                continue;
            }

            var working = current.ToList();
            var context = new ClaimRuleContext(Name, request.MediaType, new Dictionary<string, string>());
            foreach (var rule in group.Rules)
            {
                ClaimRuleEngine.Apply(working, resultNode, rule, group.Add, context);
            }

            current = working;
        }

        return current;
    }
}
