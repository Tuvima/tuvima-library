using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Workers;

public sealed partial class RetailMatchWorker
{
    /// <summary>Confidence the hand-off claim carries for the Wikidata ID taken from the owned book.</summary>
    internal const double OwnedBookQidConfidence = 0.93;

    /// <summary>Confidence the hand-off claim carries for the resolution method.</summary>
    internal const double OwnedBookMethodConfidence = 0.95;

    /// <summary>
    /// The one exception to "no retail match, no Wikidata": an audiobook Apple could not match takes the
    /// Wikidata ID of an owned, confirmed book with the same title and author. Nothing is looked up outside
    /// the machine. When a book is found the job is handed to the Wikidata stage with that ID already chosen
    /// (see <see cref="QidResolutionMethods.OwnedBookNoRetail"/>) and no Needs Review item is raised; otherwise
    /// the caller carries on with the normal no-match handling.
    /// </summary>
    /// <returns><c>true</c> when the job was handed over; <c>false</c> when the normal no-match path must run.</returns>
    private async Task<bool> TryLinkAudiobookToOwnedBookAsync(
        IdentityJob job,
        IReadOnlyDictionary<string, string> hints,
        WorkLineage? lineage,
        CancellationToken ct)
    {
        var title = hints.GetValueOrDefault(MetadataFieldConstants.Title);
        // The author only: a narrator is not the author, so it never stands in for one.
        var author = hints.GetValueOrDefault(MetadataFieldConstants.Author);
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(author))
        {
            return false;
        }

        if (PlaceholderTitleDetector.IsPlaceholder(title) && !PlaceholderTitleDetector.HasBridgeId(hints))
        {
            return false;
        }

        ConfirmedSiblingWorkQid? book;
        try
        {
            book = await _workRepo.FindConfirmedSiblingQidAsync(
                    MediaType.Audiobooks,
                    [MediaType.Books],
                    title,
                    author,
                    excludeWorkId: lineage?.TargetForSelfScope,
                    requireSettledIdentity: true,
                    ct: ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Owned-book lookup failed for audiobook entity {EntityId} ('{Title}'); continuing as no retail match",
                job.EntityId, title);
            return false;
        }

        if (book is null || string.IsNullOrWhiteSpace(book.WikidataQid))
        {
            return false;
        }

        // The claims are the durable hand-off: the Wikidata stage reads them to finalise with this ID and
        // skip its own lookups. Replacing (not appending) keeps a re-run from stacking duplicates.
        var claims = new List<MetadataClaim>
        {
            NewOwnedBookClaim(job.EntityId, BridgeIdKeys.WikidataQid, book.WikidataQid, OwnedBookQidConfidence),
            NewOwnedBookClaim(
                job.EntityId,
                MetadataFieldConstants.QidResolutionMethod,
                QidResolutionMethods.OwnedBookNoRetail,
                OwnedBookMethodConfidence),
        };
        await _claimRepo.ReplaceCurrentProviderClaimsAsync(
                job.EntityId,
                WellKnownProviders.Wikidata,
                [BridgeIdKeys.WikidataQid, MetadataFieldConstants.QidResolutionMethod],
                claims,
                ct)
            .ConfigureAwait(false);

        await _jobRepo.UpdateStateAsync(job.Id, IdentityJobState.RetailMatched, ct: ct).ConfigureAwait(false);
        await _timeline.RecordOwnedBookMatchedAsync(job.EntityId, book.Title, book.WikidataQid, ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Audiobook entity {EntityId} ('{Title}') has no retail match; linked to owned book Work {BookWorkId} with Wikidata QID {Qid}",
            job.EntityId, title, book.WorkId, book.WikidataQid);

        return true;
    }

    private static MetadataClaim NewOwnedBookClaim(Guid entityId, string key, string value, double confidence) => new()
    {
        Id = Guid.NewGuid(),
        EntityId = entityId,
        ProviderId = WellKnownProviders.Wikidata,
        ClaimKey = key,
        ClaimValue = value,
        Confidence = confidence,
        ClaimedAt = DateTimeOffset.UtcNow,
    };
}
