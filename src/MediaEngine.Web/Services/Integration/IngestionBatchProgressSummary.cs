using MediaEngine.Contracts.Realtime;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// Whole-pipeline progress for the Live Ingestion header, derived from the batch progress event.
/// Two equally weighted halves are averaged: file intake (registered files / total files) and
/// identity/enrichment (items settled / items to settle). Intake finishes quickly, so on its own it
/// would push the bar to ~done while most of the work (provider matching, hydration, enrichment) remains.
/// </summary>
public sealed record IngestionBatchProgressSummary(double Percent, int ItemsReady, int ItemsTotal)
{
    public static IngestionBatchProgressSummary From(BatchProgressEvent batch)
    {
        var total = Math.Max(0, batch.FilesTotal);
        if (total == 0)
        {
            return new IngestionBatchProgressSummary(0, 0, 0);
        }

        var intake = Math.Clamp(batch.FilesProcessed, 0, total) / (double)total;

        // Once every file is registered the engine counts identity work in its own units (an audiobook
        // can register many files but produce a single identity job), so switch to those units then.
        var intakeComplete = batch.FilesProcessed >= total;
        var settledFiles = batch.FilesIdentified + batch.FilesReview + batch.FilesNoMatch + batch.FilesFailed;
        var identity = intakeComplete && batch.WorkUnitsTotal > 0
            ? Math.Clamp(batch.WorkUnitsCompleted, 0, batch.WorkUnitsTotal) / (double)batch.WorkUnitsTotal
            : Math.Clamp(settledFiles, 0, total) / (double)total;

        var percent = (intake + identity) / 2d * 100d;
        percent = batch.IsComplete ? 100d : Math.Min(percent, 99d);
        return new IngestionBatchProgressSummary(percent, Math.Clamp(batch.FilesIdentified, 0, total), total);
    }
}
