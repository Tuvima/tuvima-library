using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;

namespace MediaEngine.Providers.Adapters;

/// <summary>What a rule may read besides the JSON node it runs on.</summary>
internal sealed record ClaimRuleContext(
    string ProviderName,
    MediaType MediaType,
    IReadOnlyDictionary<string, string> Variables);

/// <summary>
/// Turns a JSON node into claims according to declared <see cref="ClaimRuleConfiguration"/> rules.
/// Pure and synchronous: <c>lookup</c> rules, which need the network, are driven by the adapter.
/// </summary>
internal static class ClaimRuleEngine
{
    private const string RoleToken = "{role}";

    public static void Apply(
        List<ProviderClaim> claims,
        JsonNode node,
        ClaimRuleConfiguration rule,
        string defaultAdd,
        ClaimRuleContext context,
        string? roleKey = null)
    {
        switch (rule.Kind)
        {
            case ClaimRuleKinds.Field:
                ApplyField(claims, node, rule, defaultAdd, roleKey);
                break;
            case ClaimRuleKinds.PriorityPick:
                ApplyPriorityPick(claims, node, rule, defaultAdd);
                break;
            case ClaimRuleKinds.Items:
                ApplyItems(claims, node, rule, defaultAdd, context);
                break;
            case ClaimRuleKinds.Sequence:
                SequenceRuleEvaluator.Apply(claims, node, rule, defaultAdd, context);
                break;
            default:
                throw new InvalidOperationException($"Claim rule kind '{rule.Kind}' cannot run without a lookup.");
        }
    }

