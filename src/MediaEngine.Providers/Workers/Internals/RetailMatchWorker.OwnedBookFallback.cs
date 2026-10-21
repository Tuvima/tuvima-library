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
    /// <summary>
    /// The one exception to "no retail match, no Wikidata": an audiobook Apple could not match takes the
    /// Wikidata ID of an owned, confirmed book with the same title and author. Nothing is looked up outside
    /// the machine. When a book is found the job is handed to the Wikidata stage with that ID already chosen
    /// (carried on the job's own resolved QID, which a user rematch or a resumed job overwrites) and no Needs
    /// Review item is raised; otherwise the caller carries on with the normal no-match handling. The Wikidata
    /// stage records the <c>wikidata_qid</c> and <see cref="QidResolutionMethods.OwnedBookNoRetail"/> claims.
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

        // The hand-off lives on this job alone: the job row carries the chosen ID, and the Wikidata stage
        // honours it only for a job with no retail candidate. A new or resumed job (a user rematch) starts
        // without it, so a correction made later is never overridden by this link.
        await _jobRepo.SetResolvedQidAsync(job.Id, book.WikidataQid, ct).ConfigureAwait(false);
        await _jobRepo.UpdateStateAsync(job.Id, IdentityJobState.RetailMatched, ct: ct).ConfigureAwait(false);
        await _timeline.RecordOwnedBookMatchedAsync(job.EntityId, book.Title, book.WikidataQid, ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Audiobook entity {EntityId} ('{Title}') has no retail match; linked to owned book Work {BookWorkId} with Wikidata QID {Qid}",
            job.EntityId, title, book.WorkId, book.WikidataQid);

        return true;
    }
}
