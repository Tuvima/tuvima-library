using System.Text.Json;
using System.Text.Json.Serialization;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// A follow-up lookup a provider makes after one of its strategies matched a result: one more
/// request whose URL is built from fields of the match, whose response is turned into extra
/// claims by <see cref="Rules"/>. Declared in <c>follow_ups</c> of a provider file.
/// </summary>
public sealed class LookupFollowUpConfiguration
{
    /// <summary>Name used in logs and in validation messages. Unique among sibling follow-ups.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Media types (<c>Movies</c>, <c>TV</c>, <c>Comics</c> …) the follow-up applies to.
    /// Empty means every media type.
    /// </summary>
    [JsonPropertyName("media_types")]
    public List<string> MediaTypes { get; set; } = [];

    /// <summary>
    /// Values the URL needs, by variable name. Each variable lists sources in order; the first
    /// non-blank one wins. Sources: <c>claim:{key}</c> (a claim already produced),
    /// <c>result:{path}</c> (the matched result), <c>parent:{path}</c> (the response of the
    /// enclosing lookup, or the matched result at the top level), <c>request:{field}</c>,
    /// <c>hint:{key}</c>. A variable named with a trailing <c>?</c> is optional; the follow-up is
    /// skipped silently when any required variable is blank.
    /// </summary>
    [JsonPropertyName("inputs")]
    public Dictionary<string, List<string>> Inputs { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Request URL template. Placeholders: <c>{base_url}</c>, <c>{api_key}</c>, <c>{locale}</c>
    /// (<c>language-COUNTRY</c> of the request) and the declared input variables. Values are
    /// URI-escaped. A URL that uses <c>{api_key}</c> is skipped when no key is configured.
    /// </summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>Optional response cache. Absent means every call goes to the provider.</summary>
    [JsonPropertyName("cache")]
    public FollowUpCacheConfiguration? Cache { get; set; }

    /// <summary>
    /// What happens when the request or its rules throw: <c>log_and_continue</c> (default) keeps
    /// the claims gathered so far and carries on; <c>propagate</c> lets the failure reach the caller.
    /// </summary>
    [JsonPropertyName("on_error")]
    public string OnError { get; set; } = FollowUpErrorPolicies.LogAndContinue;

    /// <summary>
    /// How claims from <see cref="Rules"/> join the existing ones unless a rule says otherwise:
    /// <c>always</c>, <c>if_missing_key</c> or <c>if_missing_value</c> (default).
    /// </summary>
    [JsonPropertyName("add")]
    public string Add { get; set; } = ClaimAddModes.IfMissingValue;

    /// <summary>Ordered rules applied to the response.</summary>
    [JsonPropertyName("rules")]
    public List<ClaimRuleConfiguration> Rules { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }

    /// <summary>True when the follow-up runs for the media type.</summary>
    public bool AppliesTo(MediaType mediaType) =>
        MediaTypes.Count == 0
        || MediaTypes.Any(name => string.Equals(name, mediaType.ToString(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Source prefixes accepted in <see cref="LookupFollowUpConfiguration.Inputs"/>.</summary>
public static class LookupSourceKinds
{
    public const string Claim = "claim";
    public const string Result = "result";
    public const string Parent = "parent";
    public const string Request = "request";
    public const string Hint = "hint";

    public static readonly IReadOnlyList<string> All = [Claim, Result, Parent, Request, Hint];
}

/// <summary>Sort keys accepted in <see cref="ClaimRuleConfiguration.OrderBy"/>.</summary>
public static class SequenceOrderKeys
{
    public const string Date = "date";
    public const string Title = "title";
}

/// <summary>Accepted values of <see cref="LookupFollowUpConfiguration.OnError"/>.</summary>
public static class FollowUpErrorPolicies
{
    public const string LogAndContinue = "log_and_continue";
    public const string Propagate = "propagate";
}

/// <summary>Accepted values of <c>add</c> on follow-ups and rules.</summary>
public static class ClaimAddModes
{
    public const string Always = "always";
    public const string IfMissingKey = "if_missing_key";
    public const string IfMissingValue = "if_missing_value";
}

/// <summary>Accepted values of <see cref="ClaimRuleConfiguration.Kind"/>.</summary>
public static class ClaimRuleKinds
{
    public const string Field = "field";
    public const string PriorityPick = "priority_pick";
    public const string Items = "items";
    public const string Lookup = "lookup";
    public const string Sequence = "sequence";
}

public sealed class FollowUpCacheConfiguration
{
    /// <summary>
    /// <c>response_cache</c> keeps the response in the shared provider response cache for the
    /// provider's <c>cache_ttl_hours</c> (168 when unset); <c>memory</c> keeps one answer per
    /// <see cref="Key"/> for the lifetime of the running provider.
    /// </summary>
    [JsonPropertyName("store")]
    public string Store { get; set; } = FollowUpCacheStores.ResponseCache;

    /// <summary>Key template for the <c>memory</c> store; may use the input variables.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public static class FollowUpCacheStores
{
    public const string ResponseCache = "response_cache";
    public const string Memory = "memory";
}

/// <summary>
/// One rule applied to a JSON node. The <see cref="Kind"/> decides which of the other
/// properties are used; the validator rejects properties that do not belong to the kind.
/// </summary>
public sealed class ClaimRuleConfiguration
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = ClaimRuleKinds.Field;

    // -- field / priority_pick ------------------------------------------------------------

    [JsonPropertyName("claim_key")]
    public string? ClaimKey { get; set; }

    /// <summary>Path to a single value.</summary>
    [JsonPropertyName("json_path")]
    public string? JsonPath { get; set; }

    /// <summary>Alternative paths; the first non-blank value wins.</summary>
    [JsonPropertyName("json_paths")]
    public List<string>? JsonPaths { get; set; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("transform")]
    public string? Transform { get; set; }

    [JsonPropertyName("transform_args")]
    public string? TransformArgs { get; set; }

    /// <summary>Text put in front of the value (for example an image base URL).</summary>
    [JsonPropertyName("prefix")]
    public string? Prefix { get; set; }

    /// <summary>Removes leading slashes from the value before the prefix is applied.</summary>
    [JsonPropertyName("trim_leading_slash")]
    public bool TrimLeadingSlash { get; set; }

    /// <summary>
    /// Builds the value from several fields, for example <c>{id}::{name}</c>. Emits nothing unless
    /// every referenced field is present.
    /// </summary>
    [JsonPropertyName("template")]
    public string? Template { get; set; }

    /// <summary>Joined list used instead of <c>json_path</c> when it produces anything.</summary>
    [JsonPropertyName("join")]
    public ClaimJoinConfiguration? Join { get; set; }

    /// <summary>Overrides the follow-up's <c>add</c> mode for this rule.</summary>
    [JsonPropertyName("add")]
    public string? Add { get; set; }

    // -- priority_pick ----------------------------------------------------------------------

    /// <summary>Path to an array of objects.</summary>
    [JsonPropertyName("array")]
    public string? Array { get; set; }

    /// <summary>Path, on each array element, of the value matched against <see cref="Order"/>.</summary>
    [JsonPropertyName("key_path")]
    public string? KeyPath { get; set; }

    /// <summary>Preferred key values, best first (compared ignoring case).</summary>
    [JsonPropertyName("order")]
    public List<string>? Order { get; set; }

    /// <summary>Path, on the chosen element, of the value to emit; the first non-blank element if it yields a list.</summary>
    [JsonPropertyName("value_path")]
    public string? ValuePath { get; set; }

    // -- items ------------------------------------------------------------------------------

    /// <summary>Path to an array of objects.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    /// <summary>Keeps only items whose field at this path is not blank.</summary>
    [JsonPropertyName("where_not_blank")]
    public string? WhereNotBlank { get; set; }

    [JsonPropertyName("sort")]
    public List<ClaimSortConfiguration>? Sort { get; set; }

    /// <summary>Keeps only the first N items (after filtering and sorting).</summary>
    [JsonPropertyName("take")]
    public int Take { get; set; }

    /// <summary>
    /// Decides a claim key for each item from a job or role text. Items without a match are
    /// skipped. Rules in <see cref="Each"/> may use <c>{role}</c> inside <c>claim_key</c>.
    /// </summary>
    [JsonPropertyName("role")]
    public ClaimRoleConfiguration? Role { get; set; }

    /// <summary>Rules evaluated on every selected item.</summary>
    [JsonPropertyName("each")]
    public List<ClaimRuleConfiguration>? Each { get; set; }

    /// <summary>Rules evaluated on the first selected item only.</summary>
    [JsonPropertyName("first")]
    public List<ClaimRuleConfiguration>? First { get; set; }

    /// <summary>One claim joining a field of every selected item.</summary>
    [JsonPropertyName("joined")]
    public List<ClaimJoinedConfiguration>? Joined { get; set; }

    // -- lookup -----------------------------------------------------------------------------

    /// <summary>A nested follow-up whose inputs may read the current node as <c>parent:</c>.</summary>
    [JsonPropertyName("lookup")]
    public LookupFollowUpConfiguration? Lookup { get; set; }

    // -- sequence ---------------------------------------------------------------------------

    [JsonPropertyName("items_path")]
    public string? ItemsPath { get; set; }

    [JsonPropertyName("item")]
    public SequenceItemConfiguration? Item { get; set; }

    /// <summary>Sort keys, in order: <c>date</c>, <c>title</c>.</summary>
    [JsonPropertyName("order_by")]
    public List<string>? OrderBy { get; set; }

    /// <summary>Id of the current item (a template over input variables); needed for position claims.</summary>
    [JsonPropertyName("current_id")]
    public string? CurrentId { get; set; }

    [JsonPropertyName("emit")]
    public List<SequenceEmitConfiguration>? Emit { get; set; }

    [JsonPropertyName("manifest")]
    public SequenceManifestRuleConfiguration? Manifest { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public sealed class ClaimJoinConfiguration
{
    /// <summary>Path to an array of objects.</summary>
    [JsonPropertyName("array")]
    public string Array { get; set; } = string.Empty;

    /// <summary>Path of the text on each element.</summary>
    [JsonPropertyName("item_path")]
    public string ItemPath { get; set; } = string.Empty;

    [JsonPropertyName("sort")]
    public ClaimSortConfiguration? Sort { get; set; }

    [JsonPropertyName("distinct")]
    public bool Distinct { get; set; }

    [JsonPropertyName("take")]
    public int Take { get; set; }

    [JsonPropertyName("separator")]
    public string Separator { get; set; } = ", ";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public sealed class ClaimJoinedConfiguration
{
    [JsonPropertyName("claim_key")]
    public string ClaimKey { get; set; } = string.Empty;

    [JsonPropertyName("item_path")]
    public string ItemPath { get; set; } = string.Empty;

    [JsonPropertyName("separator")]
    public string Separator { get; set; } = ", ";

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("add")]
    public string? Add { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

/// <summary>Sort key for <c>items</c>, <c>join</c> and similar rules. LINQ ordering is stable.</summary>
public sealed class ClaimSortConfiguration
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary><c>number</c> or <c>text</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = ClaimSortTypes.Text;

    /// <summary><c>asc</c>, or <c>asc_then_reverse</c>: sort ascending, then reverse the whole list.</summary>
    [JsonPropertyName("order")]
    public string Order { get; set; } = ClaimSortOrders.Ascending;

    /// <summary>Number sorted for a missing value. Missing numbers sort last when unset.</summary>
    [JsonPropertyName("null_as")]
    public double? NullAs { get; set; }

    /// <summary>Compare text ignoring case.</summary>
    [JsonPropertyName("ci")]
    public bool IgnoreCase { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public static class ClaimSortTypes
{
    public const string Number = "number";
    public const string Text = "text";
}

public static class ClaimSortOrders
{
    public const string Ascending = "asc";
    public const string AscendingThenReverse = "asc_then_reverse";
}

public sealed class ClaimRoleConfiguration
{
    /// <summary>Paths tried in order for the role text; the first non-blank value wins.</summary>
    [JsonPropertyName("paths")]
    public List<string> Paths { get; set; } = [];

    /// <summary>Ordered entries; the first whose words appear in the text decides the claim key.</summary>
    [JsonPropertyName("map")]
    public List<ClaimRoleMapEntry> Map { get; set; } = [];

    /// <summary>Lower-case the text, strip accents and treat punctuation as spaces before matching.</summary>
    [JsonPropertyName("normalize")]
    public bool Normalize { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public sealed class ClaimRoleMapEntry
{
    /// <summary>The entry matches when the role text contains any of these words (ignoring case).</summary>
    [JsonPropertyName("contains")]
    public List<string> Contains { get; set; } = [];

    [JsonPropertyName("claim_key")]
    public string ClaimKey { get; set; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public sealed class SequenceItemConfiguration
{
    [JsonPropertyName("id_path")]
    public string IdPath { get; set; } = string.Empty;

    [JsonPropertyName("title_paths")]
    public List<string> TitlePaths { get; set; } = [];

    [JsonPropertyName("date_path")]
    public string? DatePath { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public static class SequenceEmitSources
{
    public const string Position = "position";
    public const string Count = "count";
}

public sealed class SequenceEmitConfiguration
{
    [JsonPropertyName("claim_key")]
    public string ClaimKey { get; set; } = string.Empty;

    /// <summary><c>position</c> (1-based place of the current item) or <c>count</c> (number of items).</summary>
    [JsonPropertyName("from")]
    public string? From { get; set; }

    /// <summary>Fixed text, used when <c>from</c> is absent.</summary>
    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public sealed class SequenceManifestRuleConfiguration
{
    [JsonPropertyName("claim_key")]
    public string ClaimKey { get; set; } = string.Empty;

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    /// <summary>Template over input variables, for example <c>tmdb:collection:{collection_id}</c>.</summary>
    [JsonPropertyName("container_id")]
    public string ContainerId { get; set; } = string.Empty;

    [JsonPropertyName("container_label")]
    public string? ContainerLabel { get; set; }

    /// <summary>Claim key that identifies the members in the provider's own id space.</summary>
    [JsonPropertyName("external_id_key")]
    public string ExternalIdKey { get; set; } = string.Empty;

    [JsonPropertyName("authoritative")]
    public bool Authoritative { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}
