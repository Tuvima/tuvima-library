using System.Text.Json;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Capabilities;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Ingestion;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Detection;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Pipeline;
using MediaEngine.Ingestion.Services;
using MediaEngine.Intelligence.Contracts;
using MediaEngine.Intelligence.Models;
using MediaEngine.Processors;
using MediaEngine.Processors.Contracts;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Helpers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaEngine.Ingestion;

public sealed partial class IngestionEngine
{
    // Finds the Extras beside titles (Extras spec); null when the host registers no scanner.
    private readonly IItemExtrasScanner? _extrasScanner;

    // =========================================================================
    // Live pipeline
    // =========================================================================

    private const string CoverSourceEmbedded = "embedded";
    private const string CoverSourceFolder = "folder";

    private async Task ProcessCandidateAsync(IngestionCandidate candidate, CancellationToken ct)
    {
        try
        {
            await ProcessCandidateCoreAsync(candidate, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RecordUnhandledCandidateFailureAsync(candidate, ex, ct).ConfigureAwait(false);
        }
        finally
        {
            ReleaseActivePath(candidate.Path);
            _concurrencyGuard.Cleanup();
        }
    }

    /// <summary>
    /// Records a file's technical facts. A missing media tool never stops the file: it waits (the writer keeps the hold)
    /// and every later step runs as normal. A file the working tool cannot read goes to Needs Review, and still carries on
    /// to its identity job.
    /// </summary>
    private async Task InspectPlaybackAsync(MediaAsset asset, CancellationToken ct)
    {
        if (_playbackInspection is null)
        {
            return;
        }

        var outcome = await _playbackInspection.InspectAsync(asset, ct).ConfigureAwait(false);
        if (outcome != PlaybackInspectionOutcome.Failed)
        {
            return;
        }

        if (await IsVaultEntityAsync(asset.Id, ct).ConfigureAwait(false))
        {
            // A Vault file that cannot be read stays as it is; nothing is queued for it (Videos spec D32).
            return;
        }

        if (_inspectionReviews is not null)
        {
            await _inspectionReviews.RecordFailureAsync(asset.Id, ct).ConfigureAwait(false);
        }
        else
        {
            await CreateIngestionReviewItemAsync(
                asset.Id,
                ReviewTrigger.MediaInspectionFailed,
                0.0,
                MediaInspectionReviewRecorder.UnreadableFileDetail,
                ct).ConfigureAwait(false);
        }
    }

    private async Task ProcessCandidateCoreAsync(IngestionCandidate candidate, CancellationToken ct)
    {
        // A file being moved by Library is the move lane's to look after: it is never marked missing, and its new place is never
        // taken for a new file.
        if (await IsMoveInProgressAsync(candidate.Path, candidate.OldPath, ct).ConfigureAwait(false))
        {
            _logger.LogInformation(
                "Ignored {EventType} for \"{FileName}\": Library is moving it and will bring its record up to date.",
                candidate.EventType, Path.GetFileName(candidate.Path));
            return;
        }

        var context = new IngestionPipelineContext(candidate);
        context.Library = candidate.Intake?.HasDestinationHint == true
            ? _libraryFolderResolver?.ResolveById(candidate.Intake.DestinationLibraryId!)
            : _libraryFolderResolver?.ResolveForPath(candidate.Path);

        // A file in one of the Import folder's per-type folders belongs to that type's library from the start.
        if (context.Library is null && _importFolders?.ResolveTypePrior(candidate.Path) is { } importType)
        {
            context.Library = await ResolveImportLibraryAsync(importType, ct).ConfigureAwait(false);
        }

        if (candidate.Intake?.HasDestinationHint == true && context.Library is null)
        {
            throw new InvalidOperationException(
                $"Direct intake destination library '{candidate.Intake.DestinationLibraryId}' is not configured.");
        }

        // A file in a Vault library announces nothing while it is processed (Videos spec D17, D32).
        context.IsVault = _vaultHook is not null
            && await _vaultHook.IsVaultLibraryAsync(context.Library?.Id, ct).ConfigureAwait(false);
        if (context.IsVault)
        {
            _vaultCandidatePaths[candidate.Path] = 0;
        }

        // Everything logged while a Vault file is processed carries a marker the log writers drop (Videos spec D17, D33), so a log
        // call that names the file (even by its bare name) in any stage never reaches the console or the technical log.
        using var vaultLogScope = context.IsVault
            ? _logger.BeginScope(new Dictionary<string, object> { [VaultLocationGuard.LogScopeProperty] = true })
            : null;

        // A document, picture or clip beside a title's own files is an Extra of that title (Extras spec D5): it is stored by the
        // Extras scan, never enters the episode pipeline, and never goes to Needs Review.
        if (_extrasScanner is not null && !context.IsVault)
        {
            if (candidate.EventType == FileEventType.Deleted)
            {
                _extrasScanner.NoteFile(candidate.Path);
            }
            else if (await _extrasScanner.ClaimsFileAsync(candidate.Path, context.Library, ct).ConfigureAwait(false))
            {
                _logger.LogInformation("\"{FileName}\" is an Extra of its title; it is kept with the title, not ingested", Path.GetFileName(candidate.Path));
                return;
            }
        }

        try
        {
            foreach (var stage in _ingestionStages)
            {
                await stage.ExecuteAsync(context, ct).ConfigureAwait(false);
                if (context.IsComplete)
                {
                    return;
                }
            }
        }
        finally
        {
            _vaultCandidatePaths.TryRemove(candidate.Path, out _);
            if (context.HashLock is not null && context.Hash is not null)
            {
                context.HashLock.Release();
                _concurrencyGuard.ReleaseHashLock(context.Hash.Hex);
            }
        }
    }
    private async Task RunSettleAndDetectStageAsync(IngestionPipelineContext context, CancellationToken ct)
    {
        var candidate = context.Candidate;
        var durableOperation = context.DurableOperation = await EnsureIngestionOperationAsync(candidate, MediaOperationStage.Queued, ct).ConfigureAwait(false);
        await UpdateOperationStageAsync(durableOperation, MediaOperationStage.Queued, 0, "Queued for ingestion.", ct).ConfigureAwait(false);

        // Step 2: skip failed probe candidates. A file that is gone is not a lock problem to retry; the missing-file step
        // below closes it.
        if (candidate.IsFailed && File.Exists(candidate.Path))
        {
            var reason = candidate.FailureReason ?? "Lock probe exhausted";
            var attempt = durableOperation?.AttemptCount ?? 0;
            var maxAttempts = Math.Max(0, _options.LockProbeRetryMaxAttempts);
            if (attempt < maxAttempts)
            {
                var nextRetryAt = DateTimeOffset.UtcNow.Add(ComputeLockProbeRetryDelay(attempt));
                _logger.LogWarning(
                    "Lock probe failed for \"{FileName}\" ({Attempt}/{MaxAttempts}); retrying at {NextRetryAt}: {Reason}",
                    Path.GetFileName(candidate.Path),
                    attempt + 1,
                    maxAttempts,
                    nextRetryAt,
                    reason);

                await MarkRetryableOperationAsync(durableOperation, reason, nextRetryAt, ct).ConfigureAwait(false);
                ScheduleLockProbeRetry(candidate, nextRetryAt);
                context.Complete();
                return;
            }

            _logger.LogWarning(
                "Lock probe retry cap reached for \"{FileName}\"; leaving operation interrupted for the next scan/manual retry: {Reason}",
                Path.GetFileName(candidate.Path), reason);

            await SafePublishAsync(SignalREvents.IngestionFailed, new IngestionFailedEvent(
                candidate.Path,
                reason,
                DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
            await MarkInterruptedOperationAsync(durableOperation, reason, ct).ConfigureAwait(false);
            context.Complete();
            return;
        }

        // Step 3: handle deletion.
        if (candidate.EventType == FileEventType.Deleted)
        {
            await HandleDeletedAsync(candidate, ct).ConfigureAwait(false);
            await NoResultOperationAsync(durableOperation, "File deletion event handled.", ct).ConfigureAwait(false);
            context.Complete();
            return;
        }

        if (!File.Exists(candidate.Path))
        {
            var reason = VanishedFileReason;
            _logger.LogWarning("Ingestion skipped - file missing: \"{FileName}\"", Path.GetFileName(candidate.Path));
            await _ingestionLogScribe.RecordTerminalAsync(
                candidate,
                "missing",
                reason,
                ct,
                quiet: await IsVaultPathAsync(candidate, ct).ConfigureAwait(false)).ConfigureAwait(false);
            await NoResultOperationAsync(durableOperation, reason, ct).ConfigureAwait(false);

            // The job opened when the file was first noticed has a different key (it includes the file's size and time,
            // which are gone now), so it would otherwise stay waiting for ever.
            await CloseWaitingFileOperationAsync(candidate.Path, ct).ConfigureAwait(false);
            context.Complete();
            return;
        }

        _logger.LogInformation("Detected: \"{FileName}\" — queued for ingestion", Path.GetFileName(candidate.Path));

        await SafePublishAsync(SignalREvents.IngestionStarted, new IngestionStartedEvent(
            candidate.Path, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);

        // Lifecycle log: create entry at detection.
        var logEntryId = context.LogEntryId = await _ingestionLogScribe.InsertDetectedAsync(candidate, ct, quiet: context.IsVault)
            .ConfigureAwait(false);

        context.PipelineStopwatch = System.Diagnostics.Stopwatch.StartNew();

    }
    private async Task RunHashAndDedupeStageAsync(IngestionPipelineContext context, CancellationToken ct)
    {
        var candidate = context.Candidate;
        var durableOperation = context.DurableOperation;
        var logEntryId = context.LogEntryId;
        // Step 4: hash.
        await UpdateOperationStageAsync(durableOperation, MediaOperationStage.Hashing, 10, "Hashing file.", ct).ConfigureAwait(false);
        var hashLookup = await ComputeHashWithCacheAsync(candidate.Path, ct).ConfigureAwait(false);
        var hash = context.Hash = hashLookup.Hash;
        await UpdateOperationStageAsync(
            durableOperation,
            MediaOperationStage.Hashing,
            15,
            hashLookup.CacheHit ? "Hash cache hit." : "Hash complete.",
            ct,
            new { hash = hash.Hex, bytes = hash.FileSize, cache_hit = hashLookup.CacheHit }).ConfigureAwait(false);

        _logger.LogInformation(
            "Fingerprinted \"{FileName}\" — sha256={HashPrefix}… ({SizeKB:F1} KB)",
            Path.GetFileName(candidate.Path), hash.Hex[..8], hash.FileSize / 1024.0);

        await SafePublishAsync(SignalREvents.IngestionHashed, new IngestionHashedEvent(
            candidate.Path, hash.Hex, hash.FileSize, hash.Elapsed), ct).ConfigureAwait(false);

        await SafeActivityLogAsync(new Domain.Entities.SystemActivityEntry
        {
            ActionType = Domain.Constants.SystemActionType.FileHashed,
            EntityType = "MediaAsset",
            Detail = $"Fingerprinted {Path.GetFileName(candidate.Path)}: {hash.Hex[..12]}... ({hash.FileSize / 1024.0:F1} KB)",
            ChangesJson = JsonSerializer.Serialize(new
            {
                hash_prefix = hash.Hex[..12],
                full_hash = hash.Hex,
                file_size_kb = Math.Round(hash.FileSize / 1024.0, 1),
                elapsed_ms = (long)hash.Elapsed.TotalMilliseconds,
                filename = Path.GetFileName(candidate.Path),
            }),
        }, ct).ConfigureAwait(false);

        // Lifecycle log: hashing complete.
        await _ingestionLogScribe.UpdateStatusAsync(
            candidate,
            logEntryId,
            "hashing",
            15,
            false,
            ct,
            contentHash: hash.Hex,
            quiet: context.IsVault).ConfigureAwait(false);

        // Step 5: duplicate check.
        // If the file is already ingested but still sitting in the Watch Folder
        // (e.g. it scored below the confidence gate on first pass, or LibraryRoot
        // was not configured at that time), attempt to organize it now — metadata
        // may have been enriched by external providers since the initial scan.
        // Hash lock scope: covers duplicate check, DB insert, and organization.
        // Prevents race conditions when identical files arrive simultaneously.
        // Defense-in-depth: DB uses INSERT OR IGNORE on content_hash UNIQUE constraint.
        var hashLock = context.HashLock = _concurrencyGuard.GetHashLock(hash.Hex);
        await hashLock.WaitAsync(ct).ConfigureAwait(false);
        var duplicate = await _duplicateResolver.ResolveAsync(candidate, hash.Hex, ct)
            .ConfigureAwait(false);
        var existing = duplicate.ExistingAsset;
        if (existing is not null)
        {
            // If the original file no longer exists, clean up the orphaned record
            // and all associated filesystem artifacts, then fall through to process
            // this file as a brand new asset.
            if (duplicate.Kind == DuplicateResolutionKind.OrphanedExisting)
            {
                await CleanStagedAssetAsync(existing, ct).ConfigureAwait(false);
                // Fall through — process as new asset below.
            }
            else
            {
                if (duplicate.Kind == DuplicateResolutionKind.DuplicateDifferentPath)
                {
                    // True duplicate: a different source file has the same content hash
                    // and the original is still on disk. Log it, delete the duplicate
                    // from the watch folder, and return — do NOT move it into the library.
                    _logger.LogInformation(
                        "True duplicate detected: {CandidatePath} has same hash as existing {ExistingPath} — deleting duplicate from watch folder",
                        candidate.Path, existing.FilePathRoot);

                    await SafeActivityLogAsync(new Domain.Entities.SystemActivityEntry
                    {
                        ActionType = Domain.Constants.SystemActionType.DuplicateSkipped,
                        EntityId = existing.Id,
                        EntityType = "MediaAsset",
                        Detail = $"Duplicate skipped: {Path.GetFileName(candidate.Path)} (identical to {Path.GetFileName(existing.FilePathRoot)})",
                    }, ct).ConfigureAwait(false);

                    try
                    {
                        await _ingestionLog.UpdateStatusAsync(
                            logEntryId,
                            "duplicate",
                            contentHash: hash.Hex,
                            mediaAssetId: existing.Id,
                            errorDetail: $"Duplicate of existing file: {existing.FilePathRoot}",
                            ct: ct).ConfigureAwait(false);
                        await PublishItemProgressAsync(
                            candidate,
                            logEntryId,
                            "duplicate",
                            100,
                            true,
                            ct,
                            existing.Id).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Ingestion duplicate log update failed - continuing");
                    }

                    var retryImportCopyLater = false;
                    try
                    {
                        if (_importFolders?.IsInImportFolder(candidate.Path) == true)
                        {
                            // The Import folder follows the same global duplicate rule, and only its own copy is ever removed.
                            retryImportCopyLater = !await HandleImportDuplicateAsync(candidate, existing, hash.Hex, ct)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            var resolvedSource = _libraryFolderResolver?.ResolveSourceForPath(candidate.Path);
                            // Never remove a duplicate unless the copy that stays is really there, and never from a folder that is
                            // being merged into another one (its originals are the merge's to keep or remove, not this rule's).
                            var deleteAllowed = resolvedSource is not null
                                && resolvedSource.Source.MergeState == MediaEngine.Domain.Configuration.LibrarySourceMergeStates.None
                                && File.Exists(existing.FilePathRoot)
                                && !string.Equals(Path.GetFullPath(candidate.Path), Path.GetFullPath(existing.FilePathRoot), StringComparison.OrdinalIgnoreCase)
                                && !MediaEngine.Ingestion.Organise.FileIdentity.AreSameFile(candidate.Path, existing.FilePathRoot)
                                && MediaEngine.Domain.Configuration.LibraryDuplicatePolicies.DeletesIncomingDuplicate(resolvedSource.Library.ResolveDuplicatePolicy(resolvedSource.Source, _options.DuplicatePolicy))
                                && _sourceMutationPolicyGate?.Evaluate(new SourceMutationRequest
                                {
                                    Source = FileSourceMutationPolicyFactory.Create(
                                        resolvedSource.Library,
                                        resolvedSource.Source,
                                        allowDelete: true),
                                    Mutation = SourceMutationKind.Delete,
                                    Path = candidate.Path,
                                }).Allowed == true;

                            // The copy that stays is read again right now; the stored fingerprint alone never authorizes a delete.
                            if (deleteAllowed
                                && !await KeptCopyStillMatchesAsync(candidate.Path, existing.FilePathRoot, hash.Hex, ct).ConfigureAwait(false))
                            {
                                deleteAllowed = false;
                                _logger.LogWarning(
                                    "Exact duplicate {Path} was kept: the copy at {Kept} could not be confirmed as identical",
                                    candidate.Path, existing.FilePathRoot);
                            }

                            if (deleteAllowed && File.Exists(candidate.Path))
                            {
                                File.Delete(candidate.Path);
                            }

                            if (!deleteAllowed)
                            {
                                _logger.LogInformation(
                                    "Exact duplicate was indexed without deleting {Path}; its source policy or the copy that stays does not authorize deletion",
                                    candidate.Path);
                            }

                            await DeleteHashCacheEntryAsync(candidate.Path, ct).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Could not delete duplicate file {Path}", candidate.Path);
                    }

                    await AttachOperationAssetAsync(durableOperation, existing.Id, ct).ConfigureAwait(false);
                    if (retryImportCopyLater)
                    {
                        // Kept under Keep both but not filed yet (its destination isn't ready): the next pass tries again.
                        await MarkInterruptedOperationAsync(
                            durableOperation,
                            "Waiting for the destination folder before filing this copy.",
                            ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await CompleteOperationAsync(durableOperation, "duplicate", ct).ConfigureAwait(false);
                    }

                    context.Complete();
                    return;
                }

                // Same-path re-detection: attempt re-organization (file may have been
                // enriched since first scan).
                var contentChanged = !string.Equals(existing.ContentHash, hash.Hex, StringComparison.OrdinalIgnoreCase);
                // Registration and identity enqueue are separate durable writes. A restart
                // between them must repair the missing job even when the file is unchanged.
                var missingIdentityJob = context.Library?.BypassesExternalIdentity != true
                    && await _identityJobRepo.GetByEntityAsync(existing.Id, ct).ConfigureAwait(false) is null;
                var metadataRefreshed = false;
                if (contentChanged || missingIdentityJob)
                {
                    metadataRefreshed = await RefreshExistingAssetMetadataAsync(
                        existing,
                        candidate.Path,
                        ct,
                        queueIdentityRefresh: context.Library?.BypassesExternalIdentity != true).ConfigureAwait(false);

                    if (missingIdentityJob && !metadataRefreshed)
                    {
                        throw new InvalidOperationException($"Could not recover identity enqueue for registered asset {existing.Id}.");
                    }

                    if (metadataRefreshed)
                    {
                        var hashUpdated = await _assetRepo.UpdateContentHashAsync(existing.Id, hash.Hex, ct)
                            .ConfigureAwait(false);
                        if (hashUpdated)
                        {
                            existing.ContentHash = hash.Hex;
                            await InspectPlaybackAsync(existing, ct).ConfigureAwait(false);
                            _logger.LogInformation(
                                "Re-read local metadata and updated content hash for same-path asset {AssetId}: {OldHash} -> {NewHash}",
                                existing.Id,
                                existing.ContentHash.Length >= 12 ? existing.ContentHash[..12] : existing.ContentHash,
                                hash.Hex[..12]);
                        }
                        else
                        {
                            _logger.LogWarning(
                                "Could not update content hash for same-path asset {AssetId}; hash {Hash} already belongs to another asset",
                                existing.Id,
                                hash.Hex[..12]);
                        }
                    }
                }

                await TryReorganizeExistingAsync(existing, candidate.Path, ct)
                    .ConfigureAwait(false);
                try
                {
                    await _ingestionLog.UpdateStatusAsync(
                        logEntryId,
                        "same_path_redetected",
                        contentHash: hash.Hex,
                        mediaAssetId: existing.Id,
                        errorDetail: missingIdentityJob && metadataRefreshed
                            ? "Recovered the missing identity job for an already registered file."
                            : metadataRefreshed
                            ? "The tracked file changed in place. Local metadata was re-read and identity enrichment was queued."
                            : contentChanged
                                ? "The tracked file changed in place, but local metadata could not be re-read. The previous metadata and hash were preserved for retry."
                                : "The file is already tracked at this path, so no duplicate library item was created.",
                        ct: ct).ConfigureAwait(false);
                    await PublishItemProgressAsync(
                        candidate,
                        logEntryId,
                        "same_path_redetected",
                        100,
                        true,
                        ct,
                        existing.Id).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Ingestion same-path log update failed - continuing");
                }
                await AttachOperationAssetAsync(durableOperation, existing.Id, ct).ConfigureAwait(false);
                // Only a file the Library already had, unchanged, is "seen again". A file replaced in place with new
                // content, or one whose identity job was put back, did real work and counts as found.
                var summary = missingIdentityJob
                    ? IngestionResultSummaries.IdentityRecovered
                    : contentChanged
                        ? IngestionResultSummaries.ContentChanged
                        : IngestionResultSummaries.SamePathRedetected;
                await CompleteOperationAsync(durableOperation, summary, ct).ConfigureAwait(false);
                context.Complete();
                return;
            }
        }

    }
    private async Task RunScoreAndIdentifyStageAsync(IngestionPipelineContext context, CancellationToken ct)
    {
        var candidate = context.Candidate;
        var durableOperation = context.DurableOperation;
        var logEntryId = context.LogEntryId;
        var hash = context.Hash!;
        var result = context.ProcessorResult!;
        if (ResolveAudiobookHintRoot(context.Library, candidate.Path) is { } audiobookSource)
        {
            result = context.ProcessorResult = Services.AudiobookFolderHints.Apply(result, audiobookSource);
        }
        if (ResolveVideosRoot(context.Library, candidate.Path) is { } videosRoot)
        {
            result = context.ProcessorResult = _videoReader!.Apply(result, videosRoot);
            // Stage 1 lookup (D7): only the YouTube ID leaves the machine, and only in a YouTube library (or a library with no kind
            // whose own switch is on). A plain kind (Other videos) never asks, whatever its youtube_lookup field says (Library setup D38).
            if (_videoLookup is not null
                && context.Library?.AsksYoutube == true
                // YouTube is never asked about a Vault file, whatever the library's own switch says (Videos spec D21, D26).
                && (_audienceGate is null || await _audienceGate.MayAskAboutLibraryAsync("youtube", context.Library.Id, ct).ConfigureAwait(false))
                && result.Claims.FirstOrDefault(claim => claim.Key == MetadataFieldConstants.YoutubeVideoId)?.Value is { } youtubeId
                && await _videoLookup.LookupAsync(youtubeId, ct).ConfigureAwait(false) is { } online)
            {
                result = context.ProcessorResult = _videoReader.Merge(result, online);
                context.LookupThumbnail = online.Thumbnail is { Length: > 0 } && online.ThumbnailMimeType is { } thumbnailMime
                    ? new FolderCoverImage(online.Thumbnail, thumbnailMime, "youtube")
                    : null;
            }
        }
        // Step 8: convert claims.
        var assetId = context.AssetId = Guid.NewGuid();
        var claims = context.Claims = BuildClaims(assetId, result);

        // History: file detected.
        await SafeActivityLogAsync(new SystemActivityEntry
        {
            ActionType = SystemActionType.FileDetected,
            EntityId = assetId,
            Detail = $"File detected in watch folder: {Path.GetFileName(candidate.Path)}",
        }, ct).ConfigureAwait(false);

        // History: metadata extracted.
        await SafeActivityLogAsync(new SystemActivityEntry
        {
            ActionType = "MetadataExtracted",
            EntityId = assetId,
            Detail = $"Metadata extracted — {result.Claims.Count} fields found",
        }, ct).ConfigureAwait(false);

        // -- Timeline: Stage 0 file_scanned event ----------------------------
        try
        {
            await _timelineRepo.InsertEventAsync(new EntityEvent
            {
                EntityId = assetId,
                EntityType = "Work",
                EventType = "file_scanned",
                Stage = 0,
                Trigger = "ingestion",
                ProviderId = LocalProcessorProviderId.ToString(),
                ProviderName = "local_processor",
                Confidence = result.Claims.Count > 0 ? 1.0 : 0.0,
                Detail = $"File scanned: {Path.GetFileName(candidate.Path)} — {result.Claims.Count} fields extracted ({result.DetectedType})",
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Failed to write Stage 0 timeline event for \"{FileName}\" (asset {Id})",
                Path.GetFileName(candidate.Path), assetId);
        }

        await UpdateOperationStageAsync(durableOperation, MediaOperationStage.Scoring, 45, "Scoring extracted metadata.", ct).ConfigureAwait(false);

        // Step 9: score.
        // CategoryConfidencePrior: currently 0.0 (single WatchDirectory = general catch-all).
        // When Library Folders (config/libraries.json) are implemented, category-specific
        // folders will set +0.10 and multi-type folders +0.05.
        var scoringContext = new ScoringContext
        {
            EntityId = assetId,
            Claims = claims,
            ProviderWeights = new Dictionary<Guid, double>
            { [LocalProcessorProviderId] = 1.0 },
            Configuration = _scoringConfig,
            CategoryConfidencePrior = candidate.CategoryConfidencePrior,
            DetectedMediaType = result.DetectedType,
        };

        var scored = context.Scored = await _scorer.ScoreEntityAsync(scoringContext, ct).ConfigureAwait(false);

        // History: confidence scored.
        await SafeActivityLogAsync(new SystemActivityEntry
        {
            ActionType = "ConfidenceScored",
            EntityId = assetId,
            Detail = $"Confidence: {scored.OverallConfidence:P0} — Score: {scored.OverallConfidence:F2}",
        }, ct).ConfigureAwait(false);

        // Phase 9: persist claims (append-only; enables re-scoring on weight changes).
        await _claimRepo.InsertBatchAsync(claims, ct).ConfigureAwait(false);

        await SafeActivityLogAsync(new Domain.Entities.SystemActivityEntry
        {
            ActionType = Domain.Constants.SystemActionType.FileScored,
            EntityId = assetId,
            EntityType = "MediaAsset",
            Detail = $"Score: {scored.OverallConfidence:P0} across {scored.FieldScores.Count} fields",
            ChangesJson = JsonSerializer.Serialize(new
            {
                confidence = scored.OverallConfidence,
                field_count = scored.FieldScores.Count,
                conflicted = scored.FieldScores.Count(f => f.IsConflicted),
                fields = scored.FieldScores
                    .Where(f => !string.IsNullOrEmpty(f.WinningValue))
                    .Select(f => new { field = f.Key, value = f.WinningValue, confidence = f.Confidence, conflicted = f.IsConflicted })
                    .ToList(),
            }),
        }, ct).ConfigureAwait(false);

        // Lifecycle log: scored.
        var detectedTitle = scored.FieldScores
            .FirstOrDefault(f => f.Key.Equals(MetadataFieldConstants.Title, StringComparison.OrdinalIgnoreCase))?.WinningValue;
        try
        {
            await _ingestionLog.UpdateStatusAsync(logEntryId, "scored",
            confidenceScore: scored.OverallConfidence,
            detectedTitle: detectedTitle,
            ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Ingestion log update failed — continuing"); }

        await PublishItemProgressAsync(candidate, logEntryId, "scored", 55, false, ct).ConfigureAwait(false);
        await UpdateOperationStageAsync(durableOperation, MediaOperationStage.Scoring, 55, "Scoring complete.", ct, new { confidence = scored.OverallConfidence }).ConfigureAwait(false);

        // Phase 9: persist canonical values (current winning metadata for this asset).
        // Phase B: also persist the IsConflicted flag from the scoring engine so
        // the Dashboard can surface unresolved metadata disagreements.
        var canonicals = scored.FieldScores
            .Where(f => !string.IsNullOrEmpty(f.WinningValue))
            .Select(f => new CanonicalValue
            {
                EntityId = assetId,
                Key = f.Key,
                Value = f.WinningValue!,
                LastScoredAt = scored.ScoredAt,
                IsConflicted = f.IsConflicted,
            })
            .ToList();

        // Step 6a: media type resolution from processor candidates, library folder priors, root-drop caps, and AI advisor fallback.
        var mediaTypeResolution = await _mediaTypeResolver.ResolveAsync(
            candidate.Path,
            result,
            candidate.CategoryConfidencePrior,
            ct).ConfigureAwait(false);

        var resolvedMediaType = context.ResolvedMediaType = mediaTypeResolution.MediaType;

        // A file dropped at the root of the Import folder is attributed to its type's library once the type is known.
        if (_importFolders?.IsInImportFolder(candidate.Path) == true
            && (context.Library is null || !context.Library.MediaTypes.Contains(resolvedMediaType)))
        {
            context.Library = resolvedMediaType == MediaType.Unknown
                ? null
                : await ResolveImportLibraryAsync(resolvedMediaType, ct).ConfigureAwait(false);
        }

        bool mediaTypeIsConflicted = mediaTypeResolution.IsConflicted;
        bool mediaTypeNeedsReview = context.MediaTypeNeedsReview = mediaTypeResolution.NeedsReview;
        candidate.CategoryConfidencePrior = mediaTypeResolution.CategoryConfidencePrior;
        var candidateList = mediaTypeResolution.Candidates.ToList();
        context.MediaTypeCandidates = candidateList;

        // Always persist the resolved media_type as a canonical value so that
        // TryReorganizeExistingAsync (and any future re-score) knows the file type.
        // Without this, re-organization from canonical values loses the media type
        // and defaults to "Other".
        canonicals.Add(new CanonicalValue
        {
            EntityId = assetId,
            Key = MetadataFieldConstants.MediaTypeField,
            Value = resolvedMediaType.ToString(),
            LastScoredAt = scored.ScoredAt,
            IsConflicted = mediaTypeIsConflicted,
        });

        // A file in a Vault library: its picture is kept with the Vault's own artwork and never in the shared artwork tables (WP12).
        context.IsVault = _vaultHook is not null
            && await _vaultHook.IsVaultLibraryAsync(context.Library?.Id, ct).ConfigureAwait(false);

        // Embedded art always wins; the sibling folder image (cover.jpg, folder.jpg, ...) is only a
        // fallback for Books, Audiobooks and Music files that carry no embedded picture.
        var hasEmbeddedCover = result.CoverImage is { Length: > 0 };
        context.FolderCover = hasEmbeddedCover
            ? null
            : FolderCoverImageReader.AppliesTo(resolvedMediaType)
                ? FolderCoverImageReader.TryRead(candidate.Path)
                : resolvedMediaType == MediaType.Videos && ResolveVideosRoot(context.Library, candidate.Path) is { } coverRoot
                    ? FolderCoverImageReader.TryReadForVideo(candidate.Path, coverRoot)
                    : null;

        // Order of covers for a video (D11): the person's own picture, a picture in its folder, the lookup thumbnail, then a
        // good still frame (never a black tile; the pick is cached).
        if (context.FolderCover is null && !hasEmbeddedCover && resolvedMediaType == MediaType.Videos)
        {
            context.FolderCover = context.LookupThumbnail;
        }

        if (context.FolderCover is null && !hasEmbeddedCover && resolvedMediaType == MediaType.Videos && _videoFrames is not null)
        {
            context.FolderCover = await _videoFrames.GetOrCreateAsync(candidate.Path, ct).ConfigureAwait(false);
        }

        if (context.IsVault)
        {
            context.VaultStill = hasEmbeddedCover && result.CoverImage is { } embedded
                ? new FolderCoverImage(embedded, result.CoverImageMimeType ?? "image/jpeg", candidate.Path)
                : context.FolderCover;
            context.FolderCover = null;
        }
        else if (hasEmbeddedCover || context.FolderCover is not null)
        {
            canonicals.Add(new CanonicalValue
            {
                EntityId = assetId,
                Key = MetadataFieldConstants.CoverUrl,
                Value = $"/stream/{assetId}/cover",
                LastScoredAt = scored.ScoredAt,
            });

            canonicals.AddRange(ArtworkCanonicalHelper.CreateFlags(
                assetId,
                coverState: "present",
                coverSource: hasEmbeddedCover ? CoverSourceEmbedded : CoverSourceFolder,
                heroState: "missing",
                lastScoredAt: scored.ScoredAt,
                settled: true));
        }
        else
        {
            canonicals.AddRange(ArtworkCanonicalHelper.CreateFlags(
                assetId,
                coverState: "pending",
                coverSource: null,
                heroState: "missing",
                lastScoredAt: scored.ScoredAt,
                settled: false));
        }

        await _canonicalRepo.UpsertBatchAsync(canonicals, ct).ConfigureAwait(false);
        await PersistLocalArrayFieldsAsync(assetId, claims, clearWhenEmpty: false, ct).ConfigureAwait(false);

        // Create MetadataConflict review item when any canonical value has IsConflicted=true.
        // Conflicts don't block organization — the file proceeds with the best-guess value.
        var conflictedFields = canonicals
            .Where(c => c.IsConflicted && c.Key != MetadataFieldConstants.MediaTypeField) // media_type handled separately
            .Select(c => c.Key)
            .ToList();

        if (conflictedFields.Count > 0 && !context.IsVault)
        {
            await CreateMetadataConflictReviewItemAsync(
                assetId,
                scored.OverallConfidence,
                conflictedFields,
                ct).ConfigureAwait(false);
        }

        // Create AmbiguousMediaType review item when media type confidence is below threshold.
        if (mediaTypeNeedsReview && candidateList.Count > 0 && !context.IsVault)
        {
            var candidatesJson = JsonSerializer.Serialize(
                candidateList.Select(c => new
                {
                    type = c.Type.ToString(),
                    confidence = c.Confidence,
                    reason = c.Reason,
                }),
                JsonSerializerOptions.Default);

            await CreateAmbiguousMediaTypeReviewItemAsync(
                assetId,
                candidateList[0].Confidence,
                candidatesJson,
                candidateList[0].Reason,
                ct).ConfigureAwait(false);
        }

        // Create RootWatchFolder review item when a file was dropped directly into any
        // configured source root with an ambiguous extension.
        if (mediaTypeResolution.RootWatchFolderReview && !context.IsVault)
        {
            await CreateIngestionReviewItemAsync(
                assetId,
                ReviewTrigger.RootWatchFolder,
                candidateList.Count > 0 ? candidateList[0].Confidence : 0.0,
                mediaTypeResolution.RootWatchFolderDetail ?? "File dropped into a source root - please confirm the media type.",
                ct).ConfigureAwait(false);
        }

        // Enrich the candidate with resolved metadata.
        candidate.Metadata = BuildMetadataDict(scored);
        candidate.DetectedMediaType = resolvedMediaType;

        // Step 9b: create Collection ? Work ? Edition chain so the FK on media_assets
        // can be satisfied.  The factory reuses an existing Collection when a matching
        // display name is found; otherwise it creates a fresh chain.
        //
        // Books: siblings in one folder (the EPUB beside the AZW3) must resolve one at a time,
        // from "find the existing Work" through "register the asset" (the lookup reads registered
        // assets), otherwise both create a Work. The folder lock covers exactly that span.
        var folderLock = resolvedMediaType == MediaType.Books
            && Path.GetDirectoryName(candidate.Path) is { Length: > 0 } bookFolder
                ? await _concurrencyGuard.AcquireFolderLockAsync(bookFolder, ct).ConfigureAwait(false)
                : null;
        Guid editionId;
        MediaAsset asset;
        bool inserted;
        using (folderLock)
        {
            editionId = await _chainFactory.EnsureEntityChainAsync(
                resolvedMediaType,
                candidate.Metadata,
                candidate.Path,
                ct).ConfigureAwait(false);

            await SafeActivityLogAsync(new Domain.Entities.SystemActivityEntry
            {
                ActionType = Domain.Constants.SystemActionType.EntityChainCreated,
                EntityId = assetId,
                EntityType = "MediaAsset",
                Detail = $"Catalogue entry created for \"{candidate.Metadata?.GetValueOrDefault(MetadataFieldConstants.Title, "Unknown") ?? "Unknown"}\"",
                ChangesJson = JsonSerializer.Serialize(new
                {
                    title = candidate.Metadata?.GetValueOrDefault(MetadataFieldConstants.Title),
                    author = candidate.Metadata?.GetValueOrDefault(MetadataFieldConstants.Author),
                    media_type = resolvedMediaType.ToString(),
                    edition_id = editionId.ToString(),
                }),
            }, ct).ConfigureAwait(false);

            // Step 10: insert asset.
            asset = new MediaAsset
            {
                Id = assetId,
                EditionId = editionId,
                ContentHash = hash.Hex,
                FilePathRoot = candidate.Path,
                LibraryId = context.Library?.Id,
                Status = AssetStatus.Normal,
            };

            inserted = await _assetRepo.InsertAsync(asset, ct).ConfigureAwait(false);
        }

        if (!inserted)
        {
            // Race: another thread inserted the same hash concurrently.
            _logger.LogDebug("Asset already inserted by concurrent task: {Hash}", hash.Hex[..12]);
            await CompleteOperationAsync(durableOperation, "duplicate_race", ct).ConfigureAwait(false);
            context.Complete();
            return;
        }

        await InspectPlaybackAsync(asset, ct).ConfigureAwait(false);

        if (resolvedMediaType == MediaType.Videos && _videoReader is not null)
        {
            await _videoReader.RecordPlacementAsync(assetId, candidate.Metadata, ct).ConfigureAwait(false);
        }

        if (context.IsVault && _vaultHook is not null && context.Library?.Id is { } vaultLibraryId)
        {
            // The Vault's own path (WP12): the still frame, the lookup and a quiet activity line. It raises no review item, no event and
            // no notification, and a lookup that fails never fails ingestion.
            try
            {
                await _vaultHook.OnFileRegisteredAsync(
                    new VaultRegisteredFile(assetId, vaultLibraryId, candidate.Path, context.VaultStill?.Bytes, context.VaultStill?.MimeType), ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("The Vault step for a newly registered file failed ({Type}); the file stays registered", ex.GetType().Name);
            }
        }

        var resolvedTitle = context.ResolvedTitle = candidate.Metadata?.GetValueOrDefault(MetadataFieldConstants.Title, "Unknown") ?? "Unknown";
        var resolvedAuthor = context.ResolvedAuthor = candidate.Metadata?.GetValueOrDefault(MetadataFieldConstants.Author, string.Empty) ?? string.Empty;

        try
        {
            await _ingestionLog.UpdateStatusAsync(
                logEntryId,
                "registered",
                mediaType: resolvedMediaType.ToString(),
                detectedTitle: resolvedTitle,
                mediaAssetId: assetId,
                ct: ct).ConfigureAwait(false);
            await PublishItemProgressAsync(
                candidate,
                logEntryId,
                "registered",
                70,
                false,
                ct,
                assetId,
                resolvedTitle,
                resolvedMediaType.ToString()).ConfigureAwait(false);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Ingestion log update failed — continuing"); }
        await AttachOperationAssetAsync(durableOperation, assetId, ct).ConfigureAwait(false);
        await UpdateOperationStageAsync(durableOperation, MediaOperationStage.Registered, 70, "Asset registered.", ct, new { asset_id = assetId, media_type = resolvedMediaType.ToString() }).ConfigureAwait(false);
        if (_scoreStageDependencies.CapabilityPlanner is not null)
        {
            await _scoreStageDependencies.CapabilityPlanner.EnsureForAssetAsync(assetId, "asset", resolvedMediaType.ToString(), ct).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Ingested [{Type}] '{Title}' (confidence={Confidence:P0}, hash={Hash})",
            resolvedMediaType, resolvedTitle, scored.OverallConfidence, hash.Hex[..12]);

        // Log to activity ledger so the Activity tab shows what was ingested and matched.
        string authorPart = string.IsNullOrWhiteSpace(resolvedAuthor) ? string.Empty : $" by {resolvedAuthor}";

        // Build structured JSON for the rich match card in the Dashboard.
        var resolvedYear = candidate.Metadata?.GetValueOrDefault(MetadataFieldConstants.Year, string.Empty) ?? string.Empty;
        var resolvedDescription = candidate.Metadata?.GetValueOrDefault(MetadataFieldConstants.Description, string.Empty) ?? string.Empty;
        var richJson = JsonSerializer.Serialize(new
        {
            title = resolvedTitle,
            author = resolvedAuthor,
            year = resolvedYear,
            media_type = resolvedMediaType.ToString(),
            confidence = scored.OverallConfidence,
            source_file = Path.GetFileName(candidate.Path),
            description = resolvedDescription,
            entity_id = assetId.ToString(),
        });

        // Demoted from activity ledger to debug log (Phase 5 — activity consolidation).
        // The consolidated MediaAdded event is created at the end of the hydration pipeline.
        _logger.LogDebug(
            "FileDetected — \"{Title}\"{Author} ({Confidence:P0})",
            resolvedTitle, authorPart, scored.OverallConfidence);

        await SafePublishAsync(SignalREvents.IngestionCompleted, new IngestionCompletedEvent(
            candidate.Path,
            resolvedMediaType.ToString(),
            DateTimeOffset.UtcNow), ct).ConfigureAwait(false);

    }
    private async Task RunOrganizeStageAsync(IngestionPipelineContext context, CancellationToken ct)
    {
        var candidate = context.Candidate;
        var assetId = context.AssetId;
        var claims = context.Claims;
        var scored = context.Scored!;
        var mediaTypeNeedsReview = context.MediaTypeNeedsReview;
        var candidateList = context.MediaTypeCandidates;
        // Foreign-language metadata check removed — handled by LanguageMismatch trigger
        // in HydrationPipelineService (runs after Stage 1 with more context).

        // Step 11: gate evaluation and identity job creation.
        // Files stay in the watch folder during initial processing (staging eliminated).
        // AutoOrganizeService moves them directly to the library after Stage 1 retail match.
        bool hasUserLock = claims.Any(c => c.IsUserLocked);

        // Calculate the standard relative path once: the gate checks the category it files under ("Other" is held back),
        // and a file already at that path is left where it is.
        bool willOrganize = _options.AutoOrganize && !string.IsNullOrWhiteSpace(_options.LibraryRoot);
        string? gateRelativePath = willOrganize
            ? LibraryOrganizationPath.Calculate(_organizer, candidate, context.Library, _options.ResolveTemplate(candidate.DetectedMediaType?.ToString()))
            : null;

        var candidateCanonicals = candidate.Metadata
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var gateResult = _gate.Evaluate(
            scored.OverallConfidence,
            candidateCanonicals,
            hasUserLock,
            mediaTypeNeedsReview,
            willOrganize ? LibraryOrganizationPath.FilingCategory(context.Library, candidate.DetectedMediaType) : null);

        // Side-by-side-with-Plex plan §C — skip the staging move when the file
        // is already at the path Tuvima would have organised it to. This is how
        // files arriving from an existing Plex / Jellyfin / ABS tree flow through
        // the pipeline without being disturbed: they pass through hydration in
        // place and never leave their source directory.
        bool isAtTargetLocation = gateRelativePath is not null
            && candidate.Path.Replace('\\', '/').EndsWith(
                gateRelativePath.Replace('\\', '/'),
                StringComparison.OrdinalIgnoreCase);

        // Staging eliminated: files stay in the watch folder during initial processing.
        // AutoOrganizeService moves them directly to the library after Stage 1 produces
        // a resolved title. Review items are still created when the gate signals them.
        context.CurrentPath = candidate.Path;
        if (gateResult.ReviewTrigger is not null && !context.IsVault && context.Library?.BypassesExternalIdentity != true
            && !(gateResult.ReviewTrigger == ReviewTrigger.LowConfidence && !mediaTypeNeedsReview
                && (candidateCanonicals.ContainsKey("audiobook_recording_key")
                    || _libraryFolderResolver?.ResolveSourceForPath(candidate.Path)?.Source.ManagementMode
                        == MediaEngine.Domain.Configuration.LibrarySourceManagementModes.ExistingLibrary)))
        {
            await CreateIngestionReviewItemAsync(
                assetId, gateResult.ReviewTrigger, scored.OverallConfidence,
                gateResult.ReviewDetail!,
                ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Review: asset {AssetId} queued for review — trigger={Trigger}",
                assetId, gateResult.ReviewTrigger);
        }

    }
    private async Task RunWriteBackStageAsync(IngestionPipelineContext context, CancellationToken ct)
    {
        var candidate = context.Candidate;
        var assetId = context.AssetId;
        var result = context.ProcessorResult!;
        var resolvedTitle = context.ResolvedTitle;
        var currentPath = context.CurrentPath;
        // Step 11b: persist the cover into the central asset store. Embedded art always wins;
        // the sibling folder image (cover.jpg, folder.jpg, ...) is only used when no picture is embedded.
        var hasEmbeddedCover = result.CoverImage is { Length: > 0 };
        var coverBytes = hasEmbeddedCover ? result.CoverImage : context.FolderCover?.Bytes;
        var coverMimeType = hasEmbeddedCover ? result.CoverImageMimeType : context.FolderCover?.MimeType;
        var coverSource = hasEmbeddedCover ? CoverSourceEmbedded : CoverSourceFolder;
        if (!context.IsVault
            && _writeBackStageDependencies.EntityAssetRepository is not null
            && _writeBackStageDependencies.AssetPathService is not null
            && coverBytes is { Length: > 0 })
        {
            try
            {
                var (ownerEntityId, isSharedCoverOwner) = await ResolveEmbeddedCoverOwnerAsync(assetId, ct).ConfigureAwait(false);

                // Several files (e.g. the tracks of one album or audiobook) resolve to the same owner and the same
                // variant path, so the whole read-check-write-upsert-reconcile sequence is serialised per owner.
                using var ownerLock = await _concurrencyGuard.AcquireArtworkOwnerLockAsync(ownerEntityId, ct).ConfigureAwait(false);

                var existingAssets = await _writeBackStageDependencies.EntityAssetRepository.GetByEntityAsync(ownerEntityId.ToString(), "CoverArt", ct)
                    .ConfigureAwait(false);
                var preferredUserOverride = existingAssets.FirstOrDefault(asset => asset.IsPreferred && asset.IsUserOverride);
                if (preferredUserOverride is not null)
                {
                    _logger.LogDebug(
                        "Skipping embedded cover persistence for {AssetId} because owner {OwnerEntityId} already has a user-selected cover",
                        assetId,
                        ownerEntityId);
                }
                else
                {
                    var existingLocalCover = existingAssets.FirstOrDefault(asset =>
                        string.Equals(asset.SourceProvider, CoverSourceEmbedded, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(asset.SourceProvider, "local_processor", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(asset.SourceProvider, CoverSourceFolder, StringComparison.OrdinalIgnoreCase));

                    if (KeepsFirstSharedLocalCover(existingLocalCover, coverSource, isSharedCoverOwner)
                        || await IsLocalCoverAlreadyPersistedAsync(existingLocalCover, coverSource, coverBytes, ct).ConfigureAwait(false))
                    {
                        _logger.LogDebug(
                            "Skipping {CoverSource} cover persistence for {AssetId}: owner {OwnerEntityId} already has this cover",
                            coverSource,
                            assetId,
                            ownerEntityId);
                    }
                    else
                    {
                        var coverVariantId = existingLocalCover?.Id ?? Guid.NewGuid();
                        var coverPath = _writeBackStageDependencies.AssetPathService.GetCentralAssetPath(
                            "Work",
                            ownerEntityId,
                            "CoverArt",
                            coverVariantId,
                            InferArtworkExtension(coverMimeType));
                        var coverFolder = Path.GetDirectoryName(coverPath) ?? string.Empty;
                        AssetPathService.EnsureDirectory(coverPath);

                        await BoundedHttpContent.WriteFileAtomicallyAsync(coverPath, coverBytes, ct).ConfigureAwait(false);

                        var storedAsset = existingLocalCover ?? new EntityAsset
                        {
                            Id = coverVariantId,
                            EntityId = ownerEntityId.ToString(),
                            EntityType = "Work",
                            AssetTypeValue = "CoverArt",
                            AssetClassValue = "Artwork",
                            StorageLocationValue = "Central",
                            OwnerScope = "Work",
                            CreatedAt = DateTimeOffset.UtcNow,
                        };

                        storedAsset.ImageUrl = $"/stream/artwork/{coverVariantId}";
                        storedAsset.LocalImagePath = coverPath;
                        storedAsset.SourceProvider = coverSource;
                        storedAsset.IsPreferred = true;
                        storedAsset.IsUserOverride = false;
                        storedAsset.IsLocallyExported = false;
                        storedAsset.IsPreferredExported = false;
                        ArtworkVariantHelper.StampMetadataAndRenditions(storedAsset, _writeBackStageDependencies.AssetPathService);

                        await _writeBackStageDependencies.EntityAssetRepository.UpsertAsync(storedAsset, ct).ConfigureAwait(false);
                        await _writeBackStageDependencies.EntityAssetRepository.SetPreferredAsync(storedAsset.Id, ct).ConfigureAwait(false);
                        await _canonicalRepo.UpsertBatchAsync(
                            [
                                .. ArtworkCanonicalHelper.CreatePreferredAssetCanonicals(
                                    ownerEntityId,
                                    storedAsset,
                                    DateTimeOffset.UtcNow),
                                .. ArtworkCanonicalHelper.CreateFlags(
                                    ownerEntityId,
                                    coverState: "present",
                                    coverSource: coverSource,
                                    heroState: "missing",
                                    lastScoredAt: DateTimeOffset.UtcNow,
                                    settled: true),
                            ],
                            ct).ConfigureAwait(false);

                        if (_writeBackStageDependencies.AssetExportService is not null)
                        {
                            await _writeBackStageDependencies.AssetExportService.ReconcileArtworkAsync(
                                storedAsset.EntityId,
                                storedAsset.EntityType,
                                storedAsset.AssetTypeValue,
                                ct).ConfigureAwait(false);
                        }

                        await SafeActivityLogAsync(new Domain.Entities.SystemActivityEntry
                        {
                            ActionType = Domain.Constants.SystemActionType.CoverArtSaved,
                            EntityId = assetId,
                            EntityType = "MediaAsset",
                            CollectionName = resolvedTitle,
                            ChangesJson = JsonSerializer.Serialize(new
                            {
                                cover_size_bytes = coverBytes.Length,
                                cover_source = coverSource,
                                filename = Path.GetFileName(coverPath),
                                folder = coverFolder,
                                location = "central_asset_store",
                                owner_entity_id = ownerEntityId,
                            }),
                            Detail = $"Cover art saved ({coverBytes.Length / 1024} KB)",
                        }, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist {CoverSource} cover art for {Path}", coverSource, currentPath);
            }
        }

        // Step 12: write-back. Nothing is written into the file now (batched write-back D1): a file in a library folder whose source
        // policy allows tag write-back goes on the durable write list, and the scheduled run writes its tags (and, where switched on,
        // its embedded cover) from Library's values at that moment, which is also after the retail match and Wikidata bridge have
        // improved them. The run records the file's new fingerprint, so the watcher does not take its own write for a change.
        var effectiveLibraryRoot = !string.IsNullOrWhiteSpace(context.Library?.LibraryRoot)
            ? context.Library.LibraryRoot
            : _options.LibraryRoot;
        bool fileIsInLibrary = context.FileIsInLibrary = !string.IsNullOrWhiteSpace(effectiveLibraryRoot)
            && currentPath.StartsWith(effectiveLibraryRoot, StringComparison.OrdinalIgnoreCase);
        List<string> tagsWritten = context.TagsWritten = [];
        bool coverWritten = context.CoverWritten = false;
        try
        {
            var resolvedSource = _libraryFolderResolver?.ResolveSourceForPath(currentPath);
            var writebackAllowed = resolvedSource is not null
                && _sourceMutationPolicyGate?.Evaluate(new SourceMutationRequest
                {
                    Source = FileSourceMutationPolicyFactory.Create(
                        resolvedSource.Library,
                        resolvedSource.Source),
                    Mutation = SourceMutationKind.MetadataWriteback,
                    Path = currentPath,
                }).Allowed == true;

            if (_options.WriteBack && fileIsInLibrary && writebackAllowed && candidate.Metadata is not null
                && _writeBackStageDependencies.FileWriteQueue is { } fileWrites)
            {
                await fileWrites.EnqueueAssetAsync(assetId, "auto_match", ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Identity-job creation historically preceded write-back. Defer the
            // failure until that durable job exists, then preserve the original
            // terminal failure behavior.
            context.DeferredWriteBackFailure = ex;
        }

        context.TagsWritten = tagsWritten;
        context.CoverWritten = coverWritten;

    }
    private async Task RunIdentityJobStageAsync(IngestionPipelineContext context, CancellationToken ct)
    {
        var candidate = context.Candidate;
        var durableOperation = context.DurableOperation;
        var logEntryId = context.LogEntryId;
        var pipelineStopwatch = context.PipelineStopwatch!;
        var hash = context.Hash!;
        var assetId = context.AssetId;
        var scored = context.Scored!;
        var resolvedMediaType = context.ResolvedMediaType;
        var resolvedTitle = context.ResolvedTitle;
        var resolvedAuthor = context.ResolvedAuthor;
        var currentPath = context.CurrentPath;
        var fileIsInLibrary = context.FileIsInLibrary;
        var tagsWritten = context.TagsWritten;
        var coverWritten = context.CoverWritten;
        var bypassesExternalIdentity = context.Library?.BypassesExternalIdentity == true;
        // Phase 9 pipeline: enqueue non-blocking three-stage hydration pipeline.
        // IMPORTANT: placed AFTER the organization gate so that any LowConfidence review
        // item created above already exists in the database before the hydration pipeline's
        // TryAutoResolveAndOrganizeAsync runs. This prevents a race condition where the
        // hydration pipeline resolves before the review item is even written, leaving a
        // stale review item in the queue for a file that was successfully organized.
        if (!bypassesExternalIdentity)
        {
            var identityJobCt = context.DeferredWriteBackFailure is OperationCanceledException
                ? CancellationToken.None
                : ct;
            await _identityJobRepo.CreateAsync(new Domain.Entities.IdentityJob
            {
                EntityId = assetId,
                EntityType = EntityType.MediaAsset.ToString(),
                MediaType = resolvedMediaType.ToString(),
                Pass = "Quick",
            }, identityJobCt).ConfigureAwait(false);
            await _assetRepo.MarkPresentedAsync(assetId, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            _identityStageDependencies.Signal?.Signal(IdentityPipelineSignalKind.Retail);

            _logger.LogInformation(
                "Identity job created for [{MediaType}] '{Title}'{AuthorPart} — queued for retail match ({AssetId12})",
                resolvedMediaType,
                resolvedTitle,
                string.IsNullOrWhiteSpace(resolvedAuthor) ? string.Empty : $" by {resolvedAuthor}",
                assetId.ToString("N")[..12]);

            try
            {
                await _ingestionLog.UpdateStatusAsync(
                    logEntryId,
                    "queued_identity",
                    mediaType: resolvedMediaType.ToString(),
                    mediaAssetId: assetId,
                    ct: ct).ConfigureAwait(false);
                await PublishItemProgressAsync(
                    candidate,
                    logEntryId,
                    "queued_identity",
                    80,
                    false,
                    ct,
                    assetId,
                    resolvedTitle,
                    resolvedMediaType.ToString()).ConfigureAwait(false);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Ingestion log update failed — continuing"); }
            await UpdateOperationStageAsync(durableOperation, MediaOperationStage.QueuedIdentity, 80, "Identity work queued.", ct, new { asset_id = assetId, media_type = resolvedMediaType.ToString() }).ConfigureAwait(false);

            await SafeActivityLogAsync(new Domain.Entities.SystemActivityEntry
            {
                ActionType = Domain.Constants.SystemActionType.HydrationEnqueued,
                EntityId = assetId,
                EntityType = "MediaAsset",
                CollectionName = resolvedTitle,
                ChangesJson = JsonSerializer.Serialize(new { entity_id = assetId.ToString(), media_type = resolvedMediaType.ToString() }),
                Detail = $"Queued for metadata enrichment ({resolvedMediaType})",
            }, ct).ConfigureAwait(false);
        }
        else
        {
            await _assetRepo.MarkPresentedAsync(assetId, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            await MarkYoutubeFileWithoutIdUnmatchedAsync(context, ct).ConfigureAwait(false);

            try
            {
                await _ingestionLog.UpdateStatusAsync(
                    logEntryId,
                    "complete",
                    mediaType: resolvedMediaType.ToString(),
                    detectedTitle: resolvedTitle,
                    mediaAssetId: assetId,
                    ct: ct).ConfigureAwait(false);
                await PublishItemProgressAsync(
                    candidate,
                    logEntryId,
                    "complete",
                    100,
                    true,
                    ct,
                    assetId,
                    resolvedTitle,
                    resolvedMediaType.ToString()).ConfigureAwait(false);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Ingestion log update failed — continuing"); }

            _logger.LogInformation(
                "Local metadata policy completed [{MediaType}] '{Title}' without external identity providers ({AssetId12})",
                resolvedMediaType,
                resolvedTitle,
                assetId.ToString("N")[..12]);
        }

        // Phase 9: person enrichment is deferred to the hydration pipeline (Stage 1),
        // which has pen name detection logic.  Running it here causes a race condition:
        // the background MetadataHarvestingService would enrich "James S.A. Corey" with
        // a co-author's bio before the pipeline can detect it as a collective pseudonym.
        // HydrationPipelineService.ExtractPersonReferencesFromRawClaims handles person
        // creation, linking, and enrichment after pen name detection has run.

        if (context.DeferredWriteBackFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(context.DeferredWriteBackFailure)
                .Throw();
        }

        // -- Phase 2 activity: FileIngested — fires after the full pipeline --
        // Summarises the outcome: organized, staged, or awaiting enrichment.
        // Rebuild richJson to include organization destination (PathUpdated is folded in).
        string? organizedTo = fileIsInLibrary ? currentPath
            : !string.Equals(currentPath, candidate.Path, StringComparison.Ordinal) ? "staging"
            : null;

        // Build per-field provenance so the Dashboard can show exactly
        // how each metadata field was matched and which source won.
        var fieldProvenance = scored.FieldScores
            .Where(f => !string.IsNullOrEmpty(f.WinningValue))
            .Select(f => new
            {
                field = f.Key,
                value = f.WinningValue,
                confidence = f.Confidence,
                source = f.WinningProviderId == LocalProcessorProviderId ? "embedded"
                            : f.WinningProviderId.HasValue ? "provider" : "unknown",
                provider_id = f.WinningProviderId?.ToString(),
                conflicted = f.IsConflicted,
            })
            .ToList();

        // Determine the primary match method for the summary.
        string matchMethod;
        var titleField = scored.FieldScores.FirstOrDefault(f => f.Key == MetadataFieldConstants.Title);
        if (titleField is not null && titleField.WinningProviderId == LocalProcessorProviderId)
        {
            matchMethod = "embedded_metadata";
        }
        else if (titleField is not null && titleField.WinningProviderId.HasValue)
        {
            matchMethod = "provider_match";
        }
        else
        {
            matchMethod = "filename_fallback";
        }

        var finalRichJson = JsonSerializer.Serialize(new
        {
            title = resolvedTitle,
            author = resolvedAuthor,
            year = candidate.Metadata?.GetValueOrDefault(MetadataFieldConstants.Year, string.Empty) ?? string.Empty,
            media_type = resolvedMediaType.ToString(),
            confidence = scored.OverallConfidence,
            source_file = Path.GetFileName(candidate.Path),
            source_path = candidate.Path,
            description = candidate.Metadata?.GetValueOrDefault(MetadataFieldConstants.Description, string.Empty) ?? string.Empty,
            entity_id = assetId.ToString(),
            organized_to = organizedTo,
            cover_url = fileIsInLibrary ? $"/stream/{assetId}/cover" : (string?)null,
            match_method = matchMethod,
            field_sources = fieldProvenance,
            tags_written = tagsWritten,
            cover_written = coverWritten,
        });

        // "Sent to review" is determined by the hydration pipeline, not at this stage.
        // Use "awaiting enrichment" for all staged files; the MediaAdded activity entry
        // written at the end of HydrationPipelineService carries the real outcome.
        string matchLabel = matchMethod switch
        {
            "embedded_metadata" => "matched from embedded tags",
            "provider_match" => "matched via provider",
            _ => "matched from filename",
        };
        string outcome = bypassesExternalIdentity
            ? $"Indexed locally — \"{resolvedTitle}\" ({matchLabel})"
            : fileIsInLibrary
            ? $"Ingested — \"{resolvedTitle}\" ({matchLabel}, {scored.OverallConfidence:P0}) ? Library"
            : $"Ingested — \"{resolvedTitle}\" ({matchLabel}, {scored.OverallConfidence:P0}) — awaiting enrichment";

        await SafeActivityLogAsync(new Domain.Entities.SystemActivityEntry
        {
            ActionType = Domain.Constants.SystemActionType.FileIngested,
            EntityId = assetId,
            EntityType = "MediaAsset",
            CollectionName = resolvedTitle,
            ChangesJson = finalRichJson,
            Detail = outcome,
        }, ct).ConfigureAwait(false);

        // -- Performance log --------------------------------------------------
        pipelineStopwatch.Stop();
        await CompleteOperationAsync(durableOperation, "ingestion completed", ct).ConfigureAwait(false);

        _logger.LogInformation(
            "[PERF] {FileName}: Total={TotalMs}ms Hash={HashMs}ms (type={MediaType}, confidence={Confidence:P0})",
            Path.GetFileName(candidate.Path),
            pipelineStopwatch.ElapsedMilliseconds,
            (long)hash.Elapsed.TotalMilliseconds,
            resolvedMediaType,
            scored.OverallConfidence);

    }

}
