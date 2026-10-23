using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion.Services;

/// <summary>Marks a file Unmatched while it is being ingested (Library setup spec D34), with its next lookup set from <c>unmatched_retry_days</c>.</summary>
public sealed class UnmatchedFileMarker(
    IUnmatchedAssetRepository unmatched,
    IConfigurationLoader configuration,
    ILogger<UnmatchedFileMarker> logger,
    IFolderFirstCheckRepository? firstChecks = null)
{
    /// <summary>
    /// Marks a YouTube library file with no video ID Unmatched (<c>no_youtube_id</c>). Best effort: the file is already registered and plays,
    /// so a failure is logged and never fails its ingestion.
    /// </summary>
    public async Task MarkNoYoutubeIdAsync(Guid assetId, string libraryId, string path, CancellationToken ct)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var days = configuration.LoadMaintenance().UnmatchedRetryDays;
            await unmatched.MarkUnmatchedAsync(
                assetId,
                libraryId,
                UnmatchedReasons.NoYoutubeId,
                now,
                UnmatchedRetryLimits.NextRetryAt(now, days),
                ct).ConfigureAwait(false);

            // A file with no video ID has its outcome as soon as it is registered, and counts as Unmatched in its folder's first check.
            if (firstChecks is not null)
            {
                await firstChecks.RecordOutcomeAsync(assetId, FolderFirstCheckOutcomes.Unmatched, ct).ConfigureAwait(false);
            }

            logger.LogInformation(
                "{Path} is in a YouTube library but has no YouTube ID: marked Unmatched and left in place ({AssetId12})",
                path,
                assetId.ToString("N")[..12]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not mark {Path} Unmatched; it stays registered without the mark", path);
        }
    }
}
