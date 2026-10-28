using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaEngine.Ingestion.Services;

/// <summary>Default <see cref="IImportFolderFiler"/>.</summary>
public sealed class ImportFolderFiler : IImportFolderFiler
{
    // Most files looked at in one pass, so a very large import folder can't hold up the rest of the engine. The next pass
    // carries on where this one stopped, so no file is starved however many are ahead of it.
    private const int MaxFilesPerPass = 2000;

    // Identity work that has finished far enough for filing to be worth trying. A file whose job is still searching,
    // matching or hydrating is left for the pipeline, which tries to file it itself when that work is done.
    private static readonly HashSet<IdentityJobState> SettledStates =
    [
        IdentityJobState.RetailMatchedNeedsReview,
        IdentityJobState.RetailNoMatch,
        IdentityJobState.QidNeedsReview,
        IdentityJobState.QidNoMatch,
        IdentityJobState.UniverseEnriching,
        IdentityJobState.Ready,
        IdentityJobState.ReadyWithoutUniverse,
    ];

    private readonly IImportFolderService _importFolders;
    private readonly IMediaAssetRepository _assets;
    private readonly IIdentityJobRepository _identityJobs;
    private readonly ICanonicalValueRepository _canonicals;
    private readonly IAutoOrganizeService _autoOrganize;
    private readonly IFileOrganizer _organizer;
    private readonly ISourceMutationPolicyGate _gate;
    private readonly ImportFileMover _mover;
    private readonly ImportFilingJournal? _journal;
    private readonly IngestionOptions _options;
    private readonly ILogger<ImportFolderFiler> _logger;
    private int _cursor;

    public ImportFolderFiler(
        IImportFolderService importFolders,
        IMediaAssetRepository assets,
        IIdentityJobRepository identityJobs,
        ICanonicalValueRepository canonicals,
        IAutoOrganizeService autoOrganize,
        IFileOrganizer organizer,
        ISourceMutationPolicyGate gate,
        ImportFileMover mover,
        IOptions<IngestionOptions> options,
        ILogger<ImportFolderFiler> logger,
        ImportFilingJournal? journal = null)
    {
        _importFolders = importFolders;
        _assets = assets;
        _identityJobs = identityJobs;
        _canonicals = canonicals;
        _autoOrganize = autoOrganize;
        _organizer = organizer;
        _gate = gate;
        _mover = mover;
        _journal = journal;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<int> FilePendingAsync(CancellationToken ct = default)
    {
        var root = _importFolders.GetImportPath();
        if (root is null || !Directory.Exists(root))
        {
            return 0;
        }

        List<string> files;
        try
        {
            files = SafeEnumerate(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories), root)
                .Where(file => !_importFolders.IsWorkingPath(file) && !_importFolders.IsViewSectionPath(file))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read the import folder {Path} to file waiting items", root);
            return 0;
        }

        if (files.Count == 0)
        {
            _cursor = 0;
            return 0;
        }

        var filed = 0;
        var start = _cursor >= files.Count ? 0 : _cursor;
        var take = Math.Min(files.Count, MaxFilesPerPass);
        for (var offset = 0; offset < take; offset++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[(start + offset) % files.Count];

            var asset = await _assets.FindByPathRootAsync(Path.GetFullPath(file), ct).ConfigureAwait(false);
            if (asset is null || !await IdentityWorkSettledAsync(asset.Id, ct).ConfigureAwait(false))
            {
                continue;
            }

            await _autoOrganize.TryAutoOrganizeAsync(asset.Id, ct).ConfigureAwait(false);
            if (!File.Exists(file))
            {
                filed++;
            }
        }

        _cursor = (start + take) % files.Count;
        if (filed > 0)
        {
            _logger.LogInformation("Filed {Count} waiting file(s) out of the import folder", filed);
        }

        return filed;
    }

    public async Task<int> RecoverInterruptedFilingsAsync(CancellationToken ct = default)
    {
        if (_journal is null)
        {
            return 0;
        }

        var finished = 0;
        foreach (var intent in _journal.ReadPending())
        {
            ct.ThrowIfCancellationRequested();
            var asset = await _assets.FindByIdAsync(intent.AssetId, ct).ConfigureAwait(false);
            var landed = asset is null
                ? null
                : await _mover.FindCopyByFingerprintAsync(intent.DestinationPath, intent.ContentHash, ct).ConfigureAwait(false);
            if (asset is null || landed is null)
            {
                // The asset is gone, or the file never reached its destination: there is nothing to finish.
                _journal.Complete(intent.AssetId);
                continue;
            }

            if (!PathSafety.Comparer.Equals(Path.GetFullPath(asset.FilePathRoot), Path.GetFullPath(landed)))
            {
                await _assets.UpdateFilePathAsync(asset.Id, landed, ct).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(intent.LibraryId)
                && !string.Equals(asset.LibraryId, intent.LibraryId, StringComparison.OrdinalIgnoreCase))
            {
                await _assets.SetLibraryIdAsync(asset.Id, intent.LibraryId, ct).ConfigureAwait(false);
            }

            RemoveOriginal(intent.SourcePath);
            _journal.Complete(intent.AssetId);
            finished++;
            _logger.LogInformation(
                "Finished an interrupted import: asset {Id} now points at {Path}", asset.Id, landed);
        }

        return finished;
    }

