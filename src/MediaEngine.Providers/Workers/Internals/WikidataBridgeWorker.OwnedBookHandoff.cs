using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Workers;

public sealed partial class WikidataBridgeWorker
{
    /// <summary>
    /// Reads the hand-off the retail stage leaves on the job itself for an audiobook that took the Wikidata ID
    /// of an owned, confirmed book. It is the job's resolved QID, set before the job reaches this stage and
    /// honoured only for an audiobook job with no selected retail candidate. A job created or resumed for a
    /// user rematch (Editing Mode) is built without a resolved QID, so it always does the normal lookups and a
    /// correction is never overridden. Returns <c>null</c> for every other job.
    /// </summary>
    internal static string? FindOwnedBookHandoffQid(IdentityJob job)
    {
        if (job.SelectedCandidateId.HasValue
            || string.IsNullOrWhiteSpace(job.ResolvedQid)
            || !string.Equals(job.MediaType, nameof(MediaType.Audiobooks), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return job.ResolvedQid.Trim();
    }

    /// <summary>
    /// The Wikidata item a person linked to the Work this job would write its Wikidata identity to (universal editor v5, Match tab), or
    /// <c>null</c> when the Work has none that is a person's. The automatic alignment that follows a retail match keeps such a link and looks
    /// nothing up, so it only ever runs for an item with no Wikidata identity yet and never replaces one a person set. For media whose Wikidata
    /// item belongs to the parent (a show, an album) the parent Work is read; for every other media the item's own Work.
    /// </summary>
    internal async Task<string?> FindPersonLinkedQidAsync(MediaType mediaType, WorkLineage? lineage, CancellationToken ct)
    {
        if (_itemCanonical is null || lineage is null)
        {
            return null;
        }

        var target = ClaimScopeCatalog.IsParentScoped(BridgeIdKeys.WikidataQid, mediaType)
            ? lineage.TargetForParentScope
            : lineage.TargetForSelfScope;
        var state = await _itemCanonical.LoadWorkWikidataStateAsync(target, ct).ConfigureAwait(false);
        return state is not null
               && !string.IsNullOrWhiteSpace(state.Qid)
               && !WorkWikidataStatus.IsAutomationOwned(state.Status, state.Source, state.Locked)
            ? state.Qid.Trim()
            : null;
    }

    /// <summary>Finalises the job with the Wikidata item a person linked, as the owned-book hand-off does for its own: no lookup runs.</summary>
    private void ApplyPersonLinked(JobContext ctx, string qid)
    {
        ctx.ResolvedQid = qid;
        ctx.MatchedBy = QidResolutionMethods.PersonLinked;
        ctx.PrimaryBridgeIdType = null;
        AddDistinctAdditionalClaim(ctx, BridgeIdKeys.WikidataQid, qid, 0.93);

        _logger.LogInformation(
            "Wikidata: '{Title}' keeps the QID {Qid} a person linked; no outside lookup [entity {EntityId}]",
            ctx.TitleHint, qid, ctx.Job.EntityId);
    }

    /// <summary>
    /// Finalises the job with the Wikidata ID already chosen from the owned book, exactly as the sibling
    /// step does after a match. The caller skips the bridge-ID and text lookups for this job.
    /// </summary>
    private void ApplyOwnedBookHandoff(JobContext ctx, string qid)
    {
        ctx.ResolvedQid = qid;
        ctx.MatchedBy = QidResolutionMethods.OwnedBookNoRetail;
        ctx.PrimaryBridgeIdType = null;
        AddDistinctAdditionalClaim(ctx, BridgeIdKeys.WikidataQid, qid, 0.93);
        AddDistinctAdditionalClaim(ctx, MetadataFieldConstants.QidResolutionMethod, QidResolutionMethods.OwnedBookNoRetail, 0.95);

        _logger.LogInformation(
            "Wikidata: audiobook '{Title}' keeps QID {Qid} taken from its owned book; no outside lookup [entity {EntityId}]",
            ctx.TitleHint, qid, ctx.Job.EntityId);
    }
}
