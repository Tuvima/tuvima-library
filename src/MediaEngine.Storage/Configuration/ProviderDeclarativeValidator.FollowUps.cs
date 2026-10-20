using System.Text.Json;
using System.Text.RegularExpressions;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Storage.Configuration;

/// <summary>
/// Load-time checks for the declarative parts of a provider file. Every unknown property and bad
/// shape is reported with its path, so a typo in a provider file fails when the file is loaded
/// instead of silently doing nothing.
/// </summary>
internal static partial class ProviderDeclarativeValidator
{
    private const int MaxLookupDepth = 3;

    private static readonly Regex Placeholder = new(@"\{([a-z_][a-z0-9_]*)\}", RegexOptions.Compiled);
    private static readonly Regex VariableName = new("^[a-z][a-z0-9_]*$", RegexOptions.Compiled);

    private static readonly string[] BuiltInUrlPlaceholders = ["base_url", "api_key", "locale"];

    /// <summary>Facts about the surrounding provider file that rules are checked against.</summary>
    private sealed record Scope(bool PagedRequestDeclared);

    public static void Validate(ProviderConfiguration provider, List<string> errors)
    {
        var scope = new Scope(provider.SequenceManifest?.Enabled == true);
        ValidateFollowUps(provider.FollowUps, scope, errors);
        ValidateResultRules(provider.ResultRules, errors);
        ValidateCandidateRanking(provider.CandidateRanking, errors);
        ValidateStrategyClaimRules(provider.SearchStrategies, errors);
        ValidateTextTrack(provider, errors);
        ValidateApiAndFlows(provider, errors);
    }

    // -- follow-ups ---------------------------------------------------------------------------

