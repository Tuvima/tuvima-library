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

    /// <summary>What a person decided about the Wikidata item of the Work a job writes to: the item they linked, whether they removed the link, and the items they turned down.</summary>
    internal sealed record PersonWikidataDecision(string? LinkedQid, bool Removed, IReadOnlySet<string> RejectedQids)
    {
        public static PersonWikidataDecision None { get; } = new(null, false, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Reads what a person decided about the Wikidata item of the Work this job would write its Wikidata identity to (universal editor v5, Match tab).
    /// The automatic alignment keeps an item a person linked and looks nothing up; it does not run again for an item whose link a person removed
    /// (a provider rematch clears that state and may align again); and it never aligns to an item a person turned down. For media whose Wikidata
    /// item belongs to the parent (a show, an album) the parent Work is read; for every other media the item's own Work.
    /// </summary>
    internal async Task<PersonWikidataDecision> ReadPersonDecisionAsync(MediaType mediaType, WorkLineage? lineage, CancellationToken ct)
    {
        if (_itemCanonical is null || lineage is null)
        {
            return PersonWikidataDecision.None;
        }

        var target = ClaimScopeCatalog.IsParentScoped(BridgeIdKeys.WikidataQid, mediaType)
            ? lineage.TargetForParentScope
            : lineage.TargetForSelfScope;
        var state = await _itemCanonical.LoadWorkWikidataStateAsync(target, ct).ConfigureAwait(false);
        if (state is null)
        {
            return PersonWikidataDecision.None;
        }

        var rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(state.RejectedQidsJson))
        {
            try
            {
                foreach (var qid in System.Text.Json.JsonSerializer.Deserialize<List<string>>(state.RejectedQidsJson) ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(qid))
                    {
                        rejected.Add(qid.Trim());
                    }
                }
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogWarning(ex, "Wikidata: the list of turned-down items for Work {Work} could not be read; none are skipped.", target);
            }
        }

        var linked = !string.IsNullOrWhiteSpace(state.Qid)
                     && !WorkWikidataStatus.IsAutomationOwned(state.Status, state.Source, state.Locked)
            ? state.Qid.Trim()
            : null;
        var removed = string.IsNullOrWhiteSpace(state.Qid)
                      && string.Equals(state.Status, WorkWikidataStatus.UserRejected, StringComparison.OrdinalIgnoreCase)
                      && string.Equals(state.Source, WorkWikidataMatchSource.User, StringComparison.OrdinalIgnoreCase);
        return new PersonWikidataDecision(linked, removed, rejected);
    }

    /// <summary>Applies a person's decision to a job: keep their link, or skip the lookup when they removed it; note the items to never align to.</summary>
    private void ApplyPersonDecision(JobContext ctx, PersonWikidataDecision decision)
    {
        ctx.RejectedQids = decision.RejectedQids;
        if (decision.LinkedQid is { } linked)
        {
            ApplyPersonLinked(ctx, linked);
        }
        else if (decision.Removed)
        {
            ctx.LookupSkipped = true;
            _logger.LogInformation(
                "Wikidata: '{Title}' had its Wikidata link removed by a person; no automatic alignment until it is matched again [entity {EntityId}]",
                ctx.TitleHint, ctx.Job.EntityId);
        }
    }

    /// <summary>Drops a resolved item the person turned down, with the claims that carried it, so the job ends as unresolved instead.</summary>
    private void DropRejectedQid(JobContext ctx)
    {
        if (ctx.ResolvedQid is not { } qid || !ctx.RejectedQids.Contains(qid))
        {
            return;
        }

        _logger.LogInformation(
            "Wikidata: '{Title}' resolved to {Qid}, which a person turned down; it is not used [entity {EntityId}]",
            ctx.TitleHint, qid, ctx.Job.EntityId);
        ctx.ResolvedQid = null;
        ctx.MatchedBy = null;
        ctx.PrimaryBridgeIdType = null;
        ctx.AdditionalClaims.RemoveAll(claim => string.Equals(claim.Key, BridgeIdKeys.WikidataQid, StringComparison.OrdinalIgnoreCase)
            && string.Equals(claim.Value, qid, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Finalizes the job with the Wikidata item a person linked, as the owned-book hand-off does for its own: no lookup runs.</summary>
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
