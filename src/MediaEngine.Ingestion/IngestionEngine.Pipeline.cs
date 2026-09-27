using System.Text.Json;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Capabilities;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Detection;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Pipeline;
using MediaEngine.Ingestion.Services;
using MediaEngine.Intelligence.Contracts;
using MediaEngine.Intelligence.Models;
using MediaEngine.Processors.Contracts;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Helpers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaEngine.Ingestion;

public sealed partial class IngestionEngine
{
    // =========================================================================
    // Live pipeline
    // =========================================================================

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

    private async Task ProcessCandidateCoreAsync(IngestionCandidate candidate, CancellationToken ct)
    {
        var context = new IngestionPipelineContext(
            candidate,
            candidate.BatchId ?? Guid.NewGuid());
        context.Library = candidate.Intake?.HasDestinationHint == true
            ? _libraryFolderResolver?.ResolveById(candidate.Intake.DestinationLibraryId!)
            : _libraryFolderResolver?.ResolveForPath(candidate.Path);

        if (candidate.Intake?.HasDestinationHint == true && context.Library is null)
        {
            throw new InvalidOperationException(
                $"Direct intake destination library '{candidate.Intake.DestinationLibraryId}' is not configured.");
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
        // Use the batch ID as the ingestion run ID so all activity entries
        // for files in the same batch share one correlation ID.
        var ingestionRunId = context.IngestionRunId;
        var durableOperation = context.DurableOperation = await EnsureIngestionOperationAsync(candidate, ingestionRunId, MediaOperationStage.Queued, ct).ConfigureAwait(false);
        await UpdateOperationStageAsync(durableOperation, MediaOperationStage.Queued, 0, "Queued for ingestion.", ct).ConfigureAwait(false);

        // Step 2: skip failed probe candidates.
        if (candidate.IsFailed)
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
            if (candidate.BatchId.HasValue)
            {
                await SafeIncrementBatchCounterAsync(candidate.BatchId.Value, BatchCounterColumn.FilesFailed, ct).ConfigureAwait(false);
                await SafeIncrementBatchCounterAsync(candidate.BatchId.Value, BatchCounterColumn.FilesProcessed, ct).ConfigureAwait(false);
                await PublishQueuedBatchSnapshotAsync(candidate.BatchId.Value, ct).ConfigureAwait(false);
            }
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
            var reason = "The file was detected but is no longer available. It may have been moved, deleted, or locked before ingestion could read it.";
            _logger.LogWarning("Ingestion skipped - file missing: \"{FileName}\"", Path.GetFileName(candidate.Path));
            await _ingestionLogScribe.RecordTerminalAsync(
                candidate,
                ingestionRunId,
                "missing",
                reason,
                ct).ConfigureAwait(false);
            if (candidate.BatchId.HasValue)
            {
                await SafeIncrementBatchCounterAsync(candidate.BatchId.Value, BatchCounterColumn.FilesFailed, ct).ConfigureAwait(false);
                await SafeIncrementBatchCounterAsync(candidate.BatchId.Value, BatchCounterColumn.FilesProcessed, ct).ConfigureAwait(false);
                await PublishQueuedBatchSnapshotAsync(candidate.BatchId.Value, ct).ConfigureAwait(false);
            }
            await NoResultOperationAsync(durableOperation, reason, ct).ConfigureAwait(false);
            context.Complete();
            return;
        }

        _logger.LogInformation("Detected: \"{FileName}\" — queued for ingestion", Path.GetFileName(candidate.Path));

        await SafePublishAsync(SignalREvents.IngestionStarted, new IngestionStartedEvent(
            candidate.Path, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);

        // Lifecycle log: create entry at detection.
        var logEntryId = context.LogEntryId = await _ingestionLogScribe.InsertDetectedAsync(candidate, ingestionRunId, ct)
            .ConfigureAwait(false);

        context.PipelineStopwatch = System.Diagnostics.Stopwatch.StartNew();

    }
    private async Task RunHashAndDedupeStageAsync(IngestionPipelineContext context, CancellationToken ct)
    {
        var candidate = context.Candidate;
        var ingestionRunId = context.IngestionRunId;
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
            IngestionRunId = ingestionRunId,
        }, ct).ConfigureAwait(false);