    public async Task<string?> FileDuplicateCopyAsync(
        string copyPath,
        Guid heldAssetId,
        string contentHash,
        CancellationToken ct = default)
    {
        var held = await _assets.FindByIdAsync(heldAssetId, ct).ConfigureAwait(false);
        if (held is null || _importFolders.IsInImportFolder(held.FilePathRoot) || !File.Exists(copyPath))
        {
            return null;
        }

        var canonicals = await _canonicals.GetByEntityAsync(heldAssetId, ct).ConfigureAwait(false);
        var metadata = canonicals.ToDictionary(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);
        if (!metadata.TryGetValue("media_type", out var mediaTypeText)
            || !Enum.TryParse<MediaType>(mediaTypeText, ignoreCase: true, out var mediaType)
            || mediaType == MediaType.Unknown)
        {
            return null;
        }

        var destination = _importFolders.ResolveDestination(mediaType, new FileInfo(copyPath).Length);
        if (!destination.IsReady)
        {
            _logger.LogDebug("The copy {Path} stays in the import folder: {State}", copyPath, destination.State);
            return null;
        }

        var synth = new IngestionCandidate
        {
            Path = copyPath,
            EventType = FileEventType.Created,
            DetectedAt = DateTimeOffset.UtcNow,
            ReadyAt = DateTimeOffset.UtcNow,
            Metadata = metadata,
            DetectedMediaType = mediaType,
        };
        var relative = LibraryOrganizationPath.Calculate(
            _organizer, synth, destination.Library, _options.ResolveTemplate(mediaType.ToString()));
        var destPath = Path.GetFullPath(Path.Combine(destination.Source!.Path, relative));
        if (!ImportFolderGate.CanFile(_gate, _importFolders, copyPath, destPath, destination, out var reason))
        {
            _logger.LogDebug("The copy {Path} stays in the import folder: {Reason}", copyPath, reason);
            return null;
        }

        // The name is the standard one, so it normally clashes with the copy that is held and the mover gives this one a unique name.
        var filedPath = await _mover.MoveAsync(copyPath, destPath, contentHash, adoptIdenticalCopy: false, ct: ct).ConfigureAwait(false);
        if (filedPath is null)
        {
            return null;
        }

        _importFolders.RemoveEmptyFolders(Path.GetDirectoryName(copyPath) ?? string.Empty);
        return filedPath;
    }

    /// <summary>Removes a leftover original through the source-policy gate. A locked or protected file is simply left.</summary>
    private void RemoveOriginal(string sourcePath)
    {
        var policy = _importFolders.CreateImportPolicy(allowDelete: true);
        if (policy is null
            || !File.Exists(sourcePath)
            || !_gate.Evaluate(new SourceMutationRequest
            {
                Source = policy,
                Mutation = SourceMutationKind.Delete,
                Path = sourcePath,
            }).Allowed)
        {
            return;
        }

        try
        {
            File.Delete(sourcePath);
            _importFolders.RemoveEmptyFolders(Path.GetDirectoryName(sourcePath) ?? string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove the original of an interrupted import: {Path}", sourcePath);
        }
    }

    private async Task<bool> IdentityWorkSettledAsync(Guid assetId, CancellationToken ct)
    {
        var job = await _identityJobs.GetByEntityAsync(assetId, ct).ConfigureAwait(false);
        return job is not null
            && Enum.TryParse<IdentityJobState>(job.State, ignoreCase: true, out var state)
            && SettledStates.Contains(state);
    }

    private IEnumerable<string> SafeEnumerate(IEnumerable<string> files, string root)
    {
        using var enumerator = files.GetEnumerator();
        while (true)
        {
            try
            {
                if (!enumerator.MoveNext())
                {
                    yield break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Stopped reading the import folder {Path} early", root);
                yield break;
            }

            yield return enumerator.Current;
        }
    }
}