    private static void ValidateFollowUps(List<LookupFollowUpConfiguration>? followUps, Scope scope, List<string> errors)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var followUp in followUps ?? [])
        {
            if (!string.IsNullOrWhiteSpace(followUp.Name) && !names.Add(followUp.Name))
            {
                errors.Add("follow_ups[].name values must be unique.");
            }

            ValidateLookup(followUp, $"follow_ups['{followUp.Name}']", 1, scope, errors);
        }
    }

    private static void ValidateLookup(
        LookupFollowUpConfiguration lookup, string path, int depth, Scope scope, List<string> errors)
    {
        AddRequired(errors, lookup.Name, $"{path}.name");
        AddUnknown(lookup.UnmappedProperties, path, errors);
        if (depth > MaxLookupDepth)
        {
            errors.Add($"{path} is nested more than {MaxLookupDepth} lookups deep.");
            return;
        }

        foreach (var mediaType in lookup.MediaTypes.Where(name => !Enum.TryParse<MediaType>(name, true, out _)))
        {
            errors.Add($"{path}.media_types contains unknown media type '{mediaType}'.");
        }

        var variables = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (declared, sources) in lookup.Inputs)
        {
            var name = declared.EndsWith('?') ? declared[..^1] : declared;
            if (!VariableName.IsMatch(name))
            {
                errors.Add($"{path}.inputs['{declared}'] must be a lower-case name, optionally ending in '?'.");
            }

            variables.Add(name);
            if (sources.Count == 0)
            {
                errors.Add($"{path}.inputs['{declared}'] must list at least one source.");
            }

            foreach (var source in sources)
            {
                var kind = source.Split(':')[0];
                if (!LookupSourceKinds.All.Contains(kind) || !source.Contains(':') || source.EndsWith(':'))
                {
                    errors.Add($"{path}.inputs['{declared}'] source '{source}' must look like kind:name with kind one of {string.Join(", ", LookupSourceKinds.All)}.");
                }
            }
        }

        AddRequired(errors, lookup.Url, $"{path}.url");
        foreach (Match match in Placeholder.Matches(lookup.Url))
        {
            var name = match.Groups[1].Value;
            if (!BuiltInUrlPlaceholders.Contains(name) && !variables.Contains(name))
            {
                errors.Add($"{path}.url uses {{{name}}}, which is neither built in nor declared in inputs.");
            }
        }

        if (!Allowed(lookup.OnError, FollowUpErrorPolicies.LogAndContinue, FollowUpErrorPolicies.Propagate))
        {
            errors.Add($"{path}.on_error must be log_and_continue or propagate.");
        }

        ValidateAddMode(lookup.Add, $"{path}.add", errors);
        if (lookup.RequiredPaths.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add($"{path}.required_paths must not contain blank paths.");
        }

        ValidateFollowUpCache(lookup.Cache, path, variables, errors);
        if (lookup.Rules.Count == 0)
        {
            errors.Add($"{path}.rules must contain at least one rule.");
        }

        for (var index = 0; index < lookup.Rules.Count; index++)
        {
            ValidateRule(lookup.Rules[index], $"{path}.rules[{index}]", variables, depth, scope, errors);
        }
    }

    private static void ValidateFollowUpCache(
        FollowUpCacheConfiguration? cache, string path, HashSet<string> variables, List<string> errors)
    {
        if (cache is null)
        {
            return;
        }

        AddUnknown(cache.UnmappedProperties, $"{path}.cache", errors);
        if (!Allowed(cache.Store, FollowUpCacheStores.ResponseCache, FollowUpCacheStores.Memory))
        {
            errors.Add($"{path}.cache.store must be response_cache or memory.");
        }

        if (cache.Key is not null && cache.Store != FollowUpCacheStores.Memory)
        {
            errors.Add($"{path}.cache.key is only valid for the memory store.");
        }

        foreach (Match match in Placeholder.Matches(cache.Key ?? string.Empty))
        {
            if (!variables.Contains(match.Groups[1].Value))
            {
                errors.Add($"{path}.cache.key uses {{{match.Groups[1].Value}}}, which is not declared in inputs.");
            }
        }
    }

    private static void ValidateAddMode(string? mode, string path, List<string> errors)
    {
        if (mode is not null
            && !Allowed(mode, ClaimAddModes.Always, ClaimAddModes.IfMissingKey, ClaimAddModes.IfMissingValue))
        {
            errors.Add($"{path} must be always, if_missing_key or if_missing_value.");
        }
    }

    // -- rules --------------------------------------------------------------------------------

    private static readonly Dictionary<string, string[]> RuleProperties = new(StringComparer.Ordinal)
    {
        [ClaimRuleKinds.Field] =
        [
            "claim_key", "json_path", "json_paths", "confidence", "transform", "transform_args", "prefix",
            "trim_leading_slash", "template", "join", "add", "value", "requires_claim", "trim", "min_length",
        ],
        [ClaimRuleKinds.PriorityPick] = ["claim_key", "confidence", "add", "array", "key_path", "order", "value_path"],
        [ClaimRuleKinds.Items] =
        [
            "add", "path", "where_not_blank", "where", "sort", "take", "distinct_by", "role", "each", "first", "joined",
        ],
        [ClaimRuleKinds.Lookup] = ["lookup"],
        [ClaimRuleKinds.Sequence] =
        [
            "add", "items_path", "item", "order_by", "current_id", "emit", "manifest",
        ],
        [ClaimRuleKinds.PagedSequence] =
        [
            "add", "items_path", "item", "order_by", "manifest", "container", "total_path", "expected_total_path",
            "dedupe_by_id", "date_as", "cache",
        ],
    };

    private static void ValidateRule(
        ClaimRuleConfiguration rule, string path, HashSet<string> variables, int depth, Scope scope, List<string> errors,
        bool insideItems = false)
    {
        AddUnknown(rule.UnmappedProperties, path, errors);
        if (!RuleProperties.TryGetValue(rule.Kind, out var allowed))
        {
            errors.Add($"{path}.kind '{rule.Kind}' must be one of {string.Join(", ", RuleProperties.Keys)}.");
            return;
        }

        foreach (var used in UsedRuleProperties(rule).Where(name => !allowed.Contains(name)))
        {
            errors.Add($"{path}.{used} is not valid for a {rule.Kind} rule.");
        }

        ValidateAddMode(rule.Add, $"{path}.add", errors);
        switch (rule.Kind)
        {
            case ClaimRuleKinds.Field:
                ValidateFieldRule(rule, path, insideItems, errors);
                break;
            case ClaimRuleKinds.PriorityPick:
                AddRequired(errors, rule.ClaimKey, $"{path}.claim_key");
                AddRequired(errors, rule.Array, $"{path}.array");
                AddRequired(errors, rule.KeyPath, $"{path}.key_path");
                AddRequired(errors, rule.ValuePath, $"{path}.value_path");
                AddConfidence(rule.Confidence, $"{path}.confidence", errors);
                if (rule.Order is not { Count: > 0 })
                {
                    errors.Add($"{path}.order must list at least one value.");
                }

                break;
            case ClaimRuleKinds.Items:
                ValidateItemsRule(rule, path, variables, depth, errors);
                break;
            case ClaimRuleKinds.Lookup:
                if (rule.Lookup is null)
                {
                    errors.Add($"{path}.lookup is required.");
                }
                else
                {
                    ValidateLookup(rule.Lookup, $"{path}.lookup['{rule.Lookup.Name}']", depth + 1, scope, errors);
                }

                break;
            case ClaimRuleKinds.Sequence:
                ValidateSequenceRule(rule, path, variables, errors);
                break;
            case ClaimRuleKinds.PagedSequence:
                ValidatePagedSequenceRule(rule, path, variables, scope, errors);
                break;
        }
    }

    private static IEnumerable<string> UsedRuleProperties(ClaimRuleConfiguration rule)
    {
        (string Name, bool Used)[] properties =
        [
            ("trim", rule.Trim),
            ("min_length", rule.MinLength != 0),
            ("where", rule.Where is not null),
            ("value", rule.Value is not null),
            ("requires_claim", rule.RequiresClaim is not null),
            ("container", rule.Container is not null),
            ("total_path", rule.TotalPath is not null),
            ("expected_total_path", rule.ExpectedTotalPath is not null),
            ("dedupe_by_id", rule.DedupeById),
            ("date_as", rule.DateAs != SequenceDateModes.Parsed),
            ("cache", rule.Cache is not null),
            ("claim_key", rule.ClaimKey is not null),
            ("json_path", rule.JsonPath is not null),
            ("json_paths", rule.JsonPaths is not null),
            ("confidence", rule.Confidence != 0),
            ("transform", rule.Transform is not null),
            ("transform_args", rule.TransformArgs is not null),
            ("prefix", rule.Prefix is not null),
            ("trim_leading_slash", rule.TrimLeadingSlash),
            ("template", rule.Template is not null),
            ("join", rule.Join is not null),
            ("add", rule.Add is not null),
            ("array", rule.Array is not null),
            ("key_path", rule.KeyPath is not null),
            ("order", rule.Order is not null),
            ("value_path", rule.ValuePath is not null),
            ("path", rule.Path is not null),
            ("where_not_blank", rule.WhereNotBlank is not null),
            ("sort", rule.Sort is not null),
            ("take", rule.Take != 0),
            ("distinct_by", rule.DistinctBy is not null),
            ("role", rule.Role is not null),
            ("each", rule.Each is not null),
            ("first", rule.First is not null),
            ("joined", rule.Joined is not null),
            ("lookup", rule.Lookup is not null),
            ("items_path", rule.ItemsPath is not null),
            ("item", rule.Item is not null),
            ("order_by", rule.OrderBy is not null),
            ("current_id", rule.CurrentId is not null),
            ("emit", rule.Emit is not null),
            ("manifest", rule.Manifest is not null),
        ];
        return properties.Where(property => property.Used).Select(property => property.Name);
    }

    private static void ValidateFieldRule(ClaimRuleConfiguration rule, string path, bool insideItems, List<string> errors)
    {
        AddRequired(errors, rule.ClaimKey, $"{path}.claim_key");
        AddConfidence(rule.Confidence, $"{path}.confidence", errors);
        var sources = new[]
            {
                rule.JsonPath is not null, rule.JsonPaths is not null, rule.Template is not null, rule.Value is not null,
            }
            .Count(used => used);
        if (sources > 1)
        {
            errors.Add($"{path} may use only one of json_path, json_paths, template and value.");
        }

        if (sources == 0 && rule.Join is null)
        {
            errors.Add($"{path} needs json_path, json_paths, template, value or join.");
        }

        if (rule.JsonPaths is { Count: 0 })
        {
            errors.Add($"{path}.json_paths must not be empty.");
        }

        if (rule.MinLength < 0)
        {
            errors.Add($"{path}.min_length must not be negative.");
        }

        if (rule.ClaimKey?.Contains("{role}", StringComparison.Ordinal) == true && !insideItems)
        {
            errors.Add($"{path}.claim_key uses {{role}} outside an items rule with a role.");
        }

        if (rule.Join is { } join)
        {
            AddUnknown(join.UnmappedProperties, $"{path}.join", errors);
            AddRequired(errors, join.Array, $"{path}.join.array");
            AddRequired(errors, join.ItemPath, $"{path}.join.item_path");
            if (join.Take < 0)
            {
                errors.Add($"{path}.join.take must not be negative.");
            }

            if (join.Sort is not null)
            {
                ValidateSort(join.Sort, $"{path}.join.sort", errors);
            }
        }
    }

    private static void ValidateItemsRule(
        ClaimRuleConfiguration rule, string path, HashSet<string> variables, int depth, List<string> errors)
    {
        AddRequired(errors, rule.Path, $"{path}.path");
        foreach (var condition in rule.Where ?? [])
        {
            AddUnknown(condition.UnmappedProperties, $"{path}.where[]", errors);
            AddRequired(errors, condition.Path, $"{path}.where[].path");
            if (!condition.NotBlank && condition.EqualsValue is null)
            {
                errors.Add($"{path}.where[] needs not_blank or equals.");
            }

            if (condition.IgnoreCase && condition.EqualsValue is null)
            {
                errors.Add($"{path}.where[].ignore_case is only valid with equals.");
            }
        }

        if (rule.Take < 0)
        {
            errors.Add($"{path}.take must not be negative.");
        }

        for (var index = 0; index < (rule.Sort?.Count ?? 0); index++)
        {
            ValidateSort(rule.Sort![index], $"{path}.sort[{index}]", errors);
        }

        if (rule.Role is { } role)
        {
            AddUnknown(role.UnmappedProperties, $"{path}.role", errors);
            if (role.Paths.Count == 0)
            {
                errors.Add($"{path}.role.paths must list at least one path.");
            }

            if (role.Map.Count == 0)
            {
                errors.Add($"{path}.role.map must list at least one entry.");
            }

            foreach (var entry in role.Map)
            {
                AddUnknown(entry.UnmappedProperties, $"{path}.role.map[]", errors);
                AddRequired(errors, entry.ClaimKey, $"{path}.role.map[].claim_key");
                if (entry.Contains.Count == 0)
                {
                    errors.Add($"{path}.role.map[].contains must list at least one word.");
                }
            }
        }

        var children = (rule.Each?.Count ?? 0) + (rule.First?.Count ?? 0) + (rule.Joined?.Count ?? 0);
        if (children == 0)
        {
            errors.Add($"{path} needs at least one of each, first and joined.");
        }

        foreach (var (name, list) in new[] { ("each", rule.Each), ("first", rule.First) })
        {
            for (var index = 0; index < (list?.Count ?? 0); index++)
            {
                var child = list![index];
                if (child.Kind != ClaimRuleKinds.Field)
                {
                    errors.Add($"{path}.{name}[{index}] must be a field rule.");
                    continue;
                }

                ValidateRule(child, $"{path}.{name}[{index}]", variables, depth, new Scope(false), errors, insideItems: rule.Role is not null);
            }
        }

        foreach (var joined in rule.Joined ?? [])
        {
            AddUnknown(joined.UnmappedProperties, $"{path}.joined[]", errors);
            AddRequired(errors, joined.ClaimKey, $"{path}.joined[].claim_key");
            AddRequired(errors, joined.ItemPath, $"{path}.joined[].item_path");
            AddConfidence(joined.Confidence, $"{path}.joined[].confidence", errors);
            ValidateAddMode(joined.Add, $"{path}.joined[].add", errors);
        }
    }

    private static void ValidateSort(ClaimSortConfiguration sort, string path, List<string> errors)
    {
        AddUnknown(sort.UnmappedProperties, path, errors);
        AddRequired(errors, sort.Path, $"{path}.path");
        if (!Allowed(sort.Type, ClaimSortTypes.Number, ClaimSortTypes.Text))
        {
            errors.Add($"{path}.type must be number or text.");
        }

        if (!Allowed(sort.Order, ClaimSortOrders.Ascending, ClaimSortOrders.AscendingThenReverse))
        {
            errors.Add($"{path}.order must be asc or asc_then_reverse.");
        }

        if (sort.NullAs is not null && sort.Type != ClaimSortTypes.Number)
        {
            errors.Add($"{path}.null_as is only valid for number sorts.");
        }

        if (sort.IgnoreCase && sort.Type != ClaimSortTypes.Text)
        {
            errors.Add($"{path}.ci is only valid for text sorts.");
        }
    }

    private static void ValidateSequenceRule(
        ClaimRuleConfiguration rule, string path, HashSet<string> variables, List<string> errors)
    {
        AddRequired(errors, rule.ItemsPath, $"{path}.items_path");
        if (rule.Item is null)
        {
            errors.Add($"{path}.item is required.");
        }
        else
        {
            AddUnknown(rule.Item.UnmappedProperties, $"{path}.item", errors);
            AddRequired(errors, rule.Item.IdPath, $"{path}.item.id_path");
            if (rule.Item.TitlePaths.Count == 0)
            {
                errors.Add($"{path}.item.title_paths must list at least one path.");
            }
        }

        foreach (var key in rule.OrderBy ?? [])
        {
            if (!Allowed(key, SequenceOrderKeys.Date, SequenceOrderKeys.Title, SequenceOrderKeys.OrdinalNumber, SequenceOrderKeys.OrdinalText))
            {
                errors.Add($"{path}.order_by contains unknown key '{key}'.");
            }
        }

        var needsPosition = false;
        foreach (var emit in rule.Emit ?? [])
        {
            AddUnknown(emit.UnmappedProperties, $"{path}.emit[]", errors);
            AddRequired(errors, emit.ClaimKey, $"{path}.emit[].claim_key");
            AddConfidence(emit.Confidence, $"{path}.emit[].confidence", errors);
            if (emit.From is not null && !Allowed(emit.From, SequenceEmitSources.Position, SequenceEmitSources.Count))
            {
                errors.Add($"{path}.emit[].from must be position or count.");
            }

            if ((emit.From is null) == (emit.Value is null))
            {
                errors.Add($"{path}.emit[] needs exactly one of from and value.");
            }

            needsPosition |= emit.From == SequenceEmitSources.Position;
        }

        if (needsPosition && string.IsNullOrWhiteSpace(rule.CurrentId))
        {
            errors.Add($"{path}.current_id is required when an emit entry reads the position.");
        }

        if (rule.Manifest is { } manifest)
        {
            AddUnknown(manifest.UnmappedProperties, $"{path}.manifest", errors);
            AddRequired(errors, manifest.ClaimKey, $"{path}.manifest.claim_key");
            AddRequired(errors, manifest.ContainerId, $"{path}.manifest.container_id");
            AddRequired(errors, manifest.ExternalIdKey, $"{path}.manifest.external_id_key");
            AddConfidence(manifest.Confidence, $"{path}.manifest.confidence", errors);
        }

        if ((rule.Emit?.Count ?? 0) == 0 && rule.Manifest is null)
        {
            errors.Add($"{path} needs emit entries or a manifest.");
        }

        foreach (var template in new[] { rule.CurrentId, rule.Manifest?.ContainerId, rule.Manifest?.ContainerLabel })
        {
            foreach (Match match in Placeholder.Matches(template ?? string.Empty))
            {
                if (!variables.Contains(match.Groups[1].Value))
                {
                    errors.Add($"{path} uses {{{match.Groups[1].Value}}}, which is not declared in the lookup's inputs.");
                }
            }
        }
    }

    private static void ValidatePagedSequenceRule(
        ClaimRuleConfiguration rule, string path, HashSet<string> variables, Scope scope, List<string> errors)
    {
        if (!scope.PagedRequestDeclared)
        {
            errors.Add($"{path} needs an enabled sequence_manifest request on the provider.");
        }

        AddRequired(errors, rule.ItemsPath, $"{path}.items_path");
        AddRequired(errors, rule.Container, $"{path}.container");
        if (!Allowed(rule.DateAs, SequenceDateModes.Parsed, SequenceDateModes.Text))
        {
            errors.Add($"{path}.date_as must be parsed or text.");
        }

        if (rule.Item is null)
        {
            errors.Add($"{path}.item is required.");
        }
        else
        {
            AddUnknown(rule.Item.UnmappedProperties, $"{path}.item", errors);
            AddRequired(errors, rule.Item.IdPath, $"{path}.item.id_path");
            AddRequired(errors, rule.Item.OrdinalPath, $"{path}.item.ordinal_path");
        }

        foreach (var key in rule.OrderBy ?? [])
        {
            if (!Allowed(key, SequenceOrderKeys.Title, SequenceOrderKeys.Date, SequenceOrderKeys.OrdinalNumber, SequenceOrderKeys.OrdinalText))
            {
                errors.Add($"{path}.order_by contains unknown key '{key}'.");
            }
        }

        if (rule.Manifest is not { } manifest)
        {
            errors.Add($"{path}.manifest is required.");
        }
        else
        {
            AddUnknown(manifest.UnmappedProperties, $"{path}.manifest", errors);
            AddRequired(errors, manifest.ClaimKey, $"{path}.manifest.claim_key");
            AddRequired(errors, manifest.ContainerId, $"{path}.manifest.container_id");
            AddRequired(errors, manifest.ExternalIdKey, $"{path}.manifest.external_id_key");
            AddConfidence(manifest.Confidence, $"{path}.manifest.confidence", errors);
            if (manifest.ConfidenceWhenNotAuthoritative is { } lower)
            {
                AddConfidence(lower, $"{path}.manifest.confidence_when_not_authoritative", errors);
            }
        }

        if (rule.Cache is not null)
        {
            AddUnknown(rule.Cache.UnmappedProperties, $"{path}.cache", errors);
            if (rule.Cache.Store != FollowUpCacheStores.Memory)
            {
                errors.Add($"{path}.cache.store must be memory.");
            }
        }

        foreach (var template in new[] { rule.Container, rule.Manifest?.ContainerId, rule.Manifest?.ContainerLabel }
            .Concat(rule.Item?.TitleFallbacks ?? []))
        {
            foreach (Match match in Placeholder.Matches(template ?? string.Empty))
            {
                var name = match.Groups[1].Value;
                if (name != "ordinal" && !variables.Contains(name))
                {
                    errors.Add($"{path} uses {{{name}}}, which is not declared in the lookup's inputs.");
                }
            }
        }
    }

    private static void ValidateResultRules(List<ResultClaimRulesConfiguration>? groups, List<string> errors)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups ?? [])
        {
            var path = $"result_rules['{group.Name}']";
            AddRequired(errors, group.Name, $"{path}.name");
            AddUnknown(group.UnmappedProperties, path, errors);
            if (!string.IsNullOrWhiteSpace(group.Name) && !names.Add(group.Name))
            {
                errors.Add("result_rules[].name values must be unique.");
            }

            ValidateMediaTypes(group.MediaTypes, $"{path}.media_types", errors);
            ValidateAddMode(group.Add, $"{path}.add", errors);
            if (group.Rules.Count == 0)
            {
                errors.Add($"{path}.rules must contain at least one rule.");
            }

            for (var index = 0; index < group.Rules.Count; index++)
            {
                if (group.Rules[index].Kind is ClaimRuleKinds.Lookup or ClaimRuleKinds.PagedSequence)
                {
                    errors.Add($"{path}.rules[{index}] cannot be a {group.Rules[index].Kind} rule; results are not fetched.");
                    continue;
                }

                ValidateRule(
                    group.Rules[index], $"{path}.rules[{index}]", new HashSet<string>(StringComparer.Ordinal), 1,
                    new Scope(false), errors);
            }
        }
    }

    private static void ValidateMediaTypes(List<string> mediaTypes, string path, List<string> errors)
    {
        foreach (var mediaType in mediaTypes.Where(name => !Enum.TryParse<MediaType>(name, true, out _)))
        {
            errors.Add($"{path} contains unknown media type '{mediaType}'.");
        }
    }

    // -- shared helpers -----------------------------------------------------------------------

    private static void AddUnknown(Dictionary<string, JsonElement>? unmapped, string path, List<string> errors)
    {
        foreach (var name in unmapped?.Keys ?? Enumerable.Empty<string>())
        {
            errors.Add($"{path}.{name} is not a known property.");
        }
    }

    private static void AddRequired(List<string> errors, string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{field} is required.");
        }
    }

    private static void AddConfidence(double confidence, string field, List<string> errors)
    {
        if (confidence is <= 0 or > 1)
        {
            errors.Add($"{field} must be greater than 0 and at most 1.");
        }
    }

    private static bool Allowed(string value, params string[] allowed) =>
        allowed.Contains(value, StringComparer.OrdinalIgnoreCase);
}