    /// <summary>Adds a claim unless the add mode says the claims already hold it.</summary>
    public static void AddClaim(List<ProviderClaim> claims, string key, string? value, double confidence, string mode)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var exists = mode switch
        {
            ClaimAddModes.Always => false,
            ClaimAddModes.IfMissingKey => claims.Any(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)),
            _ => claims.Any(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase)),
        };
        if (!exists)
        {
            claims.Add(new ProviderClaim(key, value, confidence));
        }
    }

    /// <summary>First non-blank text among the paths; an array result yields its first non-blank element.</summary>
    internal static string? FirstText(JsonNode? node, IEnumerable<string?> paths)
    {
        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            var found = JsonPathEvaluator.Evaluate(node, path);
            var text = found is JsonArray array
                ? array.Select(JsonPathEvaluator.GetStringValue).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                : JsonPathEvaluator.GetStringValue(found);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static void ApplyField(
        List<ProviderClaim> claims, JsonNode node, ClaimRuleConfiguration rule, string defaultAdd, string? roleKey)
    {
        var key = rule.ClaimKey ?? string.Empty;
        if (key.Contains(RoleToken, StringComparison.Ordinal))
        {
            if (roleKey is null)
            {
                return;
            }

            key = key.Replace(RoleToken, roleKey, StringComparison.Ordinal);
        }

        AddClaim(claims, key, ResolveFieldValue(node, rule), rule.Confidence, rule.Add ?? defaultAdd);
    }

    private static string? ResolveFieldValue(JsonNode node, ClaimRuleConfiguration rule)
    {
        string? value;
        if (rule.Template is not null)
        {
            value = ExpandFromNode(rule.Template, node);
        }
        else
        {
            value = rule.Join is not null ? JoinValues(node, rule.Join) : null;
            IEnumerable<string?> paths = rule.JsonPaths is { Count: > 0 } ? rule.JsonPaths : new[] { rule.JsonPath };
            value ??= FirstText(node, paths);
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!string.IsNullOrEmpty(rule.Transform))
        {
            value = string.IsNullOrEmpty(rule.TransformArgs)
                ? ValueTransformCatalog.Apply(rule.Transform, value)
                : ValueTransformCatalog.Apply(rule.Transform, value, rule.TransformArgs);
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }
        }

        if (rule.TrimLeadingSlash)
        {
            value = value.TrimStart('/');
        }

        return rule.Prefix is null ? value : rule.Prefix + value;
    }

    private static string? ExpandFromNode(string template, JsonNode node)
    {
        var missing = false;
        var expanded = Regex.Replace(template, @"\{([^{}]+)\}", match =>
        {
            var text = FirstText(node, [match.Groups[1].Value]);
            missing |= string.IsNullOrWhiteSpace(text);
            return text ?? string.Empty;
        });
        return missing ? null : expanded;
    }

    private static string? JoinValues(JsonNode node, ClaimJoinConfiguration join)
    {
        if (JsonPathEvaluator.Evaluate(node, join.Array) is not JsonArray array)
        {
            return null;
        }

        IEnumerable<JsonNode> elements = array.Where(element => element is not null).Select(element => element!);
        if (join.Sort is not null)
        {
            elements = SortNodes(elements, [join.Sort]);
        }

        var values = elements
            .Select(element => JsonPathEvaluator.GetStringValue(JsonPathEvaluator.Evaluate(element, join.ItemPath)))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!);
        if (join.Distinct)
        {
            values = values.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        if (join.Take > 0)
        {
            values = values.Take(join.Take);
        }

        var list = values.ToList();
        return list.Count == 0 ? null : string.Join(join.Separator, list);
    }

    private static void ApplyPriorityPick(
        List<ProviderClaim> claims, JsonNode node, ClaimRuleConfiguration rule, string defaultAdd)
    {
        if (JsonPathEvaluator.Evaluate(node, rule.Array ?? string.Empty) is not JsonArray array)
        {
            return;
        }

        foreach (var wanted in rule.Order ?? [])
        {
            var element = array.FirstOrDefault(candidate => string.Equals(
                JsonPathEvaluator.GetStringValue(JsonPathEvaluator.Evaluate(candidate, rule.KeyPath ?? string.Empty)),
                wanted,
                StringComparison.OrdinalIgnoreCase));
            var value = FirstText(element, [rule.ValuePath]);
            if (!string.IsNullOrWhiteSpace(value))
            {
                AddClaim(claims, rule.ClaimKey ?? string.Empty, value, rule.Confidence, rule.Add ?? defaultAdd);
                return;
            }
        }
    }

    private static void ApplyItems(
        List<ProviderClaim> claims, JsonNode node, ClaimRuleConfiguration rule, string defaultAdd, ClaimRuleContext context)
    {
        if (JsonPathEvaluator.Evaluate(node, rule.Path ?? string.Empty) is not JsonArray array)
        {
            return;
        }

        IEnumerable<JsonNode> items = array.Where(item => item is not null).Select(item => item!);
        if (!string.IsNullOrEmpty(rule.WhereNotBlank))
        {
            items = items.Where(item => !string.IsNullOrWhiteSpace(
                JsonPathEvaluator.GetStringValue(JsonPathEvaluator.Evaluate(item, rule.WhereNotBlank))));
        }

        if (rule.Sort is { Count: > 0 })
        {
            items = SortNodes(items, rule.Sort);
        }

        if (rule.Take > 0)
        {
            items = items.Take(rule.Take);
        }

        var selected = items.ToList();
        if (selected.Count == 0)
        {
            return;
        }

        foreach (var child in rule.First ?? [])
        {
            Apply(claims, selected[0], child, rule.Add ?? defaultAdd, context);
        }

        foreach (var item in selected)
        {
            string? roleKey = null;
            if (rule.Role is not null)
            {
                roleKey = ResolveRole(item, rule.Role);
                if (roleKey is null)
                {
                    continue;
                }
            }

            foreach (var child in rule.Each ?? [])
            {
                Apply(claims, item, child, rule.Add ?? defaultAdd, context, roleKey);
            }
        }

        foreach (var joined in rule.Joined ?? [])
        {
            var values = selected
                .Select(item => JsonPathEvaluator.GetStringValue(JsonPathEvaluator.Evaluate(item, joined.ItemPath)))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
            if (values.Count > 0)
            {
                AddClaim(claims, joined.ClaimKey, string.Join(joined.Separator, values), joined.Confidence,
                    joined.Add ?? rule.Add ?? defaultAdd);
            }
        }
    }

    private static string? ResolveRole(JsonNode item, ClaimRoleConfiguration role)
    {
        var text = FirstText(item, role.Paths);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (role.Normalize)
        {
            text = NormalizeText(text);
        }

        foreach (var entry in role.Map)
        {
            if (entry.Contains.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase)))
            {
                return entry.ClaimKey;
            }
        }

        return null;
    }

    /// <summary>Lower-cases, strips accents, reads "&amp;" as "and" and turns other punctuation into single spaces.</summary>
    internal static string NormalizeText(string value)
    {
        var chars = RetailTextSimilarity.StripDiacritics(value)
            .Replace("&", " and ", StringComparison.Ordinal)
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : ' ')
            .ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static IEnumerable<JsonNode> SortNodes(IEnumerable<JsonNode> nodes, IReadOnlyList<ClaimSortConfiguration> sort)
    {
        IOrderedEnumerable<JsonNode>? ordered = null;
        foreach (var key in sort)
        {
            ordered = key.Type == ClaimSortTypes.Number
                ? ordered is null
                    ? nodes.OrderBy(node => NumberOf(node, key))
                    : ordered.ThenBy(node => NumberOf(node, key))
                : ordered is null
                    ? nodes.OrderBy(node => TextOf(node, key), Comparer(key))
                    : ordered.ThenBy(node => TextOf(node, key), Comparer(key));
        }

        IEnumerable<JsonNode> result = ordered ?? nodes;
        return sort.Any(key => key.Order == ClaimSortOrders.AscendingThenReverse) ? result.Reverse() : result;
    }

    private static StringComparer Comparer(ClaimSortConfiguration key) =>
        key.IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static double NumberOf(JsonNode node, ClaimSortConfiguration key) =>
        double.TryParse(
            JsonPathEvaluator.GetStringValue(JsonPathEvaluator.Evaluate(node, key.Path)),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var number)
            ? number
            : key.NullAs ?? double.MaxValue;

    private static string TextOf(JsonNode node, ClaimSortConfiguration key) =>
        JsonPathEvaluator.GetStringValue(JsonPathEvaluator.Evaluate(node, key.Path)) ?? string.Empty;

    /// <summary>Replaces <c>{name}</c> placeholders with variable values; unknown names become empty.</summary>
    internal static string ExpandVariables(string template, IReadOnlyDictionary<string, string> variables)
    {
        var builder = new StringBuilder(template);
        foreach (var (name, value) in variables)
        {
            builder.Replace("{" + name + "}", value);
        }

        return Regex.Replace(builder.ToString(), @"\{[a-z_]+\}", string.Empty);
    }
}
