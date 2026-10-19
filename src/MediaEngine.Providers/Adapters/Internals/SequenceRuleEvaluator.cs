using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Models;
using MediaEngine.Providers.Models;

namespace MediaEngine.Providers.Adapters;

/// <summary>One member of an ordered sequence, as read from a provider response.</summary>
internal sealed record SequenceMember(string Id, string Title, string? Ordinal, string? DateText, DateOnly? Date);

/// <summary>
/// The <c>sequence</c> and <c>paged_sequence</c> claim rules: read the ordered members of a
/// container (a film collection, a series run) from a response, find the current member's place
/// and emit position, count and a provider-neutral <see cref="ProviderSequenceManifest"/> claim.
/// </summary>
internal static class SequenceRuleEvaluator
{
    public static void Apply(
        List<ProviderClaim> claims,
        JsonNode node,
        ClaimRuleConfiguration rule,
        string defaultAdd,
        ClaimRuleContext context)
    {
        if (JsonPathEvaluator.Evaluate(node, rule.ItemsPath ?? string.Empty) is not JsonArray array
            || rule.Item is null)
        {
            return;
        }

        var members = array
            .Where(part => part is not null)
            .Select(part => ReadMember(part!, rule, context.Variables))
            .OfType<SequenceMember>();
        var ordered = Order(members, rule.OrderBy ?? []);
        if (ordered.Count == 0)
        {
            return;
        }

        int? position = null;
        if (!string.IsNullOrWhiteSpace(rule.CurrentId))
        {
            var current = ClaimRuleEngine.ExpandVariables(rule.CurrentId, context.Variables);
            var index = ordered.FindIndex(member => string.Equals(member.Id, current, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return;
            }

            position = index + 1;
        }

        foreach (var emit in rule.Emit ?? [])
        {
            var value = emit.From switch
            {
                SequenceEmitSources.Position => position?.ToString(CultureInfo.InvariantCulture),
                SequenceEmitSources.Count => ordered.Count.ToString(CultureInfo.InvariantCulture),
                _ => emit.Value,
            };
            ClaimRuleEngine.AddClaim(claims, emit.ClaimKey, value, emit.Confidence, defaultAdd);
        }

        if (rule.Manifest is { } manifestRule)
        {
            var manifest = BuildManifest(ordered, manifestRule, context, null, null, null, manifestRule.Authoritative);
            ClaimRuleEngine.AddClaim(
                claims, manifestRule.ClaimKey, JsonSerializer.Serialize(manifest), manifestRule.Confidence, defaultAdd);
        }
    }

    /// <summary>Adds the manifest claim for members that a paged request already collected.</summary>
    public static void ApplyPaged(
        List<ProviderClaim> claims,
        IReadOnlyList<SequenceMember> collected,
        int? total,
        bool completedAllPages,
        ClaimRuleConfiguration rule,
        string defaultAdd,
        ClaimRuleContext context,
        string? containerKind,
        string? expectedTotalKind)
    {
        if (rule.Manifest is not { } manifestRule)
        {
            return;
        }

        IEnumerable<SequenceMember> members = collected;
        if (rule.DedupeById)
        {
            members = members.GroupBy(member => member.Id, StringComparer.OrdinalIgnoreCase).Select(group => group.First());
        }

        var ordered = Order(members, rule.OrderBy ?? []);
        if (ordered.Count == 0)
        {
            return;
        }

        var authoritative = completedAllPages && total.HasValue && ordered.Count == total.Value;
        var manifest = BuildManifest(ordered, manifestRule, context, total, containerKind, expectedTotalKind, authoritative);
        var confidence = !authoritative && manifestRule.ConfidenceWhenNotAuthoritative is { } fallback
            ? fallback
            : manifestRule.Confidence;
        ClaimRuleEngine.AddClaim(claims, manifestRule.ClaimKey, JsonSerializer.Serialize(manifest), confidence, defaultAdd);
    }

    /// <summary>Reads one member; <c>null</c> when it has no id, or no ordinal where the rule expects one.</summary>
    public static SequenceMember? ReadMember(
        JsonNode part, ClaimRuleConfiguration rule, IReadOnlyDictionary<string, string> variables)
    {
        var item = rule.Item!;
        var id = ClaimRuleEngine.FirstText(part, [item.IdPath]);
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        string? ordinal = null;
        if (!string.IsNullOrEmpty(item.OrdinalPath))
        {
            ordinal = ClaimRuleEngine.FirstText(part, [item.OrdinalPath]);
            if (string.IsNullOrWhiteSpace(ordinal))
            {
                return null;
            }
        }

        var title = ClaimRuleEngine.FirstText(part, item.TitlePaths);
        if (string.IsNullOrWhiteSpace(title))
        {
            var values = new Dictionary<string, string>(variables, StringComparer.Ordinal);
            if (ordinal is not null)
            {
                values["ordinal"] = ordinal;
            }

            title = item.TitleFallbacks.Select(template => ExpandStrict(template, values))
                .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
        }

        var dateText = ClaimRuleEngine.FirstText(part, [item.DatePath]);
        var date = ParseDate(dateText);
        var outputDate = rule.DateAs == SequenceDateModes.Text
            ? dateText
            : date?.ToString("O", CultureInfo.InvariantCulture);
        return new SequenceMember(id, title ?? string.Empty, ordinal, outputDate, date);
    }

    private static ProviderSequenceManifest BuildManifest(
        IReadOnlyList<SequenceMember> ordered,
        SequenceManifestRuleConfiguration rule,
        ClaimRuleContext context,
        int? expectedTotal,
        string? containerKind,
        string? expectedTotalKind,
        bool authoritative)
    {
        var label = rule.ContainerLabel is null ? null : ClaimRuleEngine.ExpandVariables(rule.ContainerLabel, context.Variables);
        return new ProviderSequenceManifest
        {
            Provider = context.ProviderName,
            ContainerId = ClaimRuleEngine.ExpandVariables(rule.ContainerId, context.Variables),
            ContainerLabel = string.IsNullOrWhiteSpace(label) ? null : label,
            ExternalIdKey = rule.ExternalIdKey,
            MediaType = context.MediaType.ToString(),
            ContainerKind = containerKind,
            ExpectedTotal = expectedTotal,
            ExpectedTotalKind = expectedTotalKind,
            IsAuthoritative = authoritative,
            Items = ordered.Select((member, index) => new ProviderSequenceManifestItem
            {
                ExternalId = member.Id,
                Title = member.Title,
                Ordinal = member.Ordinal ?? (index + 1).ToString(CultureInfo.InvariantCulture),
                ReleaseDate = member.DateText,
            }).ToList(),
        };
    }

    private static List<SequenceMember> Order(IEnumerable<SequenceMember> members, IReadOnlyList<string> orderBy)
    {
        IOrderedEnumerable<SequenceMember>? ordered = null;
        foreach (var key in orderBy)
        {
            ordered = key switch
            {
                SequenceOrderKeys.Date => Chain(ordered, members, member => member.Date ?? DateOnly.MaxValue),
                SequenceOrderKeys.OrdinalNumber => Chain(ordered, members, member => OrdinalNumber(member.Ordinal)),
                SequenceOrderKeys.OrdinalText => Chain(ordered, members, member => member.Ordinal ?? string.Empty, StringComparer.OrdinalIgnoreCase),
                _ => Chain(ordered, members, member => member.Title, StringComparer.OrdinalIgnoreCase),
            };
        }

        return (ordered ?? members).ToList();
    }

    private static IOrderedEnumerable<SequenceMember> Chain<TKey>(
        IOrderedEnumerable<SequenceMember>? ordered,
        IEnumerable<SequenceMember> members,
        Func<SequenceMember, TKey> key,
        IComparer<TKey>? comparer = null) =>
        ordered is null ? members.OrderBy(key, comparer) : ordered.ThenBy(key, comparer);

    private static double OrdinalNumber(string? ordinal) =>
        double.TryParse(ordinal, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : double.MaxValue;

    private static string? ExpandStrict(string template, IReadOnlyDictionary<string, string> values)
    {
        var missing = false;
        var expanded = Regex.Replace(template, @"\{([a-z_]+)\}", match =>
        {
            var found = values.TryGetValue(match.Groups[1].Value, out var value) && !string.IsNullOrWhiteSpace(value);
            missing |= !found;
            return found ? value! : string.Empty;
        });
        return missing ? null : expanded;
    }

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
}
