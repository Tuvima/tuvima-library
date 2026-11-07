using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.Matching;

/// <summary>What the edit session needs from the Wikidata identity write (kept small so the handlers can be tested with a spy).</summary>
public interface IWikidataIdentityWriter
{
    /// <summary>The Wikidata item the item itself is linked to, or null when it has none of its own (nothing is borrowed from its show or album).</summary>
    Task<string?> ReadAsync(Guid entityId, CancellationToken ct);

    /// <summary>Links the item to a Wikidata item. Only the Wikidata identity changes: the provider match, details and pictures are not touched.</summary>
    Task SetAsync(Guid entityId, string qid, CancellationToken ct);

    /// <summary>Takes the Wikidata link away and nothing else.</summary>
    Task RemoveAsync(Guid entityId, CancellationToken ct);
}

/// <summary>
/// The Engine's one way to set or take away the Wikidata identity from the Match tab (universal editor v5, 3c "Independence and conflicts").
/// The item's own Work holds it (the show for a show, the episode for an episode), and the provider match is a different set of ids that this
/// never reads or writes. The link is the person's decision: the claim is locked, the Work's Wikidata state says <c>user</c>, and the automatic
/// alignment that follows a retail match leaves it alone (see <c>WikidataBridgeWorker</c> and the retail-match write).
/// <para>
/// Claims are only appended to: removing a link retires the claims that carried it (kept as history, left out of scoring), never deletes them.
/// Nothing is sent to Wikidata and no enrichment job is queued; the link is stored and the next refresh reads from it.
/// </para>
/// </summary>
public sealed class WikidataIdentityWriter(
    IMetadataEditorRepository launches,
    IMetadataClaimRepository claims,
    ICanonicalValueRepository canonicals,
    IBridgeIdRepository bridges,
    IWorkRepository works,
    ICollectionRepository collections,
    IItemCanonicalRepository itemCanonicalData,
    ISystemActivityRepository activity) : IWikidataIdentityWriter
{
    public async Task<string?> ReadAsync(Guid entityId, CancellationToken ct)
    {
        var launch = await launches.ResolveEditorLaunchAsync(entityId, ct).ConfigureAwait(false);
        return launch is null ? null : await ReadOwnAsync(launch.WorkId, ct).ConfigureAwait(false);
    }

    public async Task SetAsync(Guid entityId, string qid, CancellationToken ct)
    {
        var launch = await launches.ResolveEditorLaunchAsync(entityId, ct).ConfigureAwait(false)
                     ?? throw new InvalidOperationException("The item is not in the Library.");
        var work = launch.WorkId;
        var was = await ReadOwnAsync(work, ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        await claims.InsertBatchAsync(
        [
            new MetadataClaim
            {
                Id = Guid.NewGuid(),
                EntityId = work,
                ProviderId = WellKnownProviders.UserManual,
                DecisionSourceProviderId = WellKnownProviders.UserManual,
                ClaimKey = BridgeIdKeys.WikidataQid,
                ClaimValue = qid,
                ClaimedAt = now,
                Confidence = 1.0,
                IsUserLocked = true,
            },
        ], ct).ConfigureAwait(false);
        await canonicals.UpsertBatchAsync(
        [
            new CanonicalValue
            {
                EntityId = work,
                Key = BridgeIdKeys.WikidataQid,
                Value = qid,
                LastScoredAt = now,
                IsConflicted = false,
                NeedsReview = false,
                WinningProviderId = WellKnownProviders.UserManual,
            },
        ], ct).ConfigureAwait(false);

        await itemCanonicalData.UpdateWorkIdentityAsync(work, qid, ct).ConfigureAwait(false);
        await works.WriteExternalIdentifiersAsync(
            work, new Dictionary<string, string> { [BridgeIdKeys.WikidataQid] = qid }, ct, overwrite: true).ConfigureAwait(false);
        await collections.UpdateWorkWikidataMatchStateAsync(
            work,
            was is null ? WorkWikidataStatus.UserConfirmed : WorkWikidataStatus.UserReplaced,
            WorkWikidataMatchSource.User,
            locked: true,
            wikidataQid: qid,
            ct: ct).ConfigureAwait(false);

        await activity.LogAsync(new SystemActivityEntry
        {
            OccurredAt = now,
            ActionType = SystemActionType.MetadataManualOverride,
            EntityId = work,
            EntityType = "Work",
            Detail = was is null ? $"Wikidata link set to {qid}." : $"Wikidata link changed from {was} to {qid}.",
        }, ct).ConfigureAwait(false);
    }

    public async Task RemoveAsync(Guid entityId, CancellationToken ct)
    {
        var launch = await launches.ResolveEditorLaunchAsync(entityId, ct).ConfigureAwait(false)
                     ?? throw new InvalidOperationException("The item is not in the Library.");
        var work = launch.WorkId;
        var was = await ReadOwnAsync(work, ct).ConfigureAwait(false);
        string[] keys = [BridgeIdKeys.WikidataQid];

        // Retire, never delete: every provider's current claim for the key stays as history and drops out of scoring.
        foreach (var provider in (await claims.GetByEntityAsync(work, ct).ConfigureAwait(false))
                     .Where(claim => string.Equals(claim.ClaimKey, BridgeIdKeys.WikidataQid, StringComparison.OrdinalIgnoreCase))
                     .Select(claim => claim.ProviderId)
                     .Distinct())
        {
            await claims.ReplaceCurrentProviderClaimsAsync(work, provider, keys, [], ct).ConfigureAwait(false);
        }

        await canonicals.DeleteByKeyAsync(work, BridgeIdKeys.WikidataQid, ct).ConfigureAwait(false);
        await bridges.DeleteByTypesAsync(work, keys, ct).ConfigureAwait(false);
        await works.RemoveExternalIdentifiersAsync(work, keys, ct).ConfigureAwait(false);

        // The removed item is remembered as one the person turned down, as the older "reject" does.
        var rejected = await itemCanonicalData.AppendRejectedQidAsync(work, was, ct).ConfigureAwait(false);
        await collections.UpdateWorkWikidataMatchStateAsync(
            work, WorkWikidataStatus.UserRejected, WorkWikidataMatchSource.User, locked: false, wikidataQid: string.Empty, rejectedQidsJson: rejected, ct: ct)
            .ConfigureAwait(false);

        await activity.LogAsync(new SystemActivityEntry
        {
            OccurredAt = DateTimeOffset.UtcNow,
            ActionType = SystemActionType.MetadataManualOverride,
            EntityId = work,
            EntityType = "Work",
            Detail = was is null ? "Wikidata link removed." : $"Wikidata link removed (was {was}).",
        }, ct).ConfigureAwait(false);
    }

    private async Task<string?> ReadOwnAsync(Guid work, CancellationToken ct)
    {
        var value = (await canonicals.GetByEntityAsync(work, ct).ConfigureAwait(false))
            .Where(field => string.Equals(field.Key, BridgeIdKeys.WikidataQid, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(field.Value))
            .OrderByDescending(field => field.LastScoredAt)
            .Select(field => field.Value.Trim())
            .FirstOrDefault();
        return value;
    }
}
