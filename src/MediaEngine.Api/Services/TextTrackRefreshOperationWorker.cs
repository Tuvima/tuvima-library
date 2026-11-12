using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Workers;

namespace MediaEngine.Api.Services;

/// <summary>
/// Executes user-requested lyrics and subtitle refreshes from the durable media
/// operation queue. A queued request can be reclaimed after an Engine restart.
/// </summary>
public sealed class TextTrackRefreshOperationWorker(
    IMediaOperationRepository operations,
    TextTrackEnrichmentWorker textTracks,
    ILogger<TextTrackRefreshOperationWorker> logger) : BackgroundService
{
    private static readonly string[] OperationTypes =
    [
        MediaOperationType.TextTrackLyrics,
        MediaOperationType.TextTrackSubtitles,
    ];

    /// <summary>
    /// A lookup whose source could not be reached is tried at most this many times in all, so an offline Library does not keep every song's
    /// lookup alive for ever. The last attempt finishes it as dead-lettered (terminal); a later Find lyrics or rescan queues a fresh one.
    /// </summary>
    internal const int MaxUnreachableAttempts = 3;

    /// <summary>The wait after the first failed attempt, then after the second. There is no wait after the last attempt: it gives up.</summary>
    internal static readonly TimeSpan[] UnreachableRetryDelays = [TimeSpan.FromMinutes(15), TimeSpan.FromHours(2)];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var leased = await operations.LeaseNextAsync(
                    Environment.MachineName + ":text-tracks",
                    OperationTypes,
                    batchSize: 4,
                    leaseDuration: TimeSpan.FromMinutes(2),
                    stoppingToken).ConfigureAwait(false);
                if (leased.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                foreach (var operation in leased)
                {
                    await ProcessAsync(operation, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Text-track operation worker failed; queued work will be retried.");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    internal async Task ProcessAsync(MediaOperation operation, CancellationToken ct)
    {
        if (operation.EntityId is not { } assetId)
        {
            await operations.MarkFailedTerminalAsync(operation.Id, "The queued operation has no media asset target.", ct);
            return;
        }

        try
        {
            await operations.UpdateStageAsync(operation.Id, MediaOperationStage.ProviderLookup, 10, ct);
            var kind = operation.OperationType == MediaOperationType.TextTrackLyrics
                ? TextTrackKind.Lyrics
                : TextTrackKind.Subtitles;
            // The person's audience was kept with the request, so the work is judged by who asked even after a restart.
            // Only a column that was never filled in means the Library asked itself; a value that cannot be read is treated as the narrowest.
            PluginAudience? requester = operation.RequestedAudience is null
                ? null
                : PluginDeclarationRules.TryParseAudience(operation.RequestedAudience, out var asked) ? asked : PluginAudience.Everyone;
            var result = await textTracks.EnrichAsync(assetId, kind, ct, requester).ConfigureAwait(false);
            switch (result.Status)
            {
                case "Updated":
                case "PreservedUserOwned":
                    await operations.MarkSucceededAsync(operation.Id, result.Message, ct);
                    break;
                case "ProviderUnavailable":
                    await RetryOrGiveUpAsync(operation, result.Message, ct);
                    break;
                case "AuthenticationRequired":
                case "Disabled":
                case "ExternalLookupBlocked":
                case "NotAllowed":
                    await operations.MarkBlockedAsync(operation.Id, result.Message, ct);
                    break;
                case "AssetMissing":
                    await operations.MarkFailedTerminalAsync(operation.Id, result.Message, ct);
                    break;
                default:
                    await operations.MarkNoResultAsync(operation.Id, result.Status, result.Message, ct);
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Text-track refresh {OperationId} failed for asset {AssetId}.", operation.Id, assetId);
            await operations.MarkFailedRetryableAsync(
                operation.Id,
                ex.Message,
                DateTimeOffset.UtcNow.AddMinutes(5),
                ct);
        }
    }

    // The poison count only grows on a failed attempt (a lease or a restart does not add to it), so it is the number of attempts already
    // made and this one is the next. The panel shows retry_waiting and dead_lettered alike as "could not be looked up right now".
    private async Task RetryOrGiveUpAsync(MediaOperation operation, string error, CancellationToken ct)
    {
        var attemptsMade = operation.PoisonAttemptCount + 1;
        if (attemptsMade >= MaxUnreachableAttempts)
        {
            logger.LogInformation(
                "Text-track lookup {OperationId} for asset {AssetId} gave up after {Attempts} attempts because its source could not be reached.",
                operation.Id, operation.EntityId, attemptsMade);
            await operations.MarkDeadLetteredAsync(operation.Id, error, ct);
            return;
        }

        var delay = UnreachableRetryDelays[Math.Min(attemptsMade - 1, UnreachableRetryDelays.Length - 1)];
        await operations.MarkFailedRetryableAsync(operation.Id, error, DateTimeOffset.UtcNow.Add(delay), ct);
    }
}
