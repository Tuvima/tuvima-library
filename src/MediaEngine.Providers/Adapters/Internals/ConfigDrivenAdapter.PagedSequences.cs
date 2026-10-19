using System.Globalization;
using System.Text.Json.Nodes;
using MediaEngine.Domain.Configuration;
using MediaEngine.Providers.Models;

namespace MediaEngine.Providers.Adapters;

public sealed partial class ConfigDrivenAdapter
{
    /// <summary>What a paged read of a container's members found.</summary>
    private sealed record CollectedMembers(IReadOnlyList<SequenceMember> Members, int? ResponseTotal, bool CompletedAllPages);

    /// <summary>
    /// The <c>paged_sequence</c> rule: reads every page of the provider's <c>sequence_manifest</c>
    /// request for one container and emits the manifest claim. Pages are remembered per container.
    /// </summary>
    private async Task ApplyPagedSequenceAsync(
        List<ProviderClaim> claims,
        JsonNode current,
        ClaimRuleConfiguration rule,
        string defaultAdd,
        ClaimRuleContext context,
        ProviderLookupRequest request,
        CancellationToken ct)
    {
        var sequenceRequest = _config.SequenceManifest;
        var baseUrl = ResolveBaseUrl(request);
        var apiKey = EffectiveApiKey;
        if (sequenceRequest?.Enabled != true || string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey)
            || rule.Item is null)
        {
            return;
        }

        var container = ClaimRuleEngine.ExpandVariables(rule.Container ?? string.Empty, context.Variables);
        var expectedFromParent = int.TryParse(
            JsonPathEvaluator.GetStringValue(JsonPathEvaluator.Evaluate(current, rule.ExpectedTotalPath ?? string.Empty)),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsedExpected)
            ? (int?)parsedExpected
            : null;

        Task<CollectedMembers?> Collect() => CollectPagesAsync(sequenceRequest, rule, context, baseUrl, apiKey, container, expectedFromParent, ct);
        var collected = rule.Cache?.Store == FollowUpCacheStores.Memory
            ? await FetchRememberedAsync(_pagedLookups, $"paged:{container}", Collect).ConfigureAwait(false)
            : await Collect().ConfigureAwait(false);
        if (collected is null)
        {
            return;
        }

        SequenceRuleEvaluator.ApplyPaged(
            claims,
            collected.Members,
            expectedFromParent ?? collected.ResponseTotal,
            collected.CompletedAllPages,
            rule,
            defaultAdd,
            context,
            sequenceRequest.ContainerKind,
            sequenceRequest.ExpectedTotalKind);
    }

    private async Task<CollectedMembers?> CollectPagesAsync(
        ProviderSequenceManifestRequestConfiguration paging,
        ClaimRuleConfiguration rule,
        ClaimRuleContext context,
        string baseUrl,
        string apiKey,
        string container,
        int? expectedFromParent,
        CancellationToken ct)
    {
        var members = new List<SequenceMember>();
        int? responseTotal = null;
        var completedAllPages = false;
        for (var page = 0; page < paging.MaxPages; page++)
        {
            var offset = page * paging.PageSize;
            var url = paging.UrlTemplate
                .Replace("{base_url}", baseUrl.TrimEnd('/'), StringComparison.Ordinal)
                .Replace("{api_key}", Uri.EscapeDataString(apiKey), StringComparison.Ordinal)
                .Replace("{container_id}", Uri.EscapeDataString(container), StringComparison.Ordinal)
                .Replace("{field_list}", Uri.EscapeDataString(string.Join(',', paging.Fields)), StringComparison.Ordinal)
                .Replace("{page_size}", paging.PageSize.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{offset}", offset.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
            var json = await FetchJsonAsync(url, useResponseCache: false, notFoundIsError: true, ct).ConfigureAwait(false);
            if (responseTotal is null
                && int.TryParse(
                    JsonPathEvaluator.GetStringValue(JsonPathEvaluator.Evaluate(json, rule.TotalPath ?? string.Empty)),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var total))
            {
                responseTotal = total;
            }

            if (JsonPathEvaluator.Evaluate(json, rule.ItemsPath ?? string.Empty) is not JsonArray results)
            {
                break;
            }

            foreach (var part in results.Where(part => part is not null))
            {
                if (SequenceRuleEvaluator.ReadMember(part!, rule, context.Variables) is { } member)
                {
                    members.Add(member);
                }
            }

            var expectedTotal = expectedFromParent ?? responseTotal;
            var complete = results.Count < paging.PageSize
                || (expectedTotal.HasValue && members.Count >= expectedTotal.Value);
            if (results.Count == 0 || complete)
            {
                completedAllPages = complete;
                break;
            }
        }

        return new CollectedMembers(members, responseTotal, completedAllPages);
    }
}
