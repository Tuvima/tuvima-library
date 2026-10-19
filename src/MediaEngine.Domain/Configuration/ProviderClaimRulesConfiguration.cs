using System.Text.Json;
using System.Text.Json.Serialization;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// Rules run on the result a strategy matched, before the follow-ups, to add claims the plain
/// field mappings cannot express (for example creators listed with free-text roles).
/// Declared in <c>result_rules</c> of a provider file.
/// </summary>
public sealed class ResultClaimRulesConfiguration
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Media types the rules apply to. Empty means all.</summary>
    [JsonPropertyName("media_types")]
    public List<string> MediaTypes { get; set; } = [];

    /// <summary>Default add mode of the rules: <c>always</c>, <c>if_missing_key</c> or <c>if_missing_value</c>.</summary>
    [JsonPropertyName("add")]
    public string Add { get; set; } = ClaimAddModes.IfMissingValue;

    [JsonPropertyName("rules")]
    public List<ClaimRuleConfiguration> Rules { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }

    public bool AppliesTo(MediaType mediaType) =>
        MediaTypes.Count == 0
        || MediaTypes.Any(name => string.Equals(name, mediaType.ToString(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Reshapes the claims a strategy produced: drops some, derives others from the request, and
/// removes text that reads as another language. Declared in <c>claim_rules</c> of a search strategy.
/// </summary>
public sealed class StrategyClaimRulesConfiguration
{
    /// <summary>Media types the rules apply to. Empty means all.</summary>
    [JsonPropertyName("media_types")]
    public List<string> MediaTypes { get; set; } = [];

    /// <summary>Claim keys removed.</summary>
    [JsonPropertyName("drop")]
    public List<string> Drop { get; set; } = [];

    /// <summary>
    /// Claim keys whose text is removed when the request expects English and the text reads as
    /// another language (a common-German-words heuristic).
    /// </summary>
    [JsonPropertyName("drop_non_english_text")]
    public List<string> DropNonEnglishText { get; set; } = [];

    /// <summary>Claims added after the drops; sources are read from the claims the strategy produced.</summary>
    [JsonPropertyName("derive")]
    public List<DerivedClaimConfiguration> Derive { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }

    public bool AppliesTo(MediaType mediaType) =>
        MediaTypes.Count == 0
        || MediaTypes.Any(name => string.Equals(name, mediaType.ToString(), StringComparison.OrdinalIgnoreCase));
}

public sealed class DerivedClaimConfiguration
{
    [JsonPropertyName("claim_key")]
    public string ClaimKey { get; set; } = string.Empty;

    /// <summary>Sources, first non-blank wins: <c>claim:{key}</c>, <c>request:{field}</c>, <c>hint:{key}</c>.</summary>
    [JsonPropertyName("from")]
    public List<string> From { get; set; } = [];

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    /// <summary>Defaults to <c>if_missing_value</c>.</summary>
    [JsonPropertyName("add")]
    public string Add { get; set; } = ClaimAddModes.IfMissingValue;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

/// <summary>Accepted values of a search strategy's <c>role</c>.</summary>
public static class StrategyRoles
{
    /// <summary>The strategy returns a container of items (a run of a series), not one item.</summary>
    public const string Container = "container";

    /// <summary>The strategy returns single items.</summary>
    public const string Item = "item";
}
