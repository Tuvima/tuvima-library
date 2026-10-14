using System.Security.Cryptography;
using System.Text;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.LocalAssets.Filing;

/// <summary>
/// Puts "apply the auto-filing rules to this document" on the durable queue. The key holds a short fingerprint of the
/// document's words, so one text read queues one job, a later text recognition that replaces the words queues another,
/// and a repeated queue of the same words is ignored.
/// </summary>
public sealed class DocumentRulesQueue(
    IMediaOperationRepository operations,
    IDocumentFilingRepository documents,
    ILogger<DocumentRulesQueue> logger)
{
    public const string QueueName = "view_document_rules";

    private const int PageSize = 500;
    private const int MaximumBatch = 50_000;

    /// <summary>A short, stable fingerprint of the words; the same for no words at all.</summary>
    public static string TextFingerprint(string? text) =>
        string.IsNullOrEmpty(text)
            ? "none"
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();

    public async Task QueueAsync(Guid itemId, string fingerprint, CancellationToken ct)
    {
        await operations.EnsureAsync(new MediaOperation
        {
            OperationType = MediaOperationType.ViewDocumentApplyRules,
            OperationKind = MediaOperationKind.ViewDocument,
            EntityId = itemId,
            EntityKind = "LocalItem",
            Status = MediaOperationStatus.Queued,
            Stage = MediaOperationStage.Queued,
            QueueName = QueueName,
            IdempotencyKey = $"view-document-rules:{itemId:D}:{fingerprint}",
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Queues the rules after a text read or text recognition wrote (or found no) words. A failure to queue never fails
    /// the read; the next scan or "apply rules" queues it again.
    /// </summary>
    public async Task TryQueueAfterTextAsync(Guid itemId, string? text, CancellationToken ct)
    {
        try
        {
            await QueueAsync(itemId, TextFingerprint(text), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not queue auto-filing for item {ItemId}.", itemId);
        }
    }

    /// <summary>
    /// Queues every active document of one library, once for this batch ("apply my rules to existing documents").
    /// Returns how many were queued.
    /// </summary>
    public async Task<int> QueueLibraryAsync(Guid libraryId, string batchStamp, CancellationToken ct)
    {
        var queued = 0;
        Guid? after = null;
        while (queued < MaximumBatch)
        {
            var page = await documents.GetDocumentIdsAsync(libraryId, after, PageSize, ct).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            foreach (var itemId in page)
            {
                await QueueAsync(itemId, "batch-" + batchStamp, ct).ConfigureAwait(false);
                queued++;
            }

            after = page[^1];
        }

        logger.LogInformation("Queued {Count} document(s) to have auto-filing rules applied.", queued);
        return queued;
    }
}
