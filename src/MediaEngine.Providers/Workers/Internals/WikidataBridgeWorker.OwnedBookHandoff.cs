using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Workers;

public sealed partial class WikidataBridgeWorker
{
    /// <summary>
    /// Reads the hand-off the retail stage leaves for an audiobook that took the Wikidata ID of an owned,
    /// confirmed book (<see cref="QidResolutionMethods.OwnedBookNoRetail"/>). Returns that ID, or <c>null</c>
    /// for every other job. A job that carries a selected retail candidate is never one of these: a stale
    /// hand-off claim from an earlier run must not skip the lookups for an audiobook that has since matched.
    /// </summary>
    private async Task<string?> FindOwnedBookHandoffQidAsync(
        IdentityJob job,
        MediaType mediaType,
        CancellationToken ct)
    {
        if (mediaType != MediaType.Audiobooks || job.SelectedCandidateId.HasValue)
        {
            return null;
        }

        try
        {
            var claims = await _claimRepo.GetByEntityAsync(job.EntityId, ct).ConfigureAwait(false);
            var handedOff = claims.Any(claim =>
                claim.IsCurrent
                && string.Equals(claim.ClaimKey, MetadataFieldConstants.QidResolutionMethod, StringComparison.OrdinalIgnoreCase)
                && string.Equals(claim.ClaimValue, QidResolutionMethods.OwnedBookNoRetail, StringComparison.OrdinalIgnoreCase));
            if (!handedOff)
            {
                return null;
            }

            return claims
                .Where(claim => claim.IsCurrent
                    && claim.ProviderId == WellKnownProviders.Wikidata
                    && string.Equals(claim.ClaimKey, BridgeIdKeys.WikidataQid, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(claim.ClaimValue))
                .OrderByDescending(claim => claim.ClaimedAt)
                .Select(claim => claim.ClaimValue.Trim())
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Without the hand-off the job carries on as a normal retail-matched job would.
            _logger.LogWarning(ex,
                "Wikidata: could not read the owned-book hand-off for audiobook entity {EntityId}", job.EntityId);
            return null;
        }
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