        // Lifecycle log: hashing complete.
        await _ingestionLogScribe.UpdateStatusAsync(
            candidate,
            logEntryId,
            "hashing",
            15,
            false,
            ct,
            contentHash: hash.Hex).ConfigureAwait(false);

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
                        Detail = $"Duplicate skipped and deleted: {Path.GetFileName(candidate.Path)} (identical to {Path.GetFileName(existing.FilePathRoot)})",
                        IngestionRunId = ingestionRunId,
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

                    try
                    {
                        var resolvedSource = _libraryFolderResolver?.ResolveSourceForPath(candidate.Path);
                        var deleteAllowed = resolvedSource is not null
                            && resolvedSource.Library.DuplicatePolicy == MediaEngine.Domain.Configuration.LibraryDuplicatePolicies.SkipExact
                            && _sourceMutationPolicyGate?.Evaluate(new SourceMutationRequest
                            {
                                Source = FileSourceMutationPolicyFactory.Create(
                                    resolvedSource.Library,
                                    resolvedSource.Source,
                                    allowDelete: true),
                                Mutation = SourceMutationKind.Delete,
                                Path = candidate.Path,
                            }).Allowed == true;

                        if (deleteAllowed && File.Exists(candidate.Path))
                        {
                            File.Delete(candidate.Path);
                        }

                        if (!deleteAllowed)
                        {
                            _logger.LogInformation(
                                "Exact duplicate was indexed without deleting {Path}; its source policy does not authorize deletion",
                                candidate.Path);
                        }

                        await DeleteHashCacheEntryAsync(candidate.Path, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Could not delete duplicate file {Path}", candidate.Path);
                    }

                    await MarkBatchFileProcessedAsync(candidate.BatchId, ct).ConfigureAwait(false);
                    await CompleteOperationAsync(durableOperation, "duplicate", ct).ConfigureAwait(false);
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
                        ingestionRunId,
                        ct,
                        queueIdentityRefresh: context.Library?.BypassesExternalIdentity != true).ConfigureAwait(false);

                    if (missingIdentityJob && !metadataRefreshed)
                        throw new InvalidOperationException($"Could not recover identity enqueue for registered asset {existing.Id}.");

