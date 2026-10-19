using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Models;
using MediaEngine.Providers.Models;

namespace MediaEngine.Providers.Adapters;

/// <summary>
/// The <c>sequence</c> claim rule: reads a list of ordered members (a film collection, a
/// series) from a response, finds the current member's place in it and emits position, count and
/// a provider-neutral <see cref="ProviderSequenceManifest"/> claim.
/// </summary>
internal static class SequenceRuleEvaluator
{
    private sealed record Member(string Id, string Title, DateOnly? Date);

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

        var item = rule.Item;
        IEnumerable<Member> members = array
            .Where(part => part is not null)
            .Select(part => new Member(
                ClaimRuleEngine.FirstText(part, [item.IdPath]) ?? string.Empty,
                ClaimRuleEngine.FirstText(part, item.TitlePaths) ?? string.Empty,
                ParseDate(ClaimRuleEngine.FirstText(part, [item.DatePath]))))
            .Where(member => !string.IsNullOrWhiteSpace(member.Id));
        members = Order(members, rule.OrderBy ?? []);

        var ordered = members.ToList();
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
            var manifest = new ProviderSequenceManifest
            {
                Provider = context.ProviderName,
                ContainerId = ClaimRuleEngine.ExpandVariables(manifestRule.ContainerId, context.Variables),
                ContainerLabel = BlankToNull(manifestRule.ContainerLabel is null
                    ? null
                    : ClaimRuleEngine.ExpandVariables(manifestRule.ContainerLabel, context.Variables)),
                ExternalIdKey = manifestRule.ExternalIdKey,
                MediaType = context.MediaType.ToString(),
                IsAuthoritative = manifestRule.Authoritative,
                Items = ordered.Select((member, index) => new ProviderSequenceManifestItem
                {
                    ExternalId = member.Id,
                    Title = member.Title,
                    Ordinal = (index + 1).ToString(CultureInfo.InvariantCulture),
                    ReleaseDate = member.Date?.ToString("O", CultureInfo.InvariantCulture),
                }).ToList(),
            };
            ClaimRuleEngine.AddClaim(
                claims, manifestRule.ClaimKey, JsonSerializer.Serialize(manifest), manifestRule.Confidence, defaultAdd);
        }
    }

    private static IEnumerable<Member> Order(IEnumerable<Member> members, IReadOnlyList<string> orderBy)
    {
        IOrderedEnumerable<Member>? ordered = null;
        foreach (var key in orderBy)
        {
            ordered = key switch
            {
                SequenceOrderKeys.Date => ordered is null
                    ? members.OrderBy(member => member.Date ?? DateOnly.MaxValue)
                    : ordered.ThenBy(member => member.Date ?? DateOnly.MaxValue),
                _ => ordered is null
                    ? members.OrderBy(member => member.Title, StringComparer.OrdinalIgnoreCase)
                    : ordered.ThenBy(member => member.Title, StringComparer.OrdinalIgnoreCase),
            };
        }

        return ordered ?? members;
    }

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;

    private static string? BlankToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
