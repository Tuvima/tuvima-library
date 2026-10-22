using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;

namespace MediaEngine.Api.Services.FileWrites;

/// <summary>
/// Writes media files (the tags of music, books, comics, video and so on) for the scheduler. This is the only place in the Engine that
/// writes tags or embedded artwork into a media file: everything else puts the file on the list (see the file-write guardrail test).
/// The tags are built from Library's values at the moment of writing, so the newest result always wins.
/// </summary>
public sealed class MediaAssetFileWriter(
    IWriteBackOutcomeService writeBack,
    ArtworkWritebackService artwork,
    IConfigurationLoader configuration,
    ILogger<MediaAssetFileWriter> logger,
    OutsideChangeGuard? changeGuard = null,
    IAssetRereader? rereader = null) : IFileWriteTargetWriter
{
    public string TargetKind => MediaFileWriteIntentRepository.MediaAssetKind;

    public async Task<IReadOnlyList<FileWriteResult>> WriteBatchAsync(
        IReadOnlyList<MediaFileWriteIntent> intents, CancellationToken ct)
    {
        var settings = configuration.LoadConfig<WriteBackConfiguration>(string.Empty, "writeback") ?? new WriteBackConfiguration();
        var results = new List<FileWriteResult>(intents.Count);
        foreach (var intent in intents)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await WriteOrReadAgainAsync(intent, settings, ct));
        }
        return results;
    }

    private async Task<FileWriteResult> WriteOrReadAgainAsync(MediaFileWriteIntent intent, WriteBackConfiguration settings, CancellationToken ct)
    {
        // A file changed by something else since the change was queued is read first, so the person's edit in the file is not
        // written over; the next run writes from the re-scored values (a locked field keeps the person's value in the scoring).
        if (changeGuard is not null && rereader is not null && await FileChangedOutsideAsync(intent, ct))
        {
            var reread = await rereader.RereadAsync(intent.AssetId, ct);
            if (string.Equals(reread.Status, "Updated", StringComparison.Ordinal))
            {
                return new FileWriteResult(
                    intent.Id,
                    WriteBackOutcome.Failed(FileChangedReason),
                    ReadAgainFirst: true);
            }

            logger.LogWarning("Reading the changed file of asset {AssetId} back failed: {Message}", intent.AssetId, reread.Message);
            return new FileWriteResult(intent.Id, WriteBackOutcome.Failed(reread.Message));
        }

        return new FileWriteResult(intent.Id, await WriteOneAsync(intent, settings, ct));
    }

    /// <summary>What the file's row says while it waits to be written again after the file was read back.</summary>
    internal const string FileChangedReason = "The file changed outside Library; it will be read again before writing.";

    private async Task<bool> FileChangedOutsideAsync(MediaFileWriteIntent intent, CancellationToken ct)
    {
        try
        {
            return await changeGuard!.ChangedSinceLibraryLastSawAsync(intent.AssetId, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file that cannot be read now is left to the write itself, which reports why it cannot be written.
            logger.LogDebug(ex, "Could not compare the file of asset {AssetId} with what Library last saw.", intent.AssetId);
            return false;
        }
    }

    private async Task<WriteBackOutcome> WriteOneAsync(MediaFileWriteIntent intent, WriteBackConfiguration settings, CancellationToken ct)
    {
        WriteBackOutcome outcome;
        try
        {
            outcome = await writeBack.WriteMetadataWithOutcomeAsync(intent.AssetId, intent.Trigger, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A stop leaves the row claimed; its lease runs out and the next run writes it.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Writing the tags of asset {AssetId} failed.", intent.AssetId);
            outcome = WriteBackOutcome.Failed(ex.Message);
        }

        if (!settings.Enabled || !settings.ArtworkEnabled || !IsArtworkMediaType(intent.MediaType))
        {
            return outcome;
        }

        // Embedded cover art is a second, opt-in write into the same file (Music and Audiobooks only), with its own state and history.
        try
        {
            var retry = string.Equals(intent.Trigger, MediaFileWriteTriggers.ArtworkRetry, StringComparison.Ordinal);
            var status = await artwork.ProcessAsync(intent.AssetId, retry, ct);
            if (changeGuard is not null && string.Equals(status?.Status, "Embedded", StringComparison.OrdinalIgnoreCase))
            {
                // The embed changed the file again after the tags were written: Library's own change, so it is the new fingerprint.
                await changeGuard.RecordOwnWriteAsync(intent.AssetId, ct);
            }

            if (string.Equals(status?.Status, "Failed", StringComparison.OrdinalIgnoreCase)
                && outcome.Kind == WriteBackOutcomeKind.Verified)
            {
                return WriteBackOutcome.Failed(status!.Reason ?? "Artwork could not be written into the file.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Embedding artwork into asset {AssetId} failed.", intent.AssetId);
            return outcome.Kind == WriteBackOutcomeKind.Verified ? WriteBackOutcome.Failed(ex.Message) : outcome;
        }

        return outcome;
    }

    private static bool IsArtworkMediaType(string? mediaType) =>
        string.Equals(mediaType, "Music", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mediaType, "Audiobooks", StringComparison.OrdinalIgnoreCase);
}
