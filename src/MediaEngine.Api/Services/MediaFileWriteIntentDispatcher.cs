using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;

namespace MediaEngine.Api.Services;

/// <summary>
/// Drains the generation-safe durable file-write queue. A row queued by a library move (Details overlay D19) first renames and moves
/// the file to fit its new place through the organiser's re-file, which asks the shared mutation policy again and leaves As-is,
/// protected and read-only files exactly where they are, then writes the tags like every other row. Both steps are safe to repeat,
/// so a row that was being worked on when the Engine stopped is simply claimed again once its lease runs out.
/// </summary>
public sealed class MediaFileWriteIntentDispatcher(
    MediaFileWriteIntentRepository intents,
    IWriteBackOutcomeService writeBack,
    IAutoOrganizeService organizer,
    ILogger<MediaFileWriteIntentDispatcher> logger) : BackgroundService
{
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    internal const int MaxAttempts = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await DispatchOnceAsync(stoppingToken))
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Media file write-intent dispatch failed; retrying the queue.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    public async Task<bool> DispatchOnceAsync(CancellationToken ct = default)
    {
        var intent = await intents.ClaimNextAsync(LeaseDuration, ct);
        if (intent is null)
        {
            return false;
        }

        string? refileFailure = null;
        if (string.Equals(intent.Trigger, MediaFileWriteTriggers.LibraryMove, StringComparison.Ordinal))
        {
            refileFailure = await RefileAsync(intent, ct);
        }

        WriteBackOutcome outcome;
        try
        {
            outcome = await writeBack.WriteMetadataWithOutcomeAsync(intent.AssetId, intent.Trigger, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Lease expiry safely recovers an interrupted process.
            throw;
        }
        catch (Exception ex)
        {
            outcome = WriteBackOutcome.Failed(ex.Message);
        }

        // A file that could not be moved this time is tried again like a failed write, whatever the tags did.
        if (refileFailure is not null)
        {
            outcome = WriteBackOutcome.Failed(refileFailure);
        }

        var (status, error) = MapCompletion(intent, outcome);
        var completed = await intents.CompleteAsync(
            intent.AssetId, intent.Generation, status, error, ct);
        if (!completed)
        {
            logger.LogInformation(
                "Ignored stale write-intent completion for asset {AssetId}, generation {Generation}.",
                intent.AssetId, intent.Generation);
            return true;
        }

        logger.LogInformation(
            "Media file write intent {OperationToken} for asset {AssetId} completed as {Status} after attempt {Attempt}.",
            intent.OperationToken, intent.AssetId, status, intent.Attempts);
        return true;
    }

    /// <summary>
    /// Renames and moves the file of a library move. Returns why it should be tried again, or null when the file is where it should be
    /// or was deliberately left alone (the organiser records that in the activity history).
    /// </summary>
    private async Task<string?> RefileAsync(MediaFileWriteIntent intent, CancellationToken ct)
    {
        try
        {
            var filed = await organizer.RefileAsync(intent.AssetId, ct);
            return filed.Kind == AssetRefileOutcomeKind.Failed ? filed.Reason ?? "The file could not be renamed and moved." : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Lease expiry safely recovers an interrupted process.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Renaming the file of asset {AssetId} after a library move failed.", intent.AssetId);
            return ex.Message;
        }
    }

    internal static (string Status, string? Error) MapCompletion(
        MediaFileWriteIntent intent, WriteBackOutcome outcome) => outcome.Kind switch
        {
            WriteBackOutcomeKind.Verified => ("verified", null),
            WriteBackOutcomeKind.Blocked => ("blocked", outcome.Reason),
            WriteBackOutcomeKind.Unsupported => ("unsupported", outcome.Reason),
            WriteBackOutcomeKind.Unverified when intent.Attempts < MaxAttempts =>
                ("pending", outcome.Reason ?? "Physical read-back was unverified."),
            WriteBackOutcomeKind.Failed when intent.Attempts < MaxAttempts =>
                ("pending", outcome.Reason ?? "Write-back failed."),
            WriteBackOutcomeKind.Unverified =>
                ("failed", outcome.Reason ?? "Physical read-back remained unverified."),
            _ => ("failed", outcome.Reason ?? "Write-back failed."),
        };
}
