using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion.Services;

public sealed class IngestionLogScribe : IIngestionLogScribe
{
    private readonly IIngestionLogRepository _ingestionLog;
    private readonly IEventPublisher _publisher;
    private readonly ILogger<IngestionLogScribe> _logger;

    public IngestionLogScribe(
        IIngestionLogRepository ingestionLog,
        IEventPublisher publisher,
        ILogger<IngestionLogScribe> logger)
    {
        _ingestionLog = ingestionLog;
        _publisher = publisher;
        _logger = logger;
    }

    public async Task<Guid> InsertDetectedAsync(
        IngestionCandidate candidate,
        CancellationToken ct = default,
        bool quiet = false)
    {
        var logEntryId = Guid.NewGuid();

        try
        {
            await _ingestionLog.InsertAsync(new IngestionLogEntry
            {
                Id = logEntryId,
                FilePath = candidate.Path,
                Status = "detected",
            }, ct).ConfigureAwait(false);

            await PublishProgressAsync(candidate, logEntryId, "detected", 5, false, ct, quiet: quiet)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Ingestion log insert failed for {Path} - continuing", candidate.Path);
        }

        return logEntryId;
    }

    public async Task UpdateStatusAsync(
        IngestionCandidate candidate,
        Guid logEntryId,
        string status,
        int progressPercent,
        bool isTerminal,
        CancellationToken ct = default,
        string? contentHash = null,
        string? mediaType = null,
        double? confidenceScore = null,
        string? detectedTitle = null,
        string? normalizedTitle = null,
        string? wikidataQid = null,
        Guid? mediaAssetId = null,
        string? errorDetail = null,
        string? title = null,
        bool quiet = false)
    {
        try
        {
            await _ingestionLog.UpdateStatusAsync(
                logEntryId,
                status,
                contentHash,
                mediaType,
                confidenceScore,
                detectedTitle,
                normalizedTitle,
                wikidataQid,
                mediaAssetId,
                errorDetail,
                ct).ConfigureAwait(false);

            await PublishProgressAsync(
                candidate,
                logEntryId,
                status,
                progressPercent,
                isTerminal,
                ct,
                mediaAssetId,
                title ?? detectedTitle,
                mediaType,
                quiet).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Ingestion log update failed for {Path} - continuing", candidate.Path);
        }
    }

    public async Task RecordTerminalAsync(
        IngestionCandidate candidate,
        string status,
        string detail,
        CancellationToken ct = default,
        bool quiet = false)
    {
        try
        {
            var logEntryId = Guid.NewGuid();
            await _ingestionLog.InsertAsync(new IngestionLogEntry
            {
                Id = logEntryId,
                FilePath = candidate.Path,
                Status = status,
                ErrorDetail = detail,
            }, ct).ConfigureAwait(false);

            await PublishProgressAsync(candidate, logEntryId, status, 100, true, ct, quiet: quiet)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Terminal ingestion log write failed for {Path}", candidate.Path);
        }
    }

    public Task PublishProgressAsync(
        IngestionCandidate candidate,
        Guid logEntryId,
        string stage,
        int progressPercent,
        bool isTerminal,
        CancellationToken ct = default,
        Guid? mediaAssetId = null,
        string? title = null,
        string? mediaType = null,
        bool quiet = false)
    {
        // A file in a Vault library is never announced to open screens (Videos spec D17, D32).
        if (quiet)
        {
            return Task.CompletedTask;
        }

        return SafePublishAsync(
            SignalREvents.IngestionItemProgress,
            new IngestionItemProgressEvent(
                logEntryId,
                mediaAssetId,
                candidate.Path,
                Path.GetFileName(candidate.Path),
                stage,
                ResolveItemStageOrder(stage),
                Math.Clamp(progressPercent, 0, 100),
                isTerminal,
                title,
                mediaType),
            ct);
    }

    private async Task SafePublishAsync<TPayload>(
        string eventName,
        TPayload payload,
        CancellationToken ct)
        where TPayload : notnull
    {
        try
        {
            await _publisher.PublishAsync(eventName, payload, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Ingestion progress publish failed for '{Event}' - pipeline continues", eventName);
        }
    }

    private static int ResolveItemStageOrder(string stage) => stage switch
    {
        "detected" => 0,
        "hashing" => 1,
        "processed" => 2,
        "scored" => 3,
        "registered" => 4,
        "queued_identity" => 5,
        "hydrating" => 6,
        "complete" => 7,
        "needs_review" => 7,
        "duplicate" => 7,
        "same_path_redetected" => 7,
        "missing" => 7,
        "skipped_non_media" => 7,
        "failed" => 7,
        _ => 0,
    };
}