                    if (metadataRefreshed)
                    {
                        var hashUpdated = await _assetRepo.UpdateContentHashAsync(existing.Id, hash.Hex, ct)
                            .ConfigureAwait(false);
                        if (hashUpdated)
                        {
                            existing.ContentHash = hash.Hex;
                            if (_playbackInspection is not null) await _playbackInspection.InspectAsync(existing, ct);
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
                await MarkBatchFileProcessedAsync(candidate.BatchId, ct).ConfigureAwait(false);
                await CompleteOperationAsync(durableOperation, "same_path_redetected", ct).ConfigureAwait(false);
                context.Complete();
                return;
            }
        }

    }
    private async Task RunScoreAndIdentifyStageAsync(IngestionPipelineContext context, CancellationToken ct)
    {
        var candidate = context.Candidate;
        var ingestionRunId = context.IngestionRunId;
        var durableOperation = context.DurableOperation;
        var logEntryId = context.LogEntryId;
        var hash = context.Hash!;
        var result = context.ProcessorResult!;
        if (context.Library?.MediaTypes.Contains(MediaType.Audiobooks) == true
            && _libraryFolderResolver?.ResolveSourcePath(candidate.Path) is { } audiobookSource)
            result = context.ProcessorResult = Services.AudiobookFolderHints.Apply(result, audiobookSource);
        // Step 8: convert claims.
        var assetId = context.AssetId = Guid.NewGuid();
        var claims = context.Claims = BuildClaims(assetId, result);

        // History: file detected.
        await SafeActivityLogAsync(new SystemActivityEntry
        {
            ActionType = SystemActionType.FileDetected,
            EntityId = assetId,
            Detail = $"File detected in watch folder: {Path.GetFileName(candidate.Path)}",
            IngestionRunId = ingestionRunId,
        }, ct).ConfigureAwait(false);

        // History: metadata extracted.
        await SafeActivityLogAsync(new SystemActivityEntry
        {
            ActionType = "MetadataExtracted",
            EntityId = assetId,
            Detail = $"Metadata extracted — {result.Claims.Count} fields found",
            IngestionRunId = ingestionRunId,
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
                IngestionRunId = ingestionRunId,
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
            IngestionRunId = ingestionRunId,
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
            IngestionRunId = ingestionRunId,
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

        if (result.CoverImage is { Length: > 0 })
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
                coverSource: "embedded",
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

        // Create MetadataConflict review item when any canonical value has IsConflicted=true.
        // Conflicts don't block organization — the file proceeds with the best-guess value.
        var conflictedFields = canonicals
            .Where(c => c.IsConflicted && c.Key != MetadataFieldConstants.MediaTypeField) // media_type handled separately
            .Select(c => c.Key)
            .ToList();

        if (conflictedFields.Count > 0)
        {
            await CreateMetadataConflictReviewItemAsync(
                assetId,
                scored.OverallConfidence,
                conflictedFields,
                ct, ingestionRunId).ConfigureAwait(false);
        }

        // Create AmbiguousMediaType review item when media type confidence is below threshold.
        if (mediaTypeNeedsReview && candidateList.Count > 0)
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
                ct, ingestionRunId).ConfigureAwait(false);
        }

        // Create RootWatchFolder review item when a file was dropped directly into any
        // configured source root with an ambiguous extension.
        if (mediaTypeResolution.RootWatchFolderReview)
        {
            await CreateIngestionReviewItemAsync(
                assetId,
                ReviewTrigger.RootWatchFolder,
                candidateList.Count > 0 ? candidateList[0].Confidence : 0.0,
                mediaTypeResolution.RootWatchFolderDetail ?? "File dropped into a source root - please confirm the media type.",
                ct, ingestionRunId).ConfigureAwait(false);
        }

        // Enrich the candidate with resolved metadata.
        candidate.Metadata = BuildMetadataDict(scored);
        candidate.DetectedMediaType = resolvedMediaType;

        // Step 9b: create Collection ? Work ? Edition chain so the FK on media_assets
        // can be satisfied.  The factory reuses an existing Collection when a matching
        // display name is found; otherwise it creates a fresh chain.
        var editionId = await _chainFactory.EnsureEntityChainAsync(
            resolvedMediaType,
            candidate.Metadata,
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
            IngestionRunId = ingestionRunId,
        }, ct).ConfigureAwait(false);

        // Step 10: insert asset.
        var asset = new MediaAsset
        {
            Id = assetId,
            EditionId = editionId,
            ContentHash = hash.Hex,
            FilePathRoot = candidate.Path,
            LibraryId = context.Library?.Id,
            Status = AssetStatus.Normal,
        };

        bool inserted = await _assetRepo.InsertAsync(asset, ct).ConfigureAwait(false);
        if (!inserted)
        {
            // Race: another thread inserted the same hash concurrently.
            _logger.LogDebug("Asset already inserted by concurrent task: {Hash}", hash.Hex[..12]);
            await CompleteOperationAsync(durableOperation, "duplicate_race", ct).ConfigureAwait(false);
            context.Complete();
            return;
        }

        if (_playbackInspection is not null)
            await _playbackInspection.InspectAsync(asset, ct).ConfigureAwait(false);

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

        // Emit incremental ingestion progress so the Dashboard can show a live counter.
        // Fetch the batch total from the repository; fall back to 0 if not available.
        if (candidate.BatchId.HasValue)
        {
            try
            {
                var batchSnap = await _batchRepo.GetByIdAsync(candidate.BatchId.Value, ct).ConfigureAwait(false);
                await SafePublishAsync(SignalREvents.IngestionProgress, new IngestionProgressEvent(
                    Path.GetFileName(candidate.Path),
                    batchSnap?.FilesProcessed ?? 0,
                    batchSnap?.FilesTotal ?? 0,
                    "Processing"), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "IngestionProgress publish failed — pipeline continues");
            }
        }

    }
    private async Task RunOrganizeStageAsync(IngestionPipelineContext context, CancellationToken ct)
    {
        var candidate = context.Candidate;
        var ingestionRunId = context.IngestionRunId;
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

        // Calculate the relative path once for the gate (needed for the "Other" check).
        string? gateRelativePath = _options.AutoOrganize
            && !string.IsNullOrWhiteSpace(_options.LibraryRoot)
            ? LibraryOrganizationPath.Calculate(_organizer, candidate, context.Library, _options.ResolveTemplate(candidate.DetectedMediaType?.ToString()))
            : null;

        var candidateCanonicals = candidate.Metadata
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var gateResult = _gate.Evaluate(
            scored.OverallConfidence,
            candidateCanonicals,
            hasUserLock,
            mediaTypeNeedsReview,
            gateRelativePath);

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
        if (gateResult.ReviewTrigger is not null && context.Library?.BypassesExternalIdentity != true
            && !(gateResult.ReviewTrigger == ReviewTrigger.LowConfidence && !mediaTypeNeedsReview
                && (candidateCanonicals.ContainsKey("audiobook_recording_key")
                    || _libraryFolderResolver?.ResolveSourceForPath(candidate.Path)?.Source.ManagementMode
                        == MediaEngine.Domain.Configuration.LibrarySourceManagementModes.ExistingLibrary)))
        {
            await CreateIngestionReviewItemAsync(
                assetId, gateResult.ReviewTrigger, scored.OverallConfidence,
                gateResult.ReviewDetail!,
                ct, ingestionRunId).ConfigureAwait(false);

            _logger.LogInformation(
                "Review: asset {AssetId} queued for review — trigger={Trigger}",
                assetId, gateResult.ReviewTrigger);

            if (candidate.BatchId.HasValue)
            {
                await SafeIncrementBatchCounterAsync(candidate.BatchId.Value, BatchCounterColumn.FilesIdentified, ct).ConfigureAwait(false);
            }
        }
        else if (candidate.BatchId.HasValue)
        {
            await SafeIncrementBatchCounterAsync(candidate.BatchId.Value, BatchCounterColumn.FilesIdentified, ct).ConfigureAwait(false);
        }

    }
    private async Task RunWriteBackStageAsync(IngestionPipelineContext context, CancellationToken ct)
    {
        var candidate = context.Candidate;
        var ingestionRunId = context.IngestionRunId;
        var assetId = context.AssetId;
        var result = context.ProcessorResult!;
        var resolvedTitle = context.ResolvedTitle;
        var currentPath = context.CurrentPath;
        // Step 11b: persist embedded cover art into the central asset store.
        if (_writeBackStageDependencies.EntityAssetRepository is not null
            && _writeBackStageDependencies.AssetPathService is not null
            && result.CoverImage is { Length: > 0 })
        {
            try
            {
                var ownerEntityId = await ResolveEmbeddedCoverOwnerEntityIdAsync(assetId, ct).ConfigureAwait(false);
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
                    var existingEmbedded = existingAssets.FirstOrDefault(asset =>
                        string.Equals(asset.SourceProvider, "embedded", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(asset.SourceProvider, "local_processor", StringComparison.OrdinalIgnoreCase));
                    var coverVariantId = existingEmbedded?.Id ?? Guid.NewGuid();
                    var coverPath = _writeBackStageDependencies.AssetPathService.GetCentralAssetPath(
                        "Work",
                        ownerEntityId,
                        "CoverArt",
                        coverVariantId,
                        InferArtworkExtension(result.CoverImageMimeType));
                    var coverFolder = Path.GetDirectoryName(coverPath) ?? string.Empty;
                    AssetPathService.EnsureDirectory(coverPath);

                    await File.WriteAllBytesAsync(coverPath, result.CoverImage, ct).ConfigureAwait(false);

                    var storedAsset = existingEmbedded ?? new EntityAsset
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
                    storedAsset.SourceProvider = "embedded";
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
                                coverSource: "embedded",
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
                            cover_size_bytes = result.CoverImage.Length,
                            filename = Path.GetFileName(coverPath),
                            folder = coverFolder,
                            location = "central_asset_store",
                            owner_entity_id = ownerEntityId,
                        }),
                        Detail = $"Cover art saved ({result.CoverImage.Length / 1024} KB)",
                        IngestionRunId = ingestionRunId,
                    }, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist embedded cover art for {Path}", currentPath);
            }
        }

        // Step 12: write-back is deferred to promotion.
        // Writing back to a file in the watch folder would change its content hash,
        // causing the watcher to re-detect it as a new file.
        // AutoOrganizeService handles write-back after promotion to the Library.
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
                        resolvedSource.Source,
                        globalMetadataWritebackEnabled: _options.WriteBack),
                    Mutation = SourceMutationKind.MetadataWriteback,
                    Path = currentPath,
                }).Allowed == true;

            if (_options.WriteBack && fileIsInLibrary && writebackAllowed && candidate.Metadata is not null)
            {
                var tagger = _taggers.FirstOrDefault(t => t.CanHandle(currentPath));
                if (tagger is not null)
                {
                    await tagger.WriteTagsAsync(currentPath, candidate.Metadata, ct)
                                 .ConfigureAwait(false);
                    tagsWritten = [.. candidate.Metadata.Keys];

                    if (result.CoverImage is { Length: > 0 })
                    {
                        await tagger.WriteCoverArtAsync(currentPath, result.CoverImage, ct)
                                     .ConfigureAwait(false);
                        coverWritten = true;
                    }

                    await SafeActivityLogAsync(new Domain.Entities.SystemActivityEntry
                    {
                        ActionType = Domain.Constants.SystemActionType.MetadataTagsWritten,
                        EntityId = assetId,
                        EntityType = "MediaAsset",
                        CollectionName = resolvedTitle,
                        ChangesJson = JsonSerializer.Serialize(new
                        {
                            tags_written = tagsWritten,
                            cover_written = coverWritten,
                            file = Path.GetFileName(currentPath),
                        }),
                        Detail = $"Tags written back to file ({tagsWritten.Count} field(s){(coverWritten ? " + cover art" : "")})",
                        IngestionRunId = ingestionRunId,
                    }, ct).ConfigureAwait(false);

                    // -- Timeline: sync writeback event ---------------------------
                    try
                    {
                        var writebackEvent = new EntityEvent
                        {
                            EntityId = assetId,
                            EntityType = "Work",
                            EventType = "sync_writeback",
                            Stage = null,
                            Trigger = "ingestion",
                            IngestionRunId = ingestionRunId,
                            Detail = $"Metadata written back to file: {tagsWritten.Count} field(s){(coverWritten ? " + cover art" : "")}",
                        };
                        await _timelineRepo.InsertEventAsync(writebackEvent, ct).ConfigureAwait(false);

                        // Record each written field as a field change.
                        if (candidate.Metadata.Count > 0)
                        {
                            var fieldChanges = candidate.Metadata
                                .Select(kvp => new EntityFieldChange
                                {
                                    EventId = writebackEvent.Id,
                                    EntityId = assetId,
                                    Field = kvp.Key,
                                    NewValue = kvp.Value,
                                })
                                .ToList();
                            await _timelineRepo.InsertFieldChangesAsync(fieldChanges, ct).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Failed to write sync writeback timeline event for asset {Id}", assetId);
                    }
                }
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
        var ingestionRunId = context.IngestionRunId;
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
                IngestionRunId = ingestionRunId,
                Pass = "Quick",
            }, identityJobCt).ConfigureAwait(false);
            await _assetRepo.MarkPresentedAsync(assetId, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            await SafePublishAsync(SignalREvents.MediaAdded,
                new MediaAddedEvent(assetId, null, resolvedMediaType.ToString(), resolvedTitle), ct).ConfigureAwait(false);
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
                IngestionRunId = ingestionRunId,
            }, ct).ConfigureAwait(false);
        }
        else
        {
            await _assetRepo.MarkPresentedAsync(assetId, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            await SafePublishAsync(
                SignalREvents.MediaAdded,
                new MediaAddedEvent(assetId, null, resolvedMediaType.ToString(), resolvedTitle),
                ct).ConfigureAwait(false);

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

        // Batch counter: file has been fully processed through the ingestion pipeline.
        if (candidate.BatchId.HasValue)
        {
            await SafeIncrementBatchCounterAsync(candidate.BatchId.Value, BatchCounterColumn.FilesProcessed, ct).ConfigureAwait(false);
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
            IngestionRunId = ingestionRunId,
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
