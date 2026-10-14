using MediaEngine.Api.Services.LocalAssets.Filing;
using MediaEngine.Api.Services.LocalAssets.Ocr;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.LocalAssets;

/// <summary>What happened when one document item's text was read.</summary>
public enum DocumentTextReadStatus
{
    /// <summary>Text was found and stored.</summary>
    Read,

    /// <summary>The file has no embedded text, is protected, or could not be parsed; recorded as none.</summary>
    NoText,

    /// <summary>The item or its file no longer exists.</summary>
    Missing,
}

public sealed record DocumentTextReadOutcome(DocumentTextReadStatus Status, string Summary);

/// <summary>
/// Reads the embedded text of one View document item and stores it through
/// <see cref="ILocalAssetRepository.SetDocumentTextAsync"/>. Document text is private
/// to the owner's scope, so nothing here logs it: only file names and counts.
/// </summary>
public sealed class DocumentTextReadService(
    ILocalAssetRepository repository,
    IConfigurationLoader configuration,
    ILogger<DocumentTextReadService> logger,
    DocumentOcrQueue? ocrQueue = null,
    DocumentRulesQueue? rulesQueue = null)
{
    /// <summary>True when a file with this extension has its text read by the background job.</summary>
    public static bool NeedsBackgroundRead(string extension) => DocumentTextReader.IsSupported(extension);

    public async Task<DocumentTextReadOutcome> ReadItemAsync(Guid itemId, CancellationToken ct)
    {
        var location = repository.ResolveContent(itemId);
        if (location is null || !File.Exists(location.FilePath))
        {
            return new DocumentTextReadOutcome(DocumentTextReadStatus.Missing, "The document is no longer on disk.");
        }

        var policy = configuration.LoadLibraries().PersonalLibraryPolicy;
        var limits = new DocumentTextReadLimits(
            Math.Max(1, policy.DocumentTextMaxCharacters),
            Math.Max(1, policy.DocumentTextMaxPdfPages),
            TimeSpan.FromSeconds(Math.Max(1, policy.DocumentTextReadTimeoutSeconds)),
            Math.Max(1, configuration.LoadOcr().TextLayerMinCharacters));
        var fileName = Path.GetFileName(location.FilePath);

        var result = await Task.Run(() => DocumentTextReader.Read(location.FilePath, limits, ct), ct).ConfigureAwait(false);
        if (result.Failed)
        {
            logger.LogWarning("Could not read the text of document {FileName}: {Reason}.", fileName, result.FailureReason);
        }

        var hasText = result.HasText;
        var written = await repository.SetDocumentTextAsync(
            itemId,
            new LocalAssetDocumentTextWrite(
                hasText ? result.Text : null,
                hasText ? LocalAssetTextSources.Embedded : LocalAssetTextSources.None,
                Truncated: hasText && result.Truncated,
                Encrypted: result.Encrypted,
                PageCount: result.PageCount),
            ct).ConfigureAwait(false);
        if (!written)
        {
            return new DocumentTextReadOutcome(DocumentTextReadStatus.Missing, "The document no longer has an index entry.");
        }

        logger.LogDebug(
            "Read {Characters} characters from document {FileName} (encrypted {Encrypted}, truncated {Truncated}).",
            result.Text?.Length ?? 0, fileName, result.Encrypted, result.Truncated);
        if (rulesQueue is not null)
        {
            // Auto-filing rules and Inbox suggestions look at the words just stored (or at none, for rules on names).
            await rulesQueue.TryQueueAfterTextAsync(itemId, hasText ? result.Text : null, ct).ConfigureAwait(false);
        }

        await QueueRecognitionIfScannedAsync(location, result, ct).ConfigureAwait(false);
        return hasText
            ? new DocumentTextReadOutcome(DocumentTextReadStatus.Read, $"Read {result.Text!.Length} characters of text.")
            : new DocumentTextReadOutcome(
                DocumentTextReadStatus.NoText,
                result.Encrypted ? "The document is password protected." : "The document has no embedded text.");
    }

    /// <summary>
    /// A PDF with pages that have no text layer is handed to text recognition. A failure to queue never
    /// fails the text read; the next scan or "read again" queues it.
    /// </summary>
    private async Task QueueRecognitionIfScannedAsync(
        LocalAssetContentLocation location,
        DocumentTextReadResult result,
        CancellationToken ct)
    {
        if (ocrQueue is null
            || result.Failed
            || result.Encrypted
            || result.LowTextPages <= 0
            || !OcrFormats.IsPdf(Path.GetExtension(location.FilePath)))
        {
            return;
        }

        try
        {
            await ocrQueue.TryQueueAsync(location.ItemId, location.ContentHash, location.FilePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not queue text recognition for {FileName}.", Path.GetFileName(location.FilePath));
        }
    }

    /// <summary>Queues the text read for an item. The key includes the content hash, so each edit queues once.</summary>
    public static Task QueueAsync(
        IMediaOperationRepository operations,
        Guid itemId,
        string contentHash,
        string sourcePath,
        CancellationToken ct) =>
        operations.EnsureAsync(new MediaOperation
        {
            OperationType = MediaOperationType.ViewDocumentTextRead,
            OperationKind = MediaOperationKind.ViewDocument,
            EntityId = itemId,
            EntityKind = "LocalItem",
            SourcePath = sourcePath,
            ContentHash = contentHash,
            Status = MediaOperationStatus.Queued,
            Stage = MediaOperationStage.Queued,
            QueueName = "view_document_text",
            IdempotencyKey = $"view-document-text:{itemId:D}:{contentHash}",
        }, ct);
}
