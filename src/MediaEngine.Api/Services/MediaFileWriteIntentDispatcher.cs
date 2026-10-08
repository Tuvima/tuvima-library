using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;

namespace MediaEngine.Api.Services;

/// <summary>Drains the generation-safe durable file-write queue.</summary>
public sealed class MediaFileWriteIntentDispatcher(
    MediaFileWriteIntentRepository intents,
    IWriteBackOutcomeService writeBack,
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
