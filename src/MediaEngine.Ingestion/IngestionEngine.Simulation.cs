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
    /// <inheritdoc/>
    public async Task<IReadOnlyList<PendingOperation>> DryRunAsync(
        string rootPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var operations = new List<PendingOperation>();

        var files = Directory.EnumerateFiles(rootPath, "*.*", SearchOption.AllDirectories);

        foreach (var filePath in files)
        {
            ct.ThrowIfCancellationRequested();

            // A Vault file is never listed, hashed or read by a dry run (Videos spec D17): it is absent from the answer.
            if (await IsVaultFilePathAsync(filePath, ct).ConfigureAwait(false))
            {
                continue;
            }

            try
            {
                var ops = await SimulateFileAsync(filePath, ct).ConfigureAwait(false);
                operations.AddRange(ops);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DryRun: error simulating {Path}", filePath);
            }
        }

        return operations;
    }
    private async Task<IEnumerable<PendingOperation>> SimulateFileAsync(
        string filePath, CancellationToken ct)
    {
        var ops = new List<PendingOperation>();

        // Hash (read-only — no side effects).
        var hash = await _hasher.ComputeAsync(filePath, ct).ConfigureAwait(false);

        // Duplicate check.
        var existing = await _assetRepo.FindByHashAsync(hash.Hex, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            ops.Add(new PendingOperation
            {
                SourcePath = filePath,
                DestinationPath = filePath,
                OperationKind = "Skip",
                Reason = $"Duplicate of existing asset (hash={hash.Hex[..12]})",
            });
            return ops;
        }

        // Process.
        var result = await _processors.ProcessAsync(filePath, ct).ConfigureAwait(false);
        if (result.IsCorrupt)
        {
            ops.Add(new PendingOperation
            {
                SourcePath = filePath,
                DestinationPath = filePath,
                OperationKind = "Quarantine",
                Reason = result.CorruptReason,
            });
            return ops;
        }

        // Build a minimal candidate for path calculation.
        var assetId = Guid.NewGuid();
        var claims = BuildClaims(assetId, result);
        var scored = await _scorer.ScoreEntityAsync(new ScoringContext
        {
            EntityId = assetId,
            Claims = claims,
            ProviderWeights = new Dictionary<Guid, double> { [LocalProcessorProviderId] = 1.0 },
            Configuration = _scoringConfig,
        }, ct).ConfigureAwait(false);

        var candidate = new IngestionCandidate
        {
            Path = filePath,
            EventType = FileEventType.Created,
            DetectedAt = DateTimeOffset.UtcNow,
            ReadyAt = DateTimeOffset.UtcNow,
        };
        candidate.Metadata = BuildMetadataDict(scored);
        candidate.DetectedMediaType = result.DetectedType;

        // Simulate move.
        if (_options.AutoOrganize && !string.IsNullOrWhiteSpace(_options.LibraryRoot))
        {
            var dryRunTemplate = _options.ResolveTemplate(candidate.DetectedMediaType?.ToString());
            var dryRunLibrary = _libraryFolderResolver?.ResolveForPath(filePath);
            var relative = LibraryOrganizationPath.Calculate(_organizer, candidate, dryRunLibrary, dryRunTemplate);
            // The template names the path inside the type's own folder, including the filename.
            var destPath = Path.Combine(
                LibraryOrganizationPath.TypeFolder(dryRunLibrary, _options.LibraryRoot, candidate.DetectedMediaType), relative);

            ops.Add(new PendingOperation
            {
                SourcePath = filePath,
                DestinationPath = destPath,
                OperationKind = "Move",
                Reason = $"AutoOrganize template: {dryRunTemplate}",
            });
        }

        // Simulate write-back.
        if (_options.WriteBack && candidate.Metadata is not null)
        {
            var tagger = _taggers.FirstOrDefault(t => t.CanHandle(filePath));
            if (tagger is not null)
            {
                ops.Add(new PendingOperation
                {
                    SourcePath = filePath,
                    DestinationPath = filePath,
                    OperationKind = "WriteTag",
                    Reason = $"Tagger: {tagger.GetType().Name}; " +
                                      $"{candidate.Metadata.Count} tag(s)",
                });

                if (result.CoverImage is { Length: > 0 })
                {
                    ops.Add(new PendingOperation
                    {
                        SourcePath = filePath,
                        DestinationPath = filePath,
                        OperationKind = "WriteCoverArt",
                        Reason = $"Cover image {result.CoverImage.Length} bytes",
                    });
                }
            }
        }

        return ops;
    }

    // =========================================================================
    // Initial directory scan
    // =========================================================================

    /// <summary>
    /// Enumerates every file already present in the Watch Folder and synthesises
    /// a <see cref="FileEvent.Created"/> for each one, feeding them into the
    /// <see cref="DebounceQueue"/>.  This ensures files that were dropped into the
    /// folder before the Engine started are processed through the normal pipeline.
    ///
    /// Duplicates are harmless: step 5 (hash-based duplicate check) in
    /// <see cref="ProcessCandidateAsync"/> short-circuits them instantly.
    /// </summary>
    private async Task TryReorganizeExistingAsync(
        MediaAsset existing, string currentPath, CancellationToken ct)
    {
        // Only attempt if the file is currently in one of the configured source folders.
        var sourceRoot = ResolveContainingWatchDirectory(currentPath);
        if (string.IsNullOrWhiteSpace(sourceRoot))
        {
            _logger.LogDebug(
                "Duplicate (hash={Hash}) not in a configured source folder; skipping: {Path}",
                existing.ContentHash[..12], currentPath);
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.LibraryRoot))
        {
            _logger.LogInformation(
                "Cannot re-organize {Hash}: LibraryRoot is not configured. " +
                "Set a Library Folder in Server Settings.",
                existing.ContentHash[..12]);
            return;
        }

        // Load existing canonical values — these contain the resolved metadata
        // (possibly enriched by external providers since the initial scan).
        var canonicals = await _canonicalRepo.GetByEntityAsync(existing.Id, ct)
                                             .ConfigureAwait(false);
        if (canonicals.Count == 0)
        {
            _logger.LogInformation(
                "Cannot re-organize {Hash}: no canonical values found for asset {Id}.",
                existing.ContentHash[..12], existing.Id);
            return;
        }

        var metadata = canonicals.ToDictionary(
            c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);

        // Determine media type from canonical values or fall back to Unknown.
        var mediaType = metadata.TryGetValue(MetadataFieldConstants.MediaTypeField, out var mtStr)
            && Enum.TryParse<MediaType>(mtStr, ignoreCase: true, out var mt)
                ? mt
                : (MediaType?)null;

        // Confidence gate: only re-organize when metadata is trustworthy.
        var reorgClaims = await _claimRepo.GetByEntityAsync(existing.Id, ct).ConfigureAwait(false);
        var reorgScoringContext = new ScoringContext
        {
            EntityId = existing.Id,
            Claims = reorgClaims,
            ProviderWeights = new Dictionary<Guid, double>
            { [LocalProcessorProviderId] = 1.0 },
            Configuration = _scoringConfig,
        };
        var reorgScored = await _scorer.ScoreEntityAsync(reorgScoringContext, ct).ConfigureAwait(false);
        bool reorgHasUserLock = reorgClaims.Any(c => c.IsUserLocked);
        bool reorgHighConfidence = reorgScored.OverallConfidence >= _scoringConfig.AutoLinkThreshold;
        if (!reorgHighConfidence && !reorgHasUserLock)
        {
            _logger.LogInformation(
                "Re-organize skipped for {Hash}: confidence {Confidence:P0} below threshold ({Threshold:P0})",
                existing.ContentHash[..12], reorgScored.OverallConfidence, _scoringConfig.AutoLinkThreshold);

            // Move to staging so the file doesn't loop on every poll sweep.
            // Only fires when the file is still in the Watch Folder (MoveToStagingAsync
            // is a no-op for files already in staging or the Library).
            string lowSubcategory = reorgScored.OverallConfidence < 0.40
                ? "unidentifiable"
                : "low-confidence";
            var lowStaged = await MoveToStagingAsync(currentPath, lowSubcategory, ct, existing.Id)
                                     .ConfigureAwait(false);
            if (lowStaged is not null)
            {
                CleanEmptyWatchParents(currentPath, sourceRoot);
                await _assetRepo.UpdateFilePathAsync(existing.Id, lowStaged, ct)
                                 .ConfigureAwait(false);
                await CreateIngestionReviewItemAsync(
                    existing.Id, ReviewTrigger.LowConfidence,
                    reorgScored.OverallConfidence,
                    $"Confidence {reorgScored.OverallConfidence:P0} below organization " +
                    "threshold. Staged for review.",
                    ct).ConfigureAwait(false);
            }
            return;
        }

        var reorgLibrary = _libraryFolderResolver?.ResolveForPath(currentPath);
        bool filesAsOther = string.Equals(
            LibraryOrganizationPath.FilingCategory(reorgLibrary, mediaType),
            LibraryOrganizationPath.UnknownCategory,
            StringComparison.OrdinalIgnoreCase);

        // Guard: never re-organize into the "Other" category. If the media type
        // couldn't be determined from canonical values, move to staging and create
        // a review item so the user can manually classify the file.
        if (filesAsOther)
        {
            _logger.LogWarning(
                "Re-organization blocked for {Hash} — resolved category is 'Other'. " +
                "Moving to staging and creating review item.",
                existing.ContentHash[..12]);

            var otherStaged = await MoveToStagingAsync(currentPath, "low-confidence", ct, existing.Id).ConfigureAwait(false);
            if (otherStaged is not null)
            {
                CleanEmptyWatchParents(currentPath, sourceRoot);
                await _assetRepo.UpdateFilePathAsync(existing.Id, otherStaged, ct).ConfigureAwait(false);
            }

            await CreateIngestionReviewItemAsync(
                existing.Id, ReviewTrigger.LowConfidence, 0.0,
                $"Re-organization would place file in 'Other' category (media type unknown). " +
                "File moved to staging for manual classification.",
                ct).ConfigureAwait(false);
            return;
        }

        // Placeholder title guard: stage as low-confidence when the title is a
        // well-known placeholder and no bridge ID confirms identity.
        string? reorgTitle = metadata.GetValueOrDefault(MetadataFieldConstants.Title);
        bool isPlaceholder = MetadataGuards.IsPlaceholderTitle(reorgTitle)
            && !MetadataGuards.HasBridgeId(metadata);

        // Staging-first: move to staging rather than directly to library.
        // AutoOrganizeService will promote to library after hydration.
        string subcategory = isPlaceholder ? "low-confidence"
            : filesAsOther ? "other"
            : "pending";

        var staged = await MoveToStagingAsync(currentPath, subcategory, ct, existing.Id)
                          .ConfigureAwait(false);
        if (staged is not null)
        {
            CleanEmptyWatchParents(currentPath, sourceRoot);
            await _assetRepo.UpdateFilePathAsync(existing.Id, staged, ct)
                            .ConfigureAwait(false);

            _logger.LogInformation(
                "Staged existing asset {Hash} ? {Dest} ({Subcategory})",
                existing.ContentHash[..12], staged, subcategory);

            // Re-enqueue hydration so AutoOrganizeService can promote after enrichment.
            await _identityJobRepo.CreateAsync(new Domain.Entities.IdentityJob
            {
                EntityId = existing.Id,
                EntityType = EntityType.MediaAsset.ToString(),
                MediaType = (mediaType ?? MediaType.Unknown).ToString(),
                Pass = "Quick",
            }, ct).ConfigureAwait(false);
            _identityStageDependencies.Signal?.Signal(IdentityPipelineSignalKind.Retail);

            if (isPlaceholder)
            {
                await CreateIngestionReviewItemAsync(
                    existing.Id, ReviewTrigger.PlaceholderTitle, reorgScored.OverallConfidence,
                    $"Title \"{reorgTitle ?? "(blank)"}\" is a placeholder with no bridge IDs. Staged for review.",
                    ct).ConfigureAwait(false);
            }
        }
    }

    // =========================================================================
    // Staging
    // =========================================================================

    /// <summary>
    /// Staging eliminated (Phase 3): files stay in the watch folder during initial
    /// processing and are moved directly to the library by AutoOrganizeService after
    /// Stage 1 retail match produces a resolved title.
    /// This method is retained as a no-op so existing call sites in the re-organize
    /// path compile without change — all callers already handle the null return.
    /// </summary>
    private static Task<string?> MoveToStagingAsync(
        string currentPath, string subcategory, CancellationToken ct,
        Guid? assetId = null)
    {
        return Task.FromResult<string?>(null);
    }

    // =========================================================================
    // Staged asset cleanup
    // =========================================================================

    /// <summary>
    /// Cleans up a staged asset whose file no longer exists on disk.
    /// Removes filesystem artifacts (cover.jpg, empty folders)
    /// and deletes all associated DB records (claims, canonical values, asset).
    /// </summary>
    private async Task CleanStagedAssetAsync(
        MediaAsset staged, CancellationToken ct)
    {
        _logger.LogInformation(
            "Cleaning staged asset {Id} — file missing at {Path}",
            staged.Id, staged.FilePathRoot);

        // 1. Clean filesystem artifacts from the edition folder.
        var editionFolder = Path.GetDirectoryName(staged.FilePathRoot);
        if (!string.IsNullOrEmpty(editionFolder) && Directory.Exists(editionFolder))
        {
            SafeDeleteFile(Path.Combine(editionFolder, "cover.jpg"));
            TryDeleteEmptyDirectory(editionFolder);
        }

        // 2. Delete DB records: claims ? canonical values ? asset.
        await _claimRepo.DeleteByEntityAsync(staged.Id, ct).ConfigureAwait(false);
        await _canonicalRepo.DeleteByEntityAsync(staged.Id, ct).ConfigureAwait(false);
        await _assetRepo.DeleteAsync(staged.Id, ct).ConfigureAwait(false);

        // 3. Log activity.
        await SafeActivityLogAsync(new Domain.Entities.SystemActivityEntry
        {
            ActionType = Domain.Constants.SystemActionType.StagedFileCleaned,
            EntityId = staged.Id,
            EntityType = "MediaAsset",
            Detail = $"Staged asset cleaned: {Path.GetFileName(staged.FilePathRoot)} (file missing)",
        }, ct).ConfigureAwait(false);
    }

    private static void SafeDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort: if the file is locked, skip silently.
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) &&
                !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort: if the directory is locked, skip silently.
        }
    }

    /// <summary>
    /// Recursively deletes empty parent directories of a source file up to (but
    /// not including) the watch folder root. Prevents empty subdirectories from
    /// accumulating in the watch folder after files are moved to the library.
    /// </summary>
    private static void CleanEmptyWatchParents(string sourceFilePath, string? watchRoot)
    {
        if (string.IsNullOrEmpty(watchRoot))
        {
            return;
        }

        try
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
            var stopNorm = watchRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            while (dir is not null && dir.Exists)
            {
                var dirNorm = dir.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                // Stop when we reach the watch root — never delete it.
                if (string.Equals(dirNorm, stopNorm, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (dir.EnumerateFileSystemInfos().Any())
                {
                    break; // Not empty — stop climbing.
                }

                var parent = dir.Parent;
                dir.Delete();
                dir = parent;
            }
        }
        catch
        {
            // Best-effort cleanup — if the directory is locked or inaccessible, skip.
        }
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static IReadOnlyList<MetadataClaim> BuildClaims(
        Guid entityId,
        Processors.Models.ProcessorResult result)
    {
        // One file read, one timestamp, so tied claims keep the file's own order (first author first).
        var claimedAt = DateTimeOffset.UtcNow;
        return result.Claims
            .Select(c => new MetadataClaim
            {
                Id = Guid.NewGuid(),
                EntityId = entityId,
                ProviderId = LocalProcessorProviderId,
                ClaimKey = c.Key,
                ClaimValue = TextEncodingRepair.RepairMojibake(c.Value),
                Confidence = c.Confidence,
                ClaimedAt = claimedAt,
            })
            .ToList();
    }

    private static IReadOnlyDictionary<string, string> BuildMetadataDict(
        Intelligence.Models.ScoringResult scored)
    {
        return scored.FieldScores
            .Where(f => !string.IsNullOrEmpty(f.WinningValue))
            .ToDictionary(
                f => f.Key,
                f => TextEncodingRepair.RepairMojibake(f.WinningValue!),
                StringComparer.OrdinalIgnoreCase);
    }


    /// <summary>
    /// Extracts author and narrator person references from resolved metadata.
    /// Returns an empty list if neither field is present.
    /// </summary>
    private static IReadOnlyList<PersonReference> ExtractPersonReferences(
        IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null or { Count: 0 })
        {
            return [];
        }

        var refs = new List<PersonReference>();

        if (metadata.TryGetValue(MetadataFieldConstants.Author, out var author) &&
            !string.IsNullOrWhiteSpace(author))
        {
            refs.Add(new PersonReference("Author", author.Trim()));
        }

        if (metadata.TryGetValue(MetadataFieldConstants.Narrator, out var narrator) &&
            !string.IsNullOrWhiteSpace(narrator))
        {
            refs.Add(new PersonReference("Narrator", narrator.Trim()));
        }

        return refs;
    }

    /// <summary>True when the entity is a file in a Vault library. Such a file never gets a review item (Videos spec D32).</summary>
    private async Task<bool> IsVaultEntityAsync(Guid entityId, CancellationToken ct) =>
        _vaultHook is not null && await _vaultHook.IsVaultAssetAsync(entityId, ct).ConfigureAwait(false);

    /// <summary>
    /// Creates a review queue entry from the ingestion pipeline. Used when the
    /// confidence gate blocks organization or when the file would be placed in
    /// the "Other" category.
    /// </summary>
    private async Task CreateIngestionReviewItemAsync(
        Guid entityId,
        string trigger,
        double confidence,
        string detail,
        CancellationToken ct)
    {
        // A Vault file never enters Needs Review (Videos spec D32): a doubtful one simply stays Unmatched.
        if (await IsVaultEntityAsync(entityId, ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            if (_organizeStageDependencies.StageOutcomeFactory is not null)
            {
                await _organizeStageDependencies.StageOutcomeFactory.CreateProvisionalAsync(
                    entityId,
                    trigger,
                    confidence,
                    detail,
                    ct).ConfigureAwait(false);
                return;
            }

            // Check if a pending review item already exists for this entity
            // (the hydration pipeline may also create one asynchronously).
            var existing = await _reviewRepo.GetByEntityAsync(entityId, ct)
                .ConfigureAwait(false);

            if (existing.Any(r => r.Status == Domain.Constants.ReviewStatus.Pending
                                  && r.Trigger == trigger))
            {
                _logger.LogDebug(
                    "Review item '{Trigger}' already exists for entity {Id} — skipping duplicate.",
                    trigger, entityId);
                return;
            }

            var entry = new ReviewQueueEntry
            {
                Id = Guid.NewGuid(),
                EntityId = entityId,
                EntityType = "MediaAsset",
                Trigger = trigger,
                ConfidenceScore = confidence,
                Detail = detail,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            await _reviewRepo.InsertAsync(entry, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Hidden provisional review item parked for entity {Id}: {Trigger} — {Detail}",
                entityId, trigger, detail);
        }
        catch (Exception ex)
        {
            // Review item creation failure must not abort the ingestion pipeline.
            _logger.LogWarning(ex,
                "Failed to create review item for entity {Id}", entityId);
        }
    }

    /// <summary>
    /// Creates an <see cref="ReviewTrigger.AmbiguousMediaType"/> review queue entry
    /// with the full list of media type candidates serialized as JSON.
    /// </summary>
    private async Task CreateAmbiguousMediaTypeReviewItemAsync(
        Guid entityId,
        double confidence,
        string candidatesJson,
        string detail,
        CancellationToken ct)
    {
        if (await IsVaultEntityAsync(entityId, ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            if (_organizeStageDependencies.StageOutcomeFactory is not null)
            {
                await _organizeStageDependencies.StageOutcomeFactory.CreateProvisionalAsync(
                    entityId,
                    ReviewTrigger.AmbiguousMediaType,
                    confidence,
                    detail,
                    ct,
                    candidatesJson: candidatesJson).ConfigureAwait(false);
                return;
            }

            var existing = await _reviewRepo.GetByEntityAsync(entityId, ct)
                .ConfigureAwait(false);

            if (existing.Any(r => r.Status == Domain.Constants.ReviewStatus.Pending
                                  && r.Trigger == ReviewTrigger.AmbiguousMediaType))
            {
                _logger.LogDebug(
                    "AmbiguousMediaType review item already exists for entity {Id} — skipping.",
                    entityId);
                return;
            }

            var entry = new ReviewQueueEntry
            {
                Id = Guid.NewGuid(),
                EntityId = entityId,
                EntityType = "MediaAsset",
                Trigger = ReviewTrigger.AmbiguousMediaType,
                ConfidenceScore = confidence,
                CandidatesJson = candidatesJson,
                Detail = detail,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            await _reviewRepo.InsertAsync(entry, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Hidden AmbiguousMediaType review item parked for entity {Id}: {Detail}",
                entityId, detail);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to create AmbiguousMediaType review item for entity {Id}", entityId);
        }
    }

    /// <summary>
    /// Creates a <see cref="ReviewTrigger.MetadataConflict"/> review queue entry
    /// when the scoring engine detects conflicting canonical values. The file still
    /// organises with the best guess — conflicts don't block the confidence gate.
    /// </summary>
    private async Task CreateMetadataConflictReviewItemAsync(
        Guid entityId,
        double confidence,
        List<string> conflictedFields,
        CancellationToken ct)
    {
        if (await IsVaultEntityAsync(entityId, ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var detail = $"Conflicting metadata: {string.Join(", ", conflictedFields)}";
            if (_organizeStageDependencies.StageOutcomeFactory is not null)
            {
                await _organizeStageDependencies.StageOutcomeFactory.CreateProvisionalAsync(
                    entityId,
                    ReviewTrigger.MetadataConflict,
                    confidence,
                    detail,
                    ct).ConfigureAwait(false);
                return;
            }

            var existing = await _reviewRepo.GetByEntityAsync(entityId, ct)
                .ConfigureAwait(false);

            if (existing.Any(r => r.Status == Domain.Constants.ReviewStatus.Pending
                                  && r.Trigger == ReviewTrigger.MetadataConflict))
            {
                _logger.LogDebug(
                    "MetadataConflict review item already exists for entity {Id} — skipping.",
                    entityId);
                return;
            }

            var entry = new ReviewQueueEntry
            {
                Id = Guid.NewGuid(),
                EntityId = entityId,
                EntityType = "MediaAsset",
                Trigger = ReviewTrigger.MetadataConflict,
                ConfidenceScore = confidence,
                Detail = detail,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            await _reviewRepo.InsertAsync(entry, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Hidden MetadataConflict review item parked for entity {Id}: {Detail}",
                entityId, detail);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to create MetadataConflict review item for entity {Id}", entityId);
        }
    }

    /// <summary>True when the file belongs to a Vault library: while it is processed, and also when a failure is recorded after processing ended.</summary>
    private async Task<bool> IsVaultPathAsync(IngestionCandidate candidate, CancellationToken ct)
    {
        if (_vaultHook is null)
        {
            return false;
        }

        if (_vaultCandidatePaths.ContainsKey(candidate.Path))
        {
            return true;
        }

        var library = candidate.Intake?.HasDestinationHint == true
            ? _libraryFolderResolver?.ResolveById(candidate.Intake.DestinationLibraryId!)
            : _libraryFolderResolver?.ResolveForPath(candidate.Path);
        return await _vaultHook.IsVaultLibraryAsync(library?.Id, ct).ConfigureAwait(false);
    }

    /// <summary>True when a file path (found by the folder watcher, before any intake exists for it) sits in a Vault library's folders.</summary>
    private async Task<bool> IsVaultFilePathAsync(string path, CancellationToken ct)
    {
        if (_vaultHook is null)
        {
            return false;
        }

        if (_vaultCandidatePaths.ContainsKey(path))
        {
            return true;
        }

        return await _vaultHook.IsVaultLibraryAsync(_libraryFolderResolver?.ResolveForPath(path)?.Id, ct).ConfigureAwait(false);
    }

    /// <summary>True for an ingestion event that names a file currently being processed from a Vault library.</summary>
    private bool IsAboutVaultFile<TPayload>(TPayload payload) where TPayload : notnull =>
        !_vaultCandidatePaths.IsEmpty
        && payload switch
        {
            IngestionStartedEvent e => _vaultCandidatePaths.ContainsKey(e.FilePath),
            IngestionHashedEvent e => _vaultCandidatePaths.ContainsKey(e.FilePath),
            IngestionCompletedEvent e => _vaultCandidatePaths.ContainsKey(e.FilePath),
            IngestionFailedEvent e => _vaultCandidatePaths.ContainsKey(e.FilePath),
            IngestionItemProgressEvent e => _vaultCandidatePaths.ContainsKey(e.FilePath),
            _ => false,
        };

    /// <summary>
    /// Publishes an event without propagating exceptions to the calling pipeline.
    /// A publish failure (e.g. transient SignalR error) must never abort file ingestion.
    /// </summary>
    private async Task SafePublishAsync<TPayload>(
        string eventName, TPayload payload, CancellationToken ct)
        where TPayload : notnull
    {
        // Nothing about a file in a Vault library is announced to open screens (Videos spec D17, D32): its progress is shown only
        // in the quiet Vault activity list.
        if (IsAboutVaultFile(payload))
        {
            return;
        }

        try
        {
            await _publisher.PublishAsync(eventName, payload, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Event publish failed for '{Event}' — pipeline continues", eventName);
        }
    }

    private CancellationToken LifetimeToken => _shutdownCts.Token;

}
