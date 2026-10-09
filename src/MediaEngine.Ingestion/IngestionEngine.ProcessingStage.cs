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
    public async Task<FileMetadataRereadResult> RereadAssetMetadataAsync(
        Guid assetId,
        CancellationToken ct = default)
    {
        var asset = await _assetRepo.FindByIdAsync(assetId, ct).ConfigureAwait(false);
        if (asset is null)
        {
            return new(assetId, "AssetMissing", false, false, "The library asset could not be found.");
        }

        var path = asset.FilePathRoot;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new(assetId, "FileUnavailable", false, false, "The media file is currently unavailable.");
        }

        var hash = await ComputeHashWithCacheAsync(path, ct).ConfigureAwait(false);
        var changed = !string.Equals(asset.ContentHash, hash.Hash.Hex, StringComparison.OrdinalIgnoreCase);
        if (changed)
        {
            var duplicate = await _assetRepo.FindByHashAsync(hash.Hash.Hex, ct).ConfigureAwait(false);
            if (duplicate is not null && duplicate.Id != asset.Id)
            {
                return new(assetId, "HashConflict", false, true, "The file now duplicates another library asset, so its existing metadata was left unchanged.");
            }
        }
        var refreshed = await RefreshExistingAssetMetadataAsync(
            asset,
            path,
            Guid.NewGuid(),
            ct,
            queueIdentityRefresh: false).ConfigureAwait(false);
        if (!refreshed)
        {
            return new(assetId, "Failed", false, changed, "The file was read, but its local metadata could not be refreshed.");
        }

        if (changed)
        {
            var hashUpdated = await _assetRepo.UpdateContentHashAsync(asset.Id, hash.Hash.Hex, ct).ConfigureAwait(false);
            if (!hashUpdated)
            {
                return new(assetId, "HashConflict", true, true, "Local metadata was refreshed, but the file bytes duplicate another library asset.");
            }
        }

        asset.ContentHash = hash.Hash.Hex;
        if (_playbackInspection is not null)
        {
            await _playbackInspection.InspectAsync(asset, ct);
        }
        return new(
            assetId,
            "Updated",
            true,
            changed,
            changed
                ? "Local tags and technical metadata were refreshed from the changed file."
                : "Local tags and technical metadata were reread; the file bytes were unchanged.");
    }

    private async Task<bool> RefreshExistingAssetMetadataAsync(
        MediaAsset asset,
        string filePath,
        Guid ingestionRunId,
        CancellationToken ct,
        bool queueIdentityRefresh = true)
    {
        try
        {
            var result = await _processors.ProcessAsync(filePath, ct).ConfigureAwait(false);
            if (_libraryFolderResolver?.ResolveForPath(filePath)?.MediaTypes.Contains(MediaType.Audiobooks) == true
                && _libraryFolderResolver.ResolveSourcePath(filePath) is { } audiobookSource)
            {
                result = Services.AudiobookFolderHints.Apply(result, audiobookSource);
            }
            if (result.IsCorrupt)
            {
                _logger.LogWarning(
                    "Changed same-path file {Path} could not be refreshed because the processor marked it corrupt: {Reason}",
                    filePath,
                    result.CorruptReason);
                return false;
            }

            var claims = BuildClaims(asset.Id, result).ToList();
            if (!claims.Any(claim => claim.ClaimKey.Equals(MetadataFieldConstants.MediaTypeField, StringComparison.OrdinalIgnoreCase)))
            {
                claims.Add(new MetadataClaim
                {
                    Id = Guid.NewGuid(),
                    EntityId = asset.Id,
                    ProviderId = LocalProcessorProviderId,
                    ClaimKey = MetadataFieldConstants.MediaTypeField,
                    ClaimValue = result.DetectedType.ToString(),
                    Confidence = 1,
                    ClaimedAt = DateTimeOffset.UtcNow,
                });
            }

            var previousClaims = await _claimRepo.GetByEntityAsync(asset.Id, ct).ConfigureAwait(false);
            var refreshedKeys = previousClaims
                .Where(claim => claim.ProviderId == LocalProcessorProviderId)
                .Select(claim => claim.ClaimKey)
                .Concat(claims.Select(claim => claim.ClaimKey))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            await _claimRepo.ReplaceCurrentProviderClaimsAsync(
                asset.Id,
                LocalProcessorProviderId,
                refreshedKeys,
                claims,
                ct).ConfigureAwait(false);

            var currentClaims = await _claimRepo.GetByEntityAsync(asset.Id, ct).ConfigureAwait(false);
            var scored = await _scorer.ScoreEntityAsync(new ScoringContext
            {
                EntityId = asset.Id,
                Claims = currentClaims,
                ProviderWeights = new Dictionary<Guid, double> { [LocalProcessorProviderId] = 1 },
                Configuration = _scoringConfig,
                DetectedMediaType = result.DetectedType,
            }, ct).ConfigureAwait(false);

            var canonicals = scored.FieldScores
                .Where(field => !string.IsNullOrWhiteSpace(field.WinningValue))
                .Where(field => !MetadataFieldConstants.IsMultiValued(field.Key))
                .Select(field => new CanonicalValue
                {
                    EntityId = asset.Id,
                    Key = field.Key,
                    Value = field.WinningValue!,
                    LastScoredAt = scored.ScoredAt,
                    IsConflicted = field.IsConflicted,
                    WinningProviderId = field.WinningProviderId,
                    NeedsReview = field.IsConflicted,
                })
                .ToList();
            await _canonicalRepo.UpsertBatchAsync(canonicals, ct).ConfigureAwait(false);

            var winningKeys = scored.FieldScores
                .Where(field => !string.IsNullOrWhiteSpace(field.WinningValue))
                .Select(field => field.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var key in refreshedKeys.Where(key => !winningKeys.Contains(key)))
            {
                await _canonicalRepo.DeleteByKeyAsync(asset.Id, key, ct).ConfigureAwait(false);
            }

            await SafeActivityLogAsync(new SystemActivityEntry
            {
                ActionType = "LocalMetadataRefreshed",
                EntityId = asset.Id,
                EntityType = "MediaAsset",
                Detail = $"Re-read {claims.Count} local metadata fields from the source file.",
                ChangesJson = JsonSerializer.Serialize(new
                {
                    file_name = Path.GetFileName(filePath),
                    media_type = result.DetectedType.ToString(),
                    claims_count = claims.Count,
                }),
                IngestionRunId = ingestionRunId,
            }, ct).ConfigureAwait(false);

            if (queueIdentityRefresh)
            {
                await _identityJobRepo.CreateAsync(new IdentityJob
                {
                    EntityId = asset.Id,
                    EntityType = EntityType.MediaAsset.ToString(),
                    MediaType = result.DetectedType.ToString(),
                    Pass = "Quick",
                    IngestionRunId = ingestionRunId,
                }, ct).ConfigureAwait(false);
                _identityStageDependencies.Signal?.Signal(IdentityPipelineSignalKind.Retail);
            }
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to re-read local metadata for changed same-path file {Path}", filePath);
            return false;
        }
    }

    private async Task RunProcessStageAsync(IngestionPipelineContext context, CancellationToken ct)
    {
        var candidate = context.Candidate;
        var ingestionRunId = context.IngestionRunId;
        var durableOperation = context.DurableOperation;
        var logEntryId = context.LogEntryId;
        var hash = context.Hash!;
        // Step 6: process.
        await UpdateOperationStageAsync(durableOperation, MediaOperationStage.Parsing, 25, "Parsing media file.", ct).ConfigureAwait(false);
        var result = await _processors.ProcessAsync(candidate.Path, ct).ConfigureAwait(false);

        // Step 6a: organisation-hint prescan.
        // Side-by-side-with-Plex plan §G. Pulls Plex / Jellyfin bridge ID
        // brackets (`{imdb-tt...}`, `[tvdbid-...]`, etc.) and the Tuvima
        // legacy `(Q12345)` marker straight out of the path and injects
        // them as high-confidence claims. When the library is curated,
        // retail Stage 1 and Wikidata Stage 2 can short-circuit external
        // lookups using the seeded IDs.
        var pathHints = OrganizationHintParser.Parse(candidate.Path);
        if (pathHints.HasHints && pathHints.BridgeIds.Count > 0)
        {
            var hintedClaims = new List<Processors.Models.ExtractedClaim>(result.Claims);
            foreach (var (key, value) in pathHints.BridgeIds)
            {
                // Don't overwrite a bridge ID the processor already extracted
                // from embedded tags — embedded data is at least as reliable.
                if (hintedClaims.Any(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                hintedClaims.Add(new Processors.Models.ExtractedClaim
                {
                    Key = key,
                    Value = value,
                    Confidence = ClaimConfidence.BridgeId,
                });
            }

            result = new Processors.Models.ProcessorResult
            {
                FilePath = result.FilePath,
                DetectedType = result.DetectedType,
                Claims = hintedClaims,
                CoverImage = result.CoverImage,
                CoverImageMimeType = result.CoverImageMimeType,
                IsCorrupt = result.IsCorrupt,
                CorruptReason = result.CorruptReason,
                MediaTypeCandidates = result.MediaTypeCandidates,
            };

            _logger.LogInformation(
                "OrganizationHintParser: seeded {Count} bridge ID claim(s) from path for {File} — {Keys}",
                pathHints.BridgeIds.Count, Path.GetFileName(candidate.Path),
                string.Join(", ", pathHints.BridgeIds.Keys));
        }

        {
            var extractedTitle = result.Claims
                .FirstOrDefault(c => c.Key.Equals(MetadataFieldConstants.Title, StringComparison.OrdinalIgnoreCase))?.Value;
            _logger.LogInformation(
                "Processed \"{FileName}\" via {ProcessorType} — {ClaimCount} claims extracted (title='{Title}')",
                Path.GetFileName(candidate.Path),
                result.DetectedType,
                result.Claims.Count,
                extractedTitle ?? "(none)");
        }

        await SafeActivityLogAsync(new Domain.Entities.SystemActivityEntry
        {
            ActionType = Domain.Constants.SystemActionType.FileProcessed,
            EntityType = "MediaAsset",
            Detail = $"Scanned {Path.GetFileName(candidate.Path)}: {result.DetectedType} - {result.Claims.Count} fields, cover {(result.CoverImage?.Length > 0 ? "found" : "absent")}",
            ChangesJson = JsonSerializer.Serialize(new
            {
                detected_type = result.DetectedType.ToString(),
                claims_count = result.Claims.Count,
                has_cover = result.CoverImage?.Length > 0,
                cover_bytes = result.CoverImage?.Length ?? 0,
                is_corrupt = result.IsCorrupt,
                corrupt_reason = result.CorruptReason,
                filename = Path.GetFileName(candidate.Path),
            }),
            IngestionRunId = ingestionRunId,
        }, ct).ConfigureAwait(false);

        try
        {
            await _ingestionLog.UpdateStatusAsync(
                logEntryId,
                "processed",
                mediaType: result.DetectedType.ToString(),
                ct: ct).ConfigureAwait(false);
            await PublishItemProgressAsync(candidate, logEntryId, "processed", 35, false, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Ingestion log update failed — continuing"); }
        await UpdateOperationStageAsync(durableOperation, MediaOperationStage.Parsing, 35, "Processor extracted metadata.", ct, new { media_type = result.DetectedType.ToString(), claims = result.Claims.Count }).ConfigureAwait(false);
        // Step 6b: AI Smart Labeling — enhance title claim using LLM.
        // The SmartLabeler understands context that regex cannot:
        // "2001 A Space Odyssey" keeps the year, "Frank Herbert - Dune" extracts the author.
        {
            var rawTitle = result.Claims.FirstOrDefault(c =>
                c.Key.Equals(MetadataFieldConstants.Title, StringComparison.OrdinalIgnoreCase));

            if (rawTitle is not null && context.Library?.MediaTypes.Any(t => t is MediaType.Audiobooks or MediaType.Music) != true)
            {
                try
                {
                    var cleaned = await _smartLabeler.CleanAsync(
                        Path.GetFileNameWithoutExtension(candidate.Path), ct).ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(cleaned.Title) && cleaned.Confidence > 0.5)
                    {
                        // Replace the title claim with the AI-cleaned version at higher confidence.
                        var updatedClaims = result.Claims
                            .Where(c => !c.Key.Equals(MetadataFieldConstants.Title, StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        updatedClaims.Add(new Processors.Models.ExtractedClaim
                        {
                            Key = MetadataFieldConstants.Title,
                            Value = cleaned.Title,
                            Confidence = Math.Max(rawTitle.Confidence, cleaned.Confidence),
                        });

                        // Add author if extracted and not already present.
                        if (!string.IsNullOrWhiteSpace(cleaned.Author)
                            && !result.Claims.Any(c => c.Key.Equals(MetadataFieldConstants.Author, StringComparison.OrdinalIgnoreCase)))
                        {
                            updatedClaims.Add(new Processors.Models.ExtractedClaim
                            {
                                Key = MetadataFieldConstants.Author,
                                Value = cleaned.Author,
                                Confidence = cleaned.Confidence * 0.9,
                            });
                        }

                        // Add year if extracted and not already present.
                        if (cleaned.Year.HasValue
                            && !result.Claims.Any(c => c.Key.Equals(MetadataFieldConstants.Year, StringComparison.OrdinalIgnoreCase)
                                                    || c.Key.Equals("release_year", StringComparison.OrdinalIgnoreCase)))
                        {
                            updatedClaims.Add(new Processors.Models.ExtractedClaim
                            {
                                Key = "release_year",
                                Value = cleaned.Year.Value.ToString(),
                                Confidence = cleaned.Confidence * 0.85,
                            });
                        }

                        // Add season number if extracted and not already present.
                        if (cleaned.Season.HasValue
                            && !updatedClaims.Any(c => c.Key.Equals(MetadataFieldConstants.SeasonNumber, StringComparison.OrdinalIgnoreCase)))
                        {
                            updatedClaims.Add(new Processors.Models.ExtractedClaim
                            {
                                Key = MetadataFieldConstants.SeasonNumber,
                                Value = cleaned.Season.Value.ToString(),
                                Confidence = cleaned.Confidence,
                            });
                        }

                        // Add episode number if extracted and not already present.
                        if (cleaned.Episode.HasValue
                            && !updatedClaims.Any(c => c.Key.Equals(MetadataFieldConstants.EpisodeNumber, StringComparison.OrdinalIgnoreCase)))
                        {
                            updatedClaims.Add(new Processors.Models.ExtractedClaim
                            {
                                Key = MetadataFieldConstants.EpisodeNumber,
                                Value = cleaned.Episode.Value.ToString(),
                                Confidence = cleaned.Confidence,
                            });
                        }

                        result = new Processors.Models.ProcessorResult
                        {
                            FilePath = result.FilePath,
                            DetectedType = result.DetectedType,
                            Claims = updatedClaims,
                            CoverImage = result.CoverImage,
                            CoverImageMimeType = result.CoverImageMimeType,
                            IsCorrupt = result.IsCorrupt,
                            CorruptReason = result.CorruptReason,
                            MediaTypeCandidates = result.MediaTypeCandidates,
                        };

                        _logger.LogInformation(
                            "SmartLabeler enhanced title: \"{OldTitle}\" ? \"{NewTitle}\" (confidence: {Conf:F2})",
                            rawTitle.Value, cleaned.Title, cleaned.Confidence);
                    }
                }
                catch (Exception ex)
                {
                    // SmartLabeler failure is non-fatal — processor claims stand.
                    _logger.LogWarning(ex, "SmartLabeler failed for {Path} — using processor title", candidate.Path);
                }
            }
        }

        // Step 7: quarantine corrupt files.
        if (result.IsCorrupt)
        {
            _logger.LogWarning("Corrupt file quarantined: {Path} — {Reason}",
                candidate.Path, result.CorruptReason);

            // Activity: media failed (replaces granular FileQuarantined).
            var failedJson = JsonSerializer.Serialize(new
            {
                source_file = Path.GetFileName(candidate.Path),
                reason = result.CorruptReason,
                error_type = "corrupt_file",
            });
            await SafeActivityLogAsync(new Domain.Entities.SystemActivityEntry
            {
                ActionType = Domain.Constants.SystemActionType.MediaFailed,
                EntityType = "MediaAsset",
                ChangesJson = failedJson,
                Detail = $"Failed — {Path.GetFileName(candidate.Path)}: {result.CorruptReason}",
                IngestionRunId = ingestionRunId,
            }, ct).ConfigureAwait(false);

            await SafePublishAsync(SignalREvents.IngestionFailed, new IngestionFailedEvent(
                candidate.Path,
                $"Corrupt: {result.CorruptReason}",
                DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
            try
            {
                await _ingestionLog.UpdateStatusAsync(
                    logEntryId,
                    "failed",
                    errorDetail: $"Corrupt: {result.CorruptReason}",
                    ct: ct).ConfigureAwait(false);
                await PublishItemProgressAsync(candidate, logEntryId, "failed", 100, true, ct).ConfigureAwait(false);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Ingestion log update failed — continuing"); }
            if (candidate.BatchId.HasValue)
            {
                await SafeIncrementBatchCounterAsync(candidate.BatchId.Value, BatchCounterColumn.FilesFailed, ct).ConfigureAwait(false);
                await SafeIncrementBatchCounterAsync(candidate.BatchId.Value, BatchCounterColumn.FilesProcessed, ct).ConfigureAwait(false);
                await PublishQueuedBatchSnapshotAsync(candidate.BatchId.Value, ct).ConfigureAwait(false);
            }
            await NoResultOperationAsync(
                durableOperation,
                $"Corrupt media file: {result.CorruptReason ?? "processor rejected the file"}",
                ct).ConfigureAwait(false);
            context.Complete();
            return;
        }

        context.ProcessorResult = result;

    }
}
