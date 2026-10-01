using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion.Services;

/// <summary>
/// Writes resolved canonical metadata back into physical media files.
///
/// Loads configuration from <c>config/writeback.json</c> via the generic
/// <see cref="IConfigurationLoader.LoadConfig{T}"/> method.
///
/// Selects the correct <see cref="IMetadataTagger"/> for the file format
/// and delegates the write operation. Respects the write-back configuration
/// (enabled toggle, trigger-specific flags, field filtering).
/// </summary>
public sealed class WriteBackService : IWriteBackOutcomeService
{
    private readonly IMediaAssetRepository _assetRepo;
    private readonly ICanonicalValueRepository _canonicalRepo;
    private readonly IWorkRepository _workRepo;
    private readonly IConfigurationLoader _configLoader;
    private readonly IEnumerable<IMetadataTagger> _taggers;
    private readonly ISystemActivityRepository _activityRepo;
    private readonly WritebackConfigState? _hashState;
    private readonly IEnrichmentConcurrencyLimiter _concurrency;
    private readonly ILibraryFolderResolver? _libraryResolver;
    private readonly ISourceMutationPolicyGate _sourceMutationGate;
    private readonly IAssetHasher? _assetHasher;
    private readonly IFileHashCacheRepository? _fileHashCache;
    private readonly ILogger<WriteBackService> _logger;

