using System.Diagnostics;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion;

/// <summary>
/// Scans a Library Root directory for media files and uses embedded metadata
/// plus batch reconciliation to rebuild the database — the "Great Inhale v2".
///
/// <para>
/// Files already known to the database (matched by content hash) have their
/// paths updated if they have moved. New files are noted for a follow-up
/// ingestion pass. People are sourced from the database and canonical .data/assets headshot paths. Universe sidecars have been removed — universe data is stored
/// exclusively in the database.
/// </para>
/// </summary>
public sealed class LibraryScanner : ILibraryScanner
{
    private readonly IAssetHasher _hasher;
    private readonly ICollectionRepository _collectionRepo;
    private readonly IMediaAssetRepository _assetRepo;
    private readonly ICanonicalValueRepository _canonicalRepo;
    private readonly IMetadataClaimRepository _claimRepo;
    private readonly IPersonRepository _personRepo;
    private readonly INarrativeRootRepository _rootRepo;
    private readonly IFictionalEntityRepository _fictionalEntityRepo;
    private readonly IEntityRelationshipRepository _relRepo;
    private readonly IQidLabelRepository _qidLabelRepo;
    private readonly ICanonicalValueArrayRepository _arrayRepo;
    private readonly IMediaTypeExtensionCatalog _extensionCatalog;
    private readonly ILogger<LibraryScanner> _logger;

    // Stable GUID representing the library-scanner as a "provider" when re-inserting
    // canonical values. Distinct from the local-processor GUID so the claim source
    // is distinguishable in the claims table.
    private static readonly Guid ScannerProviderGuid = WellKnownProviders.LibraryScanner;

    // Sub-directories inside LibraryRoot that should never be scanned for media files.
    private static readonly HashSet<string> SkipDirectories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // .data/ consolidates managed engine state; never scan it.
            ".data",
            "staging", "universe", "tuvima-shadow", "orphans",
        };

    public LibraryScanner(
        IAssetHasher hasher,
        ICollectionRepository collectionRepo,
        IMediaAssetRepository assetRepo,
        ICanonicalValueRepository canonicalRepo,
        IMetadataClaimRepository claimRepo,
        IPersonRepository personRepo,
        INarrativeRootRepository rootRepo,
        IFictionalEntityRepository fictionalEntityRepo,
        IEntityRelationshipRepository relRepo,
        IQidLabelRepository qidLabelRepo,
        ICanonicalValueArrayRepository arrayRepo,
        IMediaTypeExtensionCatalog extensionCatalog,
        ILogger<LibraryScanner> logger)
    {
        _hasher = hasher;
        _collectionRepo = collectionRepo;
        _assetRepo = assetRepo;
        _canonicalRepo = canonicalRepo;
        _claimRepo = claimRepo;
        _personRepo = personRepo;
        _rootRepo = rootRepo;
        _fictionalEntityRepo = fictionalEntityRepo;
        _relRepo = relRepo;
        _qidLabelRepo = qidLabelRepo;
        _arrayRepo = arrayRepo;
        _extensionCatalog = extensionCatalog;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<LibraryScanResult> ScanAsync(
        string libraryRoot,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        int filesScanned = 0;
        int pathsUpdated = 0;
        int errors = 0;

        _logger.LogInformation(
            "Great Inhale v2 started. Library root: {LibraryRoot}", libraryRoot);

        foreach (var filePath in EnumerateMediaFiles(libraryRoot, _extensionCatalog.GetAllMediaExtensions()))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Compute a SHA-256 hash to identify the file by its content.
                HashResult hash;
                try
                {
                    hash = await _hasher.ComputeAsync(filePath, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Great Inhale: could not hash {Path} — skipping", filePath);
                    errors++;
                    continue;
                }

                // If the asset is already in the database, update its path if it moved.
                var existing = await _assetRepo.FindByHashAsync(hash.Hex, ct)
                    .ConfigureAwait(false);

                if (existing is not null)
                {
                    if (!string.Equals(existing.FilePathRoot, filePath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        await _assetRepo.UpdateFilePathAsync(existing.Id, filePath, ct)
                            .ConfigureAwait(false);
                        pathsUpdated++;
                        _logger.LogDebug(
                            "Great Inhale: path updated for asset {Id}: {Old} → {New}",
                            existing.Id, existing.FilePathRoot, filePath);
                    }
                }
                else
                {
                    // New file not yet in the database.
                    // A full ingestion pass is needed to process it properly
                    // (processor → claims → scoring → hydration).
                    _logger.LogDebug(
                        "Great Inhale: new file not yet in DB — " +
                        "run an ingestion pass to process it: {Path}", filePath);
                }

                filesScanned++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Great Inhale: error processing {Path}", filePath);
                errors++;
            }
        }

        sw.Stop();
        _logger.LogInformation(
            "Great Inhale v2 complete — files: {Files}, paths updated: {Paths}, " +
            "errors: {Errors}, elapsed: {Elapsed}ms",
            filesScanned, pathsUpdated, errors,
            (long)sw.Elapsed.TotalMilliseconds);

        return new LibraryScanResult
        {
            CollectionsUpserted = 0,
            EditionsUpserted = filesScanned,
            Errors = errors,
            Elapsed = sw.Elapsed,
        };
    }

    // -------------------------------------------------------------------------
    // People scanning (Great Inhale person recovery)
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public Task<int> ScanPeopleAsync(
        string libraryRoot,
        CancellationToken ct = default)
    {
        _ = libraryRoot;
        _ = ct;
        _logger.LogDebug("Great Inhale: legacy person sidecar recovery is disabled; people are sourced from the database and .data/assets.");
        return Task.FromResult(0);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Enumerates all media files under <paramref name="root"/>, skipping
    /// hidden engine-owned subdirectories.
    /// </summary>
    private static IEnumerable<string> EnumerateMediaFiles(string root, IReadOnlySet<string> mediaExtensions)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        // Check root-level files first.
        foreach (var file in Directory.EnumerateFiles(root))
        {
            if (mediaExtensions.Contains(Path.GetExtension(file)))
            {
                yield return file;
            }
        }

        // Then recurse into non-skip sub-directories.
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var dirName = Path.GetFileName(dir);
            if (dirName.StartsWith(".", StringComparison.Ordinal) || SkipDirectories.Contains(dirName))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (mediaExtensions.Contains(Path.GetExtension(file)))
                {
                    yield return file;
                }
            }
        }
    }
}