    public WriteBackService(
        IMediaAssetRepository assetRepo,
        ICanonicalValueRepository canonicalRepo,
        IWorkRepository workRepo,
        IConfigurationLoader configLoader,
        IEnumerable<IMetadataTagger> taggers,
        ISystemActivityRepository activityRepo,
        ILogger<WriteBackService> logger,
        WritebackConfigState? hashState = null,
        IEnrichmentConcurrencyLimiter? concurrencyLimiter = null,
        ILibraryFolderResolver? libraryResolver = null,
        ISourceMutationPolicyGate? sourceMutationGate = null,
        IAssetHasher? assetHasher = null,
        IFileHashCacheRepository? fileHashCache = null)
    {
        _assetRepo = assetRepo;
        _canonicalRepo = canonicalRepo;
        _workRepo = workRepo;
        _configLoader = configLoader;
        _taggers = taggers;
        _activityRepo = activityRepo;
        _hashState = hashState;
        _concurrency = concurrencyLimiter ?? NoopEnrichmentConcurrencyLimiter.Instance;
        _libraryResolver = libraryResolver;
        _sourceMutationGate = sourceMutationGate ?? new SourceMutationPolicyGate();
        _assetHasher = assetHasher;
        _fileHashCache = fileHashCache;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task WriteMetadataAsync(Guid assetId, string trigger, CancellationToken ct = default, Guid? ingestionRunId = null) =>
        _ = await WriteMetadataWithOutcomeAsync(assetId, trigger, ct, ingestionRunId);

    public Task<WriteBackOutcome> WriteMetadataWithOutcomeAsync(Guid assetId, string trigger,
        CancellationToken ct = default, Guid? ingestionRunId = null) =>
        _concurrency.RunAsync(
            EnrichmentWorkKind.WriteBack,
            token => WriteMetadataCoreAsync(assetId, trigger, token, ingestionRunId),
            ct);

    private async Task<WriteBackOutcome> WriteMetadataCoreAsync(Guid assetId, string trigger, CancellationToken ct, Guid? ingestionRunId)
    {
        // Load write-back configuration.
        var config = _configLoader.LoadConfig<WriteBackConfiguration>("", "writeback")
                     ?? new WriteBackConfiguration();

        if (!config.Enabled)
        {
            _logger.LogDebug("WriteBack: disabled — skipping for asset {AssetId}", assetId);
            return WriteBackOutcome.Blocked("Write-back is disabled.");
        }

        // Check trigger-specific flags. The "config_change" trigger is the
        // auto re-tag sweep — defaults to enabled (mirrors WriteOnAutoMatch)
        // because the user has just edited writeback-fields.json and clicked Apply.
        var allowed = trigger switch
        {
            "auto_match" => config.WriteOnAutoMatch,
            "manual_override" => config.WriteOnManualOverride,
            "universe_enrichment" => config.WriteOnUniverseEnrichment,
            "config_change" => config.WriteOnAutoMatch,
            _ => config.Enabled,
        };

        if (!allowed)
        {
            _logger.LogDebug("WriteBack: trigger '{Trigger}' disabled — skipping for asset {AssetId}",
                trigger, assetId);
            return WriteBackOutcome.Blocked($"Write-back trigger '{trigger}' is disabled.");
        }

        // Resolve file path.
        var asset = await _assetRepo.FindByIdAsync(assetId, ct);
        if (asset is null)
        {
            _logger.LogDebug(
                "WriteBack: asset {AssetId} not found in database; skipping stale write-back work",
                assetId);
            return WriteBackOutcome.Failed("The asset no longer exists.");
        }

        if (string.IsNullOrWhiteSpace(asset.FilePathRoot) || !File.Exists(asset.FilePathRoot))
        {
            _logger.LogWarning("WriteBack: file not found at {Path} for asset {AssetId}",
                asset.FilePathRoot, assetId);
            return WriteBackOutcome.Blocked("The source file is unavailable.");
        }

        var resolvedSource = _libraryResolver?.ResolveSourceForPath(asset.FilePathRoot);
        if (resolvedSource is null)
        {
            _logger.LogDebug(
                "WriteBack: skipping — file {Path} has no configured source policy",
                asset.FilePathRoot);
            return WriteBackOutcome.Blocked("The file has no configured source policy.");
        }

        var sourcePolicy = FileSourceMutationPolicyFactory.Create(
            resolvedSource.Library,
            resolvedSource.Source,
            globalMetadataWritebackEnabled: config.Enabled);
        var mutationDecision = _sourceMutationGate.Evaluate(new SourceMutationRequest
        {
            Source = sourcePolicy,
            Mutation = SourceMutationKind.MetadataWriteback,
            Path = asset.FilePathRoot,
        });
        if (!mutationDecision.Allowed)
        {
            _logger.LogDebug(
                "WriteBack: source policy denied metadata write for {Path}: {Reason}",
                asset.FilePathRoot, mutationDecision.Reason);
            return WriteBackOutcome.Blocked(mutationDecision.Reason ?? "Source policy denied metadata write-back.");
        }

        // Find a tagger for this file type.
        var tagger = _taggers.FirstOrDefault(t => t.CanHandle(asset.FilePathRoot));
        if (tagger is null)
        {
            _logger.LogDebug("WriteBack: no tagger supports {Path} — skipping", asset.FilePathRoot);
            return WriteBackOutcome.Unsupported("No metadata tagger supports this file format.");
        }

        var lineage = await _workRepo.GetLineageByAssetAsync(assetId, ct);
        if (lineage is null)
        {
            _logger.LogDebug("WriteBack: no work lineage for asset {AssetId} — skipping", assetId);
            return WriteBackOutcome.Failed("The asset has no Work/Edition lineage.");
        }

        // Field ownership is media-aware: parent, Work, Edition and Asset values
        // are separate sources. Fetch them together and resolve each configured
        // field through ClaimScopeCatalog below.
        var entityIds = new[]
        {
            lineage.TargetForParentScope,
            lineage.TargetForSelfScope,
            lineage.EditionId,
            lineage.AssetId,
        }.Distinct().ToList();
        var canonicalValues = await _canonicalRepo.GetByEntitiesAsync(entityIds, ct);

        // Resolve the media type for writeback-fields.json.
        var mediaType = lineage.MediaType.ToString();

        // Load the per-media-type field catalogue (single source of truth for
        // both display in the library detail drawer and file write-back).
        var fieldsConfig = _configLoader.LoadConfig<WritebackFieldsConfiguration>("", "writeback-fields")
                           ?? new WritebackFieldsConfiguration();
        var allowedFields = new HashSet<string>(
            fieldsConfig.GetFieldsFor(mediaType),
            StringComparer.OrdinalIgnoreCase);

        if (allowedFields.Count == 0)
        {
            _logger.LogDebug("WriteBack: no writable fields configured for media type {MediaType} — skipping {AssetId}",
                mediaType, assetId);
            return WriteBackOutcome.Blocked($"No writable fields are configured for {mediaType}.");
        }

        var excludeFields = new HashSet<string>(config.ExcludeFields, StringComparer.OrdinalIgnoreCase);
        var tags = EffectiveWritebackMetadataComposer.Compose(
            lineage, canonicalValues, allowedFields, excludeFields);

        if (tags.Count == 0)
        {
            _logger.LogDebug("WriteBack: no writable fields after filtering — skipping {AssetId}", assetId);
            return WriteBackOutcome.Blocked("No configured canonical fields have values to write.");
        }

        // A configured field without a value is harmless. Only fields in the
        // effective snapshot are validated. Reject the entire request before
        // mutation if the adapter cannot consume one of those values.
        // Write tags.
        try
        {
            var capabilities = tagger.GetCapabilities(asset.FilePathRoot);
            capabilities.ValidateTags(tags);
            await tagger.WriteTagsAsync(asset.FilePathRoot, tags, ct);
            var readback = await tagger.VerifyTagsAsync(asset.FilePathRoot, tags, ct);

            // A verified physical mutation changes the asset's reconciliation
            // fingerprint. Refresh both durable identity and the watcher cache
            // before reporting success so our own file event keeps this asset ID.
            if (readback.IsVerified && _assetHasher is not null)
            {
                var fingerprint = await _assetHasher.ComputeAsync(asset.FilePathRoot, ct);
                if (!await _assetRepo.UpdateContentHashAsync(assetId, fingerprint.Hex, ct))
                    throw new IOException("The verified file fingerprint belongs to another asset.");
                if (_fileHashCache is not null)
                {
                    var file = new FileInfo(asset.FilePathRoot);
                    await _fileHashCache.UpsertAsync(Path.GetFullPath(asset.FilePathRoot),
                        fingerprint.FileSize, file.LastWriteTimeUtc, fingerprint.Hex, ct);
                }
            }

            // The applied hash is evidence of a complete physical read-back.
            // Unverified attempts retain their distinct marker so a sweep
            // cannot repeatedly select the same file in one pass.
            if (_hashState?.CurrentHashes.TryGetValue(mediaType, out var hash) == true
                && !string.IsNullOrEmpty(hash))
            {
                if (readback.IsVerified)
                    await _assetRepo.UpdateWritebackHashAsync(assetId, hash, ct);
                else
                    await _assetRepo.MarkWritebackUnverifiedAsync(assetId, hash, ct);
            }

            _logger.LogInformation("WriteBack: {Result} {Count} fields on {Path} (trigger: {Trigger}){Reason}",
                readback.IsVerified ? "verified" : "attempted", tags.Count, asset.FilePathRoot,
                trigger, readback.Reason is null ? string.Empty : $"; {readback.Reason}");

            // Log to activity ledger.
            await _activityRepo.LogAsync(new Domain.Entities.SystemActivityEntry
            {
                ActionType = Domain.Constants.SystemActionType.MetadataWrittenToFile,
                EntityId = assetId,
                Detail = $"Write-back ({trigger}): {tags.Count} field(s) "
                    + (readback.IsVerified ? "verified by physical read-back" : "attempted; physical read-back unverified")
                    + $" on {Path.GetFileName(asset.FilePathRoot)}.",
                IngestionRunId = ingestionRunId,
            }, ct);
            return readback.IsVerified
                ? WriteBackOutcome.Verified()
                : WriteBackOutcome.Unverified(readback.Reason ?? "Physical read-back did not verify the write.");
        }
        catch (Exception ex) when (trigger != "config_change" && (ex is NotSupportedException or FormatException))
        {
            if (_hashState?.CurrentHashes.TryGetValue(mediaType, out var hash) == true
                && !string.IsNullOrEmpty(hash))
                await _assetRepo.MarkWritebackUnsupportedAsync(assetId, hash, ex.Message, ct);
            _logger.LogWarning(ex, "WriteBack: unsupported field request for {Path}", asset.FilePathRoot);
            return WriteBackOutcome.Unsupported(ex.Message);
        }
        catch (Exception ex) when (trigger == "config_change")
        {
            // For sweep-driven writes, the caller (RetagSweepWorker) is
            // responsible for failure classification and retry routing.
            // Re-throw so the worker sees the original exception.
            _logger.LogWarning(ex, "WriteBack: sweep failed for {Path} — caller will classify",
                asset.FilePathRoot);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WriteBack: failed to write metadata to {Path}", asset.FilePathRoot);
            // Non-fatal — write-back failure should not break the pipeline.
            return WriteBackOutcome.Failed(ex.Message);
        }
    }
}
